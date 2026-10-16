using System;

namespace AgentBridge.Queueing
{
	[Serializable]
	public sealed class QueueDiscussion
	{
		public string Id = "";
		public string Kind = "";
		public string Session = "";
		public string Owner = "";
		public string TaskId = "";
		public string RequestKey = "";
		public string Reason = "";
		public int Seconds;
		public string State = "pending";
		public long ReplyByMs;
		public long WaitUntilMs;
		public long Revision;
		public string BoundWorkId = "";
		public string[] Blockers = new string[0];
		public QueueDiscussionAnswer[] Answers = new QueueDiscussionAnswer[0];
	}

	[Serializable]
	public sealed class QueueDiscussionAnswer
	{
		public string Session = "";
		public string Decision = "";
		public string Reason = "";
		public int Seconds;
	}
}
