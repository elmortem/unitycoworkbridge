using System;

namespace AgentBridge.Coordination
{
	public sealed class CoordinationStoreException : Exception
	{
		public CoordinationStoreException(string code, string message)
			: base(message)
		{
			Code = code;
		}

		public string Code { get; private set; }
	}
}
