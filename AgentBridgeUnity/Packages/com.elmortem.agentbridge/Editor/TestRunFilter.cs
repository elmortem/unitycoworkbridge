using System;

namespace AgentBridge
{
	[Serializable]
	public class TestRunFilter
	{
		public string TestMode;
		public string[] AssemblyNames = new string[0];
		public string[] TestNames = new string[0];
		public string[] CategoryNames = new string[0];
	}
}
