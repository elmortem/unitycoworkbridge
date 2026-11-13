using System;
using System.Reflection;

namespace AgentBridge
{
	[Serializable]
	public class RoslynLocation
	{
		public RoslynSourceKind Kind;
		public bool Available;
		public string Reason;
		public string DirectoryPath;
		public string Version;

		// The pair a compile uses: the C# compiler and the Microsoft.CodeAnalysis it is bound to.
		[NonSerialized] public Assembly CSharpAssembly;
		[NonSerialized] public Assembly CodeAnalysisAssembly;
	}
}
