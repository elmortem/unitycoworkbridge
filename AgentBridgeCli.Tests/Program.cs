using System.Text.Json;
using AgentBridge.Cli;

var root = Path.Combine(Path.GetTempPath(), "AgentBridgeCliTests_" + Guid.NewGuid().ToString("N"));

try
{
	Directory.CreateDirectory(root);
	RunProjectDiscoveryTests(root);
	RunTaskIdTests();
	RunResultClassificationTests();
	RunHealthTests(root);
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
