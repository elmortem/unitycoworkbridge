using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using AgentBridge;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

public class QueueTimeoutReproTests
{
	[UnityTest]
	[Explicit("Run with scripts/verify-test-cancellation.ps1; leaves an orphan recovery record")]
	public IEnumerator FinishedOwnerLeavesRecovery()
	{
		string error;
		Assert.IsTrue(PlayModeSceneRecovery.Begin(TaskCoordinator.ActiveTaskId, out error), error);
		yield return null;
	}

	private static void Mark(string text)
	{
		Directory.CreateDirectory("Temp/AgentBridge/repro-queue");
		File.AppendAllText("Temp/AgentBridge/repro-queue/events.txt", DateTime.UtcNow.ToString("o") + " " + text + "\n");
	}

	[UnityTest]
	[Explicit("Run with scripts/verify-test-cancellation.ps1; verifies explicit owner cancellation")]
	public IEnumerator ResponsiveTestOutlivesTimeout()
	{
		Mark("plain begin");
		double deadline = EditorApplication.timeSinceStartup + 35;
		while (EditorApplication.timeSinceStartup < deadline) yield return null;
		Mark("plain end; active=" + TaskCoordinator.ActiveTaskId);
	}

	// StopRun unregisters the job and unsubscribes update without RunFinished, exactly as the
	// framework does on RunFailed. The coroutine below never advances past this point.
	[UnityTest]
	[Explicit("Run with scripts/verify-test-cancellation.ps1; stops the Unity job without RunFinished")]
	public IEnumerator FrameworkStopsWithoutRunFinished()
	{
		Mark("lost begin");
		yield return null;
		var runners = EditorApplication.update.GetInvocationList()
			.Where(d => d.Target != null && d.Target.GetType().FullName == "UnityEditor.TestTools.TestRunner.TestRun.TestJobRunner")
			.Select(d => d.Target)
			.Distinct()
			.ToList();
		Assert.AreEqual(1, runners.Count, "Expected exactly one subscribed TestJobRunner");
		runners[0].GetType().GetMethod("StopRun", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(runners[0], null);
		double deadline = EditorApplication.timeSinceStartup + 120;
		while (EditorApplication.timeSinceStartup < deadline)
		{
			yield return null;
		}
	}

	[UnityTest]
	[Explicit("Run with scripts/verify-test-cancellation.ps1; injects pending recovery state")]
	public IEnumerator PendingRecoveryOutlivesTimeout()
	{
		Mark("recovery begin");
		string error;
		Assert.IsTrue(PlayModeSceneRecovery.Begin(TaskCoordinator.ActiveTaskId, out error), error);
		double releaseAt = EditorApplication.timeSinceStartup + 60;
		string recoveryOwner = TaskCoordinator.ActiveTaskId;
		EditorApplication.CallbackFunction cleanup = null;
		cleanup = () =>
		{
			if (EditorApplication.timeSinceStartup < releaseAt) return;
			EditorApplication.update -= cleanup;
			if (PlayModeSceneRecovery.PendingTaskId != recoveryOwner) return;
			Mark("injected recovery result; active=" + TaskCoordinator.ActiveTaskId);
			PlayModeSceneRecovery.RecordResult(new TestRunResult { aborted = true, message = "Controlled reproduction cleanup" });
		};
		EditorApplication.update += cleanup;
		double deadline = EditorApplication.timeSinceStartup + 35;
		while (EditorApplication.timeSinceStartup < deadline) yield return null;
		Mark("recovery test end; pending=" + PlayModeSceneRecovery.IsPending + "; active=" + TaskCoordinator.ActiveTaskId);
	}
}
