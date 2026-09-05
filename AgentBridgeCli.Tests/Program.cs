using System.Text.Json;
using AgentBridge.Cli;

var root = Path.Combine(Path.GetTempPath(), "AgentBridgeCliTests_" + Guid.NewGuid().ToString("N"));

try
{
	Directory.CreateDirectory(root);
	RunProjectDiscoveryTests(root);
	RunTaskIdTests();
	RunResultClassificationTests();
	RunHumanResultFormattingTests();
	RunHealthTests(root);
	RunForeignHostTests(root);
	RunScratchTests(root);
	RunSessionOptionTests();
	RunContentionFormattingTests();
	RunWakePolicyTests();
	RunBackgroundTickTimerTests();
	await RunStaleSubmissionTests(root);
	RunManualPlayPolicyTests();
	RunTelemetryTests(root);
	Console.WriteLine("AgentBridgeCli.Tests: PASS");
	return 0;
}
finally
{
	if (Directory.Exists(root))
	{
		Directory.Delete(root, true);
	}
}

static void RunProjectDiscoveryTests(string temporaryRoot)
{
	var project = Path.Combine(temporaryRoot, "Проект With Space");
	CreateProject(project);
	var nested = Path.Combine(project, "Assets", "Nested");
	Directory.CreateDirectory(nested);

	Expect(ProjectLocator.Resolve(null, nested) == Path.GetFullPath(project), "nested cwd must resolve project root");
	Expect(ProjectLocator.Resolve(project, temporaryRoot) == Path.GetFullPath(project), "explicit project must resolve");
	Expect(ProjectLocator.Resolve(null, temporaryRoot) == null, "locator must not search downward");
	Expect(ProjectLocator.Resolve(Path.Combine(temporaryRoot, "Missing"), temporaryRoot) == null, "invalid explicit project must fail");
	Expect(ProjectLocator.Resolve("\0", temporaryRoot) == null, "malformed explicit project path must fail without crashing");
}

static void RunTaskIdTests()
{
	var ids = new HashSet<string>(StringComparer.Ordinal);
	for (var index = 0; index < 1000; index++)
	{
		Expect(ids.Add(TaskIdGenerator.NewId()), "generated task ids must be unique");
	}
}

static void RunResultClassificationTests()
{
	Expect(BridgeClient.ClassifyResult("""{"Kind":"csharp","Status":"success"}""") == 0, "successful csharp task must exit 0");
	Expect(BridgeClient.ClassifyResult("""{"Kind":"tests","Status":"success","Tests":{"failed":0,"inconclusive":0}}""") == 0, "green tests must exit 0");
	Expect(BridgeClient.ClassifyResult("""{"Kind":"tests","Status":"success","Tests":{"failed":1,"inconclusive":0}}""") == 1, "legacy red tests must exit 1");
	Expect(BridgeClient.ClassifyResult("""{"Kind":"tests","Status":"test_failure","Tests":{"failed":1}}""") == 1, "test_failure must exit 1");
}

static void RunHumanResultFormattingTests()
{
	var compile = TaskResultFormatter.FormatHuman(
		"""{"Id":"Task_compile","Kind":"compile","Status":"success","ForeignErrors":false,"Tests":{"passed":0,"failed":0,"skipped":0,"inconclusive":0,"total":0,"duration":0},"Timing":{"TotalMs":1234}}""");
	Expect(
		compile == "compile: success (Task_compile, foreign errors: no, 1.234s)",
		"human compile output must be a compact one-line summary");

	var tests = TaskResultFormatter.FormatHuman(
		"""{"Id":"Task_tests","Kind":"tests","Status":"success","Tests":{"passed":202,"failed":0,"skipped":0,"inconclusive":0,"total":202,"duration":4.1034231,"aborted":false,"message":"","failures":[]}}""");
	Expect(
		tests == "tests: success (Task_tests, 202 passed, 0 failed, 0 skipped, 0 inconclusive, 202 total, 4.103s)",
		"human test output must expose the complete result matrix without a JSON parser");

	var failedTests = TaskResultFormatter.FormatHuman(
		"""{"Id":"Task_failed","Kind":"tests","Status":"test_failure","Tests":{"passed":1,"failed":1,"skipped":0,"inconclusive":0,"total":2,"duration":0.5,"aborted":false,"message":"run failed","failures":[{"name":"MyTests.Fails","message":"Expected true","stacktrace":"at MyTests.Fails()"}]}}""");
	Expect(failedTests.Contains("tests: test_failure (Task_failed, 1 passed, 1 failed", StringComparison.Ordinal), "human failed-test output must keep the result matrix");
	Expect(failedTests.Contains("Message: run failed", StringComparison.Ordinal), "human failed-test output must include the runner message");
	Expect(failedTests.Contains("- MyTests.Fails: Expected true", StringComparison.Ordinal), "human failed-test output must include failure details");
	Expect(failedTests.Contains("  at MyTests.Fails()", StringComparison.Ordinal), "human failed-test output must include the stack trace");

	var csharp = TaskResultFormatter.FormatHuman(
		"""{"Id":"Task_script","Kind":"csharp","Status":"success","ReturnValue":"done","Logs":["changed 3 assets"],"Diagnostics":[{"Code":"CS0001","Severity":"warning","Message":"example","File":"Task.cs","Line":4,"Column":2}],"Artifacts":["Artifacts/Task_script/report.json"],"Timing":{"TotalMs":25}}""");
	Expect(csharp.Contains("csharp: success (Task_script, 0.025s)", StringComparison.Ordinal), "human csharp output must have a compact header");
	Expect(csharp.Contains("Result: done", StringComparison.Ordinal), "human csharp output must include the return value");
	Expect(csharp.Contains("- changed 3 assets", StringComparison.Ordinal), "human csharp output must include logs");
	Expect(csharp.Contains("- Task.cs(4,2): warning CS0001: example", StringComparison.Ordinal), "human csharp output must include diagnostics");
	Expect(csharp.Contains("- Artifacts/Task_script/report.json", StringComparison.Ordinal), "human csharp output must include artifacts");

	var running = TaskResultFormatter.FormatHuman("""{"Id":"Task_running","Status":"running"}""");
	Expect(running == "task: running (Task_running)", "human wait timeout output must remain actionable");

	var error = TaskResultFormatter.FormatHuman("""{"Ok":false,"Code":"payload_not_found","Message":"missing.cs"}""");
	Expect(error == "agentbridge: error (payload_not_found)" + Environment.NewLine + "Message: missing.cs", "human client errors must not fall back to JSON");

	var invalid = TaskResultFormatter.FormatHuman("not-json");
	Expect(invalid.StartsWith("agentbridge: invalid result", StringComparison.Ordinal), "malformed task output must not crash human formatting");
}

static void RunHealthTests(string temporaryRoot)
{
	var project = Path.Combine(temporaryRoot, "HealthProject");
	CreateProject(project);
	var bridgeRoot = Path.Combine(project, "Library", "AgentBridge");
	Directory.CreateDirectory(bridgeRoot);

	var status = new
	{
		ProtocolVersion = 1,
		PackageVersion = "0.7.0",
		ProjectPath = project,
		UnityVersion = "2022.3.62f2",
		EditorPid = Environment.ProcessId,
		SessionId = "test",
		Enabled = true,
		RoslynSource = "Local",
		RoslynReady = true,
		Capabilities = new[] { "csharp", "ui", "compile", "tests" }
	};
	File.WriteAllText(Path.Combine(bridgeRoot, "status.json"), JsonSerializer.Serialize(status));
	File.WriteAllText(
		Path.Combine(bridgeRoot, "heartbeat"),
		DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());

	var health = BridgeInspector.Inspect(project);
	Expect(health.BridgeReady, "fresh compatible status must be ready");
	Expect(health.CSharpReady, "Roslyn-ready bridge must be csharp-ready");
	Expect(!health.Warnings.Contains("editor_playing_manual"), "an idle editor must not warn about manual play");

	var manualPlay = new
	{
		status.ProtocolVersion,
		status.PackageVersion,
		status.ProjectPath,
		status.UnityVersion,
		status.EditorPid,
		status.SessionId,
		status.Enabled,
		status.RoslynSource,
		status.RoslynReady,
		status.Capabilities,
		IsPlaying = true,
		PlaySessionAgentId = ""
	};
	File.WriteAllText(Path.Combine(bridgeRoot, "status.json"), JsonSerializer.Serialize(manualPlay));
	health = BridgeInspector.Inspect(project);
	Expect(health.BridgeReady, "manual play must not make the bridge unavailable");
	Expect(health.Warnings.Contains("editor_playing_manual"), "an unowned play mode must be warned about");

	var ownedPlay = new
	{
		status.ProtocolVersion,
		status.PackageVersion,
		status.ProjectPath,
		status.UnityVersion,
		status.EditorPid,
		status.SessionId,
		status.Enabled,
		status.RoslynSource,
		status.RoslynReady,
		status.Capabilities,
		IsPlaying = true,
		PlaySessionAgentId = "agent-a"
	};
	File.WriteAllText(Path.Combine(bridgeRoot, "status.json"), JsonSerializer.Serialize(ownedPlay));
	health = BridgeInspector.Inspect(project);
	Expect(!health.Warnings.Contains("editor_playing_manual"), "an owned play session must not be reported as manual");

	File.WriteAllText(Path.Combine(bridgeRoot, "status.json"), JsonSerializer.Serialize(status));
	File.WriteAllText(
		Path.Combine(bridgeRoot, "heartbeat"),
		(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60000).ToString());
	health = BridgeInspector.Inspect(project);
	Expect(!health.BridgeReady && health.Code == "heartbeat_stale", "stale heartbeat must fail closed");
}

static void RunForeignHostTests(string temporaryRoot)
{
	var project = Path.Combine(temporaryRoot, "ForeignHostProject");
	CreateProject(project);
	var bridgeRoot = Path.Combine(project, "Library", "AgentBridge");
	Directory.CreateDirectory(bridgeRoot);

	var projectId = Guid.NewGuid().ToString("N");
	var foreignOs = HostPlatform.Current == HostPlatform.Windows ? HostPlatform.Linux : HostPlatform.Windows;
	File.WriteAllText(Path.Combine(bridgeRoot, "project-id"), projectId);
	WriteHeartbeat(bridgeRoot, 0);
	WriteStatus(bridgeRoot, projectId, foreignOs, "D:\\Somewhere\\Else", 0x7FFFFFFF);

	var health = BridgeInspector.Inspect(project);
	Expect(health.ForeignHost, "different host os must be detected as foreign");
	Expect(health.ProjectMatchedBy == "project_id", "project id must win over the path comparison");
	Expect(health.ProjectMatches, "matching project id must satisfy the identity check");
	Expect(health.EditorProcessAlive == null, "foreign host must not judge the editor process");
	Expect(health.BridgeReady, "foreign host with a fresh heartbeat must be ready");
	Expect(health.HeartbeatToleranceMs == 60000, "foreign host must use the widened heartbeat tolerance");

	WriteStatus(bridgeRoot, Guid.NewGuid().ToString("N"), foreignOs, project, Environment.ProcessId);
	health = BridgeInspector.Inspect(project);
	Expect(!health.BridgeReady && health.Code == "project_mismatch", "foreign status from another project must fail closed");

	WriteHeartbeat(bridgeRoot, 120000);
	WriteStatus(bridgeRoot, projectId, foreignOs, project, 0x7FFFFFFF);
	health = BridgeInspector.Inspect(project);
	Expect(!health.BridgeReady && health.Code == "heartbeat_stale", "foreign host must still fail on a dead heartbeat");

	WriteHeartbeat(bridgeRoot, 0);
	WriteStatus(bridgeRoot, projectId, HostPlatform.Current, project, Environment.ProcessId);
	health = BridgeInspector.Inspect(project);
	Expect(!health.ForeignHost, "same host os must not be treated as foreign");
	Expect(health.EditorProcessAlive == true, "same host must still verify the editor process");
	Expect(health.HeartbeatToleranceMs == 15000, "same host must keep the strict heartbeat tolerance");
}

static void WriteHeartbeat(string bridgeRoot, long ageMs)
{
	File.WriteAllText(
		Path.Combine(bridgeRoot, "heartbeat"),
		(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ageMs).ToString());
}

static void WriteStatus(string bridgeRoot, string projectId, string hostOs, string projectPath, int editorPid)
{
	var status = new
	{
		ProtocolVersion = 1,
		PackageVersion = "0.9.0",
		ProjectPath = projectPath,
		ProjectId = projectId,
		HostOs = hostOs,
		UnityVersion = "2022.3.62f2",
		EditorPid = editorPid,
		SessionId = "test",
		Enabled = true,
		RoslynSource = "Vendored",
		RoslynReady = true,
		Capabilities = new[] { "csharp", "ui", "compile", "tests" }
	};
	File.WriteAllText(Path.Combine(bridgeRoot, "status.json"), JsonSerializer.Serialize(status));
}

static void RunScratchTests(string temporaryRoot)
{
	var project = Path.Combine(temporaryRoot, "ScratchProject");
	CreateProject(project);
	var paths = new BridgePaths(project);

	Expect(paths.Scratch == Path.Combine(project, "Temp", "AgentBridge"), "scratch must live in the Unity Temp folder");

	paths.EnsureScratch();
	Expect(Directory.Exists(paths.Scratch), "scratch directory must be created");

	Expect(paths.IsInsideAssets(Path.Combine(project, "Assets", "Editor", "Task_1.cs")), "payload under Assets must be detected");
	Expect(paths.IsInsideAssets(Path.Combine(project, "Assets", "Task_1.cs")), "payload directly in Assets must be detected");
	Expect(!paths.IsInsideAssets(Path.Combine(paths.Scratch, "Task_1.cs")), "scratch payload must not be flagged");
	Expect(!paths.IsInsideAssets(Path.Combine(project, "AssetsExtra", "Task_1.cs")), "sibling folder must not be flagged");
	Expect(!paths.IsInsideAssets(Path.Combine(Path.GetTempPath(), "Task_1.cs")), "payload outside the project must not be flagged");
	Expect(!paths.IsInsideAssets("\0"), "malformed payload path must not crash the check");
}

static void RunSessionOptionTests()
{
	var parsed = CliOptions.Parse(new[] { "csharp", "Task.cs", "--session", "AB_20260813_1500_a1", "--note", "verifying the loot table" });
	Expect(parsed.Error == null, "valid session and note must parse");
	Expect(parsed.Session == "AB_20260813_1500_a1", "session value must be kept verbatim");
	Expect(parsed.Note == "verifying the loot table", "note value must be kept verbatim");
	Expect(parsed.Arguments.Count == 2, "session and note must not leak into positional arguments");

	var inline = CliOptions.Parse(new[] { "release", "--session=AB-1", "--note=short" });
	Expect(inline.Session == "AB-1" && inline.Note == "short", "inline --session=/--note= form must parse");

	Expect(CliOptions.Parse(new[] { "csharp", "--session", "bad id" }).Error != null, "session with a space must be rejected");
	Expect(CliOptions.Parse(new[] { "csharp", "--session", new string('a', 65) }).Error != null, "session over 64 characters must be rejected");
	Expect(CliOptions.Parse(new[] { "csharp", "--session", new string('a', 64) }).Error == null, "session of exactly 64 characters must be accepted");
	Expect(CliOptions.Parse(new[] { "csharp", "--note", new string('n', 201) }).Error != null, "note over 200 characters must be rejected");
	Expect(CliOptions.Parse(new[] { "csharp", "--session" }).Error != null, "session without a value must be rejected");

	var none = CliOptions.Parse(new[] { "csharp", "Task.cs" });
	Expect(none.Session == null && none.Note == null, "omitted session and note must stay unset");
}

static void RunContentionFormattingTests()
{
	var contended = TaskResultFormatter.FormatHuman(
		"""{"Id":"Task_a","Kind":"csharp","Status":"success","Contention":{"WaitingSessions":2,"OldestWaitSeconds":47,"Notes":["porting the shop screen"]}}""");
	Expect(contended.Contains("Contention: 2 waiting, oldest 47s", StringComparison.Ordinal), "human output must report waiting sessions");
	Expect(contended.Contains("- porting the shop screen", StringComparison.Ordinal), "human output must include contention notes");

	var quiet = TaskResultFormatter.FormatHuman(
		"""{"Id":"Task_b","Kind":"csharp","Status":"success","Contention":{"WaitingSessions":0,"OldestWaitSeconds":0,"Notes":[]}}""");
	Expect(!quiet.Contains("Contention", StringComparison.Ordinal), "an uncontended task must not mention contention");
}

static void RunWakePolicyTests()
{
	Expect(WakePolicy.Decide(null, false, 0, 0, 100d) == WakeAction.None, "unknown heartbeat age must not poke");
	Expect(WakePolicy.Decide(1000, false, 0, 0, 100d) == WakeAction.None, "fresh heartbeat must not poke");
	Expect(WakePolicy.Decide(9000, true, 0, 0, 100d) == WakeAction.Post, "focused stalled editor must still receive non-focusing wake signals");
	Expect(WakePolicy.Decide(9000, false, 0, 0, 1d) == WakeAction.None, "attempts must respect the interval");
	Expect(WakePolicy.Decide(9000, false, 0, 0, 100d) == WakeAction.Post, "stale heartbeat must post first");
	Expect(
		WakePolicy.Decide(9000, false, WakePolicy.MaxPostAttempts, 0, 100d) == WakeAction.Focus,
		"focus poke only after posts are exhausted");
	Expect(
		WakePolicy.Decide(9000, false, WakePolicy.MaxPostAttempts, WakePolicy.MaxFocusAttempts, 100d) == WakeAction.None,
		"exhausted attempts must stop poking");
	Expect(WakePolicy.Decide(9000, true, WakePolicy.MaxPostAttempts, 0, 100d) == WakeAction.None,
		"foreground editor must not receive a focus poke");

	var health = new BridgeHealth
	{
		EditorProcessAlive = true, PackageDeclared = true, ProjectMatches = true,
		ProtocolCompatible = true, HeartbeatAgeMs = 90000,
		Bridge = new BridgeStatus { Enabled = true }, Problems = new() { "heartbeat_stale" }
	};
	Expect(WakePolicy.CanRecover(health), "stale-only local editor must reach recovery");
	foreach (var problem in new[] { "protocol_mismatch", "project_mismatch", "bridge_disabled", "editor_process_not_running", "heartbeat_invalid" })
	{
		health.Problems.Add(problem);
		Expect(!WakePolicy.CanRecover(health), "staleness must not mask " + problem);
		health.Problems.Remove(problem);
	}
	health.ForeignHost = true;
	Expect(!WakePolicy.CanRecover(health), "never wake a foreign host PID");
	health.ForeignHost = false;
	health.EditorProcessAlive = false;
	Expect(!WakePolicy.CanRecover(health), "never wake a dead editor");

	var attempts = new EditorWakeAttempts();
	var now = DateTime.UtcNow;
	attempts.Observe(16000, now);
	attempts.PostAttempts = WakePolicy.MaxPostAttempts;
	attempts.FocusAttempts = WakePolicy.MaxFocusAttempts;
	Expect(!attempts.TimedOut(now.AddSeconds(30)), "exhausted pokes do not prove a busy editor is dead");
	Expect(attempts.TimedOut(now.AddSeconds(120)), "a stalled foreground editor must also have a bounded wait");
	attempts.Observe(19000, now.AddSeconds(3));
	Expect(attempts.PostAttempts == WakePolicy.MaxPostAttempts, "unchanged heartbeat must not refill wake budget");
	attempts.Observe(1000, now.AddSeconds(6));
	Expect(attempts.PostAttempts == 0 && attempts.StalledSinceUtc == null, "real heartbeat progress resets recovery");
}

static void RunBackgroundTickTimerTests()
{
	using var entered = new ManualResetEventSlim();
	using var release = new ManualResetEventSlim();
	int callerThread = Environment.CurrentManagedThreadId;
	int signalThread = callerThread;
	int signals = 0;
	var timer = new AgentBridge.BackgroundTickTimer(() =>
	{
		signalThread = Environment.CurrentManagedThreadId;
		Interlocked.Increment(ref signals);
		entered.Set();
		release.Wait();
	}, 15);
	try
	{
		Expect(entered.Wait(3000), "timer must signal without a main-thread update or message pump");
		Expect(signalThread != callerThread, "signal must originate independently of the caller thread");
		var dispose = Task.Run(timer.Dispose);
		Expect(!dispose.Wait(50), "shutdown must drain an in-flight signal before returning");
		release.Set();
		Expect(dispose.Wait(3000), "shutdown must finish when the bounded signal finishes");
		int stoppedCount = signals;
		Thread.Sleep(70);
		Expect(signals == stoppedCount, "queued callbacks must not signal after disposal");
		timer.Dispose();
	}
	finally
	{
		release.Set();
		timer.Dispose();
	}

	using var failed = new AgentBridge.BackgroundTickTimer(() => throw new InvalidOperationException("wake test"), 15);
	Expect(SpinWait.SpinUntil(() => failed.Error != null, 3000), "callback failure must be captured instead of escaping ThreadPool");
	Expect(failed.SignalCount == 0 && failed.Error!.Contains("wake test"), "failed signal must not be reported as successful");
}

static async Task RunStaleSubmissionTests(string temporaryRoot)
{
	var project = Path.Combine(temporaryRoot, "StaleSubmission");
	CreateProject(project);
	var paths = new BridgePaths(project);
	Directory.CreateDirectory(paths.WorkingRoot);
	File.WriteAllText(paths.StatusFile, JsonSerializer.Serialize(new BridgeStatus
	{
		ProtocolVersion = 1, ProjectPath = project, EditorPid = Environment.ProcessId,
		Enabled = true, RoslynReady = true, HostOs = HostPlatform.Current
	}));
	WriteHeartbeat(paths.WorkingRoot, 90000);
	var originalOutput = Console.Out;
	var originalError = Console.Error;
	using var output = new StringWriter();
	using var errors = new StringWriter();
	Console.SetOut(output);
	Console.SetError(errors);
	try
	{
		Expect(await AgentBridgeApplication.RunAsync(new[] { "status", "--project", project }) == 3,
			"read-only status must continue to report stale heartbeat");
		Expect(!Directory.Exists(paths.Inbox), "status must not enqueue a recovery task");
		output.GetStringBuilder().Clear();
		var command = AgentBridgeApplication.RunAsync(new[] { "compile", "--project", project, "--wait", "5" });
		Expect(!command.IsCompleted, "stale heartbeat before submission must enter the watchdog instead of failing preflight");
		var requests = Directory.GetFiles(paths.Inbox, "*.task.json");
		Expect(requests.Length == 1, "stale submission must enqueue exactly one task");
		var id = Path.GetFileName(requests[0]).Replace(".task.json", "");
		Directory.CreateDirectory(paths.Journal);
		WriteHeartbeat(paths.WorkingRoot, 0);
		File.WriteAllText(Path.Combine(paths.Journal, id + ".json"),
			JsonSerializer.Serialize(new { Id = id, Kind = "compile", Status = "success" }));
		Expect(await command.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "recovered submission must return the original task result");
		Expect(output.ToString().Contains(id), "returned result must identify the admitted task");
	}
	finally
	{
		Console.SetOut(originalOutput);
		Console.SetError(originalError);
	}
}

static void RunManualPlayPolicyTests()
{
	static BridgeHealth Health(bool ready, bool playing, string? owner)
	{
		return new BridgeHealth
		{
			BridgeReady = ready,
			Bridge = new BridgeStatus { IsPlaying = playing, PlaySessionAgentId = owner }
		};
	}

	Expect(ManualPlayPolicy.ShouldStop(Health(true, true, null), "csharp", 0), "manual play must be stopped for a queued task");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, true, "agent-a"), "csharp", 0), "an owned play session must not be touched");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, true, null), "stopplay", 0), "stopplay must not stop play for itself");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, true, null), "csharp", ManualPlayPolicy.MaxStops), "exhausted attempts must fall back to waiting");
	Expect(!ManualPlayPolicy.ShouldStop(Health(false, true, null), "csharp", 0), "an unready bridge must not receive stopplay");
	Expect(!ManualPlayPolicy.ShouldStop(null, "csharp", 0), "missing health must not trigger a stop");
	Expect(!ManualPlayPolicy.ShouldStop(Health(true, false, null), "csharp", 0), "an idle editor must not be stopped");
}

static void RunTelemetryTests(string temporaryRoot)
{
	var project = Path.Combine(temporaryRoot, "TelemetryProject");
	CreateProject(project);

	var disabled = new TelemetryLog(project, false);
	disabled.Write("cli_submit", "s1", "t1", new Dictionary<string, object?> { ["Cmd"] = "csharp" });
	Expect(!Directory.Exists(Path.Combine(project, "Logs")), "disabled telemetry must not create the folder");

	var enabled = new TelemetryLog(project, true);
	enabled.Write("cli_submit", "s1", "t1", new Dictionary<string, object?> { ["Cmd"] = "csharp", ["Note"] = "тест \"кавычки\"" });
	enabled.Write("cli_exit", "s1", "t1", new Dictionary<string, object?> { ["Code"] = 0 });

	var file = Directory.GetFiles(Path.Combine(project, "Logs"), "AgentBridge-client-*.jsonl").Single();
	var lines = File.ReadAllLines(file);
	Expect(lines.Length == 2, "each event must be one line");

	using var document = JsonDocument.Parse(lines[0]);
	Expect(document.RootElement.GetProperty("E").GetString() == "cli_submit", "event name must round-trip");
	Expect(document.RootElement.GetProperty("W").GetString() == "client", "writer must be marked");
	Expect(document.RootElement.GetProperty("Note").GetString()!.Contains('"'), "quotes must survive escaping");

	var truncating = new TelemetryLog(project, true);
	truncating.Write("cli_submit", "s2", "t2", new Dictionary<string, object?> { ["Note"] = new string('x', 500) });
	using var longNote = JsonDocument.Parse(File.ReadAllLines(file)[2]);
	Expect(longNote.RootElement.GetProperty("Note").GetString()!.Length == 200, "long text must be truncated");
}

static void CreateProject(string path)
{
	Directory.CreateDirectory(Path.Combine(path, "Assets"));
	Directory.CreateDirectory(Path.Combine(path, "Packages", "com.elmortem.agentbridge"));
	Directory.CreateDirectory(Path.Combine(path, "ProjectSettings"));
	File.WriteAllText(Path.Combine(path, "Packages", "manifest.json"), """{"dependencies":{}}""");
	File.WriteAllText(Path.Combine(path, "Packages", "com.elmortem.agentbridge", "package.json"), """{"name":"com.elmortem.agentbridge"}""");
	File.WriteAllText(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
}

static void Expect(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}
