using System.Collections.Generic;
using System.Reflection;

namespace AgentBridge
{
	public class CompileResult
	{
		public Assembly Assembly;
		public List<TaskDiagnostic> Diagnostics = new List<TaskDiagnostic>();
		public bool Success;
		public bool GuardrailRejected;
	}
}
