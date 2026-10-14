using System;

namespace AgentBridge.Queueing
{
	[Serializable]
	public sealed class QueueTaskInfo
	{
		public string Id = "";
		public string Kind = "";
		public string Status = "queued";
		public string AgentSessionId = "";
		public string BatchRequestId = "";
		public string AcceptedAtUtc = "";
		public string StartedAtUtc = "";
		public bool CancelRequested;
		public int Position;
	}
}
