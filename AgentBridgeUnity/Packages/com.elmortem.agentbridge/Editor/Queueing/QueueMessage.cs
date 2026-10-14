using System;

namespace AgentBridge.Queueing
{
	[Serializable]
	public sealed class QueueMessage
	{
		public string Id = "";
		public string Op = "";
		public string TaskId = "";
		public string RequestHash = "";
		public string Session = "";
		public string Incarnation = "";
		public int EditorPid;
		public long EditorStartedTicks;
		public string RequestJson = "";
		public string Payload = "";
		public string CoordinationJson = "";
		public string BeforeId = "";
		public bool Human;
	}
}
