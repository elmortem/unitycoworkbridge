using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
	public static class RoslynResolver
	{
		private const string CSharpDllFileName = "Microsoft.CodeAnalysis.CSharp.dll";
		private const int MaxSearchDepth = 4;

		private static bool _handlerInstalled;
		private static string _activeDirectory;

		public static Assembly CodeAnalysisAssembly { get; private set; }
		public static Assembly CodeAnalysisCSharpAssembly { get; private set; }

		public static bool IsReady
		{
			get { return CodeAnalysisAssembly != null && CodeAnalysisCSharpAssembly != null; }
		}

		public static RoslynLocation Probe(RoslynSourceKind kind)
		{
			switch (kind)
			{
				case RoslynSourceKind.UnityBuiltin:
					return ProbeDirectorySearch(kind, EditorApplication.applicationContentsPath, MaxSearchDepth);
				case RoslynSourceKind.Project:
					return ProbeProject();
				case RoslynSourceKind.NuGet:
					return ProbeDirectory(kind, BridgePaths.Roslyn);
				case RoslynSourceKind.Local:
					return ProbeDirectory(kind, AgentBridgeSettingsStore.GetRoslynLocalPath());
				default:
					return new RoslynLocation { Kind = kind, Available = false, Reason = "unsupported" };
			}
		}

		public static RoslynLocation ResolveAuto()
		{
			RoslynSourceKind[] order =
			{
				RoslynSourceKind.Project,
				RoslynSourceKind.Local,
				RoslynSourceKind.NuGet,
				RoslynSourceKind.UnityBuiltin
			};

			foreach (RoslynSourceKind kind in order)
			{
				RoslynLocation location = Probe(kind);
				if (location.Available)
				{
					return location;
				}
			}

			return new RoslynLocation { Kind = RoslynSourceKind.Auto, Available = false, Reason = "no source available" };
		}

		public static RoslynLocation ResolveConfigured()
		{
			RoslynSourceKind configured = ParseSourceKind(AgentBridgeSettingsStore.GetRoslynSource());
			if (configured == RoslynSourceKind.Auto)
			{
				return ResolveAuto();
			}

			return Probe(configured);
		}

		public static void InstallAssemblyResolve(string directoryPath)
		{
			_activeDirectory = directoryPath;

			if (_handlerInstalled)
			{
				return;
			}

			AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
			_handlerInstalled = true;
		}

		private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
		{
			if (string.IsNullOrEmpty(_activeDirectory) || !Directory.Exists(_activeDirectory))
			{
				return null;
			}

			string shortName = new AssemblyName(args.Name).Name;

			foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (loaded.GetName().Name == shortName)
				{
					return null;
				}
			}

			string candidate = Path.Combine(_activeDirectory, shortName + ".dll");
			if (!File.Exists(candidate))
			{
				return null;
			}

			try
			{
				return Assembly.LoadFrom(candidate);
			}
			catch
			{
				return null;
			}
		}

		private static RoslynLocation ProbeProject()
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.GetName().Name == "Microsoft.CodeAnalysis.CSharp")
				{
					return TryLoadAndVerify(RoslynSourceKind.Project, assembly.Location);
				}
			}

			string assetsRoot = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Assets");
			string found = FindFileRecursive(assetsRoot, CSharpDllFileName, MaxSearchDepth);
			if (found == null)
			{
				return new RoslynLocation { Kind = RoslynSourceKind.Project, Available = false, Reason = "not found" };
			}

			return TryLoadAndVerify(RoslynSourceKind.Project, found);
		}

		private static RoslynLocation ProbeDirectorySearch(RoslynSourceKind kind, string rootDirectory, int depth)
		{
			string found = FindFileRecursive(rootDirectory, CSharpDllFileName, depth);
			if (found == null)
			{
				return new RoslynLocation { Kind = kind, Available = false, Reason = "not found" };
			}

			return TryLoadAndVerify(kind, found);
		}

		private static RoslynLocation ProbeDirectory(RoslynSourceKind kind, string directoryPath)
		{
			if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
			{
				return new RoslynLocation { Kind = kind, Available = false, Reason = "directory missing" };
			}

			string candidate = Path.Combine(directoryPath, CSharpDllFileName);
			if (!File.Exists(candidate))
			{
				return new RoslynLocation { Kind = kind, Available = false, Reason = "dll missing" };
			}

			return TryLoadAndVerify(kind, candidate);
		}

		private static RoslynLocation TryLoadAndVerify(RoslynSourceKind kind, string dllPath)
		{
			string previousActiveDirectory = _activeDirectory;
			try
			{
				string directory = Path.GetDirectoryName(dllPath);
				InstallAssemblyResolve(directory);

				Assembly csharpAssembly = Assembly.LoadFrom(dllPath);
				Type syntaxTreeType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
				if (syntaxTreeType == null)
				{
					_activeDirectory = previousActiveDirectory;
					return new RoslynLocation { Kind = kind, Available = false, Reason = "CSharpSyntaxTree type missing", DirectoryPath = directory };
				}

				Assembly baseAssembly = FindLoadedAssembly("Microsoft.CodeAnalysis");
				if (baseAssembly == null)
				{
					string baseDllPath = Path.Combine(directory, "Microsoft.CodeAnalysis.dll");
					if (File.Exists(baseDllPath))
					{
						baseAssembly = Assembly.LoadFrom(baseDllPath);
					}
				}

				if (baseAssembly == null)
				{
					_activeDirectory = previousActiveDirectory;
					return new RoslynLocation { Kind = kind, Available = false, Reason = "Microsoft.CodeAnalysis.dll not found", DirectoryPath = directory };
				}

				CodeAnalysisAssembly = baseAssembly;
				CodeAnalysisCSharpAssembly = csharpAssembly;

				BridgeStatusWriter.Current.RoslynReady = true;
				BridgeStatusWriter.Current.RoslynSource = kind.ToString();
				BridgeStatusWriter.Write();

				return new RoslynLocation { Kind = kind, Available = true, Reason = "ok", DirectoryPath = directory };
			}
			catch (Exception ex)
			{
				_activeDirectory = previousActiveDirectory;
				return new RoslynLocation { Kind = kind, Available = false, Reason = ex.Message };
			}
		}

		private static Assembly FindLoadedAssembly(string name)
		{
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.GetName().Name == name)
				{
					return assembly;
				}
			}

			return null;
		}

		private static string FindFileRecursive(string rootDirectory, string fileName, int maxDepth)
		{
			if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
			{
				return null;
			}

			return SearchDirectory(rootDirectory, fileName, maxDepth);
		}

		private static string SearchDirectory(string directory, string fileName, int depthRemaining)
		{
			try
			{
				foreach (string file in Directory.GetFiles(directory))
				{
					if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
					{
						return file;
					}
				}

				if (depthRemaining <= 0)
				{
					return null;
				}

				foreach (string subDirectory in Directory.GetDirectories(directory))
				{
					string found = SearchDirectory(subDirectory, fileName, depthRemaining - 1);
					if (found != null)
					{
						return found;
					}
				}
			}
			catch
			{
			}

			return null;
		}

		private static RoslynSourceKind ParseSourceKind(string value)
		{
			RoslynSourceKind kind;
			if (Enum.TryParse(value, out kind))
			{
				return kind;
			}

			return RoslynSourceKind.Auto;
		}
	}
}
