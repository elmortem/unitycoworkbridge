using System;
using System.Collections.Generic;
using UnityEditor;

namespace AgentBridge
{
    // SessionState survives assembly reload without modifying submitted requests or their hashes.
    public static class TaskQueueOrder
    {
        private const string Key = "AgentBridge.ManualQueueOrder";
        public static void Set(List<string> ids) => SessionState.SetString(Key, string.Join("\n", ids));
        public static void Clear() => SessionState.EraseString(Key);
        public static int Compare(PendingTaskInfo left, PendingTaskInfo right)
        {
            var ids = new List<string>(SessionState.GetString(Key, "").Split('\n'));
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
            foreach (string id in SessionState.GetString(Key, "").Split('\n'))
                foreach (var task in pending)
                    if (task.Id == id) return task;
            return null;
        }
        public static void Apply(List<PendingTaskInfo> tasks)
        {
            var ordered = new List<PendingTaskInfo>();
            foreach (string id in SessionState.GetString(Key, "").Split('\n'))
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
