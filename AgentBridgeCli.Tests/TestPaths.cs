internal static class TestPaths
{
	// macOS temp roots can contain /var -> /private/var. Resolve every ancestor, not just
	// the final fixture directory, before testing commands that require physical project paths.
	public static string PhysicalDirectory(string path)
	{
		var directory = new DirectoryInfo(path);
		if (directory.Parent == null) return directory.FullName;
		var physical = new DirectoryInfo(Path.Combine(PhysicalDirectory(directory.Parent.FullName), directory.Name));
		try
		{
			if ((physical.Attributes & FileAttributes.ReparsePoint) != 0)
				return physical.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? physical.FullName;
		}
		catch (UnauthorizedAccessException)
		{
			// A sandbox can expose the temp directory without exposing ancestor metadata.
			// The fixture still checks the production path policy before running its scenarios.
		}
		return physical.FullName;
	}
}
