using NUnit.Framework;
using UnityEngine;

namespace AgentBridge.Tests.PlayMode
{
	public class AgentBridgeTestSelectionPlayModeTests
	{
		[Test]
		public void RunsInPlayMode()
		{
			Assert.IsTrue(Application.isPlaying);
		}
	}
}
