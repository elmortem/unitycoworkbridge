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

			foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
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

				string location;
				try
				{
					location = assembly.Location;
				}
				catch
				{
					continue;
				}

				if (string.IsNullOrEmpty(location) || !File.Exists(location))
				{
					continue;
				}

				object reference = GetOrCreateReference(location);
				if (reference != null)
				{
					result.Add(reference);
				}
			}

			_snapshot = result;
			return _snapshot;
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
