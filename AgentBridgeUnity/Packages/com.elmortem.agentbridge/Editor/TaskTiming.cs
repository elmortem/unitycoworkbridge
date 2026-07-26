using System;

namespace AgentBridge
{
	[Serializable]
	public class TaskTiming
	{
		public int QueuedMs;
		public int CompileMs;
		public int ExecuteMs;
		public int TotalMs;
	}
}
