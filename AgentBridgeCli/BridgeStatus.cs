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
	public int LoadedTaskAssemblies { get; set; }
	public int ExecutedTasks { get; set; }
	public string? ActiveTaskId { get; set; }
	public string[] Capabilities { get; set; } = Array.Empty<string>();
}
