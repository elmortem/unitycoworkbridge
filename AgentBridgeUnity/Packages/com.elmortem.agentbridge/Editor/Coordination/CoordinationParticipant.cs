using System;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationParticipant
	{
		public string Session = "";
		public string SpecId = "";
		public string Owner = "";
		public string RepoRoot = "";
		public string[] Paths = new string[0];
		public int Generation = 1;
		public string Lifecycle = CoordinationLimits.LifecycleIdle;
		public long RegisteredAtMs;
		public long LastSeenMs;

		public void Normalize()
		{
			Session = CoordinationText.Safe(Session);
			SpecId = CoordinationText.Safe(SpecId);
			Owner = CoordinationText.Safe(Owner);
			RepoRoot = CoordinationText.Safe(RepoRoot);
			Paths = CoordinationText.Safe(Paths);
			Lifecycle = CoordinationText.Safe(Lifecycle);
			if (Generation < 1)
			{
				Generation = 1;
			}
		}
	}
}
