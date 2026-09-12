using System;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationCommand
	{
		public string Op = "";
		public string Session = "";
		public string SpecId = "";
		public string Owner = "";
		public string RepoRoot = "";
		public string[] Paths = new string[0];
		public string Uuid = "";
		public string Kind = "";
		public int Seconds;
		public string Token = "";
		public CoordinationPlan Plan = new CoordinationPlan();
		public string TargetSession = "";
		public string Reason = "";
		public string RequestId = "";
		public string TaskId = "";
		public string StepId = "";

		// What the editor is actually about to run. Compared field by field against the planned
		// step, so a token cannot authorise a payload the plan never declared.
		public CoordinationStep Actual = new CoordinationStep();

		// Supplied by the caller so the engine stays a pure function: token values and request
		// ids are never invented from a clock or a random source inside the state machine.
		public string Nonce = "";

		public void Normalize()
		{
			Op = CoordinationText.Safe(Op);
			Session = CoordinationText.Safe(Session);
			SpecId = CoordinationText.Safe(SpecId);
			Owner = CoordinationText.Safe(Owner);
			RepoRoot = CoordinationText.Safe(RepoRoot);
			Paths = CoordinationText.Safe(Paths);
			Uuid = CoordinationText.Safe(Uuid);
			Kind = CoordinationText.Safe(Kind);
			Token = CoordinationText.Safe(Token);
			TargetSession = CoordinationText.Safe(TargetSession);
			Reason = CoordinationText.Safe(Reason);
			RequestId = CoordinationText.Safe(RequestId);
			TaskId = CoordinationText.Safe(TaskId);
			StepId = CoordinationText.Safe(StepId);
			Nonce = CoordinationText.Safe(Nonce);
			if (Plan == null)
			{
				Plan = new CoordinationPlan();
			}

			Plan.Normalize();
			if (Actual == null)
			{
				Actual = new CoordinationStep();
			}

			Actual.Normalize();
		}
	}
}
