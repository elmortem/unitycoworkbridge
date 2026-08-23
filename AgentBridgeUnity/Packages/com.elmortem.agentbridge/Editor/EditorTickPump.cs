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

		private static Action _signalTick;
		private static double _lastTickTime;
		private static bool _installed;

		static EditorTickPump()
		{
			Application.runInBackground = true;

			MethodInfo method = typeof(EditorApplication).GetMethod("SignalTick", BindingFlags.NonPublic | BindingFlags.Static);
			if (method == null)
			{
				BridgeStatusWriter.Current.SignalTickAvailable = false;
				Debug.LogWarning("[AgentBridge] EditorApplication.SignalTick not found; relying on AgentEditorWakeTimer alone.");
			}
			else
			{
				_signalTick = (Action)Delegate.CreateDelegate(typeof(Action), method);
				BridgeStatusWriter.Current.SignalTickAvailable = true;
			}

			BridgeStatusWriter.Write();

			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;

			AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
			AssemblyReloadEvents.beforeAssemblyReload += Unsubscribe;

			EditorApplication.quitting -= Unsubscribe;
			EditorApplication.quitting += Unsubscribe;

			_installed = true;
		}

		public static bool HasWork
		{
			get { return HasActiveTask || HasPendingWork; }
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
			int intervalMs = HasWork
				? AgentBridgeSettingsStore.GetActiveTickIntervalMs()
				: AgentBridgeSettingsStore.GetIdleTickIntervalMs();

			AgentEditorWakeTimer.Ensure(intervalMs, now);
			PublishWakeState();

			if (!ShouldSignal(now, _lastTickTime, HasWork, intervalMs))
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
