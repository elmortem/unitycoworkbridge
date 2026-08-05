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
