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
			// B fails its first step: B2 must never be supplied, C must still run.
			string status = request.Session == "b" ? "test_failure" : "success";
			File.WriteAllText(Path.Combine(BridgePaths.Journal, id + ".json"), UnityEngine.JsonUtility.ToJson(new TaskRecord { Status = status, FinishedAtUtc = "2026-09-13T00:00:00Z" }));
			// Skip the completion callback, as if its store transaction lost a race/reloaded.
			// The production pump must reconcile the journal without re-executing the task.
		}
		Expect(string.Join(",", executed) == "aV1,aV2,bV1,cV1,cV2", "FIFO, ordered steps, fail-fast, no client polling");
		Expect(state.FindWindowGrant() == null, "last batch frees editor without finish");
		Expect(state.FindRequestByUuid("a", "batch-a").Reason == "completed", "successful batch outcome");
		Expect(state.FindRequestByUuid("b", "batch-b").Reason.StartsWith("failed:V1"), "failed batch outcome");
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
}
