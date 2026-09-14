using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine.TestTools;

public class AgentBridgeCancellationPlayModeTests
{
	[UnityTest]
	public IEnumerator ShortSuccessfulRun()
	{
		yield return null;
		Assert.IsTrue(UnityEngine.Application.isPlaying);
	}

	[UnityTest]
	[Timeout(600000)]
	[Explicit("Run with scripts/verify-long-running-tasks.ps1; intentionally runs beyond 300 seconds")]
	public IEnumerator SurvivesProtectedPeriod()
	{
		DateTime until = DateTime.UtcNow.AddSeconds(360);
		while (DateTime.UtcNow < until) yield return null;
		Assert.IsTrue(UnityEngine.Application.isPlaying);
	}

	[UnityTest]
	[Explicit("Run through CLI to verify owner cancellation across domain reload")]
	public IEnumerator ResponsiveLongRun()
	{
		Directory.CreateDirectory("Temp/AgentBridge/repro-queue");
		File.AppendAllText("Temp/AgentBridge/repro-queue/events.txt", DateTime.UtcNow.ToString("o") + " play begin\n");
		DateTime until = DateTime.UtcNow.AddSeconds(35);
		while (DateTime.UtcNow < until) yield return null;
		File.AppendAllText("Temp/AgentBridge/repro-queue/events.txt", DateTime.UtcNow.ToString("o") + " play end\n");
	}
}
