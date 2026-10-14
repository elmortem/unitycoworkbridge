using System;
using System.Collections.Generic;
using System.IO;
using AgentBridge.Coordination;
using AgentBridge.Queueing;

namespace AgentBridge
{
    // Durable override is owned by the independent dispatcher.
    public static class TaskQueueOrder
    {
        private static string OrderPath => Path.Combine(QueueFiles.Root(BridgePaths.ProjectRoot), "order.txt");
        private static string Read() { string text; return SharedFile.TryReadAllText(OrderPath, out text) ? text : ""; }
        public static void Clear() { QueueEditorAdapter.Post(new QueueMessage { Op = "clear-order" }); }
        public static int Compare(PendingTaskInfo left, PendingTaskInfo right)
        {
            var ids = new List<string>(Read().Split('\n'));
            int a = ids.IndexOf(left.Id), b = ids.IndexOf(right.Id);
            if (a >= 0 || b >= 0)
            {
                if (a < 0) return 1;
                if (b < 0) return -1;
                return a.CompareTo(b);
            }
            int time = left.CreatedUtc.CompareTo(right.CreatedUtc);
            return time != 0 ? time : string.CompareOrdinal(left.Id, right.Id);
        }
        public static PendingTaskInfo First(List<PendingTaskInfo> pending)
        {
            foreach (string id in Read().Split('\n'))
                foreach (var task in pending)
                    if (task.Id == id) return task;
            return null;
        }
        public static void Apply(List<PendingTaskInfo> tasks)
        {
            var ordered = new List<PendingTaskInfo>();
            foreach (string id in Read().Split('\n'))
            {
                var task = tasks.Find(t => t.Id == id);
                if (task == null) continue;
                ordered.Add(task);
                tasks.Remove(task);
            }
            ordered.AddRange(tasks);
            tasks.Clear();
            tasks.AddRange(ordered);
        }
    }
}
