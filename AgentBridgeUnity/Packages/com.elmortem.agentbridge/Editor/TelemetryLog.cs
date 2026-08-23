using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AgentBridge
{
	// The editor half of the bridge telemetry: one JSONL line per event, one file per day.
	// Telemetry must never be able to fail a task, so every failure here is swallowed.
	public static class TelemetryLog
	{
		private const int WriteAttempts = 3;
		private const int RetryDelayMs = 20;

		private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
		private static string _prunedDay = "";

		public static void Write(string eventName, string agentSessionId, string taskId, TelemetryField[] fields)
		{
			if (!AgentBridgeSettingsStore.GetTelemetryEnabled())
			{
				return;
			}

			try
			{
				DateTime nowUtc = DateTime.UtcNow;
				Prune(nowUtc);
				string line = TelemetryJson.BuildLine(nowUtc, "editor", eventName, agentSessionId ?? "", taskId ?? "", fields);
				Append(BridgePaths.TelemetryFile("editor", nowUtc), line);
			}
			catch
			{
			}
		}

		// A task reaches its terminal status from half a dozen places — the coordinator, the test
		// runner, the play session, the caches — and only some of them go through FinishTask. The
		// rule this helper exists to keep is that every terminal record emits exactly one
		// task_finish, or the log undercounts precisely the long tasks it is meant to explain.
		public static void TaskFinished(TaskRecord record)
		{
			if (record == null)
			{
				return;
			}

			Write("task_finish", record.AgentSessionId, record.Id, new[]
			{
				TelemetryField.Text("Kind", record.Kind),
				TelemetryField.Text("Status", record.Status),
				TelemetryField.Number("TotalMs", TotalMsOf(record)),
				TelemetryField.Flag("Cached", record.Cached),
				TelemetryField.Number("Waiting", record.Contention != null ? record.Contention.WaitingSessions : 0),
				TelemetryField.Number("OldestWaitS", record.Contention != null ? record.Contention.OldestWaitSeconds : 0)
			});
		}

		// Only the tasks the coordinator finishes itself carry measured timing; a task finalized
		// across a domain reload has nothing but its own two timestamps.
		private static long TotalMsOf(TaskRecord record)
		{
			if (record.Timing != null && record.Timing.TotalMs > 0)
			{
				return record.Timing.TotalMs;
			}

			DateTime started;
			DateTime finished;
			if (!TryParseUtc(record.StartedAtUtc, out started) || !TryParseUtc(record.FinishedAtUtc, out finished))
			{
				return 0;
			}

			double elapsed = (finished - started).TotalMilliseconds;
			return elapsed > 0d ? (long)elapsed : 0;
		}

		private static bool TryParseUtc(string value, out DateTime result)
		{
			result = DateTime.MinValue;
			if (string.IsNullOrEmpty(value))
			{
				return false;
			}

			return DateTime.TryParse(
				value,
				CultureInfo.InvariantCulture,
				DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
				out result);
		}

		private static void Append(string path, string line)
		{
			byte[] bytes = Utf8.GetBytes(line + "\n");

			for (int attempt = 0; attempt < WriteAttempts; attempt++)
			{
				try
				{
					using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
					{
						stream.Write(bytes, 0, bytes.Length);
					}

					return;
				}
				catch (IOException)
				{
					Thread.Sleep(RetryDelayMs);
				}
			}
		}

		private static void Prune(DateTime nowUtc)
		{
			string day = nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
			if (_prunedDay == day)
			{
				return;
			}

			_prunedDay = day;
			DateTime threshold = nowUtc.Date.AddDays(-AgentBridgeSettingsStore.GetTelemetryKeepDays());

			foreach (string file in Directory.GetFiles(BridgePaths.LogsRoot, "AgentBridge-*.jsonl"))
			{
				try
				{
					if (File.GetLastWriteTimeUtc(file) < threshold)
					{
						File.Delete(file);
					}
				}
				catch
				{
				}
			}
		}
	}
}
