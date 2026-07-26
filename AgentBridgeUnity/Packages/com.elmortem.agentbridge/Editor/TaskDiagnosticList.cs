using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TaskDiagnosticList
	{
		public List<TaskDiagnostic> Items = new List<TaskDiagnostic>();
	}
}
