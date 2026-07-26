using System.Collections.Generic;

namespace AgentBridge
{
	public class TaskRecordOutcome
	{
		public string Status;
		public List<TaskDiagnostic> Diagnostics;
		public bool ForeignErrors;
	}
}
