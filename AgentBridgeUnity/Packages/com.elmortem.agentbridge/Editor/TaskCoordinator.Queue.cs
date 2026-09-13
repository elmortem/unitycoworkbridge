using System.Collections.Generic;
using System.IO;
using System;

namespace AgentBridge
{
    public static partial class TaskCoordinator
    {
        public static List<PendingTaskInfo> GetQueueSnapshot()
        {
            return ReadQueueWindowSnapshot().Pending;
        }

        public static TaskQueueSnapshot ReadQueueWindowSnapshot()
        {
            var snapshot = TaskQueueSnapshot.Read(BridgePaths.Inbox, BridgePaths.Journal,
                _activeTaskId ?? TestRunLifecycle.TaskId, DateTime.UtcNow);
            var pending = snapshot.Pending;
            var result = new List<PendingTaskInfo>();
            foreach (var item in AgentSessionScheduler.BuildQueue(pending))
                result.Add(pending.Find(t => t.Id == item.Id));
            pending.Clear();
            pending.AddRange(result);
            var coordination = CoordinationEditorAdapter.Snapshot;
            snapshot.IncludeCoordination(coordination);
            if (!string.IsNullOrEmpty(CoordinationEditorAdapter.UnavailableReason))
                snapshot.Errors.Add("Coordination: " + CoordinationEditorAdapter.UnavailableReason);
            return snapshot;
        }

        public static string CancelCoordinationFromQueueWindow(string id)
        {
            var request = CoordinationEditorAdapter.Snapshot?.Requests.Find(r => r.Id == id);
            if (request == null) return "Coordination request no longer exists.";
            if (request.State != Coordination.CoordinationLimits.StateWaiting) return "Request is no longer waiting; refresh the queue.";
            var command = CoordinationEditorAdapter.NewCommand(Coordination.CoordinationEngine.OpCancel);
            command.Session = request.Session;
            command.Uuid = request.Uuid;
            if (!CoordinationEditorAdapter.TryApply(command, out var reply)) return "Coordination store is busy; try again.";
            return reply.Ok ? null : reply.Code + ": " + reply.Message;
        }

        public static List<TaskRecord> GetRunningSnapshot()
        {
            return ReadQueueWindowSnapshot().Active;
        }

        public static bool MoveQueuedTask(string id, string beforeId)
        {
            var queue = GetQueueSnapshot();
            queue.RemoveAll(t => t.Kind == "cancel" || t.Kind == "stopplay");
            var task = queue.Find(t => t.Id == id);
            if (task == null || id == beforeId) return false;
            if (beforeId != null && !queue.Exists(t => t.Id == beforeId)) return false;
            queue.Remove(task);
            int index = beforeId == null ? queue.Count : queue.FindIndex(t => t.Id == beforeId);
            queue.Insert(index, task);
            TaskQueueOrder.Set(queue.ConvertAll(t => t.Id));
            UpdateQueueStatus(BuildPendingList(_activeTaskId ?? TestRunLifecycle.TaskId));
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
