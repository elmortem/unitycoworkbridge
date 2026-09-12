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
		public bool Fresh;

		// coordination-v1. Absent on a legacy submission, which is still accepted in a project
		// that has no active registrations.
		public string CoordinationWindowToken;
		public string CoordinationStepId;
	}
}
