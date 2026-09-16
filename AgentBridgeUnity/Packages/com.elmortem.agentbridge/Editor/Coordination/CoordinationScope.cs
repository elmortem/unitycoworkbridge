using System;
using System.Collections.Generic;
using System.Text;

namespace AgentBridge.Coordination
{
	// Scope paths are repo-relative, use forward slashes and mark a directory with a trailing
	// slash. Overlap is decided on whole segments, so "Foo/" never swallows "Foobar/".
	//
	// The comparison is case-insensitive on every host. That is deliberately conservative: on a
	// case-sensitive filesystem it can serialize two edits that would not actually collide.
	// Registrations may overlap; live writers must not silently share a file.
	public static class CoordinationScope
	{
		private static readonly char[] Forbidden = { '*', '?', '<', '>', '|', '"' };

		public static bool TryNormalize(string[] paths, out string[] normalized, out string error)
		{
			normalized = new string[0];
			error = "";

			if (paths == null || paths.Length == 0)
			{
				return true;
			}

			if (paths.Length > CoordinationLimits.MaxScopePaths)
			{
				error = "scope has more than " + CoordinationLimits.MaxScopePaths + " paths";
				return false;
			}

			var result = new List<string>();
			foreach (string raw in paths)
			{
				string single;
				if (!TryNormalizeOne(raw, out single, out error))
				{
					return false;
				}

				if (!result.Contains(single))
				{
					result.Add(single);
				}
			}

			result.Sort(StringComparer.Ordinal);

			// A scope that overlaps itself is a typo, not a reservation; refusing it here keeps
			// the stored set minimal and the conflict check honest.
			for (int i = 0; i < result.Count; i++)
			{
				for (int j = i + 1; j < result.Count; j++)
				{
					if (Overlaps(result[i], result[j]))
					{
						error = "scope paths overlap each other: " + result[i] + " and " + result[j];
						return false;
					}
				}
			}

			normalized = result.ToArray();
			return true;
		}

		public static bool TryNormalizeOne(string raw, out string normalized, out string error)
		{
			normalized = "";
			error = "";

			if (string.IsNullOrWhiteSpace(raw))
			{
				error = "scope path is empty";
				return false;
			}

			string value = raw.Replace('\\', '/').Trim();
			if (value.IndexOfAny(Forbidden) >= 0)
			{
				error = "scope path contains a glob or a forbidden character: " + raw;
				return false;
			}

			if (value.StartsWith("/", StringComparison.Ordinal) || HasDriveLetter(value))
			{
				error = "scope path must be relative to the repository root: " + raw;
				return false;
			}

			bool directory = value.EndsWith("/", StringComparison.Ordinal);
			string[] segments = value.Split('/');
			var kept = new List<string>();
			foreach (string segment in segments)
			{
				if (segment.Length == 0 || segment == ".")
				{
					continue;
				}

				if (segment == "..")
				{
					error = "scope path escapes the repository root: " + raw;
					return false;
				}

				kept.Add(segment);
			}

			if (kept.Count == 0)
			{
				error = "scope path resolves to the repository root: " + raw;
				return false;
			}

			var builder = new StringBuilder();
			for (int i = 0; i < kept.Count; i++)
			{
				if (i > 0)
				{
					builder.Append('/');
				}

				builder.Append(kept[i]);
			}

			if (directory)
			{
				builder.Append('/');
			}

			normalized = builder.ToString();
			return true;
		}

		public static bool Overlaps(string left, string right)
		{
			if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			return Covers(left, right) || Covers(right, left);
		}

		// True when container is a directory that contains candidate.
		private static bool Covers(string container, string candidate)
		{
			if (!container.EndsWith("/", StringComparison.Ordinal))
			{
				return false;
			}

			return candidate.StartsWith(container, StringComparison.OrdinalIgnoreCase);
		}

		public static bool AnyOverlap(string[] left, string[] right, out string leftPath, out string rightPath)
		{
			leftPath = "";
			rightPath = "";
			if (left == null || right == null)
			{
				return false;
			}

			foreach (string a in left)
			{
				foreach (string b in right)
				{
					if (Overlaps(a, b))
					{
						leftPath = a;
						rightPath = b;
						return true;
					}
				}
			}

			return false;
		}

		public static string Digest(string[] paths)
		{
			if (paths == null || paths.Length == 0)
			{
				return "";
			}

			var builder = new StringBuilder();
			foreach (string path in paths)
			{
				builder.Append(path).Append('\n');
			}

			return CoordinationDigest.Sha256(builder.ToString());
		}

		private static bool HasDriveLetter(string value)
		{
			return value.Length >= 2 && value[1] == ':';
		}
	}
}
