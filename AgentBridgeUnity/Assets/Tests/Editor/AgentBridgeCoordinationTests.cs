using System;
using System.IO;
using AgentBridge;
using AgentBridge.Coordination;
using NUnit.Framework;

// The editor half of coordination-v1 against the real Unity APIs: JsonUtility as the codec, the
// real file store, and the path policy applied to this actual project.
public class AgentBridgeCoordinationTests
{
	private string _root;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "AgentBridgeCoordUnity_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, true);
			}
		}
		catch (IOException)
		{
		}
	}

	[Test]
	public void UnityCodecRoundTripsTheWholeState()
	{
		var engine = new CoordinationEngine();
		CoordinationState state = NewState();
		long now = CoordinationSystemClock.Instance.UtcNowMs;

		engine.Apply(state, Register("a", "Game/Core/", "Docs/A.md"), now);
		CoordinationReply grant = engine.Apply(state, EditBegin("a", "u-a"), now);
		engine.Apply(state, Register("b", "Game/Ui/"), now);
		engine.Apply(state, WindowRequest("b", "u-b"), now);

		string json = CoordinationUnityCodec.Instance.Serialize(state);
		CoordinationState back = CoordinationUnityCodec.Instance.Deserialize(json);

		Assert.AreEqual(state.Revision, back.Revision, "the revision must survive JsonUtility");
		Assert.AreEqual(state.Epoch, back.Epoch, "the epoch must survive JsonUtility");
		Assert.AreEqual(2, back.Participants.Count, "participants must survive");
		Assert.AreEqual(2, back.FindParticipant("a").Paths.Length, "the scope must survive");
		Assert.AreEqual(grant.Token, back.FindGrantBySession("a", null).Token, "the token must survive");
		Assert.AreEqual(1, back.FindRequestByUuid("b", "u-b").Plan.Steps.Count, "the plan must survive");
		Assert.AreEqual("Suite.V1", back.FindRequestByUuid("b", "u-b").Plan.Steps[0].Tests[0], "step filters must survive");
	}

	[Test]
	public void UnityCodecReadsWhatTheClientWrote()
	{
		// Written by System.Text.Json in the CLI. Kept as a literal because it is the contract
		// between the two adapters, and it must not drift without a failing test.
		const string clientDocument = @"{
  ""SchemaVersion"": 1,
  ""ProjectId"": ""project-under-test"",
  ""Epoch"": ""epoch-1"",
  ""Revision"": 11,
  ""NextTicket"": 2,
  ""NextRequestNumber"": 2,
  ""EditorIncarnation"": """",
  ""Participants"": [
    {
      ""Session"": ""a"",
      ""SpecId"": ""spec-a"",
      ""Owner"": ""owner/a"",
      ""RepoRoot"": ""repo"",
      ""Paths"": [ ""Docs/A.md"", ""Game/Core/"" ],
      ""Generation"": 1,
      ""Lifecycle"": ""writing"",
      ""RegisteredAtMs"": 1700000000000,
      ""LastSeenMs"": 1700000000000
    }
  ],
  ""Requests"": [
    {
      ""Id"": ""R0001"",
      ""Uuid"": ""u-a"",
      ""Ticket"": 1,
      ""Session"": ""a"",
      ""Kind"": ""edit"",
      ""PayloadDigest"": ""d"",
      ""Token"": ""e-1"",
      ""Plan"": { ""Steps"": [], ""ArtifactRoots"": [], ""FixtureRoots"": [] },
      ""State"": ""granted"",
      ""Reason"": """",
      ""Seconds"": 120,
      ""CreatedAtMs"": 1700000000000,
      ""UpdatedAtMs"": 1700000000000
    }
  ],
  ""Grants"": [
    {
      ""Session"": ""a"",
      ""RequestId"": ""R0001"",
      ""Token"": ""e-1"",
      ""Epoch"": ""epoch-1"",
      ""ParticipantGeneration"": 1,
      ""Kind"": ""edit"",
      ""State"": ""active"",
      ""DeadlineMs"": 1700000120000,
      ""GrantedAtMs"": 1700000000000,
      ""ActiveTaskIds"": [],
      ""Steps"": []
    }
  ],
  ""Tombstones"": []
}";

		CoordinationState state = CoordinationUnityCodec.Instance.Deserialize(clientDocument);
		Assert.AreEqual(11, state.Revision, "the editor must read what the client wrote");
		Assert.IsNotNull(state.FindGrantByToken("e-1"), "a grant written by the client must be found by token");
		Assert.AreEqual(2, state.FindParticipant("a").Paths.Length, "the client's scope must survive");
		Assert.AreEqual(CoordinationLimits.StateGranted, state.FindRequest("R0001").State, "request state must survive");
	}

	[Test]
	public void FileStoreSerialisesTransactionsAndSurvivesReopening()
	{
		CoordinationFileStore store = NewStore();
		long now = CoordinationSystemClock.Instance.UtcNowMs;

		CoordinationReply registered = Commit(store, Register("a", "Game/Core/"), now);
		Assert.IsTrue(registered.Ok, "register must commit");

		// A second transaction cannot be opened while the first holds the lock.
		ICoordinationTransaction outer;
		Assert.IsTrue(store.TryBegin(out outer), "the first transaction gets the lock");
		using (outer)
		{
			ICoordinationTransaction inner;
			Assert.IsFalse(store.TryBegin(out inner), "the lock is exclusive while it is held");
		}

		CoordinationFileStore reopened = NewStore();
		CoordinationState state = reopened.Read();
		Assert.IsNotNull(state.FindParticipant("a"), "a committed registration survives reopening");
		Assert.IsTrue(File.Exists(Path.Combine(store.Root, CoordinationFileStore.MarkerFileName)),
			"the marker is written next to the state");
	}

	[Test]
	public void EditorRestartInterruptsTheWindowButKeepsRegistrationsAndEdits()
	{
		var engine = new CoordinationEngine();
		CoordinationState state = NewState();
		long now = CoordinationSystemClock.Instance.UtcNowMs;

		engine.Apply(state, Register("a", "Game/Core/"), now);
		engine.Apply(state, Register("b", "Game/Ui/"), now);
		CoordinationCommand first = Command(CoordinationEngine.OpIncarnation);
		first.Nonce = "editor-1";
		engine.Apply(state, first, now);

		engine.Apply(state, WindowRequest("a", "u-a"), now);
		CoordinationReply granted = engine.Apply(state, Command(CoordinationEngine.OpWindowConfirm), now);
		engine.Apply(state, StepBegin("a", granted.Token, "V1", "Task_1"), now);

		// The same incarnation is an ordinary domain reload: the window and its step survive.
		CoordinationCommand sameProcess = Command(CoordinationEngine.OpIncarnation);
		sameProcess.Nonce = "editor-1";
		engine.Apply(state, sameProcess, now);
		Assert.IsNotNull(state.FindWindowGrant(), "a domain reload must not invalidate the window");
		Assert.AreEqual(CoordinationLimits.GrantActive, state.FindWindowGrant().State, "the window stays active");

		CoordinationReply rejoin = engine.Apply(state, StepBegin("a", granted.Token, "V1", "Task_1"), now);
		Assert.AreEqual(CoordinationCodes.StepAttached, rejoin.Code, "the same task rejoins after the reload");

		// A new process is a restart: the window is interrupted until its recorded task is resolved.
		CoordinationCommand restarted = Command(CoordinationEngine.OpIncarnation);
		restarted.Nonce = "editor-2";
		engine.Apply(state, restarted, now);
		Assert.AreEqual(CoordinationLimits.GrantInterrupted, state.FindWindowGrant().State,
			"a restart interrupts the window");
		Assert.IsNotNull(state.FindParticipant("a"), "a restart keeps registrations");
		Assert.AreEqual(1, state.FindParticipant("a").Paths.Length, "a restart keeps scopes");

		CoordinationCommand fail = Command(CoordinationEngine.OpStepFail);
		fail.Session = "a";
		fail.Token = granted.Token;
		fail.StepId = "V1";
		fail.TaskId = "Task_1";
		fail.Reason = "editor_restart";
		engine.Apply(state, fail, now);
		Assert.IsNull(state.FindWindowGrant(), "the interrupted window closes once its task is resolved");
	}

	[Test]
	public void PlanRulesRefuseWhatTheWindowKindDoesNotAllow()
	{
		string error;
		var validation = new CoordinationPlan();
		validation.Steps.Add(new CoordinationStep { Id = "V1", Kind = "csharp", PayloadSha256 = "abc" });
		Assert.IsFalse(
			CoordinationPlanRules.Validate(validation, CoordinationLimits.KindValidation, out error),
			"a validation window must not run editor scripts");

		var play = new CoordinationPlan();
		play.Steps.Add(new CoordinationStep { Id = "V1", Kind = "play" });
		Assert.IsFalse(CoordinationPlanRules.Validate(play, CoordinationLimits.KindEditor, out error),
			"play mode is never part of a plan");

		var emptyFilter = new CoordinationPlan();
		emptyFilter.Steps.Add(new CoordinationStep { Id = "V1", Kind = "tests", Mode = "EditMode" });
		Assert.IsFalse(CoordinationPlanRules.Validate(emptyFilter, CoordinationLimits.KindValidation, out error),
			"a test step needs an exact non-empty filter");

		var duplicate = new CoordinationPlan();
		duplicate.Steps.Add(new CoordinationStep { Id = "V1", Kind = "compile" });
		duplicate.Steps.Add(new CoordinationStep { Id = "V1", Kind = "compile" });
		Assert.IsFalse(CoordinationPlanRules.Validate(duplicate, CoordinationLimits.KindValidation, out error),
			"step ids are unique inside a plan");

		var good = new CoordinationPlan();
		good.Steps.Add(new CoordinationStep { Id = "V1", Kind = "compile" });
		good.Steps.Add(new CoordinationStep
		{
			Id = "V2",
			Kind = "tests",
			Mode = "EditMode",
			Tests = new[] { "AgentBridgeCoordinationTests" }
		});
		Assert.IsTrue(CoordinationPlanRules.Validate(good, CoordinationLimits.KindValidation, out error), error);
	}

	[Test]
	public void PathPolicyAcceptsThisProjectAndRefusesNetworkPaths()
	{
		string canonical;
		string error;
		Assert.IsTrue(
			CoordinationPathPolicy.TryResolveProjectRoot(BridgePaths.ProjectRoot, out canonical, out error),
			"the project this test runs in must be supported: " + error);
		Assert.IsTrue(
			CoordinationPathPolicy.CoordinationRoot(canonical).Replace('\\', '/').EndsWith("Library/AgentBridge/Coordination"),
			"the coordination state lives under Library/AgentBridge");

		Assert.IsFalse(
			CoordinationPathPolicy.TryResolveProjectRoot(@"\\server\share\project", out canonical, out error),
			"a UNC path is refused rather than declared protected");
	}

	[Test]
	public void GateLeavesAnUncoordinatedProjectAlone()
	{
		// Nothing is registered in this project while the fixture runs, so every legacy submission
		// must still be admitted exactly as before coordination existed.
		Assert.IsFalse(CoordinationGate.Coordinated, "this fixture must run without active registrations");

		string reason;
		Assert.IsTrue(
			CoordinationGate.IsAdmitted(new TaskRequest { Kind = "tests", Id = "Task_x" }, out reason),
			"a legacy tests submission must be admitted: " + reason);
		Assert.IsTrue(
			CoordinationGate.IsAdmitted(new TaskRequest { Kind = "csharp", Id = "Task_y" }, out reason),
			"a legacy csharp submission must be admitted: " + reason);
	}

	// ---------------------------------------------------------------- helpers

	private CoordinationFileStore NewStore()
	{
		return new CoordinationFileStore(
			Path.Combine(_root, "Coordination"),
			CoordinationUnityCodec.Instance,
			CoordinationSystemClock.Instance,
			"project-under-test",
			null);
	}

	private static CoordinationReply Commit(CoordinationFileStore store, CoordinationCommand command, long nowMs)
	{
		using (ICoordinationTransaction transaction = store.BeginWithRetry(10000))
		{
			CoordinationReply reply = new CoordinationEngine().Apply(transaction.State, command, nowMs);
			if (reply.Changed)
			{
				transaction.Commit();
			}

			return reply;
		}
	}

	private static CoordinationState NewState()
	{
		return new CoordinationState { ProjectId = "project-under-test", Epoch = "epoch-1", Revision = 1 };
	}

	private static CoordinationCommand Command(string op)
	{
		return new CoordinationCommand { Op = op, Nonce = Guid.NewGuid().ToString("N") };
	}

	private static CoordinationCommand Register(string session, params string[] paths)
	{
		CoordinationCommand command = Command(CoordinationEngine.OpRegister);
		command.Session = session;
		command.SpecId = "spec-" + session;
		command.Owner = "owner/" + session;
		command.RepoRoot = "repo";
		command.Paths = paths;
		return command;
	}

	private static CoordinationCommand EditBegin(string session, string uuid)
	{
		CoordinationCommand command = Command(CoordinationEngine.OpEditBegin);
		command.Session = session;
		command.Uuid = uuid;
		command.Seconds = 120;
		return command;
	}

	private static CoordinationCommand WindowRequest(string session, string uuid)
	{
		CoordinationCommand command = Command(CoordinationEngine.OpRequest);
		command.Session = session;
		command.Uuid = uuid;
		command.Kind = CoordinationLimits.KindValidation;
		command.Seconds = 120;
		command.Plan = new CoordinationPlan();
		command.Plan.Steps.Add(new CoordinationStep
		{
			Id = "V1",
			Kind = "tests",
			Mode = "EditMode",
			Tests = new[] { "Suite.V1" }
		});
		return command;
	}

	private static CoordinationCommand StepBegin(string session, string token, string stepId, string taskId)
	{
		CoordinationCommand command = Command(CoordinationEngine.OpStepBegin);
		command.Session = session;
		command.Token = token;
		command.StepId = stepId;
		command.TaskId = taskId;
		command.Actual = new CoordinationStep
		{
			Id = stepId,
			Kind = "tests",
			Mode = "EditMode",
			Tests = new[] { "Suite." + stepId }
		};
		return command;
	}
}
