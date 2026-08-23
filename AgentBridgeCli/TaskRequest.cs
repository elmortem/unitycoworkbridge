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
}
