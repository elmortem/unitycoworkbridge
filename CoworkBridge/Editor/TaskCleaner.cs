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
			SweepOrphans(coworkPath);

			List<string> taskFiles = GetTaskFiles(coworkPath);
			if (taskFiles.Count <= keepCount)
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

			SweepOrphans(coworkPath);

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + successful.Count + " successful tasks.");
		}

		public static void CleanCompleted(string coworkPath)
		{
			int count = 0;
			foreach (string taskFile in GetTaskFiles(coworkPath))
			{
				string taskId = TaskIdOf(taskFile);
				string donePath = Path.Combine(coworkPath, "result_" + taskId + ".done");

				if (File.Exists(donePath))
				{
					DeleteTaskFiles(coworkPath, taskId);
					count++;
				}
			}

			SweepOrphans(coworkPath);

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " completed tasks.");
		}

		public static void CleanAll(string coworkPath)
		{
			int count = 0;
			foreach (string taskFile in GetTaskFiles(coworkPath))
			{
				string taskId = TaskIdOf(taskFile);
				DeleteTaskFiles(coworkPath, taskId);
				count++;
			}

			SweepOrphans(coworkPath);

			AssetDatabase.Refresh();
			Debug.Log("[CoworkBridge] Cleaned " + count + " tasks.");
		}

		// Deletes result artifacts whose owning task file (.cs / .ui.json) is
		// gone. This catches files written after their task was already trimmed
		// — notably testresult_* (produced asynchronously after a test run) and
		// UI outputs — which would otherwise pile up forever.
		public static void SweepOrphans(string coworkPath)
		{
			var taskIds = new HashSet<string>();
			foreach (string taskFile in GetTaskFiles(coworkPath))
			{
				taskIds.Add(TaskIdOf(taskFile));
			}

			int deleted = 0;
			foreach (string path in Directory.GetFiles(coworkPath))
			{
				string name = Path.GetFileName(path);
				if (name.EndsWith(".meta"))
				{
					continue;
				}

				string taskId = OrphanTaskId(name);
				if (taskId == null || taskIds.Contains(taskId))
				{
					continue;
				}

				File.Delete(path);
				deleted++;
			}

			if (deleted > 0)
			{
				AssetDatabase.Refresh();
				Debug.Log("[CoworkBridge] Swept " + deleted + " orphan result files.");
			}
		}

		public static void DeleteTaskFiles(string coworkPath, string taskId)
		{
			DeleteFile(Path.Combine(coworkPath, taskId + ".cs"));
			DeleteFile(Path.Combine(coworkPath, taskId + ".ui.json"));
			DeleteFile(Path.Combine(coworkPath, "result_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "result_" + taskId + ".done"));
			DeleteFile(Path.Combine(coworkPath, "pending_errors_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "testresult_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "testresult_" + taskId + ".done"));
			DeleteFile(Path.Combine(coworkPath, "uidump_" + taskId + ".json"));
			DeleteFile(Path.Combine(coworkPath, "shot_" + taskId + ".png"));
			DeleteFile(Path.Combine(coworkPath, "shot_" + taskId + ".png.meta"));
			DeleteFile(Path.Combine(coworkPath, "shot_" + taskId + ".png.rects.json"));
		}

		public static List<string> GetTaskFiles(string coworkPath)
		{
			var files = new List<string>();
			foreach (string path in Directory.GetFiles(coworkPath))
			{
				if (path.EndsWith(".cs") || path.EndsWith(".ui.json"))
				{
					files.Add(path);
				}
			}

			return files;
		}

		private static string TaskIdOf(string filePath)
		{
			string name = Path.GetFileName(filePath);
			if (name.EndsWith(".ui.json"))
			{
				return name.Substring(0, name.Length - ".ui.json".Length);
			}

			return Path.GetFileNameWithoutExtension(name);
		}

		// Maps a result-artifact file name to its task id, or null when the file
		// is not a Bridge-produced result (task sources, wait-for-result.sh,
		// clean.command and custom shot outputs are left untouched).
		private static string OrphanTaskId(string name)
		{
			if (TryStrip(name, "result_", ".json", out string id)) return id;
			if (TryStrip(name, "result_", ".done", out id)) return id;
			if (TryStrip(name, "pending_errors_", ".json", out id)) return id;
			if (TryStrip(name, "testresult_", ".json", out id)) return id;
			if (TryStrip(name, "testresult_", ".done", out id)) return id;
			if (TryStrip(name, "uidump_", ".json", out id)) return id;
			if (TryStrip(name, "shot_", ".png.rects.json", out id)) return id;
			if (TryStrip(name, "shot_", ".png", out id)) return id;
			return null;
		}

		private static bool TryStrip(string name, string prefix, string suffix, out string id)
		{
			id = null;
			if (name.Length <= prefix.Length + suffix.Length)
			{
				return false;
			}

			if (!name.StartsWith(prefix) || !name.EndsWith(suffix))
			{
				return false;
			}

			id = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
			return true;
		}

		private static List<string> GetSuccessfulTaskIds(string coworkPath)
		{
			List<string> files = new List<string>();
			foreach (string taskFile in GetTaskFiles(coworkPath))
			{
				string taskId = TaskIdOf(taskFile);
				if (IsSuccessful(coworkPath, taskId))
				{
					files.Add(taskFile);
				}
			}

			files.Sort((a, b) => File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)));

			List<string> ids = new List<string>();
			foreach (string taskFile in files)
			{
				ids.Add(TaskIdOf(taskFile));
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
