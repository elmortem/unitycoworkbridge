using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class CompileCacheStore
	{
		public static void Write(CompileCacheEntry entry)
		{
			string path = FilePath();
			string tempPath = path + ".tmp";
			File.WriteAllText(tempPath, JsonUtility.ToJson(entry));

			if (File.Exists(path))
			{
				File.Replace(tempPath, path, null);
			}
			else
			{
				File.Move(tempPath, path);
			}
		}

		public static bool TryRead(out CompileCacheEntry entry)
		{
			entry = null;
			string path = FilePath();
			if (!File.Exists(path))
			{
				return false;
			}

			try
			{
				entry = JsonUtility.FromJson<CompileCacheEntry>(File.ReadAllText(path));
			}
			catch
			{
				entry = null;
			}

			return entry != null;
		}

		private static string FilePath()
		{
			return Path.Combine(BridgePaths.WorkingRoot, "compile-cache.json");
		}
	}
}
