using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TestCaseResult
	{
		public string FullName;
		public string Assembly;
		public List<string> Categories = new List<string>();
		public string Status;
		public double DurationSeconds;
		public string Message;
		public string StackTrace;
		// NUnit skipped this [Explicit] case because the run did not select it explicitly.
		public bool ExplicitSkipped;
	}
}
