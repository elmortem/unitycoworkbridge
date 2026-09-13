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
	[TestCase(3600, 300)]
	[TestCase(300, 300)]
	[TestCase(10, 10)]
	[TestCase(0, 300)]
	[TestCase(-1, 300)]
	public void TestRunCannotExceedFiveMinutes(int configured, int expected)
	{
		Assert.AreEqual(expected, TestRunLifecycle.LimitSeconds(configured));
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
