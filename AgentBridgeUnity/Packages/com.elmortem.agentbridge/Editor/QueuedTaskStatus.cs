using System;

namespace AgentBridge
{
	[Serializable]
	public class QueuedTaskStatus
	{
		public string Id;
		public string AgentSessionId;
		public int Position;
	}
}
