namespace AgentBridge.Cli;

internal sealed class BridgePaths
{
	public BridgePaths(string projectRoot)
	{
		ProjectRoot = projectRoot;
		WorkingRoot = Path.Combine(projectRoot, "Library", "AgentBridge");
		Inbox = Path.Combine(WorkingRoot, "Inbox");
		Journal = Path.Combine(WorkingRoot, "Journal");
		StatusFile = Path.Combine(WorkingRoot, "status.json");
		HeartbeatFile = Path.Combine(WorkingRoot, "heartbeat");
	}

	public string ProjectRoot { get; }
	public string WorkingRoot { get; }
	public string Inbox { get; }
	public string Journal { get; }
	public string StatusFile { get; }
	public string HeartbeatFile { get; }
}
