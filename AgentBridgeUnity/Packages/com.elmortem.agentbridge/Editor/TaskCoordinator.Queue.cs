using System.Collections.Generic;
using System.IO;
using System;

namespace AgentBridge
{
    public static partial class TaskCoordinator
    {
        public static List<PendingTaskInfo> GetQueueSnapshot()
        {
            var pending = BuildPendingList(_activeTaskId ?? TestRunLifecycle.TaskId);
            var result = new List<PendingTaskInfo>();
            foreach (var item in AgentSessionScheduler.BuildQueue(pending))
                result.Add(pending.Find(t => t.Id == item.Id));
            return result;
        }

        public static List<TaskRecord> GetRunningSnapshot()
        {
            var result = new List<TaskRecord>();
            foreach (string file in Directory.GetFiles(BridgePaths.Journal, "*.json"))
                if (TaskJournal.TryRead(Path.GetFileNameWithoutExtension(file), out var record)
                    && !IsTerminal(record.Status) && record.Status != "queued") result.Add(record);
            return result;
        }

        public static bool MoveQueuedTask(string id, string beforeId)
        {
            var queue = GetQueueSnapshot();
            var task = queue.Find(t => t.Id == id);
            if (task == null || id == beforeId) return false;
            if (beforeId != null && !queue.Exists(t => t.Id == beforeId)) return false;
            queue.Remove(task);
            int index = beforeId == null ? queue.Count : queue.FindIndex(t => t.Id == beforeId);
            queue.Insert(index, task);
            TaskQueueOrder.Set(queue.ConvertAll(t => t.Id));
            UpdateQueueStatus(queue);
            return true;
        }

        // Human editor actions are authoritative, as is the existing Cancel Running Task menu.
        public static bool CancelFromQueueWindow(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (TestRunLifecycle.RequestStop(id, "canceled", "Canceled by user in Task Queue")) return true;
            if (_activeTaskId == id) { CancelActive(); return true; }
            var pending = GetQueueSnapshot().Find(t => t.Id == id);
            if (pending != null)
            {
                WriteTerminal(id, pending.Kind, "canceled", "Canceled before execution by user",
                    TaskFileHash.HashOf(pending.TaskFilePath, PayloadPathOf(pending.TaskFilePath)));
                UpdateQueueStatus(BuildPendingList(_activeTaskId ?? TestRunLifecycle.TaskId));
                return true;
            }
            if (TaskJournal.TryRead(id, out var record) && record.Status == "attached")
            {
                record.Status = "canceled";
                record.FinishedAtUtc = DateTime.UtcNow.ToString("o");
                record.Logs.Add("Canceled by user in Task Queue");
                TaskJournal.Write(record);
                TelemetryLog.TaskFinished(record);
                CoordinationGate.ReleaseByRecord(record, false, "canceled");
                return true;
            }
            return false;
        }
    }
}
