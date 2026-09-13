using System.Threading.Tasks;
using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class CompileInputContext
	{
		// Read Unity state only on the editor thread; workers receive an immutable snapshot.
		public static string[] Roots
		{
			get
			{
				string project = BridgePaths.ProjectRoot;
				var roots = new List<string> { Path.Combine(project, "Assets"), Path.Combine(project, "Packages"), Path.Combine(project, "ProjectSettings") };
				// Compilation reuse must not silently forget a package when resolution fails.
				var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
				if (packages == null) throw new InvalidOperationException("Compilation package inventory is unavailable");
				foreach (var package in packages)
				{
					if (package.source != UnityEditor.PackageManager.PackageSource.Local && package.source != UnityEditor.PackageManager.PackageSource.Embedded) continue;
					if (string.IsNullOrEmpty(package.resolvedPath)) throw new InvalidOperationException("Unresolved compile input package: " + package.name);
					string path = Path.GetFullPath(package.resolvedPath);
					if (!path.StartsWith(Path.GetFullPath(roots[1]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
						&& !roots.Contains(path)) roots.Add(path);
				}
				roots.Sort(StringComparer.Ordinal);
				return roots.ToArray();
			}
		}
		public static string Context { get { return ValidationEvidence.ContextOf("compile", ""); } }
		public static Task<string> StartCapture(string projectRoot)
		{
			string[] roots = Roots;
			string context = Context;
			return Task.Run(() => CompileFingerprint.Capture(projectRoot, roots, context));
		}
	}
}
