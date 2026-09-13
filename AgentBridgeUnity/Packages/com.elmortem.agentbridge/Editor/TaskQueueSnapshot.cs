using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AgentBridge
{
    // Observing the queue must never admit/reject work or change the editor wake state.
    public sealed class TaskQueueSnapshot
    {
        public readonly List<PendingTaskInfo> Pending = new List<PendingTaskInfo>();
        public readonly List<TaskRecord> Active = new List<TaskRecord>();
        public readonly List<TaskRecord> Recent = new List<TaskRecord>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<Coordination.CoordinationRequest> CoordinationRequests = new List<Coordination.CoordinationRequest>();

        public void IncludeCoordination(Coordination.CoordinationState state)
        {
            if (state == null) return;
            foreach (var request in state.Requests)
                if (!request.IsTerminal) CoordinationRequests.Add(request);
            CoordinationRequests.Sort((a, b) => a.Ticket.CompareTo(b.Ticket));
        }

        public static TaskQueueSnapshot Read(string inbox, string journal, string activeId, DateTime nowUtc)
        {
            var snapshot = new TaskQueueSnapshot();
            var records = new Dictionary<string, TaskRecord>();
            foreach (string path in snapshot.Files(journal, "*.json"))
            {
                try
                {
                    var record = JsonUtility.FromJson<TaskRecord>(File.ReadAllText(path));
                    if (record == null || string.IsNullOrEmpty(record.Id)) throw new FormatException("Missing task ID");
                    records[record.Id] = record;
                }
                catch (Exception e) { snapshot.Errors.Add(Path.GetFileName(path) + ": " + e.Message); }
            }
            foreach (string path in snapshot.Files(inbox, "*.task.json"))
            {
                string id = Path.GetFileName(path).Replace(".task.json", "");
                try
                {
                    records.TryGetValue(id, out var record);
                    if (id == activeId && record == null)
                    {
                        records[id] = new TaskRecord { Id = id, Status = "running", Kind = "" };
                        continue;
                    }
                    if (record != null && !TaskCoordinator.IsTerminal(record.Status))
                    {
                        // A task reserved by StartTask can still carry 'queued' during preflight.
                        if (record.Status != "queued" || id == activeId) continue;
                    }
                    if (record != null && TaskCoordinator.IsTerminal(record.Status))
                    {
                        var submitted = JsonUtility.FromJson<TaskRequest>(File.ReadAllText(path));
                        string payload = string.IsNullOrEmpty(submitted?.PayloadFile) ? null : Path.Combine(inbox, submitted.PayloadFile);
                        if (record.Hash == TaskFileHash.HashOf(path, payload)) continue;
                    }
                    var request = JsonUtility.FromJson<TaskRequest>(File.ReadAllText(path));
                    if (request == null) throw new FormatException("Unreadable request");
                    snapshot.Pending.Add(new PendingTaskInfo
                    {
                        Id = id, Kind = request.Kind ?? "", TaskFilePath = path,
                        CreatedUtc = File.GetCreationTimeUtc(path), Note = request.Note ?? "",
                        EffectiveSessionId = AgentSessionScheduler.EffectiveSessionId(request.AgentSessionId, id)
                    });
                    records.Remove(id);
                }
                catch (Exception e) { snapshot.Errors.Add(Path.GetFileName(path) + ": " + e.Message); }
            }
            foreach (var record in records.Values)
            {
                if (!TaskCoordinator.IsTerminal(record.Status)) snapshot.Active.Add(record);
                else if (DateTime.TryParse(record.FinishedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var finished)
                    && finished.ToUniversalTime() >= nowUtc.AddMinutes(-10)) snapshot.Recent.Add(record);
            }
            snapshot.Active.Sort((a, b) => string.CompareOrdinal(a.StartedAtUtc, b.StartedAtUtc));
            snapshot.Recent.Sort((a, b) => string.CompareOrdinal(b.FinishedAtUtc, a.FinishedAtUtc));
            if (snapshot.Recent.Count > 20) snapshot.Recent.RemoveRange(20, snapshot.Recent.Count - 20);
            return snapshot;
        }

        private string[] Files(string directory, string pattern)
        {
            try { return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : Array.Empty<string>(); }
            catch (Exception e) { Errors.Add(directory + ": " + e.Message); return Array.Empty<string>(); }
        }
    }
}
