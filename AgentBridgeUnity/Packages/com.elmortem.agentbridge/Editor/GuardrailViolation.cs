using System;

namespace AgentBridge
{
	[Serializable]
	public class GuardrailViolation
	{
		public string Reason;
		public int Line;
		public int Column;
	}
}
