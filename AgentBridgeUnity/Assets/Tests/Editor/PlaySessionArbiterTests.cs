using System;
using AgentBridge;
using NUnit.Framework;

public class PlaySessionArbiterTests
{
	// A moment inside the deadline with fresh owner activity: the verdicts that predate
	// preemption are all judged at this point in time.
	private static readonly DateTime WhileOwnerWorks = new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);
	private const int OwnerIdleSeconds = 10;

	private static PlaySessionState SessionOwnedBy(string ownerAgentSessionId)
	{
		return new PlaySessionState
		{
			TaskId = "task-1",
			OwnerAgentSessionId = ownerAgentSessionId,
			Phase = PlaySessionPhases.Active,
			StartedAtUtc = "2026-01-01T00:00:00.0000000Z",
			DeadlineUtc = "2026-01-01T00:02:00.0000000Z",
			OwnerLastActivityUtc = "2026-01-01T00:00:25.0000000Z",
			PendingStopTaskId = "",
			StopReason = ""
		};
	}

	[Test]
	public void Judge_RejectsWhileTestsArePending()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			SessionOwnedBy("agent-a"), "agent-a", true, true, WhileOwnerWorks, OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.RejectTests, verdict);
	}

	[Test]
	public void Judge_ReportsNotPlayingWithoutSessionAndWithoutPlayMode()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			null, "agent-a", false, false, WhileOwnerWorks, OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.NotPlaying, verdict);
	}

	[Test]
	public void Judge_StopsUnsanctionedPlayModeWithoutSession()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			null, "agent-a", true, false, WhileOwnerWorks, OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.StopUnsanctioned, verdict);
	}

	[Test]
	public void Judge_StopsOwnSession()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			SessionOwnedBy("agent-a"), "agent-a", true, false, WhileOwnerWorks, OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.StopOwn, verdict);
	}

	[Test]
	public void Judge_RejectsForeignSession()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			SessionOwnedBy("agent-a"), "agent-b", true, false, WhileOwnerWorks, OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.RejectForeign, verdict);
	}

	[Test]
	public void Judge_PreemptsForeignAfterDeadline()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			SessionOwnedBy("agent-a"), "agent-b", true, false,
			new DateTime(2026, 1, 1, 0, 2, 1, DateTimeKind.Utc), OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.StopPreempt, verdict);
	}

	[Test]
	public void Judge_PreemptsForeignWhenOwnerIsIdle()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			SessionOwnedBy("agent-a"), "agent-b", true, false,
			new DateTime(2026, 1, 1, 0, 0, 36, DateTimeKind.Utc), OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.StopPreempt, verdict);
	}

	[Test]
	public void Judge_RejectsForeignWhileOwnerIsActive()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			SessionOwnedBy("agent-a"), "agent-b", true, false,
			new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc), OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.RejectForeign, verdict);
	}

	[Test]
	public void Judge_OwnerStopsOwnSessionRegardlessOfActivity()
	{
		StopVerdict verdict = PlaySessionArbiter.Judge(
			SessionOwnedBy("agent-a"), "agent-a", true, false,
			new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc), OwnerIdleSeconds);
		Assert.AreEqual(StopVerdict.StopOwn, verdict);
	}

	[Test]
	public void CanPreempt_LegacyStateWithoutActivityFallsBackToDeadline()
	{
		PlaySessionState state = SessionOwnedBy("agent-a");
		state.OwnerLastActivityUtc = "";
		Assert.IsFalse(PlaySessionArbiter.CanPreempt(
			state, new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc), OwnerIdleSeconds));
		Assert.IsTrue(PlaySessionArbiter.CanPreempt(
			state, new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc), OwnerIdleSeconds));
	}
}
