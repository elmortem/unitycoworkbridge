using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace CoworkBridge
{
	public static class TaskCleaner
	{
		public static void TrimCompleted(string coworkPath, int keepCount)
		{
			string[] csFiles = Directory.GetFiles(coworkPath, "*.cs");
			if (csFiles.Length <= keepCount)
			{
				return;
			}

			List<string> successful = GetSuccessfulTaskIds(coworkPath);
			if (successful.Count <= keepCount)
			{
				return;
			}

			int removeCount = successful.Count - keepCount;
			for (int i = 0; i < removeCount; i++)
			{
				DeleteTaskFiles(coworkPath, successful[i]);
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Trimmed " + removeCount + " completed tasks.");
		}

		public static void CleanAllSuccessful(string coworkPath)
		{
			List<string> successful = GetSuccessfulTaskIds(coworkPath);
			foreach (string taskId in successful)
			{
				DeleteTaskFiles(coworkPath, taskId);
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + successful.Count + " successful tasks.");
		}

		public static void CleanCompleted(string coworkPath)
		{
			int count = 0;
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				string donePath = Path.Combine(coworkPath, "result_" + taskId + ".done");

				if (File.Exists(donePath))
				{
					DeleteTaskFiles(coworkPath, taskId);
					count++;
				}
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " completed tasks.");
		}

		public static void CleanAll(string coworkPath)
		{
			int count = 0;
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				DeleteTaskFiles(coworkPath, taskId);
				count++;
			}

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " tasks.");
		}

		public static void DeleteTaskFiles(string coworkPath, string taskId)
		{
			DeleteFile(Path.Combine(coworkPath, taskId + ".cs"));
			DeleteFile(Path.Combine(coworkPath, "result_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "result_" + taskId + ".done"));
			DeleteFile(Path.Combine(coworkPath, "pending_errors_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "testresult_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "testresult_" + taskId + ".done"));
		}

		private static List<string> GetSuccessfulTaskIds(string coworkPath)
		{
			List<string> files = new List<string>();
			foreach (string csFile in Directory.GetFiles(coworkPath, "*.cs"))
			{
				string taskId = Path.GetFileNameWithoutExtension(csFile);
				if (IsSuccessful(coworkPath, taskId))
				{
					files.Add(csFile);
				}
			}

			files.Sort((a, b) => File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)));

			List<string> ids = new List<string>();
			foreach (string csFile in files)
			{
				ids.Add(Path.GetFileNameWithoutExtension(csFile));
			}

			return ids;
		}

		private static bool IsSuccessful(string coworkPath, string taskId)
		{
			string donePath = Path.Combine(coworkPath, "result_" + taskId + ".done");
			if (!File.Exists(donePath))
			{
				return false;
			}

			string resultPath = Path.Combine(coworkPath, "result_" + taskId + ".json");
			if (!File.Exists(resultPath))
			{
				return false;
			}

			string json = File.ReadAllText(resultPath);
			TaskResult result = JsonUtility.FromJson<TaskResult>(json);
			if (result == null)
			{
				return false;
			}

			return result.status == "success";
		}

		private static void DeleteFile(string path)
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}
}
