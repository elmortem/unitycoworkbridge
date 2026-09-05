using System.Reflection;

namespace AgentBridge.Cli;

internal static class AgentBridgeApplication
{
	public static async Task<int> RunAsync(string[] args)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;
		var options = CliOptions.Parse(args);
		if (options.Error != null)
		{
			return WriteError("bad_usage", options.Error, options.Format);
		}

		if (options.Arguments.Count == 0 || options.Arguments[0] is "help" or "--help" or "-h")
		{
			WriteHelp();
			return options.Arguments.Count == 0 ? 3 : 0;
		}

		if (options.Arguments[0] is "--version" or "version")
		{
			Console.Out.WriteLine(GetVersion());
			return 0;
		}

		var projectRoot = ProjectLocator.Resolve(options.ProjectPath, Environment.CurrentDirectory);
		if (projectRoot == null)
		{
			return WriteError(
				"project_not_found",
				options.ProjectPath == null
					? "No Unity project found at or above the current directory. Use --project <path>."
					: "The --project path is not a Unity project.",
				options.Format);
		}

		var command = options.Arguments[0];
		var commandArguments = options.Arguments.Skip(1).ToArray();
		var paths = new BridgePaths(projectRoot);
		paths.EnsureScratch();
		var health = BridgeInspector.Inspect(projectRoot);

		if (command == "status")
		{
			WriteHealth(health, options.Format);
			return health.BridgeReady ? 0 : 3;
		}

		if (command == "doctor")
		{
			WriteDoctor(health, options.Format);
			return health.BridgeReady && health.CSharpReady ? 0 : 1;
		}

		// status/doctor remain read-only and report stale honestly. Work commands may
		// enter the bounded watchdog even if the editor fell asleep before submission.
		if (!health.BridgeReady && !WakePolicy.CanRecover(health))
		{
			WriteHealth(health, options.Format);
			return 3;
		}

		// Whether the bridge keeps telemetry is the editor's setting, and the status file is the
		// only place the client can read it from.
		var telemetry = new TelemetryLog(projectRoot, health.Bridge?.TelemetryEnabled ?? false);
		var client = new BridgeClient(projectRoot, options.Format, options.Session, options.Note, telemetry);
		switch (command)
		{
			case "csharp":
				if (commandArguments.Length != 1)
				{
					return WriteError("bad_usage", "usage: agentbridge csharp <file> [--project <path>] [--wait <seconds>] [--format json|human]", options.Format);
				}

				if (health.Bridge?.RoslynReady != true)
				{
					return WriteError("roslyn_not_ready", "Roslyn is not ready. Run agentbridge doctor.", options.Format);
				}

				WarnIfPayloadInsideAssets(paths, commandArguments[0]);
				return await client.SubmitPayloadAsync("csharp", commandArguments[0], options.WaitSeconds);

			case "ui":
				if (commandArguments.Length != 1)
				{
					return WriteError("bad_usage", "usage: agentbridge ui <file.ui.json> [--project <path>] [--wait <seconds>] [--format json|human]", options.Format);
				}

				WarnIfPayloadInsideAssets(paths, commandArguments[0]);
				return await client.SubmitPayloadAsync("ui", commandArguments[0], options.WaitSeconds);

			case "sceneshot":
				if (commandArguments.Length != 1)
				{
					return WriteError("bad_usage", "usage: agentbridge sceneshot <file.sceneshot.json> [--project <path>] [--wait <seconds>] [--format json|human]", options.Format);
				}

				WarnIfPayloadInsideAssets(paths, commandArguments[0]);
				return await client.SubmitPayloadAsync("sceneshot", commandArguments[0], options.WaitSeconds);

			case "compile":
				if (commandArguments.Length != 0)
				{
					return WriteError("bad_usage", "usage: agentbridge compile [--fresh] [--project <path>] [--wait <seconds>] [--format json|human]", options.Format);
				}

				return await client.SubmitCompileAsync(options.WaitSeconds, options.Fresh);

			case "tests":
				if (!TryParseTests(commandArguments, out var mode, out var assemblies, out var tests, out var categories, out var error))
				{
					return WriteError("bad_usage", error, options.Format);
				}

				return await client.SubmitTestsAsync(mode, assemblies, tests, categories, options.WaitSeconds, options.Fresh);

			case "release":
				if (commandArguments.Length != 0 || options.Session == null)
				{
					return WriteError("bad_usage", "usage: agentbridge release --session <id> [--project <path>] [--wait <seconds>]", options.Format);
				}

				return await client.SubmitReleaseAsync(options.WaitSeconds);

			case "play":
				if (commandArguments.Length != 0 || options.Session == null)
				{
					return WriteError("bad_usage", "usage: agentbridge play [--seconds N] --note <intent> --session <id> [--project <path>] [--wait <seconds>]", options.Format);
				}

				if (string.IsNullOrWhiteSpace(options.Note))
				{
					return WriteError("bad_usage", "play requires --note with the intent of the session (what to check and why)", options.Format);
				}

				return await client.SubmitPlayAsync(options.Seconds, options.WaitSeconds);

			case "stopplay":
				if (commandArguments.Length != 0)
				{
					return WriteError("bad_usage", "usage: agentbridge stopplay [--session <id>] [--project <path>] [--wait <seconds>]", options.Format);
				}

				return await client.SubmitStopplayAsync(options.WaitSeconds);

			case "wait":
				if (commandArguments.Length != 1)
				{
					return WriteError("bad_usage", "usage: agentbridge wait <TaskId> [--project <path>] [--wait <seconds>] [--format json|human]", options.Format);
				}

				return await client.WaitForTaskAsync(commandArguments[0], options.WaitSeconds, "wait");

			default:
				return WriteError("bad_usage", "Unknown command: " + command, options.Format);
		}
	}

	private static bool TryParseTests(
		string[] args,
		out string mode,
		out string[] assemblies,
		out string[] tests,
		out string[] categories,
		out string error)
	{
		mode = "EditMode";
		var assemblyList = new List<string>();
		var testList = new List<string>();
		var categoryList = new List<string>();

		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			if (argument is not ("--mode" or "--assembly" or "--test" or "--category") || index + 1 >= args.Length)
			{
				assemblies = Array.Empty<string>();
				tests = Array.Empty<string>();
				categories = Array.Empty<string>();
				error = "usage: agentbridge tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C] [--fresh]";
				return false;
			}

			var value = args[++index];
			switch (argument)
			{
				case "--mode":
					if (value is not ("EditMode" or "PlayMode"))
					{
						assemblies = Array.Empty<string>();
						tests = Array.Empty<string>();
						categories = Array.Empty<string>();
						error = "--mode must be EditMode or PlayMode";
						return false;
					}

					mode = value;
					break;
				case "--assembly":
					assemblyList.Add(value);
					break;
				case "--test":
					testList.Add(value);
					break;
				case "--category":
					categoryList.Add(value);
					break;
			}
		}

		assemblies = assemblyList.ToArray();
		tests = testList.ToArray();
		categories = categoryList.ToArray();
		error = "";
		return true;
	}

	private static void WarnIfPayloadInsideAssets(BridgePaths paths, string payloadPath)
	{
		if (!paths.IsInsideAssets(payloadPath))
		{
			return;
		}

		Console.Error.WriteLine(
			"[AgentBridge] warning: task file is inside Assets ("
			+ payloadPath
			+ "). Unity imports it and recompiles the project on every task. Write task files to "
			+ paths.Scratch
			+ " instead and delete the one in Assets.");
	}

	private static void WriteHealth(BridgeHealth health, string format)
	{
		if (format == "human")
		{
			Console.Out.WriteLine(health.BridgeReady ? "Agent Bridge is ready." : "Agent Bridge is unavailable: " + health.Code);
			Console.Out.WriteLine("Project: " + health.ProjectPath);
			Console.Out.WriteLine("Task files: " + health.ScratchDir);
			if (health.ForeignHost)
			{
				Console.Out.WriteLine(
					"Host: editor on " + health.HostOs + ", client on " + health.ClientOs
					+ " (process check skipped, liveness from heartbeat)");
			}
			if (health.Bridge != null)
			{
				Console.Out.WriteLine("Package: " + health.Bridge.PackageVersion);
				Console.Out.WriteLine("Unity: " + health.Bridge.UnityVersion);
				Console.Out.WriteLine("Roslyn: " + (health.Bridge.RoslynReady ? "ready" : "not ready"));
				Console.Out.WriteLine("Wake timer: "
					+ (health.Bridge.WakeTimerInstalled ? (health.Bridge.WakeTimerKind ?? "installed") : "missing"));
				Console.Out.WriteLine("Interaction mode: " + (health.Bridge.InteractionMode ?? "unknown"));
				Console.Out.WriteLine("Active task: " + (health.Bridge.ActiveTaskId ?? "none"));

				var playing = health.Bridge.IsPlaying ? "yes" : "no";
				if (!string.IsNullOrEmpty(health.Bridge.PlaySessionAgentId))
				{
					playing += " (session " + health.Bridge.PlaySessionAgentId
						+ ", until " + (health.Bridge.PlaySessionDeadlineUtc ?? "unknown") + ")";
				}
				else if (health.Bridge.IsPlaying)
				{
					playing += " (manual)";
				}

				Console.Out.WriteLine("Playing: " + playing);
			}

			return;
		}

		JsonSupport.Write(health);
	}

	private static void WriteDoctor(BridgeHealth health, string format)
	{
		if (format == "human")
		{
			WriteHealth(health, format);
			Console.Out.WriteLine("CLI version: " + GetVersion());
			Console.Out.WriteLine("CLI path: " + (Environment.ProcessPath ?? "unknown"));
			foreach (var problem in health.Problems)
			{
				Console.Out.WriteLine("- " + problem);
			}

			foreach (var warning in health.Warnings)
			{
				Console.Out.WriteLine("! " + warning);
			}

			return;
		}

		JsonSupport.Write(new
		{
			Ok = health.BridgeReady && health.CSharpReady,
			CliVersion = GetVersion(),
			CliPath = Environment.ProcessPath,
			ExpectedProtocolVersion = BridgeConstants.ProtocolVersion,
			Health = health
		});
	}

	private static string GetVersion()
	{
		return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
	}

	private static int WriteError(string code, string message, string format)
	{
		if (format == "human")
		{
			Console.Out.WriteLine("agentbridge: error (" + code + ")");
			Console.Out.WriteLine("Message: " + message);
			return 3;
		}

		JsonSupport.Write(new
		{
			Ok = false,
			Code = code,
			Message = message
		});
		return 3;
	}

	private static void WriteHelp()
	{
		Console.Out.WriteLine(
			"""
			Unity Agent Bridge CLI

			usage: agentbridge <command> [arguments] [--project <path>] [--wait <seconds>] [--format json|human]

			commands:
			  status
			  doctor
			  csharp <file.cs>          task files belong in <project>/Temp/AgentBridge, never in Assets
			  ui <file.ui.json>
			  sceneshot <file.sceneshot.json>
			  compile [--fresh]
			  tests [--mode EditMode|PlayMode] [--assembly A] [--test T] [--category C] [--fresh]
			  release --session <id>     give the editor back to the other agent sessions
			  play [--seconds N] --note <intent> --session <id>   open a play session; only csharp and sceneshot run inside it
			  stopplay [--session <id>]  end your play session, or an unsanctioned one anybody left behind
			  wait <TaskId>

			global options:
			  --project <path>   Unity project root; otherwise discovered from cwd
			  --wait <seconds>   client wait timeout, default 110
			  --seconds <n>      play session length; defaults to the editor setting (30)
			  --format <value>   json (default, machine-readable) or human for every command
			  --session <id>     agent session for fair scheduling
			  --note <text>      intent shown to the session holding the editor
			  --fresh            force a real run for tests/compile, ignore cached results
			  --version
			""");
	}
}
