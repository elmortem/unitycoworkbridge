using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentBridge
{
	public static class SourceImportVerifier
	{
		private const int MaxDiagnostics = 100;

		public static List<TaskDiagnostic> ValidateProjectSources()
		{
			var diagnostics = new List<TaskDiagnostic>();
			string projectRoot = Path.GetFullPath(BridgePaths.ProjectRoot);
			HashSet<string> compiledSources = CollectCompiledSources(projectRoot);

			ValidateRoot(Path.Combine(projectRoot, "Assets"), projectRoot, compiledSources, diagnostics);
			ValidateRoot(Path.Combine(projectRoot, "Packages"), projectRoot, compiledSources, diagnostics);
			return diagnostics;
		}

		private static void ValidateRoot(string root, string projectRoot, HashSet<string> compiledSources,
			List<TaskDiagnostic> diagnostics)
		{
			if (!Directory.Exists(root) || diagnostics.Count >= MaxDiagnostics)
			{
				return;
			}

			foreach (string sourcePath in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
			{
				if (diagnostics.Count >= MaxDiagnostics)
				{
					break;
				}

				string assetPath = ToAssetPath(sourcePath, projectRoot);
				if (IsIgnoredPackagePath(assetPath))
				{
					continue;
				}

				if (!File.Exists(sourcePath + ".meta"))
				{
					diagnostics.Add(Error("ABIMPORT001", assetPath,
						"C# source has no .meta file and is not proven to be imported by Unity."));
					continue;
				}

				if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
				{
					diagnostics.Add(Error("ABIMPORT002", assetPath,
						"C# source has no Unity AssetDatabase GUID after a synchronous refresh."));
					continue;
				}

				string assemblyName = CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath);
				if (string.IsNullOrEmpty(assemblyName))
				{
					diagnostics.Add(Error("ABIMPORT003", assetPath,
						"Unity did not assign the C# source to a script assembly."));
					continue;
				}

				string fullPath = NormalizeFullPath(sourcePath);
				if (!compiledSources.Contains(fullPath))
				{
					diagnostics.Add(Error("ABIMPORT004", assetPath,
						"C# source is assigned to " + assemblyName + " but is absent from Unity's compiled source inventory."));
				}
			}
		}

		private static HashSet<string> CollectCompiledSources(string projectRoot)
		{
			var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (UnityEditor.Compilation.Assembly assembly in CompilationPipeline.GetAssemblies())
			{
				if (assembly.sourceFiles == null)
				{
					continue;
				}

				foreach (string source in assembly.sourceFiles)
				{
					string fullPath = Path.IsPathRooted(source) ? source : Path.Combine(projectRoot, source);
					result.Add(NormalizeFullPath(fullPath));
				}
			}

			return result;
		}

		private static string ToAssetPath(string fullPath, string projectRoot)
		{
			string normalizedRoot = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				+ Path.DirectorySeparatorChar;
			string normalizedPath = Path.GetFullPath(fullPath);
			return normalizedPath.Substring(normalizedRoot.Length).Replace('\\', '/');
		}

		private static string NormalizeFullPath(string path)
		{
			return Path.GetFullPath(path).Replace('\\', '/');
		}

		private static bool IsIgnoredPackagePath(string assetPath)
		{
			if (!assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string[] segments = assetPath.Split('/');
			foreach (string segment in segments)
			{
				if (segment.EndsWith("~", StringComparison.Ordinal) || segment.StartsWith(".", StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		private static TaskDiagnostic Error(string code, string path, string message)
		{
			return new TaskDiagnostic
			{
				Code = code,
				Severity = "Error",
				Message = message,
				File = path
			};
		}
	}
}
