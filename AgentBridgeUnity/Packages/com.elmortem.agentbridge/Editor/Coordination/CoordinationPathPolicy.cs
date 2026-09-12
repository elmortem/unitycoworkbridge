using System;
using System.IO;

namespace AgentBridge.Coordination
{
	// v1 protects an ordinary local physical tree. A network share, a reparse point or a path that
	// does not resolve to itself is refused rather than declared protected.
	public static class CoordinationPathPolicy
	{
		public static bool TryResolveProjectRoot(string projectRoot, out string canonical, out string error)
		{
			canonical = "";
			error = "";

			if (string.IsNullOrWhiteSpace(projectRoot))
			{
				error = "project root is empty";
				return false;
			}

			string full;
			try
			{
				full = Path.GetFullPath(projectRoot);
			}
			catch (Exception exception)
			{
				error = "project root is not a valid path: " + exception.Message;
				return false;
			}

			full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (full.Length == 0)
			{
				error = "project root resolves to nothing";
				return false;
			}

			if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
			{
				error = "coordination v1 does not support UNC or network paths: " + full;
				return false;
			}

			if (!Directory.Exists(full))
			{
				error = "project root does not exist: " + full;
				return false;
			}

			string reparse;
			if (HasReparsePoint(full, out reparse))
			{
				error = "coordination v1 does not support symlinks or junctions in the project path: " + reparse;
				return false;
			}

			if (IsNetworkDrive(full))
			{
				error = "coordination v1 requires a local filesystem: " + full;
				return false;
			}

			canonical = full;
			return true;
		}

		public static string CoordinationRoot(string canonicalProjectRoot)
		{
			return Path.Combine(canonicalProjectRoot, "Library", "AgentBridge", "Coordination");
		}

		private static bool HasReparsePoint(string path, out string offender)
		{
			offender = "";
			string current = path;
			while (!string.IsNullOrEmpty(current))
			{
				try
				{
					if (Directory.Exists(current)
						&& (new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0)
					{
						offender = current;
						return true;
					}
				}
				catch (Exception)
				{
					// An unreadable ancestor is not evidence of a link; the existence check above
					// already proved the project root itself is reachable.
				}

				string parent = Path.GetDirectoryName(current);
				if (string.Equals(parent, current, StringComparison.Ordinal))
				{
					break;
				}

				current = parent;
			}

			return false;
		}

		private static bool IsNetworkDrive(string path)
		{
			try
			{
				string root = Path.GetPathRoot(path);
				if (string.IsNullOrEmpty(root))
				{
					return false;
				}

				var drive = new DriveInfo(root);
				return drive.DriveType == DriveType.Network;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
