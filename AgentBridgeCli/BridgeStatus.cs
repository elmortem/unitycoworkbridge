namespace AgentBridge.Cli;

internal sealed class BridgeStatus
{
	public int ProtocolVersion { get; set; }
	public string? PackageVersion { get; set; }
	public string? ProjectPath { get; set; }
	public string? ProjectId { get; set; }
	public string? HostOs { get; set; }
	public string? UnityVersion { get; set; }
	public int EditorPid { get; set; }
	public string? SessionId { get; set; }
	public string? AssemblyBuildTimeUtc { get; set; }
	public bool Enabled { get; set; }
	public string? RoslynSource { get; set; }
	public bool RoslynReady { get; set; }
	public bool SignalTickAvailable { get; set; }
	public bool WakeTimerInstalled { get; set; }
	public string? WakeTimerKind { get; set; }
	public string? InteractionMode { get; set; }
	public bool TelemetryEnabled { get; set; }
	public int LoadedTaskAssemblies { get; set; }
	public int ExecutedTasks { get; set; }
	public string? ActiveTaskId { get; set; }
	public long ActiveTaskElapsedSeconds { get; set; }
	public bool ActiveTaskCancelableByOtherAgents { get; set; }
	public string? QueueBlockReason { get; set; }
	public string? CompilationState { get; set; }
	public string? LastCompileId { get; set; }
	public string? LastCompileFinishedUtc { get; set; }
	public string? QueueBlockedSinceUtc { get; set; }
	public string? HolderAgentSessionId { get; set; }
	public bool IsPlaying { get; set; }
	public string? PlaySessionAgentId { get; set; }
	public string? PlaySessionDeadlineUtc { get; set; }
	public QueuedTaskStatus[] QueuedTasks { get; set; } = Array.Empty<QueuedTaskStatus>();
	public string[] Capabilities { get; set; } = Array.Empty<string>();
	public bool CoordinationActive { get; set; }
	public int CoordinationParticipants { get; set; }
	public long CoordinationRevision { get; set; }
	public string? CoordinationWindowSession { get; set; }
	public string? CoordinationUnavailable { get; set; }
}
