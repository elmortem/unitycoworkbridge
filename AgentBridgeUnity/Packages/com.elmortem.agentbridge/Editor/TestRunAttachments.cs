using System;
using System.Collections.Generic;
using System.IO;

namespace AgentBridge
{
	public static class TestRunAttachments
	{
		public static void Resolve(string sourceTaskId, TestRunDump dump, EvidenceRecord evidence)
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

				List<TestCaseResult> selected = TestFilterCoverage.Select(dump.Entries, request);
				if (selected.Count == 0)
				{
					TaskJournal.Delete(record.Id);
					continue;
				}

				TestRunResult result = TestResultAggregator.Aggregate(selected);
				record.Tests = result;
				record.Status = TestResultAggregator.StatusOf(result);
				record.Cached = true;
				record.SourceTaskId = sourceTaskId;
				record.Evidence = Copy(evidence);
				record.FinishedAtUtc = DateTime.UtcNow.ToString("o");
				record.Logs.Add("served from coalesced run " + sourceTaskId);
				TaskJournal.Write(record);
				TelemetryLog.TaskFinished(record);
				CoordinationGate.ReleaseByRecord(record, true, "attached");
			}
		}

		// A run whose inputs moved does not send its followers around the queue again: they get the
		// same terminal verdict, and the director decides whether a new bounded run is worth it.
		public static void Terminate(string sourceTaskId, string status, string logLine, EvidenceRecord evidence)
		{
			foreach (TaskRecord record in Attached(sourceTaskId))
			{
				record.Status = status;
				record.SourceTaskId = sourceTaskId;
				record.Evidence = Copy(evidence);
				record.FinishedAtUtc = DateTime.UtcNow.ToString("o");
				if (!string.IsNullOrEmpty(logLine))
				{
					record.Logs.Add(logLine);
				}

				TaskJournal.Write(record);
				TelemetryLog.TaskFinished(record);
				CoordinationGate.ReleaseByRecord(record, false, status);
			}
		}

		// Only for a run that never produced any verdict at all: the attachment loses its meaning
		// and the task goes back to the queue.
		public static void Requeue(string sourceTaskId)
		{
			foreach (TaskRecord record in Attached(sourceTaskId))
			{
				CoordinationGate.ReleaseByRecord(record, false, "requeued");
				TaskJournal.Delete(record.Id);
			}
		}

		private static EvidenceRecord Copy(EvidenceRecord evidence)
		{
			if (evidence == null)
			{
				return EvidenceRecord.UnknownBecause("the source run recorded no evidence");
			}

			return new EvidenceRecord
			{
				Validity = evidence.Validity,
				InputDigest = evidence.InputDigest,
				EndInputDigest = evidence.EndInputDigest,
				WindowId = evidence.WindowId,
				Reason = evidence.Reason,
				ArtifactsPresent = evidence.ArtifactsPresent
			};
		}

		private static List<TaskRecord> Attached(string sourceTaskId)
		{
			var attached = new List<TaskRecord>();
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
