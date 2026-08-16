using System;

namespace AgentBridge
{
	[Serializable]
	public class AgentBridgeSettings
	{
		public bool Enabled;
		public int KeepCompletedCount = 10;
		public int TaskTimeoutSeconds = 300;
		public int IdleTickIntervalMs = 500;
		public int ActiveTickIntervalMs = 33;
		public string RoslynSource = "Auto";
		public string RoslynLocalPath = "";
		public bool EmitPdb = true;
		public int ClientWaitSeconds = 110;
		public string DirtyUntitledScenePolicy = "Discard";
		public string DirtyScenePolicy = "Save";
		public int LeaseIdleTimeoutSeconds = 120;
		public int ContentionSliceSeconds = 90;
		public int PlaySessionDefaultSeconds = 120;
		public int PlaySessionMaxSeconds = 600;
		public int AgentPlayGraceSeconds = 5;
	}
}
