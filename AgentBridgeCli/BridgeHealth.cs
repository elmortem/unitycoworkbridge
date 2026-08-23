namespace AgentBridge.Cli;

internal sealed class BridgeHealth
{
	public bool Ok { get; set; }
	public string Code { get; set; } = "";
	public string ProjectPath { get; set; } = "";
	public string WorkingRoot { get; set; } = "";
	public string ScratchDir { get; set; } = "";
	public string ClientOs { get; set; } = "";
	public string HostOs { get; set; } = "";
	public bool ForeignHost { get; set; }
	public bool PackageDeclared { get; set; }
	public bool StatusFileExists { get; set; }
	public bool HeartbeatExists { get; set; }
	public long? HeartbeatAgeMs { get; set; }
	public long HeartbeatToleranceMs { get; set; }
	public bool? EditorProcessAlive { get; set; }
	public bool ProtocolCompatible { get; set; }
	public bool ProjectMatches { get; set; }
	public string ProjectMatchedBy { get; set; } = "";
	public bool BridgeReady { get; set; }
	public bool CSharpReady { get; set; }
	public List<string> Problems { get; set; } = new();
	public List<string> Warnings { get; set; } = new();
	public BridgeStatus? Bridge { get; set; }
}
