using System.Text.Json;
using AgentBridge.Coordination;

namespace AgentBridge.Cli;

// Where the Unity executable for a project lives. Pure functions: the environment and file reads
// come from outside, so the candidate order is testable without an installed Unity.
internal static class EditorLocator
{
	private const string VersionKey = "m_EditorVersion:";

	// The value of the "m_EditorVersion: X" line of ProjectSettings/ProjectVersion.txt; "" when absent.
	public static string ProjectVersion(string projectRoot)
	{
		if (!SharedFile.TryReadAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt"), out var text))
		{
			return "";
		}

		foreach (var line in text.Split('\n'))
		{
			var trimmed = line.Trim();
			if (trimmed.StartsWith(VersionKey, StringComparison.Ordinal))
			{
				return trimmed[VersionKey.Length..].Trim();
			}
		}

		return "";
	}

	// The binary inside a macOS bundle: "<X>.app" → "<X>.app/Contents/MacOS/Unity", otherwise unchanged.
	public static string Executable(string path)
	{
		var trimmed = path.TrimEnd('/', '\\');
		return trimmed.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? trimmed + "/Contents/MacOS/Unity" : path;
	}

	// Candidates in order, without existence checks and without repeats: the explicit path, the path
	// the editor last reported for the same version, the Hub secondary install root, the Hub default root.
	public static List<string> Candidates(string version, string? explicitPath, string? statusEditorPath,
		string? statusVersion, string os, Func<string, string?> environment, Func<string, string?> readText)
	{
		var result = new List<string>();
		void Add(string? path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return;
			}

			var executable = Executable(path);
			if (!result.Contains(executable, os == HostPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
			{
				result.Add(executable);
			}
		}

		Add(explicitPath);
		if (version.Length > 0 && string.Equals(statusVersion, version, StringComparison.Ordinal))
		{
			Add(statusEditorPath);
		}

		if (version.Length == 0)
		{
			return result;
		}

		var (hubSettings, defaultRoot) = Roots(os, environment);
		if (hubSettings != null)
		{
			Add(InRoot(SecondaryRoot(hubSettings, os, readText), version, os));
		}

		Add(InRoot(defaultRoot, version, os));
		return result;
	}

	// The first existing file among Candidates with the real environment and file system; otherwise null.
	public static string? Resolve(string version, string? explicitPath, BridgeStatus? status)
	{
		return CandidatesHere(version, explicitPath, status).FirstOrDefault(File.Exists);
	}

	// Candidates with the real environment and file system, for the editor_not_found detail.
	public static List<string> CandidatesHere(string version, string? explicitPath, BridgeStatus? status)
	{
		return Candidates(version, explicitPath, status?.EditorPath, status?.UnityVersion, HostPlatform.Current,
			Environment.GetEnvironmentVariable,
			path => SharedFile.TryReadAllText(path, out var text) ? text : null);
	}

	// The Hub settings folder holding secondaryInstallPath.json, and the Hub default install root.
	private static (string? HubSettings, string? DefaultRoot) Roots(string os, Func<string, string?> environment)
	{
		var home = environment("HOME");
		return os switch
		{
			HostPlatform.Windows => (Join(environment("APPDATA"), "UnityHub", os), Join(Join(Join(environment("ProgramFiles"), "Unity", os), "Hub", os), "Editor", os)),
			HostPlatform.MacOs => (Join(Join(Join(home, "Library", os), "Application Support", os), "UnityHub", os), "/Applications/Unity/Hub/Editor"),
			HostPlatform.Linux => (Join(Join(home, ".config", os), "UnityHub", os), Join(Join(Join(home, "Unity", os), "Hub", os), "Editor", os)),
			_ => (null, null)
		};
	}

	private static string? SecondaryRoot(string hubSettings, string os, Func<string, string?> readText)
	{
		var text = readText(Join(hubSettings, "secondaryInstallPath.json", os)!);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize<string>(text);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? InRoot(string? root, string version, string os)
	{
		var versionRoot = Join(root, version, os);
		return os switch
		{
			HostPlatform.Windows => Join(Join(versionRoot, "Editor", os), "Unity.exe", os),
			HostPlatform.MacOs => Join(versionRoot, "Unity.app", os),
			_ => Join(Join(versionRoot, "Editor", os), "Unity", os)
		};
	}

	// Joins with the separator of the target os, not of the machine running the code.
	private static string? Join(string? root, string name, string os)
	{
		if (string.IsNullOrWhiteSpace(root))
		{
			return null;
		}

		var separator = os == HostPlatform.Windows ? '\\' : '/';
		return root.TrimEnd('/', '\\') + separator + name;
	}
}
