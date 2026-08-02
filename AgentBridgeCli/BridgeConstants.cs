namespace AgentBridge.Cli;

internal static class BridgeConstants
{
	public const int ProtocolVersion = 1;
	public const long MaximumHeartbeatAgeMs = 15000;
	public const long MaximumForeignHeartbeatAgeMs = 60000;
	public const string PackageId = "com.elmortem.agentbridge";
}
