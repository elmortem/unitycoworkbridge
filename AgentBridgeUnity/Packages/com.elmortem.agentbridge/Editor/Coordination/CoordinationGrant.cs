using System;
using System.Collections.Generic;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationGrant
	{
		public string Session = "";
		public string RequestId = "";
		public string Token = "";
		public string Epoch = "";
		public int ParticipantGeneration = 1;
		public string Kind = "";
		public string State = CoordinationLimits.GrantActive;
		public long DeadlineMs;
		public long GrantedAtMs;
		public string[] ActiveTaskIds = new string[0];
		public List<CoordinationStepUse> Steps = new List<CoordinationStepUse>();

		public void Normalize()
		{
			Session = CoordinationText.Safe(Session);
			RequestId = CoordinationText.Safe(RequestId);
			Token = CoordinationText.Safe(Token);
			Epoch = CoordinationText.Safe(Epoch);
			Kind = CoordinationText.Safe(Kind);
			State = CoordinationText.Safe(State);
			ActiveTaskIds = CoordinationText.Safe(ActiveTaskIds);
			if (Steps == null)
			{
				Steps = new List<CoordinationStepUse>();
			}

			foreach (CoordinationStepUse step in Steps)
			{
				step.Normalize();
			}

			if (ParticipantGeneration < 1)
			{
				ParticipantGeneration = 1;
			}
		}

		public CoordinationStepUse FindStep(string stepId)
		{
			foreach (CoordinationStepUse step in Steps)
			{
				if (string.Equals(step.StepId, stepId, StringComparison.Ordinal))
				{
					return step;
				}
			}

			return null;
		}
	}
}
