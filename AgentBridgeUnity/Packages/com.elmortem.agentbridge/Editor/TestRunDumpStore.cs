using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class TestRunDumpStore
	{
		public static void WritePending(TestRunDump dump)
		{
			WriteAtomic(PendingPath(dump.Filter.TestMode), JsonUtility.ToJson(dump));
		}

		public static bool TryTakePending(string testMode, out TestRunDump dump)
		{
			dump = Read(PendingPath(testMode));
			DeletePending(testMode);
			return dump != null;
		}

		public static void DeletePending(string testMode)
		{
			string path = PendingPath(testMode);
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}

		public static void Write(TestRunDump dump)
		{
			WriteAtomic(FinalPath(dump.Filter.TestMode), JsonUtility.ToJson(dump));
		}

		public static bool TryRead(string testMode, out TestRunDump dump)
		{
			dump = Read(FinalPath(testMode));
			return dump != null;
		}

		private static TestRunDump Read(string path)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				return JsonUtility.FromJson<TestRunDump>(File.ReadAllText(path));
			}
			catch
			{
				return null;
			}
		}

		private static string FinalPath(string testMode)
		{
			return Path.Combine(BridgePaths.WorkingRoot, "test-cache-" + testMode.ToLowerInvariant() + ".json");
		}

		private static string PendingPath(string testMode)
		{
			return FinalPath(testMode) + ".pending";
		}

		private static void WriteAtomic(string path, string json)
		{
			string tempPath = path + ".tmp";
			File.WriteAllText(tempPath, json);

			if (File.Exists(path))
			{
				File.Replace(tempPath, path, null);
			}
			else
			{
				File.Move(tempPath, path);
			}
		}
	}
}
