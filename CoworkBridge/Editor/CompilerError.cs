using System;

namespace CoworkBridge
{
	[Serializable]
	public class CompilerError
	{
		public string type;
		public string message;
		public string file;
		public int line;
	}
}
