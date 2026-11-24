using AgentBridge;
using AgentBridge.Cli;
using AgentBridge.Coordination;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

internal static class BatchScenarios
{
	public static void Run(string root, List<string> covered)
	{
		BridgePaths.Root = Path.Combine(root, "batch-pump");
		Directory.CreateDirectory(BridgePaths.Inbox);
		Directory.CreateDirectory(BridgePaths.Journal);
		var state = NewState();
		CoordinationEditorAdapter.Snapshot = state;
		var engine = new CoordinationEngine();
		long now = CoordinationEditorAdapter.Now;
		foreach (string session in new[] { "a", "b", "c" })
		{
			engine.Apply(state, Register(session, "Assets/" + session + "/"), now);
			ExpectCode(engine.Apply(state, WindowRequest(session, "batch-" + session, plan: TestsPlan("V1", "V2")), now), CoordinationCodes.Waiting, "queue complete batch");
		}
		// No client calls after this point: the editor alone supplies all six tasks and finishes.
		var executed = new List<string>();
		for (int tick = 0; tick < 40; tick++)
		{
			if (state.FindWindowGrant() == null) engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), now);
			CoordinationBatchPump.Tick();
			var grant = state.FindWindowGrant();
			if (grant == null) continue;
			var request = state.FindRequest(grant.RequestId);
			int index = CoordinationBatch.NextStep(request, grant);
			if (index < 0) continue;
			string id = CoordinationBatch.TaskId(request, index);
			string path = Path.Combine(BridgePaths.Inbox, id + ".task.json");
			if (!File.Exists(path) || File.Exists(Path.Combine(BridgePaths.Journal, id + ".json"))) continue;
			var task = UnityEngine.JsonUtility.FromJson<TaskRequest>(File.ReadAllText(path));
			Expect(task.Id == id && task.AgentSessionId == request.Session, "stable task ownership");
			var begin = Command(CoordinationEngine.OpStepBegin);
			begin.Session = request.Session; begin.Token = grant.Token; begin.StepId = task.CoordinationStepId; begin.TaskId = id;
			begin.Actual = ActualTests(task.CoordinationStepId);
			ExpectCode(engine.Apply(state, begin, now), CoordinationCodes.Ok, "editor reserves supplied task");
			executed.Add(request.Session + task.CoordinationStepId);
			// B is canceled on its first step: B2 must never be supplied, C must still run.
			string status = request.Session == "b" ? "canceled" : "success";
			File.WriteAllText(Path.Combine(BridgePaths.Journal, id + ".json"), UnityEngine.JsonUtility.ToJson(new TaskRecord { Status = status, FinishedAtUtc = "2026-09-13T00:00:00Z" }));
			// Skip the completion callback, as if its store transaction lost a race/reloaded.
			// The production pump must reconcile the journal without re-executing the task.
		}
		Expect(string.Join(",", executed) == "aV1,aV2,bV1,cV1,cV2", "FIFO, ordered steps, fail-fast, no client polling");
		Expect(state.FindWindowGrant() == null, "last batch frees editor without finish");
		Expect(state.FindRequestByUuid("a", "batch-a").Reason == "completed", "successful batch outcome");
		Expect(state.FindRequestByUuid("b", "batch-b").Reason.StartsWith("failed:V1"), "canceled batch outcome");
		Expect(state.FindRequestByUuid("c", "batch-c").Reason == "completed", "later batch completes");
		var replay = engine.Apply(state, WindowRequest("a", "batch-a", plan: TestsPlan("V1", "V2")), now);
		ExpectCode(replay, CoordinationCodes.AlreadyClosed, "repeated submit cannot repeat effects");
		var incomplete = new CoordinationPlan();
		incomplete.Steps.Add(new CoordinationStep { Id = "script", Kind = "csharp", PayloadSha256 = "abc" });
		ExpectCode(engine.Apply(state, WindowRequest("a", "empty", CoordinationLimits.KindEditor, plan: incomplete), now), CoordinationCodes.PlanInvalid, "hash-only reservation refused");
		Expect(state.FindRequestByUuid("a", "empty") == null, "incomplete package never enters FIFO");
		var frozen = new CoordinationPlan();
		frozen.Steps.Add(new CoordinationStep { Id = "script", Kind = "csharp", Payload = "source", PayloadName = "Task_Source", PayloadSha256 = CoordinationDigest.Sha256("source") });
		ExpectCode(engine.Apply(state, WindowRequest("a", "frozen", CoordinationLimits.KindEditor, plan: frozen), now), CoordinationCodes.Waiting, "complete source accepted");
		state = CoordinationJsonCodec.Instance.Deserialize(CoordinationJsonCodec.Instance.Serialize(state));
		CoordinationEditorAdapter.Snapshot = state;
		engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), now);
		CoordinationBatchPump.Tick();
		var frozenRequest = state.FindRequestByUuid("a", "frozen");
		string frozenId = CoordinationBatch.TaskId(frozenRequest, 0);
		Expect(File.ReadAllText(Path.Combine(BridgePaths.Inbox, frozenId + ".cs")) == "source", "persisted source supplied without original file or client");
		var frozenTask = UnityEngine.JsonUtility.FromJson<TaskRequest>(File.ReadAllText(Path.Combine(BridgePaths.Inbox, frozenId + ".task.json")));
		Expect(frozenTask.EntryPointName == "Task_Source", "entry point is independent of unique batch task id");
		covered.Add("B01-B06: unattended FIFO, failure, journal recovery, deduplication, ready-only, frozen payload");
		RepairPriority(covered);
		OwnPackageYieldsToOwnEdit(covered);
	}

	static void RepairPriority(List<string> covered)
	{
		var state = NewState();
		var engine = new CoordinationEngine();
		long now = CoordinationEditorAdapter.Now;
		foreach (string name in new[] { "owner", "neighbor", "third" }) engine.Apply(state, Register(name, "Assets/" + name + "/"), now);
		engine.Apply(state, WindowRequest("neighbor", "older-validation"), now);
		engine.Apply(state, WindowRequest("third", "another-validation"), now);
		var failure = Command(CoordinationEngine.OpCompilerState);
		failure.TaskId = "failed-cycle"; failure.Reason = "compiler_error";
		engine.Apply(state, failure, now);
		var noWindow = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), now);
		Expect(!noWindow.Ok && state.FindWindowGrant() == null, "known broken inputs never reserve empty validation windows");
		var repair = engine.Apply(state, EditBegin("owner", "fix-float"), now);
		ExpectCode(repair, CoordinationCodes.Granted, "one-line repair bypasses older blocked validations");
		var end = Command(CoordinationEngine.OpEditEnd); end.Session = "owner"; end.Token = repair.Token;
		engine.Apply(state, end, now);
		engine.Apply(state, failure, now);
		Expect(!state.InputRepairPending, "old failed cycle must not reblock after the repair");
		ExpectCode(engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), now), CoordinationCodes.Granted, "validation resumes after edit-end");
		Expect(state.FindWindowGrant().Session == "neighbor", "validation FIFO preserved after repair");
		failure.TaskId = "new-failed-cycle";
		engine.Apply(state, failure, now);
		var during = engine.Apply(state, EditBegin("owner", "second-fix"), now);
		ExpectCode(during, CoordinationCodes.Waiting, "repair must not write through a live window");
		CoordinationEditorAdapter.Snapshot = state;
		CoordinationBatchPump.Tick();
		Expect(state.FindWindowGrant() == null, "idle validation window releases automatically on known compiler failure");
		Expect(state.FindGrantBySession("owner", CoordinationLimits.KindEdit) != null, "repair starts without neighbor cancellation or finish");
		covered.Add("B07: compiler repair bypasses blocked checks without bypassing a live window");
	}

	// B08 - the session's own package never refuses the session's own writer. A package that has not
	// started is superseded, so a known compiler error can no longer deadlock its owner (the package
	// held by the error, the repair held by the package). A package that holds the editor keeps it,
	// and the queued edit is issued the moment the package closes.
	static void OwnPackageYieldsToOwnEdit(List<string> covered)
	{
		var state = NewState();
		var engine = new CoordinationEngine();
		long now = CoordinationEditorAdapter.Now;
		foreach (string name in new[] { "owner", "neighbor" }) engine.Apply(state, Register(name, "Assets/" + name + "/"), now);
		var package = engine.Apply(state, WindowRequest("owner", "owner-check", plan: TestsPlan("V1", "V2")), now);
		ExpectCode(package, CoordinationCodes.Waiting, "owner queues a validation package");
		var failure = Command(CoordinationEngine.OpCompilerState);
		failure.TaskId = "owner-broken-cycle"; failure.Reason = "compiler_error";
		engine.Apply(state, failure, now);
		Expect(!engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), now).Ok && state.FindWindowGrant() == null,
			"known broken inputs hold the owner's own validation package");

		var repair = engine.Apply(state, EditBegin("owner", "owner-fix"), now);
		ExpectCode(repair, CoordinationCodes.Granted, "own waiting package cannot block its owner's repair");
		Expect(repair.Superseded == package.RequestId, "the reply names the superseded package");
		var old = state.FindRequestByUuid("owner", "owner-check");
		Expect(old.State == CoordinationLimits.StateCanceled && old.Reason == "superseded_by_edit:" + repair.RequestId,
			"the superseded package is terminal and records its cause");
		var oldStatus = engine.Inspect(state, "owner", "owner-check", now);
		Expect(oldStatus.State == CoordinationLimits.StateCanceled && oldStatus.Reason == old.Reason && oldStatus.TaskIds.Length == 2,
			"status of the old package reports the outcome for every planned task");
		ExpectCode(engine.Apply(state, WindowRequest("owner", "owner-check", plan: TestsPlan("V1", "V2")), now),
			CoordinationCodes.AlreadyClosed, "replaying the superseded uuid cannot revive it");

		var end = Command(CoordinationEngine.OpEditEnd); end.Session = "owner"; end.Token = repair.Token;
		ExpectCode(engine.Apply(state, end, now), CoordinationCodes.Ok, "repair closes");
		ExpectCode(engine.Apply(state, WindowRequest("owner", "owner-recheck", plan: TestsPlan("V1", "V2")), now),
			CoordinationCodes.Waiting, "fixed inputs get a fresh package");
		var window = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), now);
		ExpectCode(window, CoordinationCodes.Granted, "the fresh package runs after the repair");
		Expect(window.Session == "owner", "the fresh package belongs to the owner");

		var begin = Command(CoordinationEngine.OpStepBegin);
		begin.Session = "owner"; begin.Token = window.Token; begin.StepId = "V1"; begin.TaskId = "Task_owner_V1";
		begin.Actual = ActualTests("V1");
		ExpectCode(engine.Apply(state, begin, now), CoordinationCodes.Ok, "the package starts");
		var queued = engine.Apply(state, EditBegin("owner", "owner-next"), now);
		ExpectCode(queued, CoordinationCodes.Waiting, "own running package queues the edit instead of refusing it");
		Expect(queued.Superseded == "" && state.FindRequestByUuid("owner", "owner-recheck").State == CoordinationLimits.StateGranted,
			"a started package is never superseded");
		Expect(state.FindGrantBySession("owner", CoordinationLimits.KindEdit) == null, "the edit never writes through its own window");
		Expect(queued.Blockers.Contains("own_window_active:" + window.RequestId), "the queued edit names its own package as the blocker");
		ExpectCode(engine.Apply(state, EditBegin("owner", "owner-other"), now), CoordinationCodes.RequestActive,
			"still one queued edit per session");
		ExpectCode(engine.Apply(state, WindowRequest("owner", "owner-third"), now), CoordinationCodes.WindowActive,
			"a second package cannot be queued while the first holds the editor");

		var done = Command(CoordinationEngine.OpStepFinish);
		done.Session = "owner"; done.Token = window.Token; done.StepId = "V1"; done.TaskId = "Task_owner_V1"; done.Reason = "success";
		engine.Apply(state, done, now);
		var finish = Command(CoordinationEngine.OpFinish);
		finish.Session = "owner"; finish.Token = window.Token; finish.Reason = "completed";
		ExpectCode(engine.Apply(state, finish, now), CoordinationCodes.Ok, "the package closes itself");
		var issued = state.FindGrantBySession("owner", CoordinationLimits.KindEdit);
		Expect(issued != null && issued.RequestId == queued.RequestId, "the queued edit is issued as soon as the own package closes");

		// A package still queued behind someone else's run is superseded too; the edit itself still
		// waits for the live foreign window.
		var queue = NewState();
		engine.Apply(queue, Register("runner", "Assets/runner/"), now);
		engine.Apply(queue, Register("owner", "Assets/owner/"), now);
		engine.Apply(queue, WindowRequest("runner", "long-run"), now);
		var running = engine.Apply(queue, Command(CoordinationEngine.OpWindowConfirm), now);
		var behind = engine.Apply(queue, WindowRequest("owner", "behind-runner"), now);
		var fix = engine.Apply(queue, EditBegin("owner", "fix-while-queued"), now);
		ExpectCode(fix, CoordinationCodes.Waiting, "a writer still never races a live foreign window");
		Expect(fix.Superseded == behind.RequestId && fix.Blockers.Contains("window_active:runner"),
			"queued own package is superseded and the real blocker is reported");
		var stop = Command(CoordinationEngine.OpFinish);
		stop.Session = "runner"; stop.Token = running.Token;
		engine.Apply(queue, stop, now);
		Expect(queue.FindGrantBySession("owner", CoordinationLimits.KindEdit) != null, "the edit is issued when the foreign window closes");
		covered.Add("B08: own package never blocks its owner's edit right");
	}
}
