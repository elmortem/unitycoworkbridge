using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TaskRecord
	{
		public string Id;
		public string Kind;
		public string Status;
		public string Hash;
		public string ReturnValue;
		public List<string> Logs = new List<string>();
		public List<TaskDiagnostic> Diagnostics = new List<TaskDiagnostic>();
		public bool ForeignErrors;
		public List<string> Artifacts = new List<string>();
		public TestRunResult Tests;
		public TaskTiming Timing = new TaskTiming();
		public string SessionId;
		public string AgentSessionId;
		public ContentionInfo Contention = new ContentionInfo();
		public string StartedAtUtc;
		public string FinishedAtUtc;
	}
}
