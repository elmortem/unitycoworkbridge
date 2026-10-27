using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace AgentBridge
{
	// CoreCLR loader for a Roslyn directory. Unity 7 rejects Assembly.LoadFrom for dependencies
	// ("Operation is not supported", 0x80131515) and does not route AppDomain.AssemblyResolve, so the
	// Mono path in RoslynResolver cannot pull Microsoft.CodeAnalysis.dll in next to the C# compiler.
	// A private AssemblyLoadContext with every Roslyn assembly preloaded binds them to each other and
	// falls back to the editor's default context only for the BCL. The API is reached by reflection
	// because the 2022.3 Mono profile has no AssemblyLoadContext to compile against.
	public static class RoslynLoadContext
	{
		private const string ContextName = "AgentBridge.Roslyn";
		private const string ContextDataPrefix = "AgentBridge.RoslynLoadContext:";

		private static readonly Type _contextType =
			typeof(object).Assembly.GetType("System.Runtime.Loader.AssemblyLoadContext");

		public static bool IsSupported
		{
			get { return AssemblyCompat.IsCoreClr && _contextType != null; }
		}

		// A code reload keeps the process and the default context, so the context is parked in
		// AppDomain data under its directory and reused instead of loading Roslyn again on every reload.
		// It holds no reference into the package assembly, which keeps the package unloadable.
		// Overwriting the DLLs of an already loaded directory needs an editor restart.
		public static IList<Assembly> Load(string directory)
		{
			string fullDirectory = Path.GetFullPath(directory);
			string key = ContextDataPrefix + fullDirectory.ToLowerInvariant();

			object context = AppDomain.CurrentDomain.GetData(key);
			if (context == null || context.GetType() != _contextType)
			{
				context = Activator.CreateInstance(_contextType, ContextName + ":" + fullDirectory, false);
				LoadDirectory(context, fullDirectory);
				AppDomain.CurrentDomain.SetData(key, context);
			}

			var assemblies = new List<Assembly>();
			var loaded = (IEnumerable<Assembly>)_contextType.GetProperty("Assemblies").GetValue(context, null);
			assemblies.AddRange(loaded);
			return assemblies;
		}

		public static bool Owns(Assembly assembly)
		{
			if (!IsSupported || assembly == null)
			{
				return false;
			}

			try
			{
				object context = _contextType.GetMethod("GetLoadContext", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { assembly });
				string name = context != null ? _contextType.GetProperty("Name").GetValue(context, null) as string : null;
				return name != null && name.StartsWith(ContextName + ":", StringComparison.Ordinal);
			}
			catch
			{
				return false;
			}
		}

		private static void LoadDirectory(object context, string directory)
		{
			MethodInfo loadFromStream = _contextType.GetMethod("LoadFromStream", new[] { typeof(Stream) });
			IReadOnlyDictionary<string, string> platform = AssemblyCompat.GetPlatformAssemblies();

			foreach (string path in Directory.GetFiles(directory, "*.dll"))
			{
				string name = Path.GetFileNameWithoutExtension(path);
				if (!name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
					&& IsProvidedByPlatform(platform, name, path))
				{
					continue;
				}

				// Streams keep the files unlocked, so the package can still be updated while the editor runs.
				byte[] image = File.ReadAllBytes(path);
				using (var stream = new MemoryStream(image))
				{
					try
					{
						loadFromStream.Invoke(context, new object[] { stream });
					}
					catch (TargetInvocationException ex)
					{
						throw new InvalidOperationException(Path.GetFileName(path) + ": " + (ex.InnerException ?? ex).Message, ex.InnerException ?? ex);
					}
				}
			}
		}

		// The BCL already ships System.Collections.Immutable, System.Reflection.Metadata and friends;
		// a second copy in the private context would split their types from the editor's.
		private static bool IsProvidedByPlatform(IReadOnlyDictionary<string, string> platform, string name, string path)
		{
			string platformPath;
			if (!platform.TryGetValue(name, out platformPath))
			{
				return false;
			}

			try
			{
				Version platformVersion = AssemblyName.GetAssemblyName(platformPath).Version;
				Version ownVersion = AssemblyName.GetAssemblyName(path).Version;
				return platformVersion != null && ownVersion != null && platformVersion >= ownVersion;
			}
			catch
			{
				return false;
			}
		}
	}
}
