using System;

namespace AgentBridge
{
	[Serializable]
	public class RoslynLocation
	{
		public RoslynSourceKind Kind;
		public bool Available;
		public string Reason;
		public string DirectoryPath;
	}
}
