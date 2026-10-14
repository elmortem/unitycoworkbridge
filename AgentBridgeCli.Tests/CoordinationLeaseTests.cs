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
		project = TestPaths.PhysicalDirectory(project);
		Check(CoordinationPathPolicy.TryResolveProjectRoot(project, out _, out var pathError),
			$"lease fixture must use a supported physical path: {pathError}");
		string bridge = Path.Combine(project, "Library", "AgentBridge");
		Directory.CreateDirectory(bridge);
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{}");
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
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

		var offline = Invoke("register", "--session", "a", "--spec", "night", "--scope", scope);
		Check(offline.Exit == 0, "coordination admission is independent of executor capabilities: " + offline.Reply);

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
		Console.WriteLine("CLI edit leases: PASS (offline admission, overlap registration, queued handoff)");
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

}
