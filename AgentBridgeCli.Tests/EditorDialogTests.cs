using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Queueing;

// dialog-detection-v1: a modal dialog in Unity is reported as blocked_by_dialog and ends a wait early.
internal static class EditorDialogTests
{
	internal static async Task RunAsync(string root)
	{
		RunSelectionTests();
		string project = CreateProject(root, "dialog-project");
		var paths = new BridgePaths(project);
		WriteStatus(paths, Environment.ProcessId);
		WriteHeartbeat(paths, 0);

		var health = BridgeInspector.Inspect(project);
		Check(health.BridgeReady && health.BlockedBy.Length == 0, "an editor without dialogs.json must stay ready");

		WriteDialogs(paths, Environment.ProcessId, 0, 4000, "Scene(s) Have Been Modified");
		health = BridgeInspector.Inspect(project);
		Check(!health.BridgeReady && health.Code == EditorDialogReader.Code, "a fresh dialog must make the editor unavailable: " + health.Code);
		Check(health.BlockedBy.Length == 1 && health.BlockedBy[0].Title == "Scene(s) Have Been Modified", "the dialog must be named");
		Check(health.BlockedBy[0].Buttons.Length == 2 && health.BlockedBy[0].Level == "warning", "level and buttons must survive the round trip");

		WriteHeartbeat(paths, 60000);
		health = BridgeInspector.Inspect(project);
		Check(health.Code == EditorDialogReader.Code && health.Problems.Contains("heartbeat_stale"),
			"the dialog must outrank the stale heartbeat it causes: " + health.Code);
		Check(!WakePolicy.CanRecover(health), "waking cannot answer a dialog, so recovery must not poke the editor");
		WriteHeartbeat(paths, 0);

		WriteDialogs(paths, Environment.ProcessId, 30000, 4000, "Old");
		health = BridgeInspector.Inspect(project);
		Check(health.BridgeReady && health.BlockedBy.Length == 0, "a snapshot that stopped refreshing must be ignored");

		WriteDialogs(paths, Environment.ProcessId + 1, 0, 4000, "Other editor");
		health = BridgeInspector.Inspect(project);
		Check(health.BridgeReady, "a snapshot of another editor process must be ignored");

		File.WriteAllText(paths.DialogsFile, "{\"EditorPid\":" + Environment.ProcessId + ",\"UpdatedUtcMs\":" + Now() + ",\"MainThreadIdleMs\":9000,\"Dialogs\":[]}");
		health = BridgeInspector.Inspect(project);
		Check(health.BridgeReady, "an empty snapshot means nothing blocks");

		File.WriteAllText(paths.DialogsFile, "{not json");
		health = BridgeInspector.Inspect(project);
		Check(health.BridgeReady, "a torn snapshot must not break status");

		RunFormattingTests();
		await RunWaitTests(project, paths);
		Console.WriteLine("Editor dialogs: PASS (fresh/stale/foreign snapshots, priority over heartbeat, early wait exit, human output)");
	}

	private static void RunSelectionTests()
	{
		long now = Now();
		var snapshot = new EditorDialogReader.Snapshot
		{
			EditorPid = 42,
			UpdatedUtcMs = now - 1000,
			MainThreadIdleMs = 3000,
			Dialogs = new[] { new EditorDialogInfo { Id = "win32:1", Title = "Save Scene", OpenMs = 1500 } }
		};
		var selected = EditorDialogReader.Select(snapshot, 42, false, now);
		Check(selected.Length == 1 && selected[0].OpenMs == 2500, "open time must include the snapshot age");
		snapshot.Dialogs[0].OpenMs = 1500;
		snapshot.MainThreadIdleMs = 200;
		Check(EditorDialogReader.Select(snapshot, 42, false, now).Length == 0, "a dialog over a ticking main thread blocks nothing");
		snapshot.MainThreadIdleMs = 3000;
		Check(EditorDialogReader.Select(snapshot, 43, false, now).Length == 0, "pid mismatch must be ignored");
		Check(EditorDialogReader.Select(snapshot, 0, false, now).Length == 0, "an unknown editor pid must be ignored");
		snapshot.UpdatedUtcMs = now - 30000;
		Check(EditorDialogReader.Select(snapshot, 42, false, now).Length == 0, "local snapshots age out after 5 s");
		Check(EditorDialogReader.Select(snapshot, 42, true, now).Length == 1, "a foreign client keeps the heartbeat allowance");
		snapshot.UpdatedUtcMs = now + 90000;
		Check(EditorDialogReader.Select(snapshot, 42, true, now).Length == 0, "a snapshot far in the future is not believed");
		Check(EditorDialogReader.Select(null, 42, false, now).Length == 0, "missing snapshot is no dialog");
	}

	private static void RunFormattingTests()
	{
		var dialog = new EditorDialogInfo
		{
			Kind = "native_message_box", Level = "warning", Title = "Scene(s) Have Been Modified", OpenMs = 12400,
			Message = "Do you want to save\nthe changes?", Buttons = new[] { "Save", "Don't Save" }, TaskId = "Task_1"
		};
		var line = EditorDialogReader.Describe(dialog);
		Check(line == "[warning] \"Scene(s) Have Been Modified\" (native_message_box, 12s, task Task_1) — Do you want to save the changes? [Save | Don't Save]",
			"describe must give one readable line: " + line);

		var human = TaskResultFormatter.FormatHuman(JsonSerializer.Serialize(new
		{
			Id = "Task_x", Kind = "compile", Status = "queued", Accepted = true, EditorState = EditorDialogReader.Code,
			Reason = EditorDialogReader.WaitReason, BlockedBy = new[] { dialog }, Resume = "agentbridge wait Task_x"
		}, JsonSupport.Task));
		Check(human.Contains("Editor: blocked_by_dialog", StringComparison.Ordinal)
			&& human.Contains("Blocked by:" + Environment.NewLine + "- [warning] \"Scene(s) Have Been Modified\"", StringComparison.Ordinal),
			"human wait output must name the dialog: " + human);
		var quiet = TaskResultFormatter.FormatHuman("""{"Id":"Task_y","Status":"queued","BlockedBy":[]}""");
		Check(!quiet.Contains("Blocked by", StringComparison.Ordinal), "an empty BlockedBy must stay silent");
	}

	private static async Task RunWaitTests(string project, BridgePaths paths)
	{
		var stdout = Console.Out;
		var stderr = Console.Error;
		using var output = new StringWriter();
		using var errors = new StringWriter();
		Console.SetOut(output);
		Console.SetError(errors);
		try
		{
			WriteHeartbeat(paths, 0);
			WriteDialogs(paths, Environment.ProcessId, 0, 5000, "Save Scene As");
			var elapsed = Stopwatch.StartNew();
			int code = await AgentBridgeApplication.RunAsync(new[] { "compile", "--project", project, "--wait", "60" }).WaitAsync(TimeSpan.FromSeconds(20));
			Check(code == 2 && elapsed.Elapsed.TotalSeconds < 15,
				$"an open dialog must end the wait early with code 2: exit={code}, elapsed={elapsed.Elapsed}, stdout={output}, stderr={errors}");
			string id;
			using (var result = JsonDocument.Parse(output.ToString()))
			{
				var rootElement = result.RootElement;
				id = rootElement.GetProperty("Id").GetString()!;
				Check(rootElement.GetProperty("Accepted").GetBoolean(), "the task stays accepted");
				Check(rootElement.GetProperty("EditorState").GetString() == EditorDialogReader.Code, "EditorState names the dialog block");
				Check(rootElement.GetProperty("Reason").GetString() == EditorDialogReader.WaitReason, "Reason explains the early exit");
				Check(rootElement.GetProperty("BlockedBy")[0].GetProperty("Title").GetString() == "Save Scene As", "BlockedBy carries the dialog");
				Check(rootElement.GetProperty("Resume").GetString() == "agentbridge wait " + id, "resume command keeps the same task");
			}

			Check(File.Exists(Path.Combine(paths.Inbox, id + ".task.json")), "the early exit must keep the queued task");
			Check(errors.ToString().Contains("modal dialog", StringComparison.Ordinal), "stderr must explain the early exit: " + errors);

			output.GetStringBuilder().Clear();
			WriteDialogs(paths, Environment.ProcessId, 0, 5000, "Save Scene As");
			code = await AgentBridgeApplication.RunAsync(new[] { "wait", id, "--project", project, "--wait", "0" });
			using (var accepted = JsonDocument.Parse(output.ToString()))
			{
				Check(code == 0 && accepted.RootElement.GetProperty("EditorState").GetString() == EditorDialogReader.Code
					&& accepted.RootElement.GetProperty("BlockedBy").GetArrayLength() == 1,
					"--wait 0 still only confirms acceptance but reports the dialog: " + output);
			}

			output.GetStringBuilder().Clear();
			WriteDialogs(paths, Environment.ProcessId, 0, 5000, "Save Scene As");
			code = await AgentBridgeApplication.RunAsync(new[] { "status", "--project", project, "--format", "human" });
			Check(output.ToString().Contains("Blocked by dialog: [warning] \"Save Scene As\"", StringComparison.Ordinal),
				"human status must name the dialog: " + output);

			output.GetStringBuilder().Clear();
			WriteDialogs(paths, Environment.ProcessId, 0, 5000, "Save Scene As");
			code = await AgentBridgeApplication.RunAsync(new[] { "status", "--project", project });
			using (var status = JsonDocument.Parse(output.ToString()))
			{
				Check(status.RootElement.GetProperty("code").GetString() == EditorDialogReader.Code
					&& status.RootElement.GetProperty("blockedBy")[0].GetProperty("title").GetString() == "Save Scene As",
					"json status must expose code and blockedBy: " + output);
			}

			output.GetStringBuilder().Clear();
			WriteDialogs(paths, Environment.ProcessId, 0, 0, "Closing soon");
			elapsed.Restart();
			code = await AgentBridgeApplication.RunAsync(new[] { "wait", id, "--project", project, "--wait", "1" });
			Check(code == 2, "a dialog younger than the grace period does not end the wait early: " + output);
			using (var young = JsonDocument.Parse(output.ToString()))
			{
				Check(young.RootElement.GetProperty("Reason").GetString() == "awaiting_executor", "budget expiry keeps its own reason: " + output);
			}

			await new QueueClient(project).CallAsync(new QueueMessage { Op = "cancel", TaskId = id, Session = "" });
		}
		finally
		{
			Console.SetOut(stdout);
			Console.SetError(stderr);
		}
	}

	private static void WriteDialogs(BridgePaths paths, int pid, long ageMs, long openMs, string title)
	{
		var snapshot = new
		{
			EditorPid = pid,
			UpdatedUtcMs = Now() - ageMs,
			MainThreadIdleMs = 5000,
			Dialogs = new[]
			{
				new
				{
					Id = "unity:1", Source = "editor_dialog_events", Kind = "native_message_box", Level = "warning", Title = title,
					Message = "Do you want to save the changes?", Buttons = new[] { "Save", "Don't Save" },
					OpenedAtUtc = DateTime.UtcNow.ToString("o"), OpenMs = openMs, TaskId = ""
				}
			}
		};
		File.WriteAllText(paths.DialogsFile, JsonSerializer.Serialize(snapshot));
	}

	private static void WriteStatus(BridgePaths paths, int pid)
	{
		Directory.CreateDirectory(paths.WorkingRoot);
		File.WriteAllText(paths.StatusFile, JsonSerializer.Serialize(new BridgeStatus
		{
			ProtocolVersion = 1, ProjectPath = paths.ProjectRoot, EditorPid = pid, Enabled = true, RoslynReady = true,
			HostOs = HostPlatform.Current, DialogDetection = "win32",
			Capabilities = new[] { QueueFiles.Capability, "dialog-detection-v1" }
		}));
	}

	private static void WriteHeartbeat(BridgePaths paths, long ageMs)
	{
		File.WriteAllText(paths.HeartbeatFile, (Now() - ageMs).ToString());
	}

	private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	private static string CreateProject(string root, string name)
	{
		string project = Path.Combine(root, name);
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages", "com.elmortem.agentbridge"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), """{"dependencies":{}}""");
		File.WriteAllText(Path.Combine(project, "Packages", "com.elmortem.agentbridge", "package.json"), """{"name":"com.elmortem.agentbridge"}""");
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		return TestPaths.PhysicalDirectory(project);
	}

	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
