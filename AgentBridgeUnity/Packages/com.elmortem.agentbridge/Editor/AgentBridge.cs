using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class AgentBridge
	{
		static AgentBridge()
		{
			if (!IsEnabled())
			{
				return;
			}

			Initialize();
			Debug.Log("[AgentBridge] Enabled.");
		}

		[MenuItem("Tools/Agent Bridge/Start")]
		public static void Start()
		{
			SetEnabled(true);
			BridgeStatusWriter.Current.Enabled = true;
			BridgeStatusWriter.Write();
			Initialize();
			Debug.Log("[AgentBridge] Started.");
		}

		[MenuItem("Tools/Agent Bridge/Start", true)]
		private static bool StartValidate()
		{
			return !IsEnabled();
		}

		[MenuItem("Tools/Agent Bridge/Stop")]
		public static void Stop()
		{
			SetEnabled(false);
			BridgeStatusWriter.Current.Enabled = false;
			BridgeStatusWriter.Write();

			AssemblyReloadEvents.beforeAssemblyReload -= AgentEditorWakeTimer.Stop;
			EditorApplication.quitting -= AgentEditorWakeTimer.Stop;
			AgentEditorWakeTimer.Stop();

			TaskCoordinator.Stop();
			Debug.Log("[AgentBridge] Stopped.");
		}

		[MenuItem("Tools/Agent Bridge/Stop", true)]
		private static bool StopValidate()
		{
			return IsEnabled();
		}

		[MenuItem("Tools/Agent Bridge/Cancel Running Task")]
		public static void CancelRunningTask()
		{
			TaskCoordinator.CancelActive();
		}

		[MenuItem("Tools/Agent Bridge/Cancel Running Task", true)]
		private static bool CancelRunningTaskValidate()
		{
			return TaskCoordinator.HasActiveTask;
		}

		private static void Initialize()
		{
			AssemblyReloadEvents.beforeAssemblyReload -= AgentEditorWakeTimer.Stop;
			AssemblyReloadEvents.beforeAssemblyReload += AgentEditorWakeTimer.Stop;

			EditorApplication.quitting -= AgentEditorWakeTimer.Stop;
			EditorApplication.quitting += AgentEditorWakeTimer.Stop;

			TaskCoordinator.Start();
		}

		private static bool IsEnabled()
		{
			return AgentBridgeSettingsStore.IsEnabled();
		}

		private static void SetEnabled(bool value)
		{
			AgentBridgeSettingsStore.SetEnabled(value);
		}
	}
}
