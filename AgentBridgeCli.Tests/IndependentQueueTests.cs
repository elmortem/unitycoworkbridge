using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

internal static class IndependentQueueTests
{
	internal static async Task RunAsync(string root)
	{
		string project = Path.Combine(root, "offline-queue");
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages"));
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{}");
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		project = TestPaths.PhysicalDirectory(project);
		var client = new QueueClient(project);
		async Task<QueueReply> Call(string op, string id = "", string owner = "owner") => await client.CallAsync(new QueueMessage { Op = op, TaskId = id, Session = owner, Incarnation = "executor-one", RequestHash = op == "claim" ? JsonSerializer.Deserialize<QueueTask>(SharedFile.ReadAllText(QueueFiles.Task(project, id)), QueueClient.Json)!.Hash : "" });
		async Task<QueueReply> Submit(string id) => await client.CallAsync(new QueueMessage { Op = "submit", TaskId = id,
			RequestJson = JsonSerializer.Serialize(new TaskRequest { Id = id, Kind = "compile", AgentSessionId = "owner" }, JsonSupport.Task) });
		var timer = Stopwatch.StartNew();
		var accepted = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Submit("offline_" + i)));
		Check(accepted.All(r => r.Ok), "parallel offline clients must all receive acceptance");
		Check(timer.Elapsed.TotalSeconds < 12, "offline admission must not wait for editor recovery");
		Check(!File.Exists(Path.Combine(project, "Library", "AgentBridge", "status.json")), "fixture has no editor status");
		Check((await Call("list")).Tasks.Length == 12, "offline queue contains every submission exactly once");
		Check((await Submit("offline_0")).Ok && (await Call("list")).Tasks.Length == 12, "same request is idempotent");
		var conflict = await client.CallAsync(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(new TaskRequest { Id = "offline_0", Kind = "tests" }) });
		Check(!conflict.Ok && conflict.Code == "id_conflict", "id conflict cannot overwrite an accepted task");
		Check((await Call("cancel", "offline_0")).Code == "canceled", "pending cancellation needs no editor");
		Check(!(await Call("claim", "offline_0")).Ok, "canceled pending task cannot start");
		var late = Path.Combine(project, "Library", "AgentBridge", "Journal", "offline_0.json");
		QueueFiles.Write(late, "{\"Id\":\"offline_0\",\"Kind\":\"compile\",\"Status\":\"success\"}");
		Check((await Call("get", "offline_0")).Task!.Status == "canceled", "late executor callback cannot replace terminal cancellation");
		Check((await client.CallAsync(new QueueMessage { Op = "move", TaskId = "offline_11", BeforeId = "offline_1" })).Ok, "offline move works");
		Check((await Call("list")).Tasks.First(t => t.Status == "queued").Id == "offline_11", "offline move changes actual queue ordering");

        Check((await client.CallAsync(new QueueMessage { Op = "claim", TaskId = "offline_1", Incarnation = "executor-one", RequestHash = "changed-inputs" })).Code == "id_conflict", "claim must bind to the accepted input bytes");
		Check((await Call("claim", "offline_1")).Ok, "executor claims a task");
		Check((await Call("cancel", "offline_1", "other")).Code == "task_protected", "foreign execution protection preserved");
		Check((await Call("cancel", "offline_1")).Code == "cancel_requested", "running cancellation is only intent");
		Check((await Call("get", "offline_1")).Task!.Status == "cancel_requested", "stopped peer cannot falsely confirm cancellation");
		QueueFiles.Write(Path.Combine(project, "Library", "AgentBridge", "Journal", "offline_1.json"),
			"{\"Id\":\"offline_1\",\"Kind\":\"compile\",\"Status\":\"canceled\",\"FinishedAtUtc\":\"2026-10-13T00:00:00Z\"}");
		Check((await Call("get", "offline_1")).Task!.Status == "canceled", "executor terminal confirmation is observed");
		for (int i = 0; i < 20; i++)
		{
			string id = "race_" + i;
			await Submit(id);
			var race = await Task.WhenAll(Call("cancel", id), Call("claim", id));
			Check(!(race[0].Code == "canceled" && race[1].Ok), "cancel-before-start and claim cannot both win");
			Check(race[0].Ok && (race[0].Code == "canceled" || race[0].Code == "cancel_requested"), "race has a durable classified cancellation outcome");
		}

		async Task<CoordinationReply> Coord(CoordinationCommand command)
		{
			command.Nonce = Guid.NewGuid().ToString("N");
			var reply = await client.CallAsync(new QueueMessage { Op = "coord", CoordinationJson = JsonSerializer.Serialize(command, QueueClient.Json) });
			Check(reply.Ok, "offline coordination: " + reply.Code + " " + reply.Message);
			return JsonSerializer.Deserialize<CoordinationReply>(reply.CoordinationJson, QueueClient.Json)!;
		}
		await Coord(new CoordinationCommand { Op = CoordinationEngine.OpRegister, Session = "batch-owner", SpecId = "offline", Paths = new[] { "Assets/" } });
		var plan = new CoordinationPlan();
		plan.Steps.Add(new CoordinationStep { Id = "First", Kind = "compile" });
		plan.Steps.Add(new CoordinationStep { Id = "Last", Kind = "tests", Mode = "EditMode", Tests = new[] { "Example.Test" } });
		var batch = await Coord(new CoordinationCommand { Op = CoordinationEngine.OpRequest, Session = "batch-owner", Uuid = "offline-batch", Kind = CoordinationLimits.KindValidation, Seconds = 300, Plan = plan });
		Check(batch.TaskIds.Length == 2, "complete batch returns both ids");
		Check((await Call("claim", "offline_5")).Ok, "reserve ordinary execution before delayed window confirmation");
		var confirm = await client.CallAsync(new QueueMessage { Op = "coord", CoordinationJson = JsonSerializer.Serialize(new CoordinationCommand { Op = CoordinationEngine.OpWindowConfirm, Nonce = Guid.NewGuid().ToString("N") }, QueueClient.Json) });
		Check(!confirm.Ok && confirm.Code == CoordinationCodes.Busy, "a delayed ready notification cannot grant a window over an existing execution claim");
		Check((await Call("get", batch.TaskIds[1])).Task!.Status == "queued", "future batch step exists before first editor tick");
		await Coord(new CoordinationCommand { Op = CoordinationEngine.OpCancel, Session = "batch-owner", Uuid = "offline-batch" });
		Check((await Call("get", batch.TaskIds[1])).Task!.Status == "canceled", "closed batch steps are terminal, never not-found");

        QueueFiles.Write(Path.Combine(project, "Library", "AgentBridge", "Journal", "offline_3.json"), "{broken");
        Check((await Submit("after-corrupt-journal")).Ok, "one malformed result must not prevent unrelated admission");
        Check((await Call("cancel", "offline_2")).Ok, "one malformed result must not prevent unrelated cancellation");

        if (OperatingSystem.IsWindows())
        {
            string taskFile = QueueFiles.Task(project, "offline_6");
            var attributes = File.GetAttributes(taskFile);
            File.SetAttributes(taskFile, attributes | FileAttributes.ReadOnly);
            try
            {
                Check(!(await Call("cancel", "offline_6")).Ok, "failed task publication must be reported");
                Check((await Call("get", "offline_6")).Task!.Status == "queued", "failed mutation must not leak into the in-memory queue");
            }
            finally { File.SetAttributes(taskFile, attributes); }
            Check((await Call("cancel", "offline_6")).Code == "canceled", "cancellation can be retried after restoring write access");
        }

		// Kill only our fixture server: a real process crash must preserve tasks and cancellation.
		string serverFile = Path.Combine(QueueFiles.Root(project), "server.json");
		using (var metadata = JsonDocument.Parse(SharedFile.ReadAllText(serverFile)))
		using (var server = Process.GetProcessById(metadata.RootElement.GetProperty("Pid").GetInt32()))
		{
			server.Kill(); await server.WaitForExitAsync();
		}
		Check((await Call("get", "offline_4")).Task!.Status == "queued", "queued task survives server crash and automatic restart");
		Check((await Call("get", "offline_0")).Task!.Status == "canceled", "terminal cancellation survives server crash");
		Check(!(await client.CallAsync(new QueueMessage { Op = "claim", TaskId = "offline_5", Incarnation = "executor-two" })).Ok,
			"another executor cannot replay an unresolved claim");
		await Call("shutdown");
		await Until(() => !QueueFiles.Alive(project));
		// A shell captures CLI output while the server stays alive. Inheriting the shell's
		// pipe in the daemon would keep ReadToEnd blocked after the CLI itself already exits.
		string cli = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "agentbridge.exe" : "agentbridge");
		using var shellClient = Process.Start(new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true,
			RedirectStandardError = true, ArgumentList = { "queue", "list", "--project", project } })!;
		var capture = shellClient.StandardOutput.ReadToEndAsync();
		await shellClient.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		var captured = await capture.WaitAsync(TimeSpan.FromSeconds(3));
		Check(shellClient.ExitCode == 0 && captured.Contains("offline_4"), "daemon must detach from the caller's captured output");
		await Call("shutdown");
		await Until(() => !QueueFiles.Alive(project));
		Console.WriteLine("Independent queue: PASS (offline clients, idempotency, cancel/start races, batch steps, real server crash)");
	}
	internal static async Task Until(Func<bool> ready)
	{
		var deadline = Stopwatch.StartNew();
		while (!ready()) { if (deadline.Elapsed.TotalSeconds > 12) throw new InvalidOperationException("fixture did not become ready"); await Task.Delay(20); }
	}
	internal static async Task StopServers(string root)
	{
		foreach (string path in Directory.GetFiles(root, "server.json", SearchOption.AllDirectories))
		{
			string project = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))))!;
			try
			{
				if (QueueFiles.Alive(project)) await new QueueClient(project).CallAsync(new QueueMessage { Op = "shutdown" });
				await Until(() => !QueueFiles.Alive(project));
			}
			catch (Exception) { }
		}
		await Task.Delay(100);
	}
	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
