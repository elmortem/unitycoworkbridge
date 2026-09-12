using System.Text.Json;
using AgentBridge.Coordination;

namespace AgentBridge.Cli;

// The whole coordination contract, reachable from any agent without a healthy editor. Reads and
// waits never start a Unity task and never create a right; mutations go through one short
// cross-process transaction.
internal static class CoordinationCommands
{
	public const string CliContracts = "coordination-v1,evidence-v1,test-cache-v2";
	private const int LockBudgetMs = 5000;

	public static int Run(string projectRoot, string[] arguments, CliOptions options)
	{
		if (arguments.Length == 0)
		{
			return Usage(options, "usage: agentbridge coord <command> [options]; run 'agentbridge coord help'");
		}

		var command = arguments[0];
		if (command is "help" or "--help" or "-h")
		{
			WriteHelp();
			return 0;
		}

		if (arguments.Length > 1)
		{
			return Usage(options, "unexpected argument: " + arguments[1]);
		}

		string canonical;
		string pathError;
		if (!CoordinationPathPolicy.TryResolveProjectRoot(projectRoot, out canonical, out pathError))
		{
			return Fail(options, CoordinationCodes.PathUnsupported, pathError);
		}

		if (command == "capabilities")
		{
			return Capabilities(projectRoot, canonical, options);
		}

		var store = new CoordinationFileStore(
			CoordinationPathPolicy.CoordinationRoot(canonical),
			CoordinationJsonCodec.Instance,
			CoordinationSystemClock.Instance,
			EnsureProjectId(projectRoot),
			() => Guid.NewGuid().ToString("N"));

		try
		{
			return command switch
			{
				"status" => Status(store, options),
				"wait" => Wait(store, options),
				"register" => Mutate(store, options, BuildRegister),
				"scope" => Mutate(store, options, BuildScope),
				"edit-begin" => Mutate(store, options, BuildEditBegin),
				"edit-end" => Mutate(store, options, BuildTokenCommand(CoordinationEngine.OpEditEnd)),
				"renew" => Mutate(store, options, BuildTokenCommand(CoordinationEngine.OpRenew)),
				"finish" => Mutate(store, options, BuildTokenCommand(CoordinationEngine.OpFinish)),
				"request" => Mutate(store, options, BuildRequest),
				"cancel" => Mutate(store, options, BuildCancel),
				"leave" => Mutate(store, options, BuildLeave),
				"abandon" => Mutate(store, options, BuildAbandon),
				_ => Usage(options, "unknown coord command: " + command)
			};
		}
		catch (CoordinationStoreException exception)
		{
			return Fail(options, exception.Code, exception.Message);
		}
	}

	// ---------------------------------------------------------------- reads

	private static int Capabilities(string projectRoot, string canonical, CliOptions options)
	{
		var health = BridgeInspector.Inspect(projectRoot);
		var packageCapabilities = health.Bridge?.Capabilities ?? Array.Empty<string>();
		var packageSupports = packageCapabilities.Contains("coordination-v1");

		if (options.Format == "human")
		{
			Console.Out.WriteLine("coord capabilities");
			Console.Out.WriteLine("CLI: " + CliContracts);
			Console.Out.WriteLine("Package: " + (health.Bridge == null
				? "unavailable (no readable status.json)"
				: string.Join(",", packageCapabilities)));
			Console.Out.WriteLine("Package supports coordination-v1: " + (packageSupports ? "yes" : "no"));
			Console.Out.WriteLine("Coordination root: " + CoordinationPathPolicy.CoordinationRoot(canonical));
			if (!string.IsNullOrEmpty(health.Bridge?.CoordinationUnavailable))
			{
				Console.Out.WriteLine("Package reports: " + health.Bridge!.CoordinationUnavailable);
			}

			Console.Out.WriteLine("Editor ready: " + (health.BridgeReady ? "yes" : "no (" + health.Code + ")"));
			return packageSupports ? 0 : 3;
		}

		JsonSupport.Write(new
		{
			Ok = packageSupports,
			Code = packageSupports ? CoordinationCodes.Ok : CoordinationCodes.SchemaUnsupported,
			Cli = CliContracts.Split(','),
			Package = packageCapabilities,
			PackageSupportsCoordination = packageSupports,
			PackageAvailable = health.Bridge != null,
			CoordinationRoot = CoordinationPathPolicy.CoordinationRoot(canonical),
			CoordinationUnavailable = health.Bridge?.CoordinationUnavailable,
			EditorReady = health.BridgeReady
		});
		return packageSupports ? 0 : 3;
	}

	private static int Status(CoordinationFileStore store, CliOptions options)
	{
		var state = TryRead(store);
		if (state == null)
		{
			return Absent(options);
		}

		var reply = new CoordinationEngine().Inspect(
			state, options.Session ?? "", options.RequestUuid ?? "", CoordinationSystemClock.Instance.UtcNowMs);
		CoordinationResultFormatter.Write(Redact(reply, options.Session), options.Format, state);
		return 0;
	}

	private static int Wait(CoordinationFileStore store, CliOptions options)
	{
		var after = options.AfterRevision;
		using var waiter = new CoordinationWaiter(store);
		var state = waiter.Wait(after, TimeSpan.FromSeconds(options.WaitSeconds), CancellationToken.None);

		if (state == null)
		{
			// An expired wait cancels nothing. The request keeps living under its own id.
			var current = TryRead(store);
			if (current == null)
			{
				return Absent(options);
			}

			var timedOut = new CoordinationEngine().Inspect(
				current, options.Session ?? "", options.RequestUuid ?? "", CoordinationSystemClock.Instance.UtcNowMs);
			timedOut.Code = "wait_timeout";
			CoordinationResultFormatter.Write(Redact(timedOut, options.Session), options.Format, current);
			return 2;
		}

		var reply = new CoordinationEngine().Inspect(
			state, options.Session ?? "", options.RequestUuid ?? "", CoordinationSystemClock.Instance.UtcNowMs);
		CoordinationResultFormatter.Write(Redact(reply, options.Session), options.Format, state);
		return 0;
	}

	// ---------------------------------------------------------------- mutations

	private delegate int Builder(CliOptions options, out CoordinationCommand command, out string error);

	private static int Mutate(CoordinationFileStore store, CliOptions options, Builder builder)
	{
		CoordinationCommand command;
		string error;
		var usage = builder(options, out command, out error);
		if (usage != 0)
		{
			return Usage(options, error);
		}

		command.Nonce = Guid.NewGuid().ToString("N");

		using var transaction = store.BeginWithRetry(LockBudgetMs);
		var reply = new CoordinationEngine().Apply(
			transaction.State, command, CoordinationSystemClock.Instance.UtcNowMs);
		if (reply.Changed)
		{
			transaction.Commit();
		}

		CoordinationResultFormatter.Write(Redact(reply, options.Session), options.Format, null);
		return reply.Ok ? 0 : 1;
	}

	private static int BuildRegister(CliOptions options, out CoordinationCommand command, out string error)
	{
		command = new CoordinationCommand { Op = CoordinationEngine.OpRegister };
		error = "";

		if (string.IsNullOrEmpty(options.Session) || string.IsNullOrEmpty(options.SpecId))
		{
			error = "usage: agentbridge coord register --session S --spec T --repo R --scope scope.json --owner ADDRESS";
			return 1;
		}

		string[] paths;
		if (!TryReadScope(options.ScopeFile, out paths, out error))
		{
			return 1;
		}

		command.Session = options.Session;
		command.SpecId = options.SpecId;
		command.Owner = options.Owner ?? "";
		command.RepoRoot = options.RepoRoot ?? "";
		command.Paths = paths;
		return 0;
	}

	private static int BuildScope(CliOptions options, out CoordinationCommand command, out string error)
	{
		command = new CoordinationCommand { Op = CoordinationEngine.OpScope };
		error = "";

		if (string.IsNullOrEmpty(options.Session) || string.IsNullOrEmpty(options.ScopeFile))
		{
			error = "usage: agentbridge coord scope --session S --scope scope.json";
			return 1;
		}

		string[] paths;
		if (!TryReadScope(options.ScopeFile, out paths, out error))
		{
			return 1;
		}

		command.Session = options.Session;
		command.Paths = paths;
		return 0;
	}

	private static int BuildEditBegin(CliOptions options, out CoordinationCommand command, out string error)
	{
		command = new CoordinationCommand { Op = CoordinationEngine.OpEditBegin };
		error = "";

		if (string.IsNullOrEmpty(options.Session) || string.IsNullOrEmpty(options.RequestUuid))
		{
			error = "usage: agentbridge coord edit-begin --session S --request UUID [--seconds 120]";
			return 1;
		}

		command.Session = options.Session;
		command.Uuid = options.RequestUuid;
		command.Seconds = options.Seconds;
		return 0;
	}

	private static Builder BuildTokenCommand(string op)
	{
		return (CliOptions options, out CoordinationCommand command, out string error) =>
		{
			command = new CoordinationCommand { Op = op };
			error = "";

			if (string.IsNullOrEmpty(options.Session) || string.IsNullOrEmpty(options.Token))
			{
				error = "usage: agentbridge coord " + op + " --session S --token TOKEN";
				return 1;
			}

			command.Session = options.Session;
			command.Token = options.Token;
			command.Seconds = options.Seconds;
			return 0;
		};
	}

	private static int BuildRequest(CliOptions options, out CoordinationCommand command, out string error)
	{
		command = new CoordinationCommand { Op = CoordinationEngine.OpRequest };
		error = "";

		if (string.IsNullOrEmpty(options.Session)
			|| string.IsNullOrEmpty(options.RequestUuid)
			|| string.IsNullOrEmpty(options.Kind)
			|| string.IsNullOrEmpty(options.PlanFile))
		{
			error = "usage: agentbridge coord request --session S --request UUID --kind editor|validation --plan plan.json [--seconds 120]";
			return 1;
		}

		CoordinationPlan plan;
		if (!TryReadPlan(options.PlanFile, out plan, out error))
		{
			return 1;
		}

		command.Session = options.Session;
		command.Uuid = options.RequestUuid;
		command.Kind = options.Kind;
		command.Plan = plan;
		command.Seconds = options.Seconds;
		return 0;
	}

	private static int BuildCancel(CliOptions options, out CoordinationCommand command, out string error)
	{
		command = new CoordinationCommand { Op = CoordinationEngine.OpCancel };
		error = "";

		if (string.IsNullOrEmpty(options.Session) || string.IsNullOrEmpty(options.RequestUuid))
		{
			error = "usage: agentbridge coord cancel --session S --request UUID";
			return 1;
		}

		command.Session = options.Session;
		command.Uuid = options.RequestUuid;
		return 0;
	}

	private static int BuildLeave(CliOptions options, out CoordinationCommand command, out string error)
	{
		command = new CoordinationCommand { Op = CoordinationEngine.OpLeave };
		error = "";

		if (string.IsNullOrEmpty(options.Session))
		{
			error = "usage: agentbridge coord leave --session S";
			return 1;
		}

		command.Session = options.Session;
		return 0;
	}

	private static int BuildAbandon(CliOptions options, out CoordinationCommand command, out string error)
	{
		command = new CoordinationCommand { Op = CoordinationEngine.OpAbandon };
		error = "";

		if (string.IsNullOrEmpty(options.TargetSession) || string.IsNullOrEmpty(options.Reason))
		{
			error = "usage: agentbridge coord abandon --target-session S --reason TEXT "
				+ "(emergency recovery only, after the user confirmed that session's writers stopped)";
			return 1;
		}

		command.TargetSession = options.TargetSession;
		command.Reason = options.Reason;
		return 0;
	}

	// ---------------------------------------------------------------- input files

	private static bool TryReadScope(string? path, out string[] paths, out string error)
	{
		paths = Array.Empty<string>();
		error = "";

		if (string.IsNullOrEmpty(path))
		{
			return true;
		}

		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception exception)
		{
			error = "scope file could not be read: " + exception.Message;
			return false;
		}

		try
		{
			var document = JsonSerializer.Deserialize<ScopeDocument>(text, CoordinationJsonCodec.Strict);
			paths = document?.Paths ?? Array.Empty<string>();
			return true;
		}
		catch (JsonException exception)
		{
			error = "scope file is not a valid { \"Paths\": [...] } document: " + exception.Message;
			return false;
		}
	}

	private static bool TryReadPlan(string path, out CoordinationPlan plan, out string error)
	{
		plan = new CoordinationPlan();
		error = "";

		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception exception)
		{
			error = "plan file could not be read: " + exception.Message;
			return false;
		}

		try
		{
			plan = JsonSerializer.Deserialize<CoordinationPlan>(text, CoordinationJsonCodec.Strict) ?? new CoordinationPlan();
			plan.Normalize();
			return true;
		}
		catch (JsonException exception)
		{
			error = "plan file is not a valid plan document: " + exception.Message;
			return false;
		}
	}

	private sealed class ScopeDocument
	{
		public string[] Paths { get; set; } = Array.Empty<string>();
	}

	// ---------------------------------------------------------------- helpers

	private static CoordinationState? TryRead(CoordinationFileStore store)
	{
		try
		{
			return store.Read();
		}
		catch (CoordinationStoreException exception) when (exception.Code == CoordinationCodes.RecoveryRequired)
		{
			// A project that never had a coordinator is not a failure. A marker without its state
			// is, and that one keeps its diagnostic.
			if (store.Exists)
			{
				throw;
			}

			return null;
		}
	}

	// A token belongs to its owner. A shared status never carries somebody else's.
	private static CoordinationReply Redact(CoordinationReply reply, string? session)
	{
		if (string.IsNullOrEmpty(session) || reply.Session != session)
		{
			reply.Token = "";
		}

		return reply;
	}

	// The same identity the package writes, created here when coordination starts before the editor
	// ever ran in this project. The editor's ProjectIdentity.Ensure reads whatever it finds.
	private static string EnsureProjectId(string projectRoot)
	{
		var file = new BridgePaths(projectRoot).ProjectIdFile;
		try
		{
			if (File.Exists(file))
			{
				var existing = File.ReadAllText(file).Trim();
				if (existing.Length > 0)
				{
					return existing;
				}
			}

			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			var generated = Guid.NewGuid().ToString("N");
			File.WriteAllText(file, generated);
			return generated;
		}
		catch
		{
			return "";
		}
	}

	private static int Absent(CliOptions options)
	{
		var reply = CoordinationReply.Success("absent");
		reply.State = "absent";
		reply.Message = "this project has no coordination state; register a session to create one";
		CoordinationResultFormatter.Write(reply, options.Format, null);
		return 0;
	}

	private static int Fail(CliOptions options, string code, string message)
	{
		CoordinationResultFormatter.Write(CoordinationReply.Fail(code, message), options.Format, null);
		return 3;
	}

	private static int Usage(CliOptions options, string message)
	{
		return Fail(options, CoordinationCodes.BadUsage, message);
	}

	private static void WriteHelp()
	{
		Console.Out.WriteLine(
			"""
			agentbridge coord — coordination-v1

			usage: agentbridge coord <command> [options] --project <path> [--format json|human]

			commands:
			  capabilities                                            what the CLI and the package support
			  register --session S --spec T --repo R --scope f.json --owner A
			  scope --session S --scope scope.json                    replace the scope outside any right
			  edit-begin --session S --request UUID [--seconds 120]   queue a bounded package of file edits
			  edit-end --session S --token TOKEN                      confirm the writers finished
			  renew --session S --token TOKEN [--seconds 120]         extend a still valid edit grant
			  request --session S --request UUID --kind editor|validation --plan plan.json [--seconds 120]
			  finish --session S --token TOKEN                        close the window after terminal tasks
			  cancel --session S --request UUID                       cancel a request that never started
			  status [--session S] [--request UUID]                   one snapshot, no editor needed
			  wait --after REV [--wait 30] [--session S]              wait for the next meaningful change
			  leave --session S                                       close the registration and the scope
			  abandon --target-session S --reason TEXT                emergency recovery, user decision only

			exit codes: 0 success, 1 refusal or conflict, 2 wait expired, 3 bad usage or unavailable store

			work commands take --coord-window TOKEN --coord-step ID once a window is granted.
			""");
	}
}
