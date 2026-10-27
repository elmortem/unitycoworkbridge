using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentBridge
{
	[InitializeOnLoad]
	public static class ReferenceCatalog
	{
		private class CacheEntry
		{
			public object Reference;
			public long Length;
			public DateTime LastWriteUtc;
		}

		private static readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
		private static List<object> _snapshot;

		static ReferenceCatalog()
		{
			CompilationPipeline.compilationFinished -= OnCompilationFinished;
			CompilationPipeline.compilationFinished += OnCompilationFinished;

			AssemblyReloadEvents.afterAssemblyReload -= Invalidate;
			AssemblyReloadEvents.afterAssemblyReload += Invalidate;
		}

		public static int Count
		{
			get { return GetReferences().Count; }
		}

		public static void Invalidate()
		{
			_snapshot = null;
		}

		public static IReadOnlyList<object> GetReferences()
		{
			if (_snapshot != null)
			{
				return _snapshot;
			}

			if (!RoslynResolver.IsReady)
			{
				RoslynResolver.ResolveConfigured();
			}

			var result = new List<object>();
			var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (System.Reflection.Assembly assembly in EnumerateAssemblies())
			{
				if (assembly.IsDynamic)
				{
					continue;
				}

				string name = assembly.GetName().Name;
				if (name.StartsWith("AgentTask_", StringComparison.Ordinal))
				{
					continue;
				}

				string location = AssemblyCompat.GetAssemblyPath(assembly);
				if (string.IsNullOrEmpty(location) || !File.Exists(location) || !paths.Add(Path.GetFullPath(location)))
				{
					continue;
				}

				names.Add(name);
				AddReference(result, location);
			}

			// CoreCLR loads facades such as System.Runtime lazily, yet Unity's assemblies are built
			// against them, so a task would fail with CS0012 without the platform list.
			foreach (KeyValuePair<string, string> platform in AssemblyCompat.GetPlatformAssemblies())
			{
				if (!IsFrameworkAssembly(platform.Key) || names.Contains(platform.Key) || !File.Exists(platform.Value)
					|| !paths.Add(Path.GetFullPath(platform.Value)))
				{
					continue;
				}

				AddReference(result, platform.Value);
			}

			_snapshot = result;
			return _snapshot;
		}

		// AppDomain sees every load context and Unity's list sees its code-reload contexts; on Mono
		// both are the same and the path check above drops the repeats.
		private static IEnumerable<System.Reflection.Assembly> EnumerateAssemblies()
		{
			foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				yield return assembly;
			}

			foreach (System.Reflection.Assembly assembly in AssemblyCompat.GetLoadedAssemblies())
			{
				yield return assembly;
			}
		}

		private static bool IsFrameworkAssembly(string name)
		{
			return name == "mscorlib" || name == "netstandard" || name == "System"
				|| name.StartsWith("System.", StringComparison.Ordinal)
				|| name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)
				|| name == "Microsoft.CSharp" || name == "Microsoft.VisualBasic";
		}

		private static void AddReference(List<object> result, string location)
		{
			object reference = GetOrCreateReference(location);
			if (reference != null)
			{
				result.Add(reference);
			}
		}

		private static object GetOrCreateReference(string path)
		{
			string normalized = Path.GetFullPath(path);
			var info = new FileInfo(normalized);

			CacheEntry entry;
			if (_cache.TryGetValue(normalized, out entry))
			{
				if (entry.Length == info.Length && entry.LastWriteUtc == info.LastWriteTimeUtc)
				{
					return entry.Reference;
				}
			}

			if (!RoslynResolver.IsReady)
			{
				return null;
			}

			try
			{
				Type metadataReferenceType = RoslynResolver.CodeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataReference");
				MethodInfo createFromFile = RoslynReflectionHelper.FindBestOverload(
					metadataReferenceType,
					"CreateFromFile",
					BindingFlags.Public | BindingFlags.Static,
					typeof(string));

				object[] args = RoslynReflectionHelper.BuildArgsWithDefaults(createFromFile, normalized);
				object reference = createFromFile.Invoke(null, args);

				_cache[normalized] = new CacheEntry
				{
					Reference = reference,
					Length = info.Length,
					LastWriteUtc = info.LastWriteTimeUtc
				};

				return reference;
			}
			catch
			{
				return null;
			}
		}

		private static void OnCompilationFinished(object obj)
		{
			Invalidate();
		}
	}
}
