using System;

namespace AgentBridge.Coordination
{
	// Replay protection. A repeated mutation finds its own finished result here instead of
	// creating a second request or handing out a second right.
	[Serializable]
	public class CoordinationTombstone
	{
		public string Session = "";
		public string Key = "";
		public string PayloadDigest = "";
		public string RequestId = "";
		public string State = "";
		public string Code = "";
		public string Reason = "";
		public long ClosedAtMs;

		public void Normalize()
		{
			Session = CoordinationText.Safe(Session);
			Key = CoordinationText.Safe(Key);
			PayloadDigest = CoordinationText.Safe(PayloadDigest);
			RequestId = CoordinationText.Safe(RequestId);
			State = CoordinationText.Safe(State);
			Code = CoordinationText.Safe(Code);
			Reason = CoordinationText.Safe(Reason);
		}
	}
}
