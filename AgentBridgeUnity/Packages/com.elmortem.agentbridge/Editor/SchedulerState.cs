using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class SchedulerState
	{
		public int EditorPid;
		public string HolderAgentSessionId = "";
		public string HolderLastActivityUtc = "";
		public string ContentionStartedUtc = "";
		public bool HolderContextRestored = true;
		public List<SessionContext> Contexts = new List<SessionContext>();
	}
}
