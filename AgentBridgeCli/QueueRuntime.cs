using System.Security.Cryptography;
using AgentBridge.Queueing;

namespace AgentBridge.Cli;

internal static class QueueRuntime
{
	// The long-lived process must not lock the installed CLI on Windows. Snapshot only its
	// executable/runtime files; each content version is shared by all local project servers.
	internal static string Snapshot(string executable, string assembly)
	{
		string source = string.IsNullOrEmpty(assembly) ? executable : assembly;
		using var stream = File.OpenRead(source);
		string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		string root = Path.Combine(Path.GetTempPath(), "AgentBridgeQueueRuntime", digest);
		Directory.CreateDirectory(root);
		var sources = new List<string> { executable };
		if (!string.IsNullOrEmpty(assembly))
		{
			sources.Add(assembly);
			sources.Add(Path.ChangeExtension(assembly, ".deps.json"));
			sources.Add(Path.ChangeExtension(assembly, ".runtimeconfig.json"));
		}
		foreach (string file in sources.Distinct())
		{
			string target = Path.Combine(root, Path.GetFileName(file));
			if (File.Exists(target)) continue;
			string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.Copy(file, temporary);
				try { File.Move(temporary, target); }
				catch (IOException) when (File.Exists(target)) { }
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
		}
		return PhysicalPath(Path.Combine(root, Path.GetFileName(executable)));
	}

	internal static bool IsCurrent(QueueLaunch launch)
	{
		// Without an apphost the executable is just the shared dotnet host. In that case
		// the assembly argument identifies our runtime, even if the host is a PATH name.
		bool managed = launch.Arguments.Length > 0 && string.Equals(Path.GetExtension(launch.Arguments[0]), ".dll", StringComparison.OrdinalIgnoreCase);
#pragma warning disable IL3000
		string current = managed ? typeof(QueueRuntime).Assembly.Location : Environment.ProcessPath ?? "";
#pragma warning restore IL3000
		string requested = managed ? launch.Arguments[0] : launch.Executable;
		return current.Length > 0 && string.Equals(PhysicalPath(requested), PhysicalPath(current),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	private static string PhysicalPath(string path)
	{
		return ResolvePath(Path.GetFullPath(path), 0);
	}

	private static string ResolvePath(string path, int links)
	{
		if (links > 40) throw new IOException("Too many links in queue runtime path: " + path);
		string? parent = Path.GetDirectoryName(path);
		if (parent == null) return path;
		string resolved = Path.Combine(ResolvePath(parent, links), Path.GetFileName(path));
		// Resolving only the executable's final link misses aliases in its ancestors,
		// notably the macOS temporary directory under /var -> /private/var.
		FileSystemInfo entry = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
		FileSystemInfo? target;
		try { target = entry.ResolveLinkTarget(returnFinalTarget: true); }
		catch (UnauthorizedAccessException) { return resolved; } // Traversal can be allowed without ancestor metadata access.
		return target == null ? resolved : ResolvePath(target.FullName, links + 1);
	}
}
