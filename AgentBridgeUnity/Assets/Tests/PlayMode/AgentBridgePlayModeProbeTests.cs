using NUnit.Framework;
using UnityEngine;

public class AgentBridgePlayModeProbeTests
{
	[Test]
	public void PassingTest()
	{
		Assert.AreEqual(2, 1 + 1);
	}

	[Test]
	public void LeavesBootstrapSceneModifiedForRecoveryProbe()
	{
		new GameObject("AgentBridgeDirtyPlayModeProbe");
		Assert.IsNotNull(GameObject.Find("AgentBridgeDirtyPlayModeProbe"));
	}
}
