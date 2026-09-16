using AgentBridge.Cli;
using AgentBridge.Coordination;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

internal static class LeaseScenarios
{
	private static CoordinationCommand Fill(CoordinationCommand command, string session, string token)
	{
		command.Session = session;
		command.Token = token;
		return command;
	}

	public static void Run(string root, List<string> covered)
	{
		IdleDeclarations();
		OverlappingFifo();
		ExpiredLeaseAndReplay(root);
		LegacyStateMigration(root);
		covered.Add("L01-L04: idle declarations, overlap FIFO, automatic expiry, legacy migration");
	}

	private static void IdleDeclarations()
	{
		var state = NewState();
		var engine = new CoordinationEngine();
		long now = new FakeClock().UtcNowMs;
		engine.Apply(state, Register("gone", "Assets/Shots/"), now);
		now += 86_400_000;
		ExpectCode(engine.Apply(state, Register("new", "assets/shots/Night.cs"), now), CoordinationCodes.Ok,
			"a forgotten idle session never owns files");
		var edit = engine.Apply(state, EditBegin("new", "write"), now);
		ExpectCode(edit, CoordinationCodes.Granted, "idle overlap does not block writing");
		ExpectCode(engine.Apply(state, Register("gone", "Assets/Shots/Night.cs"), now), CoordinationCodes.Ok,
			"idle re-registration can overlap an active writer");
		var scope = Command(CoordinationEngine.OpScope);
		scope.Session = "gone"; scope.Paths = new[] { "Assets/" };
		ExpectCode(engine.Apply(state, scope, now), CoordinationCodes.Ok, "idle scope change is a declaration");
		ExpectCode(engine.Apply(state, EditBegin("gone", "return"), now), CoordinationCodes.Waiting,
			"returning owner must wait for the current writer");
		ExpectCode(engine.Apply(state, scope, now), CoordinationCodes.ParticipantBusy, "queued scope cannot change");
		scope.Session = "new";
		ExpectCode(engine.Apply(state, scope, now), CoordinationCodes.ParticipantBusy, "live scope cannot change");
		engine.Apply(state, Fill(Command(CoordinationEngine.OpEditEnd), "new", edit.Token), now);
		Expect(state.FindGrantBySession("gone", null) != null, "returning agent receives a fresh grant after the other writer");
	}

	private static void OverlappingFifo()
	{
		var state = NewState();
		var engine = new CoordinationEngine();
		long now = new FakeClock().UtcNowMs;
		engine.Apply(state, Register("a", "Assets/X.cs"), now);
		engine.Apply(state, Register("b", "Assets/X.cs", "Assets/Y.cs"), now);
		engine.Apply(state, Register("c", "Assets/Y.cs"), now);
		engine.Apply(state, Register("d", "Assets/Z.cs"), now);
		var a = engine.Apply(state, EditBegin("a", "a"), now);
		ExpectCode(engine.Apply(state, Fill(Command(CoordinationEngine.OpRenew), "a", a.Token), now + 1000),
			CoordinationCodes.Ok, "uncontended live lease can renew");
		var b = engine.Apply(state, EditBegin("b", "b"), now + 1000);
		ExpectCode(b, CoordinationCodes.Waiting, "overlap waits");
		ExpectCode(engine.Apply(state, EditBegin("c", "c"), now + 1000), CoordinationCodes.Waiting,
			"younger writer cannot overtake a blocked older overlapping request");
		var d = engine.Apply(state, EditBegin("d", "d"), now + 1000);
		ExpectCode(d, CoordinationCodes.Granted, "disjoint edits proceed around blocked requests");
		var blockers = engine.Inspect(state, "b", b.RequestId, now + 1000).Blockers;
		Expect(blockers.Contains("edit_active:a") && !blockers.Contains("edit_active:d"), "diagnostics identify actual overlap only");
		ExpectCode(engine.Apply(state, Fill(Command(CoordinationEngine.OpRenew), "a", a.Token), now + 1000),
			CoordinationCodes.PauseRequested, "renew cannot starve an overlapping waiter");
		ExpectCode(engine.Apply(state, Fill(Command(CoordinationEngine.OpRenew), "d", d.Token), now + 1000),
			CoordinationCodes.Ok, "disjoint waiters do not prevent renewal");
		engine.Apply(state, Fill(Command(CoordinationEngine.OpEditEnd), "a", a.Token), now + 1000);
		var grantedB = state.FindGrantBySession("b", null);
		Expect(grantedB != null && state.FindGrantBySession("c", null) == null, "oldest overlap wins");
		engine.Apply(state, Fill(Command(CoordinationEngine.OpEditEnd), "b", grantedB.Token), now + 1000);
		Expect(state.FindGrantBySession("c", null) != null, "next overlap advances without registering again");
	}

	private static void ExpiredLeaseAndReplay(string root)
	{
		var project = Path.Combine(root, "lease-expiry");
		Directory.CreateDirectory(project);
		var store = Store(project);
		var clock = new FakeClock();
		long now = clock.UtcNowMs;
		Commit(store, Register("a", "Assets/"), now);
		Commit(store, Register("b", "Assets/"), now);
		var a = Commit(store, EditBegin("a", "a", 15), now);
		var b = Commit(store, EditBegin("b", "b", 15), now);
		var engine = new CoordinationEngine();
		var snapshot = store.Read();
		engine.Inspect(snapshot, "b", b.RequestId, now + 15_000);
		Expect(snapshot.Grants.Count == 0, "read shows expiry but never grants a right");
		Expect(store.Read().FindGrantBySession("a", null) != null, "inspection never commits changes");
		ExpectCode(Commit(store, Fill(Command(CoordinationEngine.OpRenew), "a", a.Token), now + 15_000),
			CoordinationCodes.StaleToken, "deadline is exclusive and late renewal cannot resurrect a writer");
		var back = store.Read();
		Expect(back.FindGrantBySession("a", null) == null && back.FindGrantBySession("b", null) != null,
			"expiry and next grant persist even when renewal fails");
		Expect(back.FindGrantBySession("b", null).DeadlineMs == now + 30_000, "queue wait does not consume lease time");
		ExpectCode(Commit(store, EditBegin("a", "a", 15), now + 15_000), CoordinationCodes.AlreadyClosed,
			"old request UUID cannot resurrect expired permission");
		ExpectCode(Commit(store, Fill(Command(CoordinationEngine.OpEditEnd), "a", a.Token), now + 15_000),
			CoordinationCodes.AlreadyClosed, "late cleanup is idempotent");
		ExpectCode(Commit(store, EditBegin("a", "new-a", 15), now + 15_000), CoordinationCodes.Waiting,
			"returning agent requests a new lease behind the new holder");
	}

	private static void LegacyStateMigration(string root)
	{
		var project = Path.Combine(root, "lease-migration");
		Directory.CreateDirectory(project);
		var store = Store(project);
		long now = new FakeClock().UtcNowMs;
		Commit(store, Register("old", "Assets/"), now);
		var old = Commit(store, EditBegin("old", "old"), now);
		using (var tx = store.BeginWithRetry(1000))
		{
			tx.State.SchemaVersion = 1;
			tx.State.FindGrantBySession("old", null).State = CoordinationLimits.GrantOrphaned;
			tx.Commit();
		}
		var legacy = store.Read();
		Expect(legacy.SchemaVersion == 1, "legacy state can be read without migrating on observation");
		var epoch = legacy.Epoch;
		ExpectCode(Commit(store, Register("new", "Assets/"), now), CoordinationCodes.Ok,
			"legacy orphan and idle reservation do not need manual abandon");
		var migrated = store.Read();
		Expect(migrated.SchemaVersion == 2 && migrated.Epoch == epoch, "atomic migration preserves identity and fences old schedulers");
		Expect(migrated.FindGrantBySession("old", null) == null, "persisted legacy orphan is released");
		Expect(migrated.FindParticipant("old").Paths.Length == 1, "migration preserves the declaration");
		ExpectCode(Commit(store, Fill(Command(CoordinationEngine.OpRenew), "old", old.Token), now),
			CoordinationCodes.StaleToken, "legacy orphan token cannot renew");
		ExpectCode(Commit(store, EditBegin("new", "new"), now), CoordinationCodes.Granted, "migration restores useful work");

		var engine = new CoordinationEngine();
		var running = NewState();
		engine.Apply(running, Register("runner", "Assets/"), now);
		engine.Apply(running, WindowRequest("runner", "window"), now);
		var window = engine.Apply(running, Command(CoordinationEngine.OpWindowConfirm), now);
		var begin = Fill(Command(CoordinationEngine.OpStepBegin), "runner", window.Token);
		begin.StepId = "V1"; begin.TaskId = "Task_running"; begin.Actual = ActualTests("V1");
		ExpectCode(engine.Apply(running, begin, now), CoordinationCodes.Ok, "running task starts before migration");
		running.SchemaVersion = 1;
		engine.Apply(running, Register("new", "Assets/"), now + 1000);
		Expect(running.SchemaVersion == 2 && running.FindWindowGrant().Token == window.Token
			&& running.FindWindowGrant().ActiveTaskIds.Contains("Task_running"), "migration preserves running task and window identity");
		ExpectCode(engine.Apply(running, EditBegin("new", "after-upgrade"), now + 1000), CoordinationCodes.Waiting,
			"migration does not free files under a running Unity task");
	}

	public static void CrossProcess(string root, List<string> covered)
	{
		var project = Path.Combine(root, "lease-race");
		Directory.CreateDirectory(project);
		using var a = Child.Start("edit", project, "a");
		using var b = Child.Start("edit", project, "b");
		Expect(a.WaitForExit(20_000) && b.WaitForExit(20_000), "both racing clients complete");
		Expect(a.ExitCode == 0 && b.ExitCode == 0, "both racing clients succeed");
		var store = Store(project);
		var state = store.Read();
		Expect(state.Participants.Count == 2 && state.Grants.Count == 1, "two processes can register overlap but only one writes");
		Expect(state.Requests.Count(r => r.State == CoordinationLimits.StateWaiting) == 1, "losing writer remains queued");
		var first = state.Grants[0];
		Commit(store, Fill(Command(CoordinationEngine.OpEditEnd), first.Session, first.Token), CoordinationSystemClock.Instance.UtcNowMs);
		state = store.Read();
		Expect(state.Grants.Count == 1 && state.Grants[0].Session != first.Session, "transactional release hands off to the waiting process");
		covered.Add("L05: cross-process overlapping writer race");
	}
}
