namespace AgentBridge.Cli;

internal static class ProjectLocator
{
	public static string? Resolve(string? explicitPath, string workingDirectory)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(explicitPath))
			{
				var candidate = Path.GetFullPath(explicitPath, workingDirectory);
				return IsUnityProject(candidate) ? Normalize(candidate) : null;
			}

			var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
			while (directory != null)
			{
				if (IsUnityProject(directory.FullName))
				{
					return Normalize(directory.FullName);
				}

				directory = directory.Parent;
			}

			return null;
		}
		catch
		{
			return null;
		}
	}

	public static bool IsUnityProject(string path)
	{
		return Directory.Exists(Path.Combine(path, "Assets"))
			&& File.Exists(Path.Combine(path, "Packages", "manifest.json"))
			&& File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"));
	}

	private static string Normalize(string path)
	{
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
	}
}
