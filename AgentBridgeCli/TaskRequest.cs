namespace AgentBridge.Cli;

internal sealed class TaskRequest
{
	public string Id { get; set; } = "";
	public string Kind { get; set; } = "";
	public string PayloadFile { get; set; } = "";
	public string TestMode { get; set; } = "";
	public string[] AssemblyNames { get; set; } = Array.Empty<string>();
	public string[] TestNames { get; set; } = Array.Empty<string>();
	public string[] CategoryNames { get; set; } = Array.Empty<string>();
	public string AgentSessionId { get; set; } = "";
	public string Note { get; set; } = "";
	public int PlaySeconds { get; set; }
	public bool Fresh { get; set; }

	// coordination-v1. Empty unless the caller passed --coord-window/--coord-step, so a legacy
	// submission to an uncoordinated project keeps its old shape.
	public string CoordinationWindowToken { get; set; } = "";
	public string CoordinationStepId { get; set; } = "";
}
