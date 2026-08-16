using System;

namespace AgentBridge
{
	[Serializable]
	public class TaskRequest
	{
		public string Id;
		public string Kind;
		public string PayloadFile;
		public string TestMode;
		public string[] AssemblyNames;
		public string[] TestNames;
		public string[] CategoryNames;
		public string AgentSessionId;
		public string Note;
		public int PlaySeconds;
	}
}
