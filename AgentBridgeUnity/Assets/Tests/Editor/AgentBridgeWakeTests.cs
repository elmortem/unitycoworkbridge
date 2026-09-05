using NUnit.Framework;
using AgentBridge;
using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public class AgentBridgeWakeTests
{
	[UnityTest]
	public IEnumerator BackgroundSignalDoesNotDependOnPumpUpdates()
	{
		Assert.AreEqual("background_signal", AgentEditorWakeTimer.Kind);
		var method = typeof(EditorTickPump).GetMethod("OnUpdate", BindingFlags.NonPublic | BindingFlags.Static);
		var update = (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), method);
		EditorApplication.update -= update;
		try
		{
			long before = AgentEditorWakeTimer.SignalCount;
			double deadline = EditorApplication.timeSinceStartup + 3;
			while (AgentEditorWakeTimer.SignalCount <= before && EditorApplication.timeSinceStartup < deadline)
				yield return null;
			Assert.Greater(AgentEditorWakeTimer.SignalCount, before,
				"SignalTick must run even when EditorTickPump.OnUpdate is disconnected.");
		}
		finally
		{
			EditorApplication.update += update;
		}
	}

	[UnityTest]
	public IEnumerator DisabledBridgeDoesNotRearmTimerOnNextUpdate()
	{
		bool enabled = AgentBridgeSettingsStore.IsEnabled();
		try
		{
			AgentBridgeSettingsStore.SetEnabled(false);
			AgentEditorWakeTimer.Stop();
			for (int i = 0; i < 5; i++) yield return null;
			Assert.IsFalse(AgentEditorWakeTimer.Installed);
			Assert.AreEqual("none", AgentEditorWakeTimer.Kind);
		}
		finally
		{
			AgentBridgeSettingsStore.SetEnabled(enabled);
		}
		for (int i = 0; i < 5; i++) yield return null;
		Assert.AreEqual(enabled, AgentEditorWakeTimer.Installed);
	}

	[Test]
	public void TimerStopAndRearmAreIdempotent()
	{
		try
		{
			AgentEditorWakeTimer.Stop();
			AgentEditorWakeTimer.Stop();
			Assert.IsFalse(AgentEditorWakeTimer.Installed);
			AgentEditorWakeTimer.Ensure(33, EditorApplication.timeSinceStartup + 2);
			AgentEditorWakeTimer.Ensure(33, EditorApplication.timeSinceStartup + 2);
			Assert.IsTrue(AgentEditorWakeTimer.Installed);
			Assert.AreEqual("background_signal", AgentEditorWakeTimer.Kind);
		}
		finally
		{
			AgentEditorWakeTimer.Ensure(AgentBridgeSettingsStore.GetActiveTickIntervalMs(), EditorApplication.timeSinceStartup + 3);
		}
	}

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
