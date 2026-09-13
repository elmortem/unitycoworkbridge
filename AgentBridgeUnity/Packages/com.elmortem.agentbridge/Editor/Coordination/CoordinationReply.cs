using System;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationReply
	{
		public bool Ok;
		public string Code = "";
		public string ProjectId = "";
		public string Epoch = "";
		public long Revision;
		public string Session = "";
		public string RequestId = "";
		public string State = "";
		public string Token = "";
		public string[] Blockers = new string[0];
		public string Message = "";
		public string[] TaskIds = new string[0];
		public string Reason = "";

		// True when the engine mutated the state and the caller must commit the transaction.
		// Never serialized into the command answer: it is a store detail, not part of the contract.
		[NonSerialized] public bool Changed;

		public static CoordinationReply Fail(string code, string message)
		{
			return new CoordinationReply { Ok = false, Code = code, Message = CoordinationText.Safe(message) };
		}

		public static CoordinationReply Success(string code)
		{
			return new CoordinationReply { Ok = true, Code = code };
		}
	}
}
