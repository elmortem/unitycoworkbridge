using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

internal static class QueueDiscussionTests
{
	internal static async Task RunAsync(string root)
	{
		string project = Project(root, "discussions");
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		var server = new QueueServer(project, () => now);
		QueueReply Call(QueueMessage message) => server.Handle(message);
		QueueReply Submit(string id, string session, int seconds = 0, string reservation = "") => Call(new QueueMessage { Op = "submit",
			RequestJson = JsonSerializer.Serialize(new TaskRequest { Id = id, Kind = "tests", AgentSessionId = session, ExpectedSeconds = seconds, ReservationId = reservation }, JsonSupport.Task) });
		void Running(string id, int age)
		{
			var task = Call(new QueueMessage { Op = "get", TaskId = id });
			Check(Call(new QueueMessage { Op = "claim", TaskId = id, Incarnation = "fixture", RequestHash = task.RequestHash }).Ok, "claim " + id);
			QueueFiles.Write(Path.Combine(project, "Library", "AgentBridge", "Journal", id + ".json"), JsonSerializer.Serialize(new
			{ Id = id, Kind = "tests", Status = "running", StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-age).ToString("o") }));
		}
		Check(Submit("long", "owner").Ok, "ordinary submission unchanged");
		Running("long", 10);
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(new TaskRequest { Id = "obsolete-auto-stop", Kind = "stopplay", AgentSessionId = "owner", TargetTaskId = "long" }) }).Code == "automatic_stop_obsolete", "automatic stop cannot interrupt the task that already started");
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(new TaskRequest { Id = "stopplay-bypass", Kind = "stopplay", AgentSessionId = "neighbor" }) }).Code == "negotiation_required", "stopplay cannot bypass foreign task protection");
		Check(Call(new QueueMessage { Op = "cancel", TaskId = "long", Session = "neighbor" }).Code == "task_protected", "first 300 seconds protected");
		Check(Call(new QueueMessage { Op = "request-stop", TaskId = "long", Session = "neighbor", Reason = "Need a short test" }).Code == "task_protected", "no negotiation before 300");
		Running("long", 301);
		Check(Call(new QueueMessage { Op = "cancel", TaskId = "long", Session = "neighbor" }).Code == "negotiation_required", "age alone never permits cancel");
		var request = new QueueMessage { Op = "request-stop", TaskId = "long", Session = "neighbor", Reason = "Need a short test" };
		var discussion = Call(request).Discussion!;
		Check(discussion.ReplyByMs == now + 15000, "15 seconds measured at durable registration");
		now += 14000;
		server = new QueueServer(project, () => now);
		Check(Call(request).Discussion!.ReplyByMs == discussion.ReplyByMs, "retry and restart do not reset the deadline");
		var cancel = new QueueMessage { Op = "cancel", TaskId = "long", Session = "neighbor", DiscussionId = discussion.Id, Reason = "Need the editor for the short test" };
		Check(Call(cancel).Code == "reply_pending", "14 seconds is too soon");
		var ownerInbox = Call(new QueueMessage { Op = "get", TaskId = "long", Session = "owner" });
		Check(ownerInbox.Discussions.Any(d => d.Id == discussion.Id), "waiting owner receives incoming discussion");
		var answer = Call(new QueueMessage { Op = "respond", DiscussionId = discussion.Id, Session = "owner", Decision = "continue", Reason = "Only five seconds left; restart loses all recording", Seconds = 5 });
		Check(answer.Ok, "owner can explain interruption cost");
		now += 1000;
		Check(Call(cancel).Code == "response_unread", "answer racing with timeout requires explicit review");
		Check(Call(new QueueMessage { Op = "respond", DiscussionId = discussion.Id, Session = "stranger", Decision = "yield", Reason = "fake" }).Code == "bad_response", "other session cannot answer for owner");
		Check(Call(new QueueMessage { Op = "respond", DiscussionId = discussion.Id, Session = "neighbor", Decision = "wait", Reason = "Finish your recording", Seconds = 10 }).Ok, "requester agrees to wait");
		cancel.AfterRevision = long.MaxValue;
		Check(Call(cancel).Code == "agreement_active", "agreed waiting cannot be bypassed with cancel");
		now += 11000;
		discussion = Call(request).Discussion!;
		Check(discussion.State == "pending" && discussion.ReplyByMs == now + 15000, "overrun requires renewed discussion");
		Check(Submit("short", "neighbor").Ok, "neighbor's concrete work queued");
		now += 15000;
		Check(Call(cancel).Code == "cancel_requested", "unanswered request permits cancellation after 15 seconds");
		var canceled = Call(new QueueMessage { Op = "get", TaskId = "long" }).Task!;
		Check(canceled.YieldUntilTaskId == "short" && canceled.CanceledBy == "neighbor", "owner sees reason and successor");
		Check(Submit("duplicate-long", "owner").Code == "yield_pending", "new task id cannot immediately restart interrupted work");
		server = new QueueServer(project, () => now);
		Check(Submit("duplicate-long-2", "owner").Code == "yield_pending", "yield survives dispatcher restart");
		QueueFiles.Write(Path.Combine(project, "Library", "AgentBridge", "Journal", "short.json"), "{\"Id\":\"short\",\"Kind\":\"tests\",\"Status\":\"success\"}");
		Check(Submit("after-neighbor", "owner").Ok, "owner can submit after neighbor finishes");
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(new TaskRequest { Id = "raw-cancel", Kind = "cancel", TargetTaskId = "after-neighbor" }) }).Code == "negotiation_required", "raw cancel tasks cannot bypass negotiation");
		Reservations(root);
		await WaitInterruption(root);
		await ReservationRace(root);
		Console.WriteLine("Long-run negotiation: PASS (15s deadline, replies, agreement, restart, reservations, admission races, wait interruption, yield)");
	}
	private static void Reservations(string root)
	{
		string project = Project(root, "reservations");
		var server = new QueueServer(project);
		QueueReply Call(QueueMessage message) => server.Handle(message);
		var reserve = new QueueMessage { Op = "reserve", Session = "long-owner", DiscussionId = "recording", Reason = "Record the whole film", Seconds = 1200 };
		var grant = Call(reserve);
		Check(grant.Code == "granted", "empty editor grants reservation without asking anyone");
		if (OperatingSystem.IsWindows())
		{
			using var locked = new FileStream(Path.Combine(QueueFiles.Root(project), "discussions.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			bool failed = false;
			try { Call(reserve); } catch (IOException) { failed = true; }
			Check(failed, "unreadable durable negotiations must not be replaced with an empty state");
		}
		var second = Call(new QueueMessage { Op = "reserve", Session = "other-long", DiscussionId = "film2", Reason = "Another film", Seconds = 700 });
		Check(second.Code == "pending" && second.Discussion!.Blockers.Contains("long-owner"), "simultaneous empty-editor reservations cannot both win");
		var shortTask = new TaskRequest { Id = "short", Kind = "compile", AgentSessionId = "neighbor" };
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(shortTask) }).Ok, "short tasks never require reservation");
		var longTask = new TaskRequest { Id = "film", Kind = "tests", AgentSessionId = "long-owner", ExpectedSeconds = 1200 };
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(longTask) }).Code == "reservation_required", "declared long run requires reservation");
		longTask.ReservationId = grant.Discussion!.Id;
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(longTask) }).Code == "negotiation_required", "new competitor between reserve and submit is detected");
		Check(Call(new QueueMessage { Op = "respond", DiscussionId = longTask.ReservationId, Session = "neighbor", Decision = "deny", Reason = "Need compile first" }).Ok, "neighbor can object");
		Check(Call(reserve).Code == "pending", "denial blocks long admission");
		Check(Call(new QueueMessage { Op = "respond", DiscussionId = longTask.ReservationId, Session = "neighbor", Decision = "allow", Reason = "Can wait for recording" }).Ok, "neighbor can agree");
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(longTask) }).Ok, "agreed long task admitted");
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(longTask) }).Ok, "same admitted task remains idempotent");
		longTask.Id = "film-again";
		Check(Call(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(longTask) }).Code == "reservation_used", "one reservation cannot run twice under new ids");
		var coordination = new CoordinationCommand { Op = CoordinationEngine.OpRegister, Session = "batch", SpecId = "test", Paths = new[] { "Assets/" } };
		Check(Call(new QueueMessage { Op = "coord", CoordinationJson = JsonSerializer.Serialize(coordination, QueueClient.Json) }).Ok, "register batch");
		coordination = new CoordinationCommand { Op = CoordinationEngine.OpRequest, Session = "batch", Uuid = "long-batch", Kind = "validation", Seconds = 600,
			Plan = new CoordinationPlan { Steps = new() { new CoordinationStep { Id = "test", Kind = "tests", Mode = "EditMode", Tests = new[] { "Test" } } } } };
		Check(Call(new QueueMessage { Op = "coord", CoordinationJson = JsonSerializer.Serialize(coordination, QueueClient.Json), ExpectedSeconds = 1200 }).Code == "reservation_required", "batch admission uses same reservation gate");
	}
	private static async Task WaitInterruption(string root)
	{
		string project = Project(root, "discussion-wait");
		var client = new QueueClient(project);
		await client.CallAsync(new QueueMessage { Op = "submit", RequestJson = JsonSerializer.Serialize(new TaskRequest { Id = "waited", Kind = "tests", AgentSessionId = "owner" }) });
		var original = Console.Out;
		using var output = new StringWriter();
		Console.SetOut(output);
		try
		{
			var wait = new BridgeClient(project, "json", "owner", null, new TelemetryLog(project, false)).WaitForTaskAsync("waited", 60, "tests");
			await client.CallAsync(new QueueMessage { Op = "reserve", Session = "neighbor", DiscussionId = "long", Reason = "Need a long recording", Seconds = 700 });
			Check(await wait.WaitAsync(TimeSpan.FromSeconds(5)) == 2, "incoming discussion interrupts 60-second CLI wait promptly");
			Check(output.ToString().Contains("coordination_attention") && output.ToString().Contains("waited") && output.ToString().Contains("Need a long recording"), "attention contains original task and actual request");
			Check((await client.CallAsync(new QueueMessage { Op = "get", TaskId = "waited" })).Task!.Status == "queued", "attention never cancels or duplicates accepted task");
			long cursor = (await client.CallAsync(new QueueMessage { Op = "inbox", Session = "owner" })).DiscussionRevision;
			Check(await new BridgeClient(project, "json", "owner", null, new TelemetryLog(project, false)) { AfterRevision = cursor }.WaitForTaskAsync("waited", 0, "tests") == 0,
				"acknowledged discussions do not repeatedly interrupt resumed waits");
			await client.CallAsync(new QueueMessage { Op = "coord", CoordinationJson = JsonSerializer.Serialize(new CoordinationCommand
			{ Op = CoordinationEngine.OpRegister, Session = "owner", SpecId = "wait", Paths = new[] { "Assets/" } }, QueueClient.Json) });
			output.GetStringBuilder().Clear();
			var coordWait = Task.Run(() => AgentBridgeApplication.RunAsync(new[] { "coord", "wait", "--project", project, "--session", "owner", "--after", long.MaxValue.ToString(),
				"--discussion-after", cursor.ToString(), "--wait", "60" }));
			await client.CallAsync(new QueueMessage { Op = "reserve", Session = "neighbor", DiscussionId = "long2", Reason = "Another long run to discuss", Seconds = 700 });
			Check(await coordWait.WaitAsync(TimeSpan.FromSeconds(5)) == 2 && output.ToString().Contains("coordination_attention"), "coord wait is interrupted independently of its coordination revision");
		}
		finally { Console.SetOut(original); await client.CallAsync(new QueueMessage { Op = "shutdown" }); }
	}
	private static async Task ReservationRace(string root)
	{
		string project = Project(root, "reservation-race");
		var client = new QueueClient(project);
		try
		{
			var requests = new[] { "one", "two" }.Select(s => new QueueMessage { Op = "reserve", Session = s, DiscussionId = "long-work", Seconds = 600, Reason = "Record a complete run" }).ToArray();
			var answers = await Task.WhenAll(requests.Select(r => new QueueClient(project).CallAsync(r)));
			Check(answers.Count(r => r.Code == "granted") == 1 && answers.Count(r => r.Code == "pending") == 1, "parallel real clients must not both reserve an empty editor");
			var winner = answers.Single(r => r.Code == "granted").Discussion!;
			await client.CallAsync(new QueueMessage { Op = "shutdown" });
			await IndependentQueueTests.Until(() => !QueueFiles.Alive(project));
			var replay = await client.CallAsync(requests.Single(r => r.Session == winner.Session));
			Check(replay.Discussion!.Id == winner.Id && replay.Code == "granted", "reservation survives an actual dispatcher restart");
		}
		finally { await client.CallAsync(new QueueMessage { Op = "shutdown" }); }
	}
	private static string Project(string root, string name)
	{
		string project = Path.Combine(root, name);
		Directory.CreateDirectory(Path.Combine(project, "Assets"));
		Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(project, "Packages"));
		File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2");
		File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{}");
		return TestPaths.PhysicalDirectory(project);
	}
	private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
