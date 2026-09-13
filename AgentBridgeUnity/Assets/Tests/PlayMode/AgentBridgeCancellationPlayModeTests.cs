using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine.TestTools;

public class AgentBridgeCancellationPlayModeTests
{
	[UnityTest]
	[Explicit("Run through CLI with a short task deadline to verify cancellation across domain reload")]
	public IEnumerator ResponsiveLongRun()
	{
		Directory.CreateDirectory("Temp/AgentBridge/repro-queue");
		File.AppendAllText("Temp/AgentBridge/repro-queue/events.txt", DateTime.UtcNow.ToString("o") + " play begin\n");
		DateTime until = DateTime.UtcNow.AddSeconds(35);
		while (DateTime.UtcNow < until) yield return null;
		File.AppendAllText("Temp/AgentBridge/repro-queue/events.txt", DateTime.UtcNow.ToString("o") + " play end\n");
	}
}
