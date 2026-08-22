using UnityEngine;

namespace AgentBridge.Ui
{
	// A single object reference queued during the node walk and applied after
	// all mutation actions of the task have run.
	public class UiRefEntry
	{
		public Component Component;
		public string Property;
		public string Spec;
		public GameObject RelativeRoot;
	}
}
