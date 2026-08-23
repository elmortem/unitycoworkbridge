using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge
{
	public static class CompileFingerprint
	{
		private static readonly string[] Extensions = { ".cs", ".asmdef", ".asmref", ".rsp" };

		// Deliberately never memoized. The value is read to decide whether a cached result may be
		// served, and the case it has to catch is an agent that edits a file and asks the very
		// next moment — a memo of any length would answer that question with the state from
		// before the edit. It is only ever computed while a tests or compile task is actually
		// waiting, and it costs a fraction of the run it saves.
		public static string Current()
		{
			string projectRoot = BridgePaths.ProjectRoot;
			var files = new List<string>();

			Collect(Path.Combine(projectRoot, "Assets"), files);
			Collect(Path.Combine(projectRoot, "Packages"), files);
			AddIfExists(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"), files);
			AddIfExists(Path.Combine(projectRoot, "Packages", "manifest.json"), files);
			AddIfExists(Path.Combine(projectRoot, "Packages", "packages-lock.json"), files);

			files.Sort(StringComparer.Ordinal);

			var builder = new StringBuilder();
			foreach (string file in files)
			{
				var info = new FileInfo(file);
				builder.Append(file.Substring(projectRoot.Length))
					.Append('|').Append(info.Length)
					.Append('|').Append(info.LastWriteTimeUtc.Ticks)
					.Append('\n');
			}

			using (SHA256 sha = SHA256.Create())
			{
				byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
				var hex = new StringBuilder(hash.Length * 2);
				foreach (byte b in hash)
				{
					hex.Append(b.ToString("x2"));
				}

				return hex.ToString();
			}
		}

		// One walk filtered by extension rather than one walk per pattern: the tree is large and
		// this runs on the coordinator tick.
		private static void Collect(string root, List<string> files)
		{
			if (!Directory.Exists(root))
			{
				return;
			}

			foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
			{
				if (HasTrackedExtension(file))
				{
					files.Add(file);
				}
			}
		}

		private static bool HasTrackedExtension(string file)
		{
			foreach (string extension in Extensions)
			{
				if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		private static void AddIfExists(string path, List<string> files)
		{
			if (File.Exists(path))
			{
				files.Add(path);
			}
		}
	}
}
