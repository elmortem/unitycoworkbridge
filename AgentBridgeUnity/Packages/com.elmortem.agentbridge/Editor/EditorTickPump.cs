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
				BridgeStatusWriter.Write();
				Debug.LogWarning("[AgentBridge] EditorApplication.SignalTick not found; falling back to AgentEditorWakeTimer.");
				AgentEditorWakeTimer.Start();
				return;
			}

			_signalTick = (Action)Delegate.CreateDelegate(typeof(Action), method);
			BridgeStatusWriter.Current.SignalTickAvailable = true;
			BridgeStatusWriter.Write();

			EditorApplication.update -= OnUpdate;
			EditorApplication.update += OnUpdate;

			AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
			AssemblyReloadEvents.beforeAssemblyReload += Unsubscribe;

			EditorApplication.quitting -= Unsubscribe;
			EditorApplication.quitting += Unsubscribe;

			_installed = true;
		}

		private static void OnUpdate()
		{
			int intervalMs = HasActiveTask
				? AgentBridgeSettingsStore.GetActiveTickIntervalMs()
				: AgentBridgeSettingsStore.GetIdleTickIntervalMs();

			double now = EditorApplication.timeSinceStartup;
			if ((now - _lastTickTime) * 1000d < intervalMs)
			{
				return;
			}

			_lastTickTime = now;
			_signalTick();
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
			_installed = false;
		}
	}
}
