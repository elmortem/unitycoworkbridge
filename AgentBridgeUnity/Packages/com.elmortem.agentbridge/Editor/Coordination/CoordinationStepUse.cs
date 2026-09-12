using System;

namespace AgentBridge.Coordination
{
	// A step is consumed once. The task that consumed it is remembered so the same TaskId may
	// re-attach after a domain reload while a different TaskId is refused.
	[Serializable]
	public class CoordinationStepUse
	{
		public string StepId = "";
		public string TaskId = "";
		public string State = "";
		public string Reason = "";

		public void Normalize()
		{
			StepId = CoordinationText.Safe(StepId);
			TaskId = CoordinationText.Safe(TaskId);
			State = CoordinationText.Safe(State);
			Reason = CoordinationText.Safe(Reason);
		}
	}
}
