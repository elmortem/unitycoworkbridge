using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Coordination;

internal static class CoordinationLeaseTests
{
	public static void Run(string root)
	{
		string project = Path.Combine(root, "lease-cli");
		Directory.CreateDirectory(project);
		// macOS temporary paths may contain /var -> /private/var. The fixture needs a
		// physical project path so these tests reach the lease contract, not path rejection.
		project = PhysicalDirectory(project);
		Check(CoordinationPathPolicy.TryResolveProjectRoot(project, out _, out var pathError),
			$"lease fixture must use a supported physical path: {pathError}");
		string bridge = Path.Combine(project, "Library", "AgentBridge");
		Directory.CreateDirectory(bridge);
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		string scope = Path.Combine(project, "scope.json");
		File.WriteAllText(scope, """{"Paths":["Assets/Shared/"]}""");
		File.WriteAllText(Path.Combine(bridge, "status.json"), """{"Capabilities":["coordination-v1","coordination-batch-v1"]}""");

		(int Exit, JsonElement Reply) Invoke(string command, params string[] args)
		{
			var options = CliOptions.Parse(new[] { "coord", command }.Concat(args).ToArray());
			Check(options.Error == null, "test arguments must parse");
			var previous = Console.Out;
			using var output = new StringWriter();
			try
			{
				Console.SetOut(output);
				int exit = CoordinationCommands.Run(project, new[] { command }, options);
				return (exit, JsonDocument.Parse(output.ToString()).RootElement.Clone());
			}
			finally { Console.SetOut(previous); }
		}

		var legacy = Invoke("register", "--session", "a", "--spec", "night", "--scope", scope);
		Check(legacy.Exit == 3 && legacy.Reply.GetProperty("Code").GetString() == CoordinationCodes.SchemaUnsupported,
			$"new CLI must reject mutation against the old package; exit={legacy.Exit}, reply={legacy.Reply}");
		Check(!Directory.Exists(CoordinationPathPolicy.CoordinationRoot(project)), "capability refusal must not create state");
		File.WriteAllText(Path.Combine(bridge, "status.json"), JsonSerializer.Serialize(new
		{
			Capabilities = new[] { "coordination-v1", "coordination-batch-v1", CoordinationLimits.EditLeasesCapability }
		}));
		foreach (string session in new[] { "a", "b" })
			Check(Invoke("register", "--session", session, "--spec", "night", "--scope", scope).Exit == 0,
				"CLI accepts overlapping declarations");
		var a = Invoke("edit-begin", "--session", "a", "--request", "a");
		var b = Invoke("edit-begin", "--session", "b", "--request", "b");
		Check(a.Reply.GetProperty("Code").GetString() == CoordinationCodes.Granted, "first CLI writer granted");
		Check(b.Reply.GetProperty("Code").GetString() == CoordinationCodes.Waiting, "second CLI writer queued");
		Check(Invoke("edit-end", "--session", "a", "--token", a.Reply.GetProperty("Token").GetString()!).Exit == 0,
			"CLI closes edit without leaving registration");
		var status = Invoke("status", "--session", "b", "--request", "b");
		Check(status.Reply.GetProperty("State").GetString() == CoordinationLimits.StateGranted
			&& !string.IsNullOrEmpty(status.Reply.GetProperty("Token").GetString()), "status returns next writer's usable token");
		Console.WriteLine("CLI edit leases: PASS (package gate, overlap registration, queued handoff)");
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	private static string PhysicalDirectory(string path)
	{
		var directory = new DirectoryInfo(path);
		if (directory.Parent == null) return directory.FullName;
		var physical = new DirectoryInfo(Path.Combine(PhysicalDirectory(directory.Parent.FullName), directory.Name));
		try
		{
			if ((physical.Attributes & FileAttributes.ReparsePoint) != 0)
				return physical.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? physical.FullName;
		}
		catch (UnauthorizedAccessException)
		{
			// A sandbox can expose the temp directory without exposing ancestor metadata.
			// Keep that ancestor spelling; the fixture still passes the production path check.
		}
		return physical.FullName;
	}
}
