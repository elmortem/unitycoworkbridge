using System;

namespace AgentBridge
{
	// The persistent record of a sanctioned agent play session. It outlives the domain
	// reload that entering play mode triggers, so PlaySessionManager can pick the session
	// up again on the other side and finalize the journal record that opened it.
	[Serializable]
	public class PlaySessionState
	{
		public string TaskId;
		public string OwnerAgentSessionId;
		public string Phase;
		public string StartedAtUtc;
		public string DeadlineUtc;
		public string PendingStopTaskId;
		public string StopReason;
	}
}
