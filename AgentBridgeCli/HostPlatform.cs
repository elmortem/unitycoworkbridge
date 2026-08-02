namespace AgentBridge.Cli;

internal static class HostPlatform
{
	public const string Windows = "windows";
	public const string MacOs = "macos";
	public const string Linux = "linux";
	public const string Unknown = "unknown";

	public static string Current
	{
		get
		{
			if (OperatingSystem.IsWindows())
			{
				return Windows;
			}

			if (OperatingSystem.IsMacOS())
			{
				return MacOs;
			}

			if (OperatingSystem.IsLinux())
			{
				return Linux;
			}

			return Unknown;
		}
	}

	public static bool IsForeign(string? reportedOs)
	{
		if (string.IsNullOrWhiteSpace(reportedOs))
		{
			return false;
		}

		return !string.Equals(reportedOs.Trim(), Current, StringComparison.OrdinalIgnoreCase);
	}
}
