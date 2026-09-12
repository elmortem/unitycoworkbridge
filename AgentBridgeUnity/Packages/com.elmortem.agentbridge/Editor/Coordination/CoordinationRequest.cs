using System;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationRequest
	{
		public string Id = "";
		public string Uuid = "";
		public long Ticket;
		public string Session = "";
		public string Kind = "";
		public string PayloadDigest = "";

		// Allocated when the request is created, handed out when the right is finally issued.
		// A waiting edit request may be granted long after its owner's process moved on, and the
		// engine must not invent a token out of thin air at that moment.
		public string Token = "";
		public CoordinationPlan Plan = new CoordinationPlan();
		public string State = CoordinationLimits.StateWaiting;
		public string Reason = "";
		public int Seconds;
		public long CreatedAtMs;
		public long UpdatedAtMs;

		public void Normalize()
		{
			Id = CoordinationText.Safe(Id);
			Uuid = CoordinationText.Safe(Uuid);
			Session = CoordinationText.Safe(Session);
			Kind = CoordinationText.Safe(Kind);
			PayloadDigest = CoordinationText.Safe(PayloadDigest);
			Token = CoordinationText.Safe(Token);
			State = CoordinationText.Safe(State);
			Reason = CoordinationText.Safe(Reason);
			if (Plan == null)
			{
				Plan = new CoordinationPlan();
			}

			Plan.Normalize();
		}

		public bool IsTerminal
		{
			get
			{
				return State == CoordinationLimits.StateClosed
					|| State == CoordinationLimits.StateCanceled
					|| State == CoordinationLimits.StateRejected;
			}
		}
	}
}
