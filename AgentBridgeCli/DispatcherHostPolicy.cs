using AgentBridge.Queueing;

namespace AgentBridge.Cli;

// Who owns the queue dispatcher. ForeignHost is relative to the current process's OS, so the
// same decisions serve both the client and the server.
internal static class DispatcherHostPolicy
{
	public const long IdleExitMs = 120000;
	public const int InspectIntervalMs = 1000;

	public static bool EditorAlive(BridgeHealth health) =>
		health.Bridge != null && health.ProjectMatches && health.EditorProcessAlive != false
		&& health.HeartbeatAgeMs != null && health.HeartbeatAgeMs <= health.HeartbeatToleranceMs;

	public static bool HostKeepsDispatcher(BridgeHealth health) =>
		health.Bridge?.Capabilities.Contains(QueueFiles.HostCapability) == true;

	// True for a process on another OS than the editor: it must neither start nor keep a server.
	public static bool HostOwnsDispatcher(BridgeHealth health) =>
		health.ForeignHost && EditorAlive(health) && HostKeepsDispatcher(health);

	// For a non-current launch: a host server yields only to a launcher it could run itself.
	public static bool ShouldYield(bool artifactExists, bool foreign) => artifactExists || foreign;

	public static bool MayIdleExit(long idleMs, BridgeHealth? health) =>
		idleMs > IdleExitMs && (health == null || health.ForeignHost || !EditorAlive(health));
}
