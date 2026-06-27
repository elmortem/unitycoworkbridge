using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor.PackageManager;

namespace CoworkBridge
{
	public static class WaitScriptInstaller
	{
		public const string ScriptName = "wait-for-result.sh";

		public static void EnsureInstalled(string coworkPath)
		{
			string destination = Path.Combine(coworkPath, ScriptName);
			if (File.Exists(destination))
			{
				return;
			}

			string source = ResolveSourcePath();
			if (source == null || !File.Exists(source))
			{
				Debug.LogWarning("[CoworkBridge] Wait helper source not found, skipped install: " + ScriptName);
				return;
			}

			File.Copy(source, destination);
			Debug.Log("[CoworkBridge] Wait helper installed: " + destination);
		}

		private static string ResolveSourcePath()
		{
			Assembly assembly = typeof(WaitScriptInstaller).Assembly;
			PackageInfo package = PackageInfo.FindForAssembly(assembly);
			if (package == null)
			{
				return null;
			}

			return Path.Combine(package.resolvedPath, ScriptName);
		}
	}
}
