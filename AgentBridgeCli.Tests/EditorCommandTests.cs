using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Queueing;

// `agentbridge editor open|close`: the order of executable candidates and the exit codes of every
// refusal. Launch is replaced, and the closing cases stop before any real window or process is touched.
internal static class EditorCommandTests
{
	private const string Version = "2022.3.62f2";

	internal static async Task RunAsync(string root)
	{
		RunVersionTests(root);
		RunCandidateTests();
		await RunOpenTests(root);
		await RunCloseTests(root);
		Console.WriteLine("Editor control: PASS (project version, candidate order, open/close refusals and exit codes)");
	}

	private static void RunVersionTests(string root)
	{
		var project = CreateProject(root, "editor-version", Version);
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"),
			"m_EditorVersion: 2022.3.62f2\r\nm_EditorVersionWithRevision: 2022.3.62f2 (7670c08855a9)\r\n");
		Check(EditorLocator.ProjectVersion(project) == Version, "the version comes from the m_EditorVersion line");
		File.Delete(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"));
		Check(EditorLocator.ProjectVersion(project) == "", "a missing ProjectVersion.txt gives an empty version");
	}

	private static void RunCandidateTests()
	{
		string? Windows(string name) => name switch
		{
			"APPDATA" => @"C:\Users\u\AppData\Roaming",
			"ProgramFiles" => @"C:\Program Files",
			_ => null
		};
		string? Secondary(string path) => path == @"C:\Users\u\AppData\Roaming\UnityHub\secondaryInstallPath.json" ? "\"D:\\\\Unity\\\\\"" : null;

		var all = EditorLocator.Candidates(Version, @"E:\U\Unity.exe", @"C:\S\Unity.exe", Version, HostPlatform.Windows, Windows, Secondary);
		Check(all.SequenceEqual(new[]
		{
			@"E:\U\Unity.exe",
			@"C:\S\Unity.exe",
			@"D:\Unity\2022.3.62f2\Editor\Unity.exe",
			@"C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe"
		}), "windows order: explicit, status, secondary root, Hub root: " + string.Join(" | ", all));

		var otherVersion = EditorLocator.Candidates(Version, null, @"C:\S\Unity.exe", "2021.3.1f1", HostPlatform.Windows, Windows, Secondary);
		Check(!otherVersion.Contains(@"C:\S\Unity.exe") && otherVersion.Count == 2, "the status path of another version is skipped: " + string.Join(" | ", otherVersion));

		var repeated = EditorLocator.Candidates(Version, @"D:\Unity\2022.3.62f2\Editor\Unity.exe", null, null, HostPlatform.Windows, Windows, Secondary);
		Check(repeated.Count == 2, "a candidate is listed once: " + string.Join(" | ", repeated));

		string? Mac(string name) => name == "HOME" ? "/Users/u" : null;
		var mac = EditorLocator.Candidates(Version, null, "/Opt/U/Unity.app", Version, HostPlatform.MacOs, Mac, _ => null);
		Check(mac.SequenceEqual(new[]
		{
			"/Opt/U/Unity.app/Contents/MacOS/Unity",
			"/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity"
		}), "macos bundles resolve to the binary inside: " + string.Join(" | ", mac));

		foreach (var broken in new[] { null, "", "{not json", "42" })
		{
			var list = EditorLocator.Candidates(Version, null, null, null, HostPlatform.Windows, Windows, _ => broken);
			Check(list.SequenceEqual(new[] { @"C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe" }),
				"an unreadable secondaryInstallPath.json leaves the Hub root: " + string.Join(" | ", list));
		}

		Check(EditorLocator.Executable("/A/Unity.app/") == "/A/Unity.app/Contents/MacOS/Unity", "a trailing slash on a bundle is tolerated");
		Check(EditorLocator.Executable(@"C:\U\Unity.exe") == @"C:\U\Unity.exe", "a plain executable is unchanged");
	}

	private static async Task RunOpenTests(string root)
	{
		var launches = new List<ProcessStartInfo>();
		var original = EditorCommands.Launch;
		EditorCommands.Launch = info => { launches.Add(info); return 4242; };
		try
		{
			var live = CreateProject(root, "editor-open-live", Version);
			WriteStatus(live, Environment.ProcessId);
			var result = await Run("editor", "open", "--project", live);
			Expect(result, 0, EditorCommands.AlreadyRunning);
			Check(result.Json.GetProperty("pid").GetInt32() == Environment.ProcessId, "already_running reports the PID from status");
			Check(launches.Count == 0, "a running editor is not launched again");

			var fresh = CreateProject(root, "editor-open-fresh", Version);
			var executable = Path.Combine(root, "FakeUnity.exe");
			File.WriteAllText(executable, "");
			result = await Run("editor", "open", "--project", fresh, "--editor-path", executable);
			Expect(result, 0, EditorCommands.Launched);
			Check(result.Json.GetProperty("pid").GetInt32() == 4242, "launched reports the started PID");
			Check(launches.Count == 1 && launches[0].FileName == executable
				&& launches[0].ArgumentList.SequenceEqual(new[] { "-projectPath", fresh }),
				"the explicit path is launched on the project root: " + string.Join(" ", launches.SelectMany(l => l.ArgumentList)));

			var locked = CreateProject(root, "editor-open-locked", Version);
			Directory.CreateDirectory(Path.Combine(locked, "Temp"));
			using (new FileStream(Path.Combine(locked, "Temp", "UnityLockfile"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
			{
				result = await Run("editor", "open", "--project", locked, "--editor-path", executable);
				Expect(result, 1, EditorCommands.ProjectLocked);
			}

			var missing = CreateProject(root, "editor-open-missing", "0.0.0agentbridge");
			result = await Run("editor", "open", "--project", missing);
			Expect(result, 1, EditorCommands.NotFound);
			Check(result.Json.GetProperty("detail").GetString()!.Contains("0.0.0agentbridge", StringComparison.Ordinal), "the detail names the version");
			Check(launches.Count == 1, "refusals launch nothing");
		}
		finally
		{
			EditorCommands.Launch = original;
		}
	}

	private static async Task RunCloseTests(string root)
	{
		var dead = CreateProject(root, "editor-close-dead", Version);
		WriteStatus(dead, 0x7FFFFFF0);
		Expect(await Run("editor", "close", "--reason", "test", "--project", dead), 0, EditorCommands.NotRunning);

		var busy = CreateProject(root, "editor-close-busy", Version);
		WriteStatus(busy, Environment.ProcessId, status => { status.ActiveTaskId = "Task_busy"; status.HolderAgentSessionId = "agent-a"; });
		var result = await Run("editor", "close", "--reason", "test", "--project", busy);
		Expect(result, 1, EditorCommands.TaskRunning);
		Check(result.Json.GetProperty("detail").GetString()!.Contains("Task_busy", StringComparison.Ordinal), "task_running names the task");

		var playing = CreateProject(root, "editor-close-playing", Version);
		WriteStatus(playing, Environment.ProcessId, status => status.IsPlaying = true);
		result = await Run("editor", "close", "--reason", "test", "--project", playing);
		Expect(result, 1, EditorCommands.Playing);
		Check(result.Json.GetProperty("detail").GetString() == "manual", "an ownerless play mode is manual");

		// The test process is not Unity: neither the close request nor the kill may reach it.
		var clean = CreateProject(root, "editor-close-clean", Version);
		WriteStatus(clean, Environment.ProcessId);
		Expect(await Run("editor", "close", "--reason", "test", "--project", clean), 1, EditorCommands.PidMismatch);
		Expect(await Run("editor", "close", "--reason", "test", "--force", "--project", clean), 1, EditorCommands.PidMismatch);

		var usage = await Run("editor", "close", "--project", clean);
		Expect(usage, 3, "bad_usage");
		Expect(await Run("editor", "close", "--reason", new string('r', EditorCommands.ReasonLimit + 1), "--project", clean), 3, "bad_usage");
		Expect(await Run("editor", "restart", "--project", clean), 3, "bad_usage");
	}

	private static void Expect((int Code, JsonElement Json, string Output) result, int exit, string code)
	{
		Check(result.Code == exit && result.Json.GetProperty("code").GetString() == code,
			$"expected {code}/{exit}: exit={result.Code}, stdout={result.Output}");
	}

	private static async Task<(int Code, JsonElement Json, string Output)> Run(params string[] args)
	{
		var previous = Console.Out;
		using var writer = new StringWriter();
		Console.SetOut(writer);
		int code;
		try
		{
			code = await AgentBridgeApplication.RunAsync(args);
		}
		finally
		{
			Console.SetOut(previous);
		}

		var output = writer.ToString();
		using var document = JsonDocument.Parse(output);
		return (code, document.RootElement.Clone(), output);
	}

	private static void WriteStatus(string project, int pid, Action<BridgeStatus>? change = null)
	{
		var paths = new BridgePaths(project);
		Directory.CreateDirectory(paths.WorkingRoot);
		var status = new BridgeStatus
		{
			ProtocolVersion = 1, ProjectPath = paths.ProjectRoot, EditorPid = pid, Enabled = true, RoslynReady = true,
			HostOs = HostPlatform.Current, UnityVersion = Version, Capabilities = new[] { QueueFiles.Capability }
		};
		change?.Invoke(status);
		File.WriteAllText(paths.StatusFile, JsonSerializer.Serialize(status));
		File.WriteAllText(paths.HeartbeatFile, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
	}

	private static string CreateProject(string root, string name, string version)
	{
		string project = Path.Combine(root, name);
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages", "com.elmortem.agentbridge"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), """{"dependencies":{}}""");
		File.WriteAllText(Path.Combine(project, "Packages", "com.elmortem.agentbridge", "package.json"), """{"name":"com.elmortem.agentbridge"}""");
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: " + version);
		return TestPaths.PhysicalDirectory(project);
	}

	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
