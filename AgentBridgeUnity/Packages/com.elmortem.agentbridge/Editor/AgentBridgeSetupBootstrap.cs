using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class AgentBridgeSetupBootstrap
	{
		private const string DismissedKey = "AgentBridge_SetupDismissed";

		static AgentBridgeSetupBootstrap()
		{
			EditorApplication.delayCall += TryShow;
		}

		private static void TryShow()
		{
			if (Application.isBatchMode)
			{
				return;
			}

			RoslynLocation location = RoslynResolver.ResolveConfigured();
			if (location.Available)
			{
				return;
			}

			if (SessionState.GetBool(DismissedKey, false))
			{
				return;
			}

			AgentBridgeSetupWindow.Open();
		}
	}
}
