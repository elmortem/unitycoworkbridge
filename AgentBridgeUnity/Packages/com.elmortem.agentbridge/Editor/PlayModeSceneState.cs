using System;

namespace AgentBridge
{
	[Serializable]
	public class PlayModeSceneState
	{
		public string TaskId;
		public SceneSetupState[] OriginalSetup;
		public string BootstrapScenePath;
		public TestRunResult Result;
		public bool HasResult;
		public string RecoveryError;
	}

	[Serializable]
	public class SceneSetupState
	{
		public string Path;
		public bool IsLoaded;
		public bool IsActive;
		public bool IsSubScene;
	}
}
