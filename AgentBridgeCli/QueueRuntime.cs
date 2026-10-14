using System.Security.Cryptography;

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
		return Path.Combine(root, Path.GetFileName(executable));
	}
}
