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
		public int LoadedTaskAssemblies;
		public int ExecutedTasks;
		public string ActiveTaskId;
		public string[] Capabilities;
	}
}
