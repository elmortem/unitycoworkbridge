using System.Text;
using System.Text.Json;
using AgentBridge.Cli;
using AgentBridge.Coordination;
using static AgentBridge.Coordination.Tests.Harness;

namespace AgentBridge.Coordination.Tests;

internal static class Scenarios
{
	// C01 — two disjoint scopes and their edit grants coexist; an overlapping scope is refused
	// without touching anything that was already reserved.
	public static void C01_ScopeReservation(List<string> covered)
	{
		var engine = new CoordinationEngine();
		var clock = new FakeClock();
		var state = NewState();

		ExpectCode(engine.Apply(state, Register("a", "Game/Core/"), clock.UtcNowMs), CoordinationCodes.Ok, "A must register");
		ExpectCode(engine.Apply(state, Register("b", "Game/Ui/"), clock.UtcNowMs), CoordinationCodes.Ok, "B must register");

		var grantA = engine.Apply(state, EditBegin("a", "u-a1"), clock.UtcNowMs);
		var grantB = engine.Apply(state, EditBegin("b", "u-b1"), clock.UtcNowMs);
		ExpectCode(grantA, CoordinationCodes.Granted, "disjoint writers run at the same time");
		ExpectCode(grantB, CoordinationCodes.Granted, "disjoint writers run at the same time");
		Expect(grantA.Token != grantB.Token && grantA.Token.Length > 0, "each writer gets its own token");

		var revisionBefore = state.Revision;
		var conflict = engine.Apply(state, Register("c", "Game/Core/Deep/"), clock.UtcNowMs);
		ExpectCode(conflict, CoordinationCodes.ScopeConflict, "a nested path is a conflict");
		Expect(state.FindParticipant("c") == null, "a refused registration must not be created");
		Expect(state.Revision == revisionBefore, "a refused registration must not move the revision");

		// Segment boundaries: Foo/ does not contain Foobar/.
		ExpectCode(
			engine.Apply(state, Register("d", "Game/Corefiles/"), clock.UtcNowMs),
			CoordinationCodes.Ok,
			"a sibling directory with a shared prefix is not an overlap");

		Expect(!CoordinationScope.Overlaps("Foo/", "Foobar/"), "Foo/ must not cover Foobar/");
		Expect(CoordinationScope.Overlaps("Foo/", "Foo/bar.cs"), "Foo/ must cover Foo/bar.cs");
		Expect(CoordinationScope.Overlaps("Docs/A.md", "Docs/A.md"), "the same file is an overlap");

		string[] normalized;
		string error;
		Expect(!CoordinationScope.TryNormalize(new[] { "../outside/" }, out normalized, out error), "escaping the root is refused");
		Expect(!CoordinationScope.TryNormalize(new[] { "Game/**/*.cs" }, out normalized, out error), "globs are refused");
		Expect(!CoordinationScope.TryNormalize(new[] { "Game/", "Game/Core/" }, out normalized, out error), "a self-overlapping scope is refused");

		covered.Add("C01");
	}

	// C02 — a window waits for every writer, younger edits wait for the window, and only then does
	// the editor confirmation hand the window over.
	public static void C02_WindowWaitsForWriters(List<string> covered)
	{
		var engine = new CoordinationEngine();
		var clock = new FakeClock();
		var state = NewState();

		engine.Apply(state, Register("a", "Game/Core/"), clock.UtcNowMs);
		engine.Apply(state, Register("b", "Game/Ui/"), clock.UtcNowMs);

		var writerB = engine.Apply(state, EditBegin("b", "u-b1"), clock.UtcNowMs);
		ExpectCode(writerB, CoordinationCodes.Granted, "B writes first");

		var windowA = engine.Apply(state, WindowRequest("a", "u-a-window"), clock.UtcNowMs);
		ExpectCode(windowA, CoordinationCodes.Waiting, "a window queues behind the live writer");

		var paused = engine.Apply(state, Command(CoordinationEngine.OpRenew) is var renew
			? Fill(renew, "b", writerB.Token)
			: null, clock.UtcNowMs);
		ExpectCode(paused, CoordinationCodes.PauseRequested, "a waiting window pauses the writer");

		var refused = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs);
		ExpectCode(refused, CoordinationCodes.EditActive, "the editor must not confirm over a live writer");

		// A younger edit does not overtake the window.
		var lateEdit = engine.Apply(state, EditBegin("a", "u-a-edit"), clock.UtcNowMs);
		ExpectCode(lateEdit, CoordinationCodes.RequestActive, "one pending request per session");

		var endB = engine.Apply(state, Fill(Command(CoordinationEngine.OpEditEnd), "b", writerB.Token), clock.UtcNowMs);
		ExpectCode(endB, CoordinationCodes.Ok, "B closes its package");

		var confirmed = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs);
		ExpectCode(confirmed, CoordinationCodes.Granted, "only now does A get the window");
		Expect(confirmed.Session == "a", "the window goes to the head of the queue");
		Expect(state.FindWindowGrant() != null, "the window grant exists");

		// While the window is open, a new writer queues instead of racing it.
		var writerDuringWindow = engine.Apply(state, EditBegin("b", "u-b2"), clock.UtcNowMs);
		ExpectCode(writerDuringWindow, CoordinationCodes.Waiting, "a window and a file edit never coexist");
		Expect(state.FindGrantBySession("b", null) == null, "no edit grant is issued during a window");

		// Requesting a window while holding an edit grant is self-deadlock, and is refused.
		var state2 = NewState();
		engine.Apply(state2, Register("z", "Game/Z/"), clock.UtcNowMs);
		engine.Apply(state2, EditBegin("z", "u-z"), clock.UtcNowMs);
		ExpectCode(
			engine.Apply(state2, WindowRequest("z", "u-z-window"), clock.UtcNowMs),
			CoordinationCodes.EditActive,
			"a window cannot be requested while holding an edit grant");

		covered.Add("C02");
	}

	// C03 — replaying a uuid returns the same request, a different payload under the same uuid is a
	// conflict, and a consumed step refuses a different task id.
	public static void C03_Idempotency(List<string> covered)
	{
		var engine = new CoordinationEngine();
		var clock = new FakeClock();
		var state = NewState();

		engine.Apply(state, Register("a", "Game/Core/"), clock.UtcNowMs);

		var first = engine.Apply(state, EditBegin("a", "u-1", 120), clock.UtcNowMs);
		var replay = engine.Apply(state, EditBegin("a", "u-1", 120), clock.UtcNowMs);
		Expect(replay.RequestId == first.RequestId, "the same uuid returns the same request");
		Expect(replay.Token == first.Token, "the same uuid returns the same token");
		Expect(!replay.Changed, "a replay changes nothing");

		var conflict = engine.Apply(state, EditBegin("a", "u-1", 300), clock.UtcNowMs);
		ExpectCode(conflict, CoordinationCodes.RequestConflict, "the same uuid with another payload is a conflict");

		engine.Apply(state, Fill(Command(CoordinationEngine.OpEditEnd), "a", first.Token), clock.UtcNowMs);

		var window = engine.Apply(state, WindowRequest("a", "u-w", CoordinationLimits.KindValidation, 120, TestsPlan("V1", "V2")), clock.UtcNowMs);
		Expect(window.State == CoordinationLimits.StateWaiting, "the window queues");
		var granted = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs);
		var token = granted.Token;

		var step = StepCommand(CoordinationEngine.OpStepBegin, "a", token, "V1", "Task_1");
		ExpectCode(engine.Apply(state, step, clock.UtcNowMs), CoordinationCodes.Ok, "the first step is reserved");

		var sameTask = StepCommand(CoordinationEngine.OpStepBegin, "a", token, "V1", "Task_1");
		ExpectCode(engine.Apply(state, sameTask, clock.UtcNowMs), CoordinationCodes.StepAttached,
			"the same task id rejoins its own reservation after a reload");

		var otherTask = StepCommand(CoordinationEngine.OpStepBegin, "a", token, "V1", "Task_2");
		ExpectCode(engine.Apply(state, otherTask, clock.UtcNowMs), CoordinationCodes.StepConsumed,
			"a different task id must not run a consumed step");

		var unknown = StepCommand(CoordinationEngine.OpStepBegin, "a", token, "V9", "Task_3");
		ExpectCode(engine.Apply(state, unknown, clock.UtcNowMs), CoordinationCodes.StepUnknown,
			"a step outside the plan is not authorised");

		// A token without a matching payload authorises nothing.
		var mismatch = StepCommand(CoordinationEngine.OpStepBegin, "a", token, "V2", "Task_4");
		mismatch.Actual.Tests = new[] { "Suite.Something.Else" };
		ExpectCode(engine.Apply(state, mismatch, clock.UtcNowMs), CoordinationCodes.StepMismatch,
			"the actual filter must match the planned one");

		covered.Add("C03");
	}

	// C06 — an expired writer becomes orphaned and blocks the next window until it confirms; a token
	// from before an abandon is refused afterwards.
	public static void C06_OrphanedWriterAndAbandon(List<string> covered)
	{
		var engine = new CoordinationEngine();
		var clock = new FakeClock();
		var state = NewState();

		engine.Apply(state, Register("a", "Game/Core/"), clock.UtcNowMs);
		engine.Apply(state, Register("b", "Game/Ui/"), clock.UtcNowMs);
		var writer = engine.Apply(state, EditBegin("a", "u-a", 15), clock.UtcNowMs);
		engine.Apply(state, WindowRequest("b", "u-b-window"), clock.UtcNowMs);

		clock.Advance(20_000);
		var confirm = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs);
		ExpectCode(confirm, CoordinationCodes.OrphanedWriter, "an expired writer is orphaned, not free");
		Expect(state.FindGrantBySession("a", null).State == CoordinationLimits.GrantOrphaned, "the grant is orphaned");
		Expect(state.FindParticipant("a").Lifecycle == CoordinationLimits.LifecycleOrphaned, "the participant is orphaned");

		var renew = engine.Apply(state, Fill(Command(CoordinationEngine.OpRenew), "a", writer.Token), clock.UtcNowMs);
		ExpectCode(renew, CoordinationCodes.OrphanedWriter, "an orphaned grant is not renewed");

		// Only edit-end accepts an orphaned token, and only as the confirmation that writers stopped.
		var close = engine.Apply(state, Fill(Command(CoordinationEngine.OpEditEnd), "a", writer.Token), clock.UtcNowMs);
		ExpectCode(close, CoordinationCodes.Ok, "edit-end accepts the owner's orphaned token");
		var repeat = engine.Apply(state, Fill(Command(CoordinationEngine.OpEditEnd), "a", writer.Token), clock.UtcNowMs);
		ExpectCode(repeat, CoordinationCodes.AlreadyClosed, "edit-end is idempotent");

		ExpectCode(engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs),
			CoordinationCodes.Granted, "the window is free once the writer confirmed");

		// abandon: a deliberate recovery of one session, not a way around the queue.
		var abandonState = NewState();
		engine.Apply(abandonState, Register("a", "Game/Core/"), clock.UtcNowMs);
		var doomed = engine.Apply(abandonState, EditBegin("a", "u-x", 15), clock.UtcNowMs);
		clock.Advance(20_000);

		var noReason = Command(CoordinationEngine.OpAbandon);
		noReason.TargetSession = "a";
		ExpectCode(engine.Apply(abandonState, noReason, clock.UtcNowMs), CoordinationCodes.BadUsage,
			"abandon requires an explicit reason");

		var abandon = Command(CoordinationEngine.OpAbandon);
		abandon.TargetSession = "a";
		abandon.Reason = "the user confirmed the writers stopped";
		ExpectCode(engine.Apply(abandonState, abandon, clock.UtcNowMs), CoordinationCodes.Ok, "abandon closes the target");
		Expect(abandonState.FindParticipant("a").Generation == 2, "abandon bumps only the target generation");
		Expect(abandonState.FindGrantBySession("a", null) == null, "abandon removes the target's grant");

		var stale = engine.Apply(abandonState, Fill(Command(CoordinationEngine.OpEditEnd), "a", doomed.Token), clock.UtcNowMs);
		ExpectCode(stale, CoordinationCodes.StaleToken, "a token from before the recovery is refused");

		covered.Add("C06");
	}

	// C07 — an expired window with a live task drains instead of stopping it; an idle expired window
	// simply closes.
	public static void C07_DrainingWindow(List<string> covered)
	{
		var engine = new CoordinationEngine();
		var clock = new FakeClock();
		var state = NewState();

		engine.Apply(state, Register("a", "Game/Core/"), clock.UtcNowMs);
		engine.Apply(state, WindowRequest("a", "u-w", CoordinationLimits.KindValidation, 15, TestsPlan("V1", "V2")), clock.UtcNowMs);
		var granted = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs);
		var token = granted.Token;

		engine.Apply(state, StepCommand(CoordinationEngine.OpStepBegin, "a", token, "V1", "Task_1"), clock.UtcNowMs);

		clock.Advance(20_000);
		var afterDeadline = engine.Apply(state, StepCommand(CoordinationEngine.OpStepBegin, "a", token, "V2", "Task_2"), clock.UtcNowMs);
		ExpectCode(afterDeadline, CoordinationCodes.WindowDraining, "a drained window takes no new steps");
		Expect(state.FindWindowGrant() != null, "the deadline does not free the editor under a running task");
		Expect(state.FindWindowGrant().State == CoordinationLimits.GrantDraining, "the window is draining");

		var finish = StepCommand(CoordinationEngine.OpStepFinish, "a", token, "V1", "Task_1");
		ExpectCode(engine.Apply(state, finish, clock.UtcNowMs), CoordinationCodes.Ok, "the task reports terminal");
		Expect(state.FindWindowGrant() == null, "the window closes once its last task is terminal");

		// An idle window past its deadline needs nothing to close it.
		var idle = NewState();
		engine.Apply(idle, Register("a", "Game/Core/"), clock.UtcNowMs);
		engine.Apply(idle, WindowRequest("a", "u-idle", CoordinationLimits.KindValidation, 15), clock.UtcNowMs);
		engine.Apply(idle, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs);
		clock.Advance(20_000);
		engine.Apply(idle, Command(CoordinationEngine.OpSweep), clock.UtcNowMs);
		Expect(idle.FindWindowGrant() == null, "an idle expired window closes");

		covered.Add("C07");
	}

	// C19 — the client codec round-trips the shared state, reads what JsonUtility writes, and both
	// an unsupported schema and a stale epoch are refused.
	public static void C19_Codecs(List<string> covered)
	{
		var engine = new CoordinationEngine();
		var clock = new FakeClock();
		var state = NewState();
		engine.Apply(state, Register("a", "Game/Core/", "Docs/A.md"), clock.UtcNowMs);
		engine.Apply(state, EditBegin("a", "u-a"), clock.UtcNowMs);
		engine.Apply(state, Register("b", "Game/Ui/"), clock.UtcNowMs);
		engine.Apply(state, WindowRequest("b", "u-b", CoordinationLimits.KindEditor, 120, EditorPlan()), clock.UtcNowMs);

		var json = CoordinationJsonCodec.Instance.Serialize(state);
		var roundTripped = CoordinationJsonCodec.Instance.Deserialize(json);

		Expect(roundTripped.Revision == state.Revision, "the revision must survive serialisation");
		Expect(roundTripped.Epoch == state.Epoch, "the epoch must survive serialisation");
		Expect(roundTripped.Participants.Count == state.Participants.Count, "participants must survive");
		Expect(roundTripped.FindParticipant("a").Paths.Length == 2, "the scope must survive");
		Expect(roundTripped.Grants.Count == state.Grants.Count, "grants must survive");
		Expect(roundTripped.FindGrantBySession("a", null).Token == state.FindGrantBySession("a", null).Token,
			"tokens must survive");
		Expect(roundTripped.FindRequestByUuid("b", "u-b").Plan.Steps.Count == 1, "the plan must survive");
		Expect(roundTripped.FindRequestByUuid("b", "u-b").Plan.Steps[0].PayloadSha256 == CoordinationDigest.Sha256("frozen source"),
			"step payload hashes must survive");

		// The same document written by Unity's JsonUtility. Kept as a literal on purpose: it is the
		// contract between the two adapters, and it must not drift silently.
		const string unityDocument = """
			{
			    "SchemaVersion": 1,
			    "ProjectId": "project-under-test",
			    "Epoch": "epoch-1",
			    "Revision": 7,
			    "NextTicket": 3,
			    "NextRequestNumber": 3,
			    "EditorIncarnation": "inc-1",
			    "Participants": [
			        {
			            "Session": "a",
			            "SpecId": "spec-a",
			            "Owner": "owner/a",
			            "RepoRoot": "repo",
			            "Paths": ["Docs/A.md", "Game/Core/"],
			            "Generation": 1,
			            "Lifecycle": "writing",
			            "RegisteredAtMs": 1700000000000,
			            "LastSeenMs": 1700000000000
			        }
			    ],
			    "Requests": [
			        {
			            "Id": "R0001",
			            "Uuid": "u-a",
			            "Ticket": 1,
			            "Session": "a",
			            "Kind": "edit",
			            "PayloadDigest": "d",
			            "Token": "e-1",
			            "Plan": { "Steps": [], "ArtifactRoots": [], "FixtureRoots": [] },
			            "State": "granted",
			            "Reason": "",
			            "Seconds": 120,
			            "CreatedAtMs": 1700000000000,
			            "UpdatedAtMs": 1700000000000
			        }
			    ],
			    "Grants": [
			        {
			            "Session": "a",
			            "RequestId": "R0001",
			            "Token": "e-1",
			            "Epoch": "epoch-1",
			            "ParticipantGeneration": 1,
			            "Kind": "edit",
			            "State": "active",
			            "DeadlineMs": 1700000120000,
			            "GrantedAtMs": 1700000000000,
			            "ActiveTaskIds": [],
			            "Steps": []
			        }
			    ],
			    "Tombstones": []
			}
			""";

		var fromUnity = CoordinationJsonCodec.Instance.Deserialize(unityDocument);
		Expect(fromUnity.Revision == 7, "the client must read what the editor wrote");
		Expect(fromUnity.FindGrantByToken("e-1") != null, "a grant written by the editor must be found by token");
		Expect(fromUnity.FindParticipant("a").Paths.Length == 2, "the scope written by the editor must survive");
		Expect(fromUnity.FindRequest("R0001").State == CoordinationLimits.StateGranted, "request state must survive");

		// Unknown additive fields must not make an older client unable to read the state at all.
		var additive = unityDocument.Replace("\"Revision\": 7", "\"Revision\": 7,\n    \"SomethingNewer\": 42");
		Expect(CoordinationJsonCodec.Instance.Deserialize(additive).Revision == 7,
			"an unknown additive field must not break reading");

		var unsupported = CoordinationJsonCodec.Instance.Deserialize(unityDocument);
		unsupported.SchemaVersion = 99;
		ExpectCode(
			engine.Apply(unsupported, Command(CoordinationEngine.OpSweep), clock.UtcNowMs),
			CoordinationCodes.SchemaUnsupported,
			"an unsupported schema is refused, not guessed at");

		var staleEpoch = CoordinationJsonCodec.Instance.Deserialize(unityDocument);
		staleEpoch.Epoch = "epoch-2";
		ExpectCode(
			engine.Apply(staleEpoch, Fill(Command(CoordinationEngine.OpEditEnd), "a", "e-1"), clock.UtcNowMs),
			CoordinationCodes.StaleToken,
			"a token from another epoch is refused");

		covered.Add("C19");
	}

	// C20 — the client going away changes nothing: the window survives until its task is terminal.
	public static void C20_RunningTaskOutlivesTheClient(List<string> covered)
	{
		var engine = new CoordinationEngine();
		var clock = new FakeClock();
		var state = NewState();

		engine.Apply(state, Register("a", "Game/Core/"), clock.UtcNowMs);
		engine.Apply(state, WindowRequest("a", "u-w", CoordinationLimits.KindValidation, 120, TestsPlan("V1")), clock.UtcNowMs);
		var granted = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), clock.UtcNowMs);
		engine.Apply(state, StepCommand(CoordinationEngine.OpStepBegin, "a", granted.Token, "V1", "Task_1"), clock.UtcNowMs);

		var finish = engine.Apply(state, Fill(Command(CoordinationEngine.OpFinish), "a", granted.Token), clock.UtcNowMs);
		ExpectCode(finish, CoordinationCodes.Draining, "finish under a running task drains, it does not abort");
		Expect(state.FindWindowGrant() != null, "the window is not released before its task");

		// Reading the state does not release anything either.
		var inspected = engine.Inspect(state, "a", "", clock.UtcNowMs);
		Expect(!inspected.Changed, "an inspection never mutates");
		Expect(state.FindWindowGrant() != null, "an inspection never releases the window");

		engine.Apply(state, StepCommand(CoordinationEngine.OpStepFinish, "a", granted.Token, "V1", "Task_1"), clock.UtcNowMs);
		Expect(state.FindWindowGrant() == null, "the window is released once the task is terminal");

		covered.Add("C20");
	}

	// C12 — the input digest follows content, not size and timestamps.
	public static void C12_InputDigest(string root, List<string> covered)
	{
		var project = Path.Combine(root, "digest");
		var assets = Path.Combine(project, "Assets");
		var settings = Path.Combine(project, "ProjectSettings");
		var temp = Path.Combine(project, "Temp");
		Directory.CreateDirectory(Path.Combine(assets, "Scripts"));
		Directory.CreateDirectory(settings);
		Directory.CreateDirectory(temp);

		var script = Path.Combine(assets, "Scripts", "Player.cs");
		File.WriteAllText(script, "class Player { int a; }");
		File.WriteAllText(script + ".meta", "guid: 1111");
		File.WriteAllText(Path.Combine(settings, "ProjectSettings.asset"), "settings: 1");

		var roots = new[] { assets, settings };
		var excluded = new[] { temp };
		var before = ValidationInputSnapshot.Capture(roots, excluded, "ctx");
		Expect(before.Complete, "a readable project must produce a complete snapshot");
		Expect(before.FileCount == 3, "every input file counts, including .meta");

		// Same length, and the write time put back exactly where it was.
		var stamp = File.GetLastWriteTimeUtc(script);
		File.WriteAllText(script, "class Player { int b; }");
		File.SetLastWriteTimeUtc(script, stamp);
		Expect(new FileInfo(script).Length == 23, "the test edit must keep the file length");

		var afterEdit = ValidationInputSnapshot.Capture(roots, excluded, "ctx");
		Expect(afterEdit.Digest != before.Digest, "a content edit with a restored timestamp must move the digest");

		// A move is a change, even though every byte still exists somewhere.
		File.Move(script, Path.Combine(assets, "Scripts", "Player2.cs"));
		var afterMove = ValidationInputSnapshot.Capture(roots, excluded, "ctx");
		Expect(afterMove.Digest != afterEdit.Digest, "moving a file must move the digest");

		// A .meta on its own is an input.
		File.WriteAllText(Path.Combine(assets, "Scripts", "Player2.cs.meta"), "guid: 2222");
		var afterMeta = ValidationInputSnapshot.Capture(roots, excluded, "ctx");
		Expect(afterMeta.Digest != afterMove.Digest, "a .meta change must move the digest");

		// A local package outside the project is an input too.
		var package = Path.Combine(root, "local-package");
		Directory.CreateDirectory(package);
		File.WriteAllText(Path.Combine(package, "package.json"), "{\"version\":\"1.0.0\"}");
		var withPackage = new[] { assets, settings, package };
		var packageDigest = ValidationInputSnapshot.Capture(withPackage, excluded, "ctx");
		File.WriteAllText(Path.Combine(package, "package.json"), "{\"version\":\"1.0.1\"}");
		Expect(
			ValidationInputSnapshot.Capture(withPackage, excluded, "ctx").Digest != packageDigest.Digest,
			"a resolved local package is part of the inputs");

		// Editor output is not.
		File.WriteAllText(Path.Combine(temp, "artifact.png"), "not an input");
		Expect(
			ValidationInputSnapshot.Capture(roots, excluded, "ctx").Digest == afterMeta.Digest,
			"an excluded Temp artifact must not move the digest");

		// The build target and the package version are part of the claim.
		Expect(
			ValidationInputSnapshot.Capture(roots, excluded, "other-context").Digest != afterMeta.Digest,
			"the context is part of the digest");

		// An unreachable root is unknown, never an optimistic partial hash.
		var missing = ValidationInputSnapshot.Capture(new[] { Path.Combine(root, "nope") }, excluded, "ctx");
		Expect(!missing.Complete, "an unreachable input root makes the snapshot unknown");
		Expect(missing.Reason.Length > 0, "an unknown snapshot says why");

		covered.Add("C12");
	}

	// C13 — the observer is the second witness: it catches a change that was reverted, and it does
	// not fire for editor output.
	public static void C13_ObserverVerdict(string root, List<string> covered)
	{
		var project = Path.Combine(root, "observe");
		var assets = Path.Combine(project, "Assets");
		var temp = Path.Combine(project, "Temp");
		Directory.CreateDirectory(assets);
		Directory.CreateDirectory(temp);

		var script = Path.Combine(assets, "Thing.cs");
		File.WriteAllText(script, "original");
		var before = ValidationInputSnapshot.Capture(new[] { assets }, new[] { temp }, "ctx");

		using (var monitor = new ValidationInputMonitor(new[] { assets, temp }, new[] { temp }))
		{
			Expect(monitor.Observed, "a monitor on a real directory observes");

			File.WriteAllText(script, "changed");
			Expect(WaitUntil(() => monitor.EventCount > 0, 5000), "the observer must see a change");
			File.WriteAllText(script, "original");

			var after = ValidationInputSnapshot.Capture(new[] { assets }, new[] { temp }, "ctx");
			Expect(after.Digest == before.Digest, "a change that was reverted leaves the digest equal");
			Expect(monitor.EventCount > 0, "and the observer is what turns it into a stale verdict");

			var eventsBeforeTemp = monitor.EventCount;
			File.WriteAllText(Path.Combine(temp, "AgentBridge.log"), "ordinary editor output");
			Thread.Sleep(400);
			Expect(monitor.EventCount == eventsBeforeTemp, "an excluded artifact must not invalidate the evidence");
		}

		using (var blind = new ValidationInputMonitor(new[] { Path.Combine(root, "gone") }, Array.Empty<string>()))
		{
			Expect(!blind.Observed, "a root that cannot be observed is not claimed as observed");
		}

		// The bridge's own declared scratch is not a foreign change. The Unity Test Framework
		// creates Assets/InitTestScene*.unity for a PlayMode run and the bridge deletes it again;
		// counting that create-then-delete would make every PlayMode validation randomly stale.
		Func<string, bool> ignoreScratch = path =>
			ValidationInputSnapshot.Normalize(path).EndsWith("/InitTestScene.unity", StringComparison.OrdinalIgnoreCase);

		var scratch = Path.Combine(assets, "InitTestScene.unity");
		ValidationInputSnapshot withoutScratch = ValidationInputSnapshot.Capture(
			new[] { assets }, new[] { temp }, "ctx", ignoreScratch);
		File.WriteAllText(scratch, "temporary bootstrap scene");

		using (var monitor = new ValidationInputMonitor(new[] { assets }, new[] { temp }, ignoreScratch))
		{
			Expect(
				ValidationInputSnapshot.Capture(new[] { assets }, new[] { temp }, "ctx", ignoreScratch).Digest
					== withoutScratch.Digest,
				"the bridge's own temporary scene must not enter the digest");

			File.Delete(scratch);
			Thread.Sleep(400);
			Expect(monitor.EventCount == 0, "creating and deleting the bridge's own scratch is not a foreign change");
		}

		// An ordinary asset with a similar name is still an input.
		var lookalike = Path.Combine(assets, "InitTestSceneHelper.cs");
		File.WriteAllText(lookalike, "class InitTestSceneHelper {}");
		Expect(
			ValidationInputSnapshot.Capture(new[] { assets }, new[] { temp }, "ctx", ignoreScratch).Digest
				!= withoutScratch.Digest,
			"only the declared scratch is ignored, not everything that looks like it");

		covered.Add("C13");
	}

	// C18 — the index keeps 32 completed sets, evicting the least recently used.
	public static void C18_CacheEviction(List<string> covered)
	{
		var index = new TestCacheIndex();
		for (var i = 0; i < 33; i++)
		{
			index.Entries.Add(new TestCacheEntryInfo
			{
				Id = "entry-" + i,
				TestMode = "EditMode",
				InputDigest = "digest-" + i,
				Validity = "valid",
				LastUsedMs = 1000 + i
			});
		}

		var evicted = index.Trim();
		Expect(index.Entries.Count == TestCacheIndex.MaxEntries, "the index keeps exactly 32 sets");
		Expect(evicted.Count == 1 && evicted[0] == "entry-0", "the least recently used set is evicted");
		Expect(index.Find("entry-32") != null, "the newest set survives");
		Expect(index.Find("entry-0") == null, "the evicted set is gone from the index");

		// Touching an old entry saves it from the next eviction.
		index.Find("entry-1").LastUsedMs = 99_999;
		index.Entries.Add(new TestCacheEntryInfo { Id = "entry-33", LastUsedMs = 1100 });
		var second = index.Trim();
		Expect(!second.Contains("entry-1"), "a recently used set is not evicted");
		Expect(index.Entries.Count == TestCacheIndex.MaxEntries, "the cap holds");

		covered.Add("C18");
	}

	// C04 — two processes ask at the same time; at most one window exists and no revision is lost.
	public static void C04_TwoProcessesOneWindow(string root, List<string> covered)
	{
		var project = Path.Combine(root, "concurrent");
		Directory.CreateDirectory(project);

		var store = Store(project);
		var now = CoordinationSystemClock.Instance.UtcNowMs;
		Commit(store, Register("seed", "Game/Seed/"), now);

		var first = Child.Start("window", project, "alpha");
		var second = Child.Start("window", project, "beta");
		first.WaitForExit(60_000);
		second.WaitForExit(60_000);

		var firstOutput = first.StandardOutput.ReadToEnd().Trim();
		var secondOutput = second.StandardOutput.ReadToEnd().Trim();
		Expect(first.ExitCode == 0 && second.ExitCode == 0,
			"both children must finish cleanly: " + firstOutput + " | " + secondOutput + " | "
			+ first.StandardError.ReadToEnd() + second.StandardError.ReadToEnd());

		var state = store.Read();
		var windows = 0;
		foreach (var grant in state.Grants)
		{
			if (CoordinationLimits.IsWindowKind(grant.Kind))
			{
				windows++;
			}
		}

		Expect(windows <= 1, "two processes must never both hold a window");
		Expect(state.FindParticipant("alpha") != null && state.FindParticipant("beta") != null,
			"both registrations survive the race");
		Expect(state.Requests.Count == 2, "no request is lost in the race");
		Expect(state.Revision >= 4, "every committed change moves the revision: " + state.Revision);

		var grantedCount = (firstOutput.Contains(CoordinationCodes.Granted) ? 1 : 0)
			+ (secondOutput.Contains(CoordinationCodes.Granted) ? 1 : 0);
		Expect(grantedCount == 1, "exactly one confirmation wins: " + firstOutput + " / " + secondOutput);

		covered.Add("C04");
	}

	// C05 — a crash between the temporary file and the replace leaves the published state whole; a
	// damaged state is a diagnostic, never a fresh empty coordinator.
	public static void C05_CrashAndCorruption(string root, List<string> covered)
	{
		var project = Path.Combine(root, "crash");
		Directory.CreateDirectory(project);
		var store = Store(project);
		var now = CoordinationSystemClock.Instance.UtcNowMs;
		Commit(store, Register("a", "Game/Core/"), now);

		var before = store.Read();
		var coordinationRoot = CoordinationPathPolicy.CoordinationRoot(project);
		var statePath = Path.Combine(coordinationRoot, CoordinationFileStore.StateFileName);
		var published = File.ReadAllText(statePath);

		var crasher = Child.Start("crash", project, "");
		crasher.WaitForExit(30_000);
		Expect(crasher.ExitCode != 0, "the crashing child must not exit cleanly");

		Expect(File.ReadAllText(statePath) == published, "an interrupted write must not touch the published state");
		var after = store.Read();
		Expect(after.Revision == before.Revision, "the surviving snapshot is the last whole one");

		// The next transaction sweeps up only its own leftovers.
		Commit(store, Register("b", "Game/Ui/"), now);
		Expect(store.Read().FindParticipant("b") != null, "the store keeps working after an interrupted write");

		// A damaged snapshot is refused, not replaced by an empty one.
		var damaged = Path.Combine(root, "damaged");
		Directory.CreateDirectory(damaged);
		var damagedStore = Store(damaged);
		Commit(damagedStore, Register("a", "Game/Core/"), now);
		var damagedPath = Path.Combine(CoordinationPathPolicy.CoordinationRoot(damaged), CoordinationFileStore.StateFileName);
		File.WriteAllText(damagedPath, "{ this is not json");

		var corrupt = Catch(() => damagedStore.Read());
		Expect(corrupt != null && corrupt.Code == CoordinationCodes.Corrupt,
			"a damaged state must be reported as corrupt");
		var corruptWrite = Catch(() =>
		{
			ICoordinationTransaction transaction;
			damagedStore.TryBegin(out transaction);
			transaction?.Dispose();
		});
		Expect(corruptWrite != null && corruptWrite.Code == CoordinationCodes.Corrupt,
			"a damaged state must not be silently replaced by an empty coordinator");

		// A marker without its state needs an explicit decision.
		var orphaned = Path.Combine(root, "orphaned");
		Directory.CreateDirectory(orphaned);
		var orphanedStore = Store(orphaned);
		Commit(orphanedStore, Register("a", "Game/Core/"), now);
		File.Delete(Path.Combine(CoordinationPathPolicy.CoordinationRoot(orphaned), CoordinationFileStore.StateFileName));
		var recovery = Catch(() => orphanedStore.Read());
		Expect(recovery != null && recovery.Code == CoordinationCodes.RecoveryRequired,
			"a missing state with a live marker requires explicit recovery");

		// And the lock file is never removed to "fix" anything.
		Expect(File.Exists(Path.Combine(coordinationRoot, CoordinationFileStore.LockFileName)),
			"the transaction lock is persistent");

		covered.Add("C05");
	}

	// C08 — a change that lands before the watcher is installed is still seen, and an expired wait
	// cancels nothing.
	public static void C08_Waiting(string root, List<string> covered)
	{
		var project = Path.Combine(root, "waiting");
		Directory.CreateDirectory(project);
		var store = Store(project);
		var now = CoordinationSystemClock.Instance.UtcNowMs;
		Commit(store, Register("a", "Game/Core/"), now);

		var baseline = store.Read().Revision;

		// The change happens before the waiter even exists: a waiter that only trusted its watcher
		// would sit here until the timeout.
		Commit(store, Register("b", "Game/Ui/"), now);

		using (var waiter = new CoordinationWaiter(store))
		{
			var seen = waiter.Wait(baseline, TimeSpan.FromSeconds(5), CancellationToken.None);
			Expect(seen != null, "a change made before the subscription must still be reported");
			Expect(seen.Revision > baseline, "the reported snapshot is newer than the baseline");
		}

		var currentRevision = store.Read().Revision;

		using (var waiter = new CoordinationWaiter(store))
		{
			var started = DateTime.UtcNow;
			var nothing = waiter.Wait(currentRevision, TimeSpan.FromSeconds(2), CancellationToken.None);
			Expect(nothing == null, "a quiet wait expires instead of inventing a change");
			Expect((DateTime.UtcNow - started).TotalSeconds >= 1.5, "the wait honours its timeout");
			Expect(store.Read().Revision == currentRevision, "an expired wait must not move the revision");
		}

		using (var waiter = new CoordinationWaiter(store))
		{
			using var cancellation = new CancellationTokenSource();
			var pending = Task.Run(() => waiter.Wait(currentRevision, TimeSpan.FromSeconds(30), cancellation.Token));
			Thread.Sleep(200);
			cancellation.Cancel();
			Expect(pending.Wait(5000), "cancelling the client wait must return promptly");
			Expect(pending.Result == null, "a cancelled wait reports nothing");
			Expect(store.Read().Revision == currentRevision, "cancelling the client wait must not change the state");
		}

		// A real change while waiting arrives through the watcher.
		using (var waiter = new CoordinationWaiter(store))
		{
			var pending = Task.Run(() => waiter.Wait(currentRevision, TimeSpan.FromSeconds(20), CancellationToken.None));
			Thread.Sleep(300);
			Commit(store, Register("c", "Game/Audio/"), now);
			Expect(pending.Wait(20_000), "the waiter must return after a real change");
			Expect(pending.Result != null && pending.Result.Revision > currentRevision, "and report the newer revision");
		}

		covered.Add("C08");
	}

	// ---------------------------------------------------------------- helpers

	private static CoordinationCommand Fill(CoordinationCommand command, string session, string token)
	{
		command.Session = session;
		command.Token = token;
		return command;
	}

	private static CoordinationCommand StepCommand(string op, string session, string token, string stepId, string taskId)
	{
		var command = Command(op);
		command.Session = session;
		command.Token = token;
		command.StepId = stepId;
		command.TaskId = taskId;
		command.Actual = ActualTests(stepId);
		return command;
	}

	private static CoordinationPlan EditorPlan()
	{
		var plan = new CoordinationPlan();
		plan.Steps.Add(new CoordinationStep
		{
			Id = "E1",
			Kind = "csharp",
			Payload = "frozen source", PayloadName = "Task_Example",
			PayloadSha256 = CoordinationDigest.Sha256("frozen source")
		});
		plan.ArtifactRoots = new[] { "Temp/AgentBridge/Artifacts/" };
		return plan;
	}

	private static CoordinationStoreException Catch(Action action)
	{
		try
		{
			action();
			return null;
		}
		catch (CoordinationStoreException exception)
		{
			return exception;
		}
	}
}
