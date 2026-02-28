using System;
using System.Collections.Generic;

namespace CoworkBridge
{
	[Serializable]
	public class CompilerErrorList
	{
		public List<CompilerError> errors = new List<CompilerError>();
	}
}
