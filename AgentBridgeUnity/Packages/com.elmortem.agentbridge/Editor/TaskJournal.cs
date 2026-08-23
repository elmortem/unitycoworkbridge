using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AgentBridge
{
	public static class TaskJournal
	{
		public static void Write(TaskRecord record)
		{
			string path = Path.Combine(BridgePaths.Journal, record.Id + ".json");
			string tempPath = path + ".tmp";

			string json = JsonUtility.ToJson(record, true);
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

		public static bool TryRead(string id, out TaskRecord record)
		{
			string path = Path.Combine(BridgePaths.Journal, id + ".json");
			if (!File.Exists(path))
			{
				record = null;
				return false;
			}

			string json = File.ReadAllText(path);
			record = JsonUtility.FromJson<TaskRecord>(json);
			return record != null;
		}

		public static void Delete(string id)
		{
			string path = Path.Combine(BridgePaths.Journal, id + ".json");
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}

		public static void Trim(int keep)
		{
			string[] files = Directory.GetFiles(BridgePaths.Journal, "*.json");
			if (files.Length <= keep)
			{
				TrimPendingFiles();
				return;
			}

			var records = new List<TaskRecord>();
			foreach (string file in files)
			{
				try
				{
					string json = File.ReadAllText(file);
					TaskRecord record = JsonUtility.FromJson<TaskRecord>(json);
					if (record != null && record.Status != "attached")
					{
						records.Add(record);
					}
				}
				catch
				{
				}
			}

			records.Sort(CompareRecencyDescending);

			for (int i = keep; i < records.Count; i++)
			{
				RemoveRecord(records[i]);
			}

			TrimPendingFiles();
		}

		private static void TrimPendingFiles()
		{
			foreach (string pendingFile in Directory.GetFiles(BridgePaths.WorkingRoot, "pending_*.json"))
			{
				try
				{
					string fileName = Path.GetFileNameWithoutExtension(pendingFile);
					string id = fileName.Substring("pending_".Length);
					TaskRecord record;
					if (TryRead(id, out record) && IsIntermediate(record.Status))
					{
						continue;
					}

					File.Delete(pendingFile);
				}
				catch
				{
				}
			}
		}

		private static bool IsIntermediate(string status)
		{
			return status == "queued" || status == "compiling" || status == "running";
		}

		private static int CompareRecencyDescending(TaskRecord a, TaskRecord b)
		{
			bool aOpen = string.IsNullOrEmpty(a.FinishedAtUtc);
			bool bOpen = string.IsNullOrEmpty(b.FinishedAtUtc);

			if (aOpen && bOpen)
			{
				return 0;
			}

			if (aOpen)
			{
				return -1;
			}

			if (bOpen)
			{
				return 1;
			}

			return string.CompareOrdinal(b.FinishedAtUtc, a.FinishedAtUtc);
		}

		private static void RemoveRecord(TaskRecord record)
		{
			string journalPath = Path.Combine(BridgePaths.Journal, record.Id + ".json");
			if (File.Exists(journalPath))
			{
				File.Delete(journalPath);
			}

			if (Directory.Exists(BridgePaths.Inbox))
			{
				foreach (string inboxFile in Directory.GetFiles(BridgePaths.Inbox, record.Id + ".*"))
				{
					File.Delete(inboxFile);
				}
			}

			string artifactsDir = Path.Combine(BridgePaths.ArtifactsRoot, record.Id);
			if (Directory.Exists(artifactsDir))
			{
				Directory.Delete(artifactsDir, true);
			}
		}
	}
}
