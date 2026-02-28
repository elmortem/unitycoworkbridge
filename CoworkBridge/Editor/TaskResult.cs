using System;
using System.Collections.Generic;

namespace CoworkBridge
{
	[Serializable]
	public class TaskResult
	{
		public string id;
		public string status;
		public List<string> logs = new List<string>();
		public string return_value;
		public List<CompilerError> compiler_errors = new List<CompilerError>();
		public bool foreign_errors;
	}
}
