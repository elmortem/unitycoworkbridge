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

		// The value decides whether a cached result may be served, and the case it has to catch is
		// an agent that edits a file and asks the very next moment. So the fingerprint itself is
		// never memoized; only a file's hash is reused, and only while its stamp — change time
		// included — has not moved. An edit moves the change time, and a file changed within
		// InputHashCache.RacyWindowTicks of the capture is read and never remembered.
		public static string Current()
		{
			return Capture(BridgePaths.ProjectRoot, CompileInputContext.Roots, CompileInputContext.Context, CompileInputContext.HashCache);
		}

		// The caller captures the Unity-derived project path before dispatching to a worker.
		public static string Capture(string projectRoot)
		{
			return Capture(projectRoot, new[] { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages"), Path.Combine(projectRoot, "ProjectSettings") }, "");
		}

		public static string Capture(string projectRoot, string[] roots, string context)
		{
			return Capture(projectRoot, roots, context, null);
		}

		public static string Capture(string projectRoot, string[] roots, string context, InputHashCache cache)
		{
			var files = new List<string>();
			foreach (string root in roots)
			{
				if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Compile input root unavailable: " + root);
				Collect(root, files);
			}
			if (Directory.Exists(Path.Combine(projectRoot, "ProjectSettings")))
				foreach (string file in Directory.EnumerateFiles(Path.Combine(projectRoot, "ProjectSettings"), "*", SearchOption.AllDirectories))
					if (!files.Contains(file)) files.Add(file);
			AddIfExists(Path.Combine(projectRoot, "Packages", "manifest.json"), files);
			AddIfExists(Path.Combine(projectRoot, "Packages", "packages-lock.json"), files);

			files.Sort(StringComparer.Ordinal);

			var builder = new StringBuilder("compile-v3|" + context + "\n");
			using (var hasher = new InputFileHasher(cache))
			{
				foreach (string file in files)
				{
					// Length and hash describe the same bytes, so a torn read needs no retry here.
					long length;
					string hash;
					hasher.TryHash(file, out length, out hash);
					builder.Append(file.Replace('\\', '/')).Append('|').Append(length).Append('|').Append(hash).Append('\n');
				}

				// Only part of the digest's roots is seen here: append, never forget.
				hasher.Commit(new string[0]);
			}

			using (SHA256 sha = ContentHash.Create())
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
				bool ignored = false;
				foreach (string segment in file.Substring(root.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
					if (segment.StartsWith(".", StringComparison.Ordinal) || segment.EndsWith("~", StringComparison.Ordinal)) { ignored = true; break; }
				if (ignored) continue;
				if (HasTrackedExtension(file))
				{
					files.Add(file);
				}
			}
		}

		private static bool HasTrackedExtension(string file)
		{
			if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) file = file.Substring(0, file.Length - 5);
			if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == "package.json") return true;
			foreach (string extension in Extensions)
			{
				if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		// The files a compilation reads, and nothing it merely references: a change here can only
		// matter to a result after the editor compiles it.
		public static bool IsSource(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			string name = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 5) : path;
			foreach (string extension in Extensions)
			{
				if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
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
