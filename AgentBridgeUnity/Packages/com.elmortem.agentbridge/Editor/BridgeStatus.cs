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
		public string QueueBlockReason;
		public string CompilationState;
		public string LastCompileId;
		public string LastCompileFinishedUtc;
		public string QueueBlockedSinceUtc;
		public string HolderAgentSessionId;
		public bool IsPlaying;
		public string PlaySessionAgentId;
		public string PlaySessionDeadlineUtc;
		public QueuedTaskStatus[] QueuedTasks;
		public string[] Capabilities;

		// coordination-v1 summary. Never carries anybody's token: the status file is world
		// readable to every agent working in this project.
		public bool CoordinationActive;
		public int CoordinationParticipants;
		public long CoordinationRevision;
		public string CoordinationWindowSession;
		public string CoordinationUnavailable;
	}
}
