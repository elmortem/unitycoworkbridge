using System;

namespace AgentBridge.Queueing
{
	[Serializable]
	public sealed class QueueReply
	{
		public bool Ok;
		public string RequestHash = "";
		public string Code = "";
		public string Message = "";
		public string ResultJson = "";
		public string CoordinationJson = "";
		public QueueTaskInfo Task;
		public QueueTaskInfo[] Tasks = new QueueTaskInfo[0];
	}
}
