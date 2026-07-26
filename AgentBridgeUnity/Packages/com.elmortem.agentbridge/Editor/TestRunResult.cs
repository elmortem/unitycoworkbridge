using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class TestRunResult
	{
		public int passed;
		public int failed;
		public int skipped;
		public int inconclusive;
		public int total;
		public double duration;
		public bool aborted;
		public string message;
		public List<TestFailure> failures = new List<TestFailure>();
	}
}
