using System;
using System.Collections.Generic;

namespace AgentBridge
{
	[Serializable]
	public class ContentionInfo
	{
		public int WaitingSessions;
		public int OldestWaitSeconds;
		public List<string> Notes = new List<string>();
	}
}
