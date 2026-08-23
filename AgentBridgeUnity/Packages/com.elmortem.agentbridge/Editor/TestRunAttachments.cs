using System;
using System.IO;

namespace AgentBridge
{
	public static class TestRunAttachments
	{
		public static void Resolve(string sourceTaskId, TestRunDump dump)
		{
			foreach (TaskRecord record in Attached(sourceTaskId))
			{
				string taskFilePath = Path.Combine(BridgePaths.Inbox, record.Id + ".task.json");
				TaskRequest request;
				if (!TaskRequestReader.TryRead(taskFilePath, out request) || !TestFilterCoverage.Covers(dump, request))
				{
					TaskJournal.Delete(record.Id);
					continue;
				}

				TestRunResult result = TestResultAggregator.Aggregate(TestFilterCoverage.Select(dump.Entries, request));
				record.Tests = result;
				record.Status = TestResultAggregator.StatusOf(result);
				record.Cached = true;
				record.SourceTaskId = sourceTaskId;
				record.FinishedAtUtc = DateTime.UtcNow.ToString("o");
				record.Logs.Add("served from coalesced run " + sourceTaskId);
				TaskJournal.Write(record);
			}
		}

		public static void Requeue(string sourceTaskId)
		{
			foreach (TaskRecord record in Attached(sourceTaskId))
			{
				TaskJournal.Delete(record.Id);
			}
		}

		private static System.Collections.Generic.List<TaskRecord> Attached(string sourceTaskId)
		{
			var attached = new System.Collections.Generic.List<TaskRecord>();
			if (!Directory.Exists(BridgePaths.Journal))
			{
				return attached;
			}

			foreach (string file in Directory.GetFiles(BridgePaths.Journal, "*.json"))
			{
				TaskRecord record;
				if (!TaskJournal.TryRead(Path.GetFileNameWithoutExtension(file), out record))
				{
					continue;
				}

				if (record.Status == "attached" && record.AttachedToTaskId == sourceTaskId)
				{
					attached.Add(record);
				}
			}

			return attached;
		}
	}
}
