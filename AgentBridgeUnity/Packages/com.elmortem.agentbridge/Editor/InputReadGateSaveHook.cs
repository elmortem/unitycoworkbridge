using UnityEditor;

namespace AgentBridge
{
	// Shuts InputReadGate when Unity announces a save of assets or scenes. The save, with its rename
	// over the target, runs synchronously after this callback, so the next editor tick is the first
	// moment the files are free again.
	public sealed class InputReadGateSaveHook : AssetModificationProcessor
	{
		private static string[] OnWillSaveAssets(string[] paths)
		{
			if (InputReadGate.Close())
			{
				EditorApplication.delayCall += InputReadGate.Open;
			}

			return paths;
		}
	}
}
