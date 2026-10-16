using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

internal static class DispatcherHostTests
{
	private const int ForeignPid = 0x7FFFFFFF;
	private static readonly string Foreign = HostPlatform.Current == HostPlatform.Windows ? HostPlatform.Linux : HostPlatform.Windows;
	private static readonly string[] Full = { QueueFiles.Capability, QueueFiles.HostCapability };
	private static readonly string[] Legacy = { QueueFiles.Capability };

	internal static async Task RunAsync(string root)
	{
		string project = CreateProject(root, "dispatcher-policy");
		WriteStatus(project, Foreign, Full, 0, ForeignPid);
		var health = BridgeInspector.Inspect(project);
		Check(DispatcherHostPolicy.HostOwnsDispatcher(health) && health.DispatcherOwner == "editor_host", "policy_owner: live foreign editor with capability owns the dispatcher");
		WriteStatus(project, Foreign, Legacy, 0, ForeignPid);
		Check(!DispatcherHostPolicy.HostOwnsDispatcher(BridgeInspector.Inspect(project)), "policy_owner: legacy package keeps any-client launch");
		WriteStatus(project, Foreign, Full, 90000, ForeignPid);
		Check(!DispatcherHostPolicy.HostOwnsDispatcher(BridgeInspector.Inspect(project)), "policy_owner: dead foreign editor does not own the dispatcher");
		WriteStatus(project, HostPlatform.Current, Full, 0, Environment.ProcessId);
		health = BridgeInspector.Inspect(project);
		Check(!DispatcherHostPolicy.HostOwnsDispatcher(health) && health.DispatcherOwner == "any_client", "policy_owner: same-host client may start the dispatcher");

		Check(!DispatcherHostPolicy.ShouldYield(false, false), "policy_yield: host ignores a launcher it cannot run");
		Check(DispatcherHostPolicy.ShouldYield(true, false) && DispatcherHostPolicy.ShouldYield(false, true), "policy_yield: runnable launcher or foreign server yields");

		Check(!DispatcherHostPolicy.MayIdleExit(130000, BridgeInspector.Inspect(project)), "policy_idle: host server stays while the editor lives");
		WriteStatus(project, HostPlatform.Current, Full, 90000, Environment.ProcessId);
		health = BridgeInspector.Inspect(project);
		Check(DispatcherHostPolicy.MayIdleExit(130000, health), "policy_idle: dead editor allows idle exit");
		Check(!DispatcherHostPolicy.MayIdleExit(100000, health), "policy_idle: idle exit waits for the timeout");
		Check(DispatcherHostPolicy.MayIdleExit(130000, null), "policy_idle: uninspected server may idle exit");

		string deferred = CreateProject(root, "dispatcher-deferred");
		WriteStatus(deferred, Foreign, Full, 0, ForeignPid);
		var reply = await new QueueClient(deferred) { AcknowledgeTimeoutMs = 1500 }.CallAsync(new QueueMessage { Op = "list" });
		Check(reply.Code == "dispatcher_unavailable" && reply.Message.Contains("owns the dispatcher"), "foreign_client_defers: " + reply.Code + " " + reply.Message);
		Check(!File.Exists(Path.Combine(QueueFiles.Root(deferred), "launch.json")), "foreign_client_defers: no launch.json");
		Check(!File.Exists(Path.Combine(QueueFiles.Root(deferred), "heartbeat")), "foreign_client_defers: no server started");
		Check(Events(deferred, "queue_unacknowledged").Any(e => e.GetProperty("Reason").GetString() == "host_dispatcher_missing" && e.GetProperty("Foreign").GetBoolean()),
			"foreign_client_defers: queue_unacknowledged with Reason and Foreign");

		string legacy = CreateProject(root, "dispatcher-legacy");
		WriteStatus(legacy, Foreign, Legacy, 0, ForeignPid);
		var legacyClient = new QueueClient(legacy);
		try
		{
			reply = await legacyClient.CallAsync(new QueueMessage { Op = "list" });
			Check(reply.Ok, "foreign_client_launches_without_capability: " + reply.Code);
			Check(File.Exists(Path.Combine(QueueFiles.Root(legacy), "launch.json")), "foreign_client_launches_without_capability: launch.json published");
		}
		finally { await Stop(legacyClient, legacy); }

		string handover = CreateProject(root, "dispatcher-handover");
		WriteStatus(handover, HostPlatform.Current, Full, 0, Environment.ProcessId);
		var handoverClient = new QueueClient(handover);
		try
		{
			reply = await handoverClient.CallAsync(new QueueMessage { Op = "list" });
			Check(reply.Ok, "foreign_server_hands_over: list " + reply.Code);
			WriteStatus(handover, Foreign, Full, 0, ForeignPid);
			await IndependentQueueTests.Until(() => !QueueFiles.Alive(handover));
			await WaitServerExit(handover);
			Check(Events(handover, "queue_handover").Any(), "foreign_server_hands_over: queue_handover logged");
		}
		finally { if (QueueFiles.Alive(handover)) await Stop(handoverClient, handover); }

		string host = CreateProject(root, "dispatcher-host");
		WriteStatus(host, HostPlatform.Current, Full, 0, Environment.ProcessId);
		var hostClient = new QueueClient(host);
		try
		{
			reply = await hostClient.CallAsync(new QueueMessage { Op = "list" });
			Check(reply.Ok, "host_server_ignores_foreign_launch: first list " + reply.Code);
			int pid = ServerPid(host);
			var launch = new QueueLaunch { Executable = Path.Combine(host, "missing", "agentbridge"), Arguments = new[] { "queue-server", "--project", host } };
			QueueFiles.Write(Path.Combine(QueueFiles.Root(host), "launch.json"), JsonSerializer.Serialize(launch, QueueClient.Json));
			await Task.Delay(1500);
			reply = await hostClient.CallAsync(new QueueMessage { Op = "list" });
			Check(reply.Ok && ServerPid(host) == pid, "host_server_ignores_foreign_launch: same server keeps the queue: " + reply.Code + " " + pid + "->" + ServerPid(host));
			Check(Events(host, "queue_launch_ignored").Any(), "host_server_ignores_foreign_launch: queue_launch_ignored logged");
		}
		finally { await Stop(hostClient, host); }
		Console.WriteLine("Dispatcher host: PASS (owner policy, yield, idle, foreign defer, legacy launch, handover, ignored foreign launcher)");
	}

	private static async Task Stop(QueueClient client, string project)
	{
		await client.CallAsync(new QueueMessage { Op = "shutdown" });
		await IndependentQueueTests.Until(() => !QueueFiles.Alive(project));
		await WaitServerExit(project);
	}

	// Release the snapshot executable before the fixture is deleted on Windows.
	private static async Task WaitServerExit(string project)
	{
		try
		{
			using var server = System.Diagnostics.Process.GetProcessById(ServerPid(project));
			await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		}
		catch (ArgumentException) { } // Already exited.
	}

	private static int ServerPid(string project)
	{
		using var metadata = JsonDocument.Parse(SharedFile.ReadAllText(Path.Combine(QueueFiles.Root(project), "server.json")));
		return metadata.RootElement.GetProperty("Pid").GetInt32();
	}

	private static List<JsonElement> Events(string project, string name)
	{
		string logs = Path.Combine(project, "Logs");
		if (!Directory.Exists(logs)) return new List<JsonElement>();
		return Directory.GetFiles(logs, "AgentBridge-client-*.jsonl")
			.SelectMany(path => SharedFile.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries))
			.Select(line => JsonDocument.Parse(line).RootElement)
			.Where(e => e.GetProperty("E").GetString() == name)
			.ToList();
	}

	private static void WriteStatus(string project, string hostOs, string[] capabilities, long heartbeatAgeMs, int editorPid)
	{
		string bridge = Path.Combine(project, "Library", "AgentBridge");
		Directory.CreateDirectory(bridge);
		const string projectId = "dispatcher-host-test";
		File.WriteAllText(Path.Combine(bridge, "project-id"), projectId);
		var status = new
		{
			ProtocolVersion = 1,
			ProjectId = projectId,
			ProjectPath = project,
			HostOs = hostOs,
			EditorPid = editorPid,
			Enabled = true,
			RoslynReady = true,
			Capabilities = capabilities
		};
		File.WriteAllText(Path.Combine(bridge, "status.json"), JsonSerializer.Serialize(status));
		File.WriteAllText(Path.Combine(bridge, "heartbeat"), (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - heartbeatAgeMs).ToString());
	}

	private static string CreateProject(string root, string name)
	{
		string project = Path.Combine(root, name);
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{}");
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		return TestPaths.PhysicalDirectory(project);
	}

	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
