using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TestRunDump
	{
		public int Version = 1;
		public string Fingerprint;
		public string SourceFingerprint;
		public string SourceTaskId;
		public TestRunFilter Filter = new TestRunFilter();
		public string FinishedAtUtc;
		public List<TestCaseResult> Entries = new List<TestCaseResult>();
	}
}
