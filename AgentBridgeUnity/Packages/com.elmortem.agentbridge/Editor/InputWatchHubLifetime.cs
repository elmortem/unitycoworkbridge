using UnityEditor;

namespace AgentBridge
{
	// Shared observers outlive the monitor that opened them, so something has to put them down when
	// the domain goes away. Shutdown only closes the watchers: every open monitor keeps the verdict
	// it had, which is why the order against other reload callbacks does not matter.
	[InitializeOnLoad]
	public static class InputWatchHubLifetime
	{
		static InputWatchHubLifetime()
		{
			AssemblyReloadEvents.beforeAssemblyReload -= InputWatchHub.Shutdown;
			AssemblyReloadEvents.beforeAssemblyReload += InputWatchHub.Shutdown;
			EditorApplication.quitting -= InputWatchHub.Shutdown;
			EditorApplication.quitting += InputWatchHub.Shutdown;
		}
	}
}
