using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class CompileCacheEntry
	{
		public int Version = 1;
		public string Fingerprint;
		public string SourceTaskId;
		public string Status;
		public List<TaskDiagnostic> Diagnostics = new List<TaskDiagnostic>();
		public string FinishedAtUtc;
	}
}
