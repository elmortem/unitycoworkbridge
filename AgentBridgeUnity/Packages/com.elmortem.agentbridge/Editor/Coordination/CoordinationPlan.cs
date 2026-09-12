using System;
using System.Collections.Generic;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationPlan
	{
		public List<CoordinationStep> Steps = new List<CoordinationStep>();
		public string[] ArtifactRoots = new string[0];
		public string[] FixtureRoots = new string[0];

		public void Normalize()
		{
			if (Steps == null)
			{
				Steps = new List<CoordinationStep>();
			}

			for (int i = 0; i < Steps.Count; i++)
			{
				if (Steps[i] == null)
				{
					Steps[i] = new CoordinationStep();
				}

				Steps[i].Normalize();
			}

			ArtifactRoots = CoordinationText.Safe(ArtifactRoots);
			FixtureRoots = CoordinationText.Safe(FixtureRoots);
		}

		public CoordinationStep Find(string stepId)
		{
			foreach (CoordinationStep step in Steps)
			{
				if (string.Equals(step.Id, stepId, StringComparison.Ordinal))
				{
					return step;
				}
			}

			return null;
		}
	}
}
