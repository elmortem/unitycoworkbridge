using NUnit.Framework;
using AgentBridge;

public class AgentBridgeWakeTests
{
	[Test]
	public void SignalsEveryTickWhileWorkIsPending()
	{
		Assert.IsTrue(EditorTickPump.ShouldSignal(10d, 9.999d, true, 500));
	}

	[Test]
	public void ThrottlesWhenIdle()
	{
		Assert.IsFalse(EditorTickPump.ShouldSignal(10d, 9.9d, false, 500));
		Assert.IsTrue(EditorTickPump.ShouldSignal(10d, 9.4d, false, 500));
	}

	[Test]
	public void UnknownInteractionModeIsNotThrottled()
	{
		Assert.IsFalse(InteractionModeProbe.IsThrottled("unknown"));
		Assert.IsFalse(InteractionModeProbe.IsThrottled("NoThrottling"));
		Assert.IsTrue(InteractionModeProbe.IsThrottled("MonitorRefreshRate"));
	}
}
