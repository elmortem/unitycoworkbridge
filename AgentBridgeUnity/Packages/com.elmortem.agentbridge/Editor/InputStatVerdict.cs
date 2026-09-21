using System.Collections.Generic;

namespace AgentBridge
{
	// What comparing two stat manifests concluded. An incomplete verdict is not "nothing changed":
	// it means the second witness is missing and the run cannot be called valid on its word.
	public sealed class InputStatVerdict
	{
		public bool Complete;
		public string Reason = "";
		public int Changed;
		public List<string> Paths = new List<string>();
	}
}
