namespace AgentBridge.Cli;

internal sealed class QueuedTaskStatus
{
	public string Id { get; set; } = "";
	public string AgentSessionId { get; set; } = "";
	public int Position { get; set; }
}
