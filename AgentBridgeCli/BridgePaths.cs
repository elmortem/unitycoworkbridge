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
		Scratch = Path.Combine(projectRoot, "Temp", "AgentBridge");
		AssetsRoot = Path.Combine(projectRoot, "Assets");
	}

	public string ProjectRoot { get; }
	public string WorkingRoot { get; }
	public string Inbox { get; }
	public string Journal { get; }
	public string StatusFile { get; }
	public string HeartbeatFile { get; }
	public string Scratch { get; }
	public string AssetsRoot { get; }

	public void EnsureScratch()
	{
		try
		{
			Directory.CreateDirectory(Scratch);
		}
		catch
		{
		}
	}

	public bool IsInsideAssets(string payloadPath)
	{
		string fullPayloadPath;
		try
		{
			fullPayloadPath = Path.GetFullPath(payloadPath);
		}
		catch
		{
			return false;
		}

		var assetsPrefix = Path.GetFullPath(AssetsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			+ Path.DirectorySeparatorChar;
		var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return fullPayloadPath.StartsWith(assetsPrefix, comparison);
	}
}
