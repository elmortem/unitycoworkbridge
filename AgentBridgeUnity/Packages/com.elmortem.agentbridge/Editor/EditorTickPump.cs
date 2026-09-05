using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class EditorTickPump
	{
		public static bool HasActiveTask;
		public static bool HasPendingWork;

		private const double GapThresholdMs = 2000d;

		private static Action _signalTick;
		private static double _lastTickTime;
		private static double _lastUpdateTime;
		private static bool _installed;
		private static bool _startReported;

		static EditorTickPump()
		{
			if (Application.isBatchMode) return;
			Application.runInBackground = true;

			MethodInfo method = typeof(EditorApplication).GetMethod("SignalTick", BindingFlags.NonPublic | BindingFlags.Static);
			if (method == null)
			{
				BridgeStatusWriter.Current.SignalTickAvailable = false;
				Debug.LogWarning("[AgentBridge] EditorApplication.SignalTick not found; relying on AgentEditorWakeTimer alone.");
			}
			else
			{
				try
				{
					_signalTick = (Action)Delegate.CreateDelegate(typeof(Action), method);
					BridgeStatusWriter.Current.SignalTickAvailable = true;
				}
				catch (Exception exception)
				{
					Debug.LogWarning("[AgentBridge] Cannot bind SignalTick: " + exception.Message);
				}
			}
			AgentEditorWakeTimer.ConfigureSignal(_signalTick);

			BridgeStatusWriter.Write();

			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;

			AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
			AssemblyReloadEvents.beforeAssemblyReload += Unsubscribe;

			EditorApplication.quitting -= Unsubscribe;
			EditorApplication.quitting += Unsubscribe;

			_installed = true;
			// Arm before the first update: that update itself may need waking.
			RefreshTimer(EditorApplication.timeSinceStartup);
		}

		public static bool HasWork
		{
			get { return HasActiveTask || HasPendingWork; }
		}

		public static void Refresh()
		{
			if (_installed) RefreshTimer(EditorApplication.timeSinceStartup);
		}

		public static bool ShouldSignal(double nowSeconds, double lastSignalSeconds, bool hasWork, int intervalMs)
		{
			if (hasWork)
			{
				return true;
			}

			return (nowSeconds - lastSignalSeconds) * 1000d >= intervalMs;
		}

		private static void OnUpdate()
		{
			double now = EditorApplication.timeSinceStartup;

			// The gap between two consecutive updates is the only direct evidence that the editor
			// stopped ticking: everything else only shows the delay it caused downstream.
			if (_lastUpdateTime > 0d)
			{
				double gapMs = (now - _lastUpdateTime) * 1000d;
				if (gapMs >= GapThresholdMs)
				{
					TelemetryLog.Write("tick_gap", "", "", new[]
					{
						TelemetryField.Number("GapMs", (long)gapMs),
						TelemetryField.Flag("HasWork", HasWork),
						TelemetryField.Flag("Focused", UnityEditorInternal.InternalEditorUtility.isApplicationActive)
					});
				}
			}

			_lastUpdateTime = now;

			int intervalMs = RefreshTimer(now);

			if (!_startReported)
			{
				_startReported = true;
				BridgeStatusWriter.WriteStartTelemetry();
			}

			if (!AgentBridgeSettingsStore.IsEnabled()
				|| AgentEditorWakeTimer.Kind == "background_signal"
				|| !ShouldSignal(now, _lastTickTime, HasWork, intervalMs))
			{
				return;
			}

			_lastTickTime = now;

			if (_signalTick == null)
			{
				return;
			}

			_signalTick();
		}

		private static int RefreshTimer(double now)
		{
			int intervalMs = HasWork
				? AgentBridgeSettingsStore.GetActiveTickIntervalMs()
				: AgentBridgeSettingsStore.GetIdleTickIntervalMs();
			if (AgentBridgeSettingsStore.IsEnabled()) AgentEditorWakeTimer.Ensure(intervalMs, now);
			else AgentEditorWakeTimer.Stop();
			PublishWakeState();
			return intervalMs;
		}

		private static void PublishWakeState()
		{
			string kind = AgentEditorWakeTimer.Kind ?? "none";
			if (BridgeStatusWriter.Current.WakeTimerInstalled == AgentEditorWakeTimer.Installed
				&& BridgeStatusWriter.Current.WakeTimerKind == kind)
			{
				return;
			}

			BridgeStatusWriter.Current.WakeTimerInstalled = AgentEditorWakeTimer.Installed;
			BridgeStatusWriter.Current.WakeTimerKind = kind;
			BridgeStatusWriter.Write();
		}

		private static void Unsubscribe()
		{
			if (!_installed)
			{
				return;
			}

			EditorApplication.update -= OnUpdate;
			AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
			EditorApplication.quitting -= Unsubscribe;
			AgentEditorWakeTimer.Stop();
			_installed = false;
		}
	}
}
