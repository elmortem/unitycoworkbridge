using AgentBridge.Cli;
using AgentBridge.Coordination;

namespace AgentBridge.Coordination.Tests;

internal sealed class FakeClock : ICoordinationClock
{
	public long UtcNowMs { get; set; } = 1_700_000_000_000;

	public void Advance(long ms)
	{
		UtcNowMs += ms;
	}
}

internal static class Harness
{
	public static CoordinationState NewState()
	{
		return new CoordinationState
		{
			ProjectId = "project-under-test",
			Epoch = "epoch-1",
			Revision = 1
		};
	}

	public static CoordinationCommand Command(string op)
	{
		return new CoordinationCommand { Op = op, Nonce = Guid.NewGuid().ToString("N") };
	}

	public static CoordinationCommand Register(string session, params string[] paths)
	{
		var command = Command(CoordinationEngine.OpRegister);
		command.Session = session;
		command.SpecId = "spec-" + session;
		command.Owner = "owner/" + session;
		command.RepoRoot = "repo";
		command.Paths = paths;
		return command;
	}

	public static CoordinationCommand EditBegin(string session, string uuid, int seconds = 120)
	{
		var command = Command(CoordinationEngine.OpEditBegin);
		command.Session = session;
		command.Uuid = uuid;
		command.Seconds = seconds;
		return command;
	}

	public static CoordinationCommand WindowRequest(
		string session,
		string uuid,
		string kind = CoordinationLimits.KindValidation,
		int seconds = 120,
		CoordinationPlan plan = null)
	{
		var command = Command(CoordinationEngine.OpRequest);
		command.Session = session;
		command.Uuid = uuid;
		command.Kind = kind;
		command.Seconds = seconds;
		command.Plan = plan ?? TestsPlan("V1");
		return command;
	}

	public static CoordinationPlan TestsPlan(params string[] stepIds)
	{
		var plan = new CoordinationPlan();
		foreach (var id in stepIds)
		{
			plan.Steps.Add(new CoordinationStep
			{
				Id = id,
				Kind = "tests",
				Mode = "EditMode",
				Tests = new[] { "Suite." + id }
			});
		}

		return plan;
	}

	public static CoordinationStep ActualTests(string stepId)
	{
		return new CoordinationStep
		{
			Id = stepId,
			Kind = "tests",
			Mode = "EditMode",
			Tests = new[] { "Suite." + stepId }
		};
	}

	public static CoordinationFileStore Store(string projectRoot, ICoordinationClock clock = null)
	{
		var coordinationRoot = CoordinationPathPolicy.CoordinationRoot(projectRoot);
		return new CoordinationFileStore(
			coordinationRoot,
			CoordinationJsonCodec.Instance,
			clock ?? CoordinationSystemClock.Instance,
			"project-under-test",
			() => Guid.NewGuid().ToString("N"));
	}

	public static CoordinationReply Commit(CoordinationFileStore store, CoordinationCommand command, long nowMs)
	{
		using var transaction = store.BeginWithRetry(10_000);
		var reply = new CoordinationEngine().Apply(transaction.State, command, nowMs);
		if (reply.Changed)
		{
			transaction.Commit();
		}

		return reply;
	}

	public static void Expect(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}

	public static void ExpectCode(CoordinationReply reply, string code, string message)
	{
		Expect(reply.Code == code, message + " (got " + reply.Code + ": " + reply.Message + ")");
	}

	public static void TryDelete(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}
		}
		catch (IOException)
		{
		}
	}

	public static bool WaitUntil(Func<bool> condition, int timeoutMs)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return true;
			}

			Thread.Sleep(20);
		}

		return condition();
	}
}
