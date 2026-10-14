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
            if (!CoordinationEditorAdapter.TryApply(command, out var reply)) return null; // The durable command will be applied by the dispatcher.
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
            return QueueEditorAdapter.Move(id, beforeId);
        }

        // Human editor actions are authoritative, as is the existing Cancel Running Task menu.
        public static bool CancelFromQueueWindow(string id)
        {
            if (string.IsNullOrEmpty(id) || QueueEditorAdapter.IsCanceled(id)) return false;
            return QueueEditorAdapter.Cancel(id);
        }
    }
}
