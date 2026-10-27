using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AgentBridge
{
	// Unity 7 runs the editor on CoreCLR: Assembly.Location is empty for assemblies Unity loads from
	// memory, AppDomain assembly APIs see unloading contexts, and Assembly.LoadFrom/Load(byte[]) live
	// outside code reload. Unity's replacements exist only in newer editors, so they are bound by
	// reflection and the package still compiles on 2022.3.
	public static class AssemblyCompat
	{
		private static readonly MethodInfo _getLoadedAssemblies;
		private static readonly MethodInfo _getLoadedAssemblyPath;
		private static readonly MethodInfo _loadFromBytes;
		private static readonly MethodInfo _loadFromBytesWithSymbols;
		private static Dictionary<string, string> _platformAssemblies;

		static AssemblyCompat()
		{
			IsCoreClr = Type.GetType("Mono.Runtime") == null
				&& typeof(object).Assembly.GetType("System.Runtime.Loader.AssemblyLoadContext") != null;

			Assembly core = typeof(UnityEngine.Object).Assembly;
			Type currentAssemblies = core.GetType("UnityEngine.Assemblies.CurrentAssemblies");
			if (currentAssemblies != null)
			{
				_getLoadedAssemblies = currentAssemblies.GetMethod("GetLoadedAssemblies", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
				_loadFromBytes = currentAssemblies.GetMethod("LoadFromBytes", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(byte[]) }, null);
				_loadFromBytesWithSymbols = currentAssemblies.GetMethod("LoadFromBytes", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(byte[]), typeof(byte[]) }, null);
			}

			Type assemblyExtension = core.GetType("UnityEngine.AssemblyExtension");
			if (assemblyExtension != null)
			{
				_getLoadedAssemblyPath = assemblyExtension.GetMethod("GetLoadedAssemblyPath", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Assembly) }, null);
			}
		}

		public static bool IsCoreClr { get; private set; }

		public static IEnumerable<Assembly> GetLoadedAssemblies()
		{
			if (_getLoadedAssemblies != null)
			{
				object loaded = null;
				try
				{
					loaded = _getLoadedAssemblies.Invoke(null, null);
				}
				catch
				{
				}

				var assemblies = loaded as IEnumerable<Assembly>;
				if (assemblies != null)
				{
					return assemblies;
				}
			}

			return AppDomain.CurrentDomain.GetAssemblies();
		}

		public static string GetAssemblyPath(Assembly assembly)
		{
			if (assembly == null || assembly.IsDynamic)
			{
				return null;
			}

			if (_getLoadedAssemblyPath != null)
			{
				try
				{
					string path = _getLoadedAssemblyPath.Invoke(null, new object[] { assembly }) as string;
					if (!string.IsNullOrEmpty(path))
					{
						return path;
					}
				}
				catch
				{
				}
			}

			try
			{
				string location = assembly.Location;
				return string.IsNullOrEmpty(location) ? null : location;
			}
			catch
			{
				return null;
			}
		}

		public static Assembly LoadFromBytes(byte[] rawAssembly, byte[] rawSymbols)
		{
			if (rawSymbols != null && _loadFromBytesWithSymbols != null)
			{
				return InvokeLoader(_loadFromBytesWithSymbols, rawAssembly, rawSymbols);
			}

			if (rawSymbols == null && _loadFromBytes != null)
			{
				return InvokeLoader(_loadFromBytes, rawAssembly);
			}

			return rawSymbols != null ? Assembly.Load(rawAssembly, rawSymbols) : Assembly.Load(rawAssembly);
		}

		// Name -> path of every assembly the CoreCLR host resolves without a probe (the BCL and
		// whatever the editor put on its trusted platform list). Empty on Mono.
		public static IReadOnlyDictionary<string, string> GetPlatformAssemblies()
		{
			if (_platformAssemblies != null)
			{
				return _platformAssemblies;
			}

			var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			string list = AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
			if (!string.IsNullOrEmpty(list))
			{
				foreach (string path in list.Split(Path.PathSeparator))
				{
					if (string.IsNullOrEmpty(path))
					{
						continue;
					}

					string name = Path.GetFileNameWithoutExtension(path);
					if (!result.ContainsKey(name))
					{
						result[name] = path;
					}
				}
			}

			_platformAssemblies = result;
			return _platformAssemblies;
		}

		private static Assembly InvokeLoader(MethodInfo method, params object[] args)
		{
			try
			{
				return (Assembly)method.Invoke(null, args);
			}
			catch (TargetInvocationException ex)
			{
				if (ex.InnerException != null)
				{
					ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
				}

				throw;
			}
		}
	}
}
