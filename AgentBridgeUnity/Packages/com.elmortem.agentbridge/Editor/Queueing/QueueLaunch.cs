using System;

namespace AgentBridge.Queueing
{
	[Serializable]
	public sealed class QueueLaunch
	{
		public string Executable = "";
		public string[] Arguments = new string[0];
	}
}
