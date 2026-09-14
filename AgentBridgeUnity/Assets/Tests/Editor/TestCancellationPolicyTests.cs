using AgentBridge;
using NUnit.Framework;

public class TestCancellationPolicyTests
{
	[Test]
	public void ActiveFrameworkRunStillBlocksInEditMode()
	{
		Assert.IsFalse(UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode);
		Assert.IsTrue(TestRunnerCancellation.IsRunning(), "The current NUnit run must retain the editor even outside Play Mode.");
	}

	private class Job { public string guid; public bool isRunning; }
	[Test]
	public void LegacyCancellationRecoversOnlyTheSingleRunningJob()
	{
		Assert.AreEqual("live", TestRunnerCancellation.UniqueRunningJobId(new[] {
			new Job { guid = "old", isRunning = false }, new Job { guid = "live", isRunning = true } }));
		Assert.AreEqual("", TestRunnerCancellation.UniqueRunningJobId(new Job[0]));
		Assert.Throws<System.InvalidOperationException>(() => TestRunnerCancellation.UniqueRunningJobId(new[] {
			new Job { guid = "one", isRunning = true }, new Job { guid = "two", isRunning = true } }));
	}
	[TestCase(299999, true)]
	[TestCase(300000, false)]
	[TestCase(3600000, false)]
	public void ForeignCancellationUsesExecutionAge(long elapsedMs, bool expected)
	{
		const long start = 1700000000000;
		string started = System.DateTimeOffset.FromUnixTimeMilliseconds(start).ToString("o");
		Assert.AreEqual(expected, TaskCancellationPolicy.IsProtected("owner", "other", started, start + elapsedMs));
		Assert.IsFalse(TaskCancellationPolicy.IsProtected("owner", "owner", started, start));
	}

	[Test]
	public void AnonymousCallerAndMissingStartDoNotBypassProtection()
	{
		Assert.IsTrue(TaskCancellationPolicy.IsProtected("", "", null, 1700000000000));
		Assert.IsTrue(TaskCancellationPolicy.IsProtected("owner", "other", "invalid", 1700000000000));
		Assert.IsFalse(TaskCancellationPolicy.IsProtected("owner", "owner", null, 1700000000000));
	}

	[Test]
	public void PreemptionReasonNamesInitiatorAndDuration()
	{
		const long start = 1700000000000;
		string started = System.DateTimeOffset.FromUnixTimeMilliseconds(start).ToString("o");
		string reason = TaskCancellationPolicy.Reason("owner", "neighbor", started, start + 427000);
		StringAssert.Contains("preempted_after_300s", reason);
		StringAssert.Contains("neighbor", reason);
		StringAssert.Contains("427 seconds", reason);
	}

	[Test]
	public void OnlyALiveForeignWindowProtectsATask()
	{
		var grant = new AgentBridge.Coordination.CoordinationGrant { DeadlineMs = 200 };
		Assert.IsTrue(TaskCoordinator.IsCancellationProtected(grant, "owner", "other", 100));
		Assert.IsFalse(TaskCoordinator.IsCancellationProtected(grant, "owner", "owner", 100));
		Assert.IsFalse(TaskCoordinator.IsCancellationProtected(grant, "owner", "other", 200));
		Assert.IsFalse(TaskCoordinator.IsCancellationProtected(null, "owner", "other", 100));
	}

	[Test]
	public void CancellationIsNotATerminalState()
	{
		Assert.IsFalse(TaskCoordinator.IsTerminal("canceling"));
		Assert.IsTrue(TaskCoordinator.IsTerminal("timeout"));
		Assert.IsTrue(TaskCoordinator.IsTerminal("canceled"));
	}
}
