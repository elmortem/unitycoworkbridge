using System;

namespace AgentBridge
{
	[Serializable]
	public class SessionContext
	{
		public string AgentSessionId;
		public SceneSetupState[] Setup;
		public string PrefabStagePath = "";
		public string SavedAtUtc;
	}
}
