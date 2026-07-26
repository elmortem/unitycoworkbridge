using System;

namespace AgentBridge
{
	[Serializable]
	public class TaskDiagnostic
	{
		public string Code;
		public string Severity;
		public string Message;
		public string File;
		public int Line;
		public int Column;
	}
}
