using AgentBridge;
using NUnit.Framework;

public class PlaySessionArbiterTests
{
	private static PlaySessionState SessionOwnedBy(string ownerAgentSessionId)
	{
		return new PlaySessionState
		{
			TaskId = "task-1",
			OwnerAgentSessionId = ownerAgentSessionId,
			Phase = PlaySessionPhases.Active,
			StartedAtUtc = "2026-01-01T00:00:00.0000000Z",
			DeadlineUtc = "2026-01-01T00:02:00.0000000Z",
			PendingStopTaskId = "",
			StopReason = ""
		};
	}

	[Test]
	public void Judge_RejectsWhileTestsArePending()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(SessionOwnedBy("agent-a"), "agent-a", true, true);
		Assert.AreEqual(StopVerdict.RejectTests, verdict);
	}

	[Test]
	public void Judge_ReportsNotPlayingWithoutSessionAndWithoutPlayMode()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(null, "agent-a", false, false);
		Assert.AreEqual(StopVerdict.NotPlaying, verdict);
	}

	[Test]
	public void Judge_StopsUnsanctionedPlayModeWithoutSession()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(null, "agent-a", true, false);
		Assert.AreEqual(StopVerdict.StopUnsanctioned, verdict);
	}

	[Test]
	public void Judge_StopsOwnSession()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(SessionOwnedBy("agent-a"), "agent-a", true, false);
		Assert.AreEqual(StopVerdict.StopOwn, verdict);
	}

	[Test]
	public void Judge_RejectsForeignSession()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(SessionOwnedBy("agent-a"), "agent-b", true, false);
		Assert.AreEqual(StopVerdict.RejectForeign, verdict);
	}
}
