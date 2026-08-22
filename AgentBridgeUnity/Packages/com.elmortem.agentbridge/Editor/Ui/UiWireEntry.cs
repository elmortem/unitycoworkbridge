using System.Collections.Generic;
using UnityEngine.UI;

namespace AgentBridge.Ui
{
	// A button onClick wiring queued during the node walk and applied after all
	// mutation actions of the task have run.
	public class UiWireEntry
	{
		public Button Button;
		public IList<object> Wires;
	}
}
