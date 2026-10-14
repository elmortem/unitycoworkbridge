using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal sealed class QueueTask
{
	public QueueTaskInfo Info = new();
	public string RequestJson = "";
	public string Payload = "";
	public string Hash = "";
	public string AuthorizedCancelTarget = "";
	public string ClaimOwner = "";
	public string ResultJson = "";
	public bool Published;
	public long Order;
}
