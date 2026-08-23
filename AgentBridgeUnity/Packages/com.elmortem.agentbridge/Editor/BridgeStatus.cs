using System;

namespace AgentBridge
{
	[Serializable]
	public class BridgeStatus
	{
		public int ProtocolVersion;
		public string PackageVersion;
		public string ProjectPath;
		public string ProjectId;
		public string HostOs;
		public string UnityVersion;
		public int EditorPid;
		public string SessionId;
		public string AssemblyBuildTimeUtc;
		public bool Enabled;
		public string RoslynSource;
		public bool RoslynReady;
		public bool SignalTickAvailable;
		public bool WakeTimerInstalled;
		public string WakeTimerKind;
		public string InteractionMode;
		public bool TelemetryEnabled;
		public int LoadedTaskAssemblies;
		public int ExecutedTasks;
		public string ActiveTaskId;
		public string HolderAgentSessionId;
		public bool IsPlaying;
		public string PlaySessionAgentId;
		public string PlaySessionDeadlineUtc;
		public QueuedTaskStatus[] QueuedTasks;
		public string[] Capabilities;
	}
}
