using System;
using System.Collections.Generic;
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
		private const int EditorSearchDepth = 3;

		private static readonly string[] EditorRoslynDirectories =
		{
			"DotNetSdkRoslyn",
			"Tools/Roslyn",
			"Roslyn"
		};

		private static readonly Dictionary<RoslynSourceKind, RoslynLocation> _probeCache = new Dictionary<RoslynSourceKind, RoslynLocation>();

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
				case RoslynSourceKind.Vendored:
					return ProbeVendored();
				case RoslynSourceKind.Project:
					return ProbeProject();
				case RoslynSourceKind.Local:
					return ProbeDirectory(kind, AgentBridgeSettingsStore.GetRoslynLocalPath());
				case RoslynSourceKind.Editor:
					return ProbeEditor();
				default:
					return new RoslynLocation { Kind = kind, Available = false, Reason = "unsupported" };
			}
		}

		public static RoslynLocation ProbeCached(RoslynSourceKind kind)
		{
			RoslynLocation cached;
			if (_probeCache.TryGetValue(kind, out cached))
			{
				return cached;
			}

			RoslynLocation location = Probe(kind);
			_probeCache[kind] = location;
			return location;
		}

		public static void ClearProbeCache()
		{
			_probeCache.Clear();
		}

		public static RoslynLocation ResolveAuto()
		{
			RoslynSourceKind[] order =
			{
				RoslynSourceKind.Project,
				RoslynSourceKind.Local,
				RoslynSourceKind.Vendored,
				RoslynSourceKind.Editor
			};

			foreach (RoslynSourceKind kind in order)
			{
				RoslynLocation location = ProbeCached(kind);
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

			return ProbeCached(configured);
		}

		private static string GetVendoredDirectory()
		{
			UnityEditor.PackageManager.PackageInfo package =
				UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RoslynResolver).Assembly);
			if (package == null || string.IsNullOrEmpty(package.resolvedPath))
			{
				return null;
			}

			return Path.Combine(package.resolvedPath, "Roslyn~");
		}

		private static RoslynLocation ProbeVendored()
		{
			string directory = GetVendoredDirectory();
			if (directory == null)
			{
				return new RoslynLocation { Kind = RoslynSourceKind.Vendored, Available = false, Reason = "package path unavailable" };
			}

			return ProbeDirectory(RoslynSourceKind.Vendored, directory);
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

			var requested = new AssemblyName(args.Name);
			string candidate = Path.Combine(_activeDirectory, requested.Name + ".dll");

			if (File.Exists(candidate))
			{
				try
				{
					AssemblyName candidateName = AssemblyName.GetAssemblyName(candidate);
					if (requested.Version == null || candidateName.Version >= requested.Version)
					{
						return Assembly.LoadFrom(candidate);
					}
				}
				catch
				{
				}
			}

			foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (loaded.GetName().Name != requested.Name)
				{
					continue;
				}

				if (requested.Version == null || loaded.GetName().Version >= requested.Version)
				{
					return loaded;
				}
			}

			return null;
		}

		private static RoslynLocation ProbeProject()
		{
			string vendoredDirectory = GetVendoredDirectory();

			foreach (Assembly assembly in AssemblyCompat.GetLoadedAssemblies())
			{
				if (assembly.GetName().Name != "Microsoft.CodeAnalysis.CSharp")
				{
					continue;
				}

				string assemblyLocation = AssemblyCompat.GetAssemblyPath(assembly);

				if (vendoredDirectory != null && !string.IsNullOrEmpty(assemblyLocation)
					&& Path.GetFullPath(assemblyLocation).StartsWith(Path.GetFullPath(vendoredDirectory), StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (RoslynLoadContext.IsSupported)
				{
					// The bridge's own contexts and the editor's platform copy are other sources.
					if (RoslynLoadContext.Owns(assembly) || IsPlatformPath(assemblyLocation))
					{
						continue;
					}

					return UseLoaded(RoslynSourceKind.Project, assembly, assemblyLocation);
				}

				if (string.IsNullOrEmpty(assemblyLocation))
				{
					continue;
				}

				return TryLoadAndVerify(RoslynSourceKind.Project, assemblyLocation);
			}

			string assetsRoot = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Assets");
			string found = FindFileRecursive(assetsRoot, CSharpDllFileName, MaxSearchDepth);
			if (found == null)
			{
				return new RoslynLocation { Kind = RoslynSourceKind.Project, Available = false, Reason = "not found" };
			}

			return TryLoadAndVerify(RoslynSourceKind.Project, found);
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
			if (RoslynLoadContext.IsSupported)
			{
				return TryLoadIsolated(kind, dllPath);
			}

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

				return new RoslynLocation { Kind = kind, Available = true, Reason = "ok", DirectoryPath = directory, Version = VersionOf(csharpAssembly) };
			}
			catch (Exception ex)
			{
				_activeDirectory = previousActiveDirectory;
				return new RoslynLocation { Kind = kind, Available = false, Reason = ex.Message };
			}
		}

		private static RoslynLocation TryLoadIsolated(RoslynSourceKind kind, string dllPath)
		{
			string directory = Path.GetDirectoryName(dllPath);
			try
			{
				IList<Assembly> loaded = RoslynLoadContext.Load(directory);
				Assembly csharpAssembly = FindByName(loaded, "Microsoft.CodeAnalysis.CSharp");
				if (csharpAssembly == null)
				{
					return new RoslynLocation { Kind = kind, Available = false, Reason = CSharpDllFileName + " did not load", DirectoryPath = directory };
				}

				return UseLoaded(kind, csharpAssembly, directory, FindByName(loaded, "Microsoft.CodeAnalysis"));
			}
			catch (Exception ex)
			{
				return new RoslynLocation { Kind = kind, Available = false, Reason = ex.Message, DirectoryPath = directory };
			}
		}

		// Binds to a Roslyn the runtime already resolved, instead of loading a second copy that
		// would split its types from the first. The type lookup below pulls Microsoft.CodeAnalysis
		// in through the assembly's own load context, so a broken dependency surfaces here.
		private static RoslynLocation UseLoaded(RoslynSourceKind kind, Assembly csharpAssembly, string location, Assembly baseAssembly = null)
		{
			string directory = string.IsNullOrEmpty(location) ? null
				: Directory.Exists(location) ? location : Path.GetDirectoryName(location);
			try
			{
				Type syntaxTreeType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
				if (syntaxTreeType == null)
				{
					return new RoslynLocation { Kind = kind, Available = false, Reason = "CSharpSyntaxTree type missing", DirectoryPath = directory };
				}

				if (baseAssembly == null)
				{
					Type baseTreeType = syntaxTreeType.BaseType;
					while (baseTreeType != null && baseTreeType.FullName != "Microsoft.CodeAnalysis.SyntaxTree")
					{
						baseTreeType = baseTreeType.BaseType;
					}

					baseAssembly = baseTreeType != null ? baseTreeType.Assembly : null;
				}

				if (baseAssembly == null)
				{
					return new RoslynLocation { Kind = kind, Available = false, Reason = "Microsoft.CodeAnalysis.dll not found", DirectoryPath = directory };
				}

				CodeAnalysisAssembly = baseAssembly;
				CodeAnalysisCSharpAssembly = csharpAssembly;

				return new RoslynLocation { Kind = kind, Available = true, Reason = "ok", DirectoryPath = directory, Version = VersionOf(csharpAssembly) };
			}
			catch (Exception ex)
			{
				return new RoslynLocation { Kind = kind, Available = false, Reason = ex.Message, DirectoryPath = directory };
			}
		}

		// Unity 7 compiles scripts with a Roslyn of its own. When the editor puts it on the
		// runtime's platform list it is bound by name; otherwise the copy in the editor's
		// installation is loaded like any other directory.
		private static RoslynLocation ProbeEditor()
		{
			if (!RoslynLoadContext.IsSupported)
			{
				return new RoslynLocation { Kind = RoslynSourceKind.Editor, Available = false, Reason = "CoreCLR editor only" };
			}

			string platformPath;
			if (AssemblyCompat.GetPlatformAssemblies().TryGetValue("Microsoft.CodeAnalysis.CSharp", out platformPath))
			{
				try
				{
					Assembly platformAssembly = Assembly.Load(new AssemblyName("Microsoft.CodeAnalysis.CSharp"));
					return UseLoaded(RoslynSourceKind.Editor, platformAssembly, platformPath);
				}
				catch (Exception ex)
				{
					return new RoslynLocation { Kind = RoslynSourceKind.Editor, Available = false, Reason = ex.Message, DirectoryPath = Path.GetDirectoryName(platformPath) };
				}
			}

			string contents = EditorApplication.applicationContentsPath;
			string found = null;
			foreach (string relative in EditorRoslynDirectories)
			{
				string candidate = Path.Combine(Path.Combine(contents, relative), CSharpDllFileName);
				if (File.Exists(candidate))
				{
					found = candidate;
					break;
				}
			}

			if (found == null)
			{
				found = FindFileRecursive(contents, CSharpDllFileName, EditorSearchDepth);
			}

			if (found == null)
			{
				return new RoslynLocation { Kind = RoslynSourceKind.Editor, Available = false, Reason = "not found in " + contents };
			}

			return TryLoadIsolated(RoslynSourceKind.Editor, found);
		}

		private static bool IsPlatformPath(string path)
		{
			string platformPath;
			return !string.IsNullOrEmpty(path)
				&& AssemblyCompat.GetPlatformAssemblies().TryGetValue(Path.GetFileNameWithoutExtension(path), out platformPath)
				&& string.Equals(Path.GetFullPath(platformPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
		}

		private static Assembly FindByName(IEnumerable<Assembly> assemblies, string name)
		{
			foreach (Assembly assembly in assemblies)
			{
				if (assembly.GetName().Name == name)
				{
					return assembly;
				}
			}

			return null;
		}

		private static string VersionOf(Assembly assembly)
		{
			Version version = assembly.GetName().Version;
			return version != null ? version.ToString() : null;
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
