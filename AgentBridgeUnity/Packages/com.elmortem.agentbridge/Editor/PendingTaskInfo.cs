using System;

namespace AgentBridge
{
	public class PendingTaskInfo
	{
		public string Id;
		public string TaskFilePath;
		public DateTime CreatedUtc;
		public string EffectiveSessionId;
		public string Note;
		public string Kind;
	}
}
