using System;

namespace AgentBridge.Coordination
{
	// Written once, next to the state, and never cleared by a restart. It is what tells a missing
	// state.json apart from a project that never had a coordinator.
	[Serializable]
	public class CoordinationMarker
	{
		public int SchemaVersion = CoordinationLimits.SchemaVersion;
		public string ProjectId = "";
		public string Epoch = "";
		public long CreatedAtMs;
	}
}
