using System;

namespace AgentBridge.Coordination
{
	[Serializable]
	public class CoordinationStep
	{
		public string Id = "";
		public string Kind = "";
		public string Mode = "";
		public string[] Assemblies = new string[0];
		public string[] Tests = new string[0];
		public string[] Categories = new string[0];
		public string PayloadSha256 = "";
		public bool Fresh;

		public void Normalize()
		{
			Id = CoordinationText.Safe(Id);
			Kind = CoordinationText.Safe(Kind);
			Mode = CoordinationText.Safe(Mode);
			Assemblies = CoordinationText.Safe(Assemblies);
			Tests = CoordinationText.Safe(Tests);
			Categories = CoordinationText.Safe(Categories);
			PayloadSha256 = CoordinationText.Safe(PayloadSha256);
		}
	}
}
