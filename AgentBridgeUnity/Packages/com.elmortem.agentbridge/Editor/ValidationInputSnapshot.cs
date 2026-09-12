using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge
{
	// A content digest of everything a validation result depends on: imported sources, assets and
	// their .meta, project settings, package manifests and every resolved local package.
	//
	// Deliberately not the compile fingerprint. That one hashes paths, sizes and write times of a
	// few extensions, which is enough to decide whether to reuse a compile and nowhere near enough
	// to claim a test run describes a known project. A file edited back to its original size with
	// its timestamp restored moves this digest and not that one.
	[Serializable]
	public class ValidationInputSnapshot
	{
		public string Digest = "";
		public int FileCount;
		public long TotalBytes;
		public bool Complete;
		public string Reason = "";
		public string[] Roots = new string[0];
		public string[] ExcludedRoots = new string[0];

		public static ValidationInputSnapshot Incomplete(string reason)
		{
			return new ValidationInputSnapshot { Complete = false, Reason = reason ?? "" };
		}

		// Pure file IO and hashing: safe on a worker thread, and never calls a Unity API.
		//
		// Paths enter the digest relative to their own root and prefixed by the root's index, so
		// moving or renaming a file inside the project moves the digest while moving the whole
		// project does not.
		public static ValidationInputSnapshot Capture(string[] roots, string[] excludedRoots, string context)
		{
			return Capture(roots, excludedRoots, context, null);
		}

		// ignore answers "this file is the bridge's own declared scratch, not an input". It is a
		// callback rather than a path list because the test framework's temporary scenes are
		// recognised by name, and this file must stay free of Unity APIs.
		public static ValidationInputSnapshot Capture(
			string[] roots,
			string[] excludedRoots,
			string context,
			Func<string, bool> ignore)
		{
			var snapshot = new ValidationInputSnapshot
			{
				Roots = roots ?? new string[0],
				ExcludedRoots = excludedRoots ?? new string[0]
			};

			using (var sha = SHA256.Create())
			using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
			{
				Append(stream, "context\n" + (context ?? "") + "\n");

				for (int index = 0; index < snapshot.Roots.Length; index++)
				{
					string root = snapshot.Roots[index];
					if (string.IsNullOrEmpty(root))
					{
						continue;
					}

					string prefix = "r" + index;
					Append(stream, "root\n" + prefix + "\n");

					List<string> files;
					string error;
					if (!TryCollect(root, snapshot.ExcludedRoots, ignore, out files, out error))
					{
						snapshot.Complete = false;
						snapshot.Reason = error;
						return snapshot;
					}

					foreach (string file in files)
					{
						string relative = prefix + "/" + RelativeTo(root, file);
						byte[] content;
						try
						{
							content = File.ReadAllBytes(file);
						}
						catch (FileNotFoundException)
						{
							// A file that vanished between the walk and the read is a real change;
							// the digest records the absence and the monitor reports the event.
							Append(stream, "missing\n" + relative + "\n");
							continue;
						}
						catch (DirectoryNotFoundException)
						{
							Append(stream, "missing\n" + relative + "\n");
							continue;
						}
						catch (Exception exception)
						{
							snapshot.Complete = false;
							snapshot.Reason = "input file " + relative + " could not be read: " + exception.Message;
							return snapshot;
						}

						Append(stream, "file\n" + relative + "\n" + content.Length + "\n");
						stream.Write(content, 0, content.Length);
						snapshot.FileCount++;
						snapshot.TotalBytes += content.Length;
					}
				}

				stream.FlushFinalBlock();
				snapshot.Digest = ToHex(sha.Hash);
			}

			snapshot.Complete = true;
			return snapshot;
		}

		private static bool TryCollect(
			string root,
			string[] excludedRoots,
			Func<string, bool> ignore,
			out List<string> files,
			out string error)
		{
			files = new List<string>();
			error = "";

			if (File.Exists(root))
			{
				files.Add(root);
				return true;
			}

			if (!Directory.Exists(root))
			{
				// An input root that cannot be read makes the whole snapshot unknown. Hashing what
				// is left would describe a project nobody asked about.
				error = "input root is not reachable: " + root;
				return false;
			}

			try
			{
				foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				{
					if (IsExcluded(file, excludedRoots))
					{
						continue;
					}

					if (ignore != null && ignore(file))
					{
						continue;
					}

					files.Add(file);
				}
			}
			catch (Exception exception)
			{
				error = "input root " + root + " could not be walked: " + exception.Message;
				return false;
			}

			files.Sort(StringComparer.OrdinalIgnoreCase);
			return true;
		}

		// Everything the digest must not include. Library, Temp, Logs and obj are editor output,
		// and a declared fixture root is only excluded when it was provably empty beforehand.
		public static bool IsExcluded(string path, string[] excludedRoots)
		{
			if (excludedRoots == null)
			{
				return false;
			}

			string normalized = Normalize(path);
			foreach (string root in excludedRoots)
			{
				if (string.IsNullOrEmpty(root))
				{
					continue;
				}

				string prefix = Normalize(root);
				if (!prefix.EndsWith("/", StringComparison.Ordinal))
				{
					prefix += "/";
				}

				if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		public static string Normalize(string path)
		{
			return (path ?? "").Replace('\\', '/');
		}

		private static string RelativeTo(string root, string file)
		{
			string normalizedRoot = Normalize(root).TrimEnd('/');
			string normalizedFile = Normalize(file);
			if (normalizedFile.Length > normalizedRoot.Length
				&& normalizedFile.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
			{
				return normalizedFile.Substring(normalizedRoot.Length + 1);
			}

			return Path.GetFileName(normalizedFile);
		}

		private static void Append(Stream stream, string text)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(text);
			stream.Write(bytes, 0, bytes.Length);
		}

		private static string ToHex(byte[] bytes)
		{
			var builder = new StringBuilder(bytes.Length * 2);
			foreach (byte value in bytes)
			{
				builder.Append(value.ToString("x2"));
			}

			return builder.ToString();
		}
	}
}
