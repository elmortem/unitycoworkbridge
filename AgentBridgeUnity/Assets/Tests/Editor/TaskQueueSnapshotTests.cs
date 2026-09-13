using System;
using System.IO;
using AgentBridge;
using NUnit.Framework;
using UnityEngine;

public class TaskQueueSnapshotTests
{
    private string _root, _inbox, _journal;
    [SetUp] public void Setup()
    {
        _root = Path.Combine(Application.temporaryCachePath, "queue-snapshot-" + Guid.NewGuid().ToString("N"));
        _inbox = Path.Combine(_root, "Inbox");
        _journal = Path.Combine(_root, "Journal");
        Directory.CreateDirectory(_inbox);
        Directory.CreateDirectory(_journal);
    }
    [TearDown] public void Cleanup() { Directory.Delete(_root, true); }
    private string Request(string id, string kind = "compile")
    {
        string path = Path.Combine(_inbox, id + ".task.json");
        File.WriteAllText(path, JsonUtility.ToJson(new TaskRequest { Id = id, Kind = kind }));
        return path;
    }
    private void Record(string id, string status, string hash = "")
    {
        File.WriteAllText(Path.Combine(_journal, id + ".json"), JsonUtility.ToJson(new TaskRecord
        { Id = id, Kind = "compile", Status = status, Hash = hash, FinishedAtUtc = DateTime.UtcNow.ToString("o") }));
    }
    private TaskQueueSnapshot Read(string active = "") => TaskQueueSnapshot.Read(_inbox, _journal, active, DateTime.UtcNow);

    [Test] public void PendingControlRequestsRemainVisible()
    {
        Request("cancel", "cancel"); Request("stop", "stopplay"); Request("normal");
        Assert.AreEqual(3, Read().Pending.Count);
    }
    [Test] public void ReservedQueuedRecordDoesNotDisappearBetweenPendingAndRunning()
    {
        Request("reserved"); Record("reserved", "queued");
        var snapshot = Read("reserved");
        Assert.AreEqual(0, snapshot.Pending.Count);
        Assert.AreEqual("reserved", snapshot.Active[0].Id);
    }
    [Test] public void FastCompletedTaskRemainsVisibleEvenIfNoPollSawItRun()
    {
        string path = Request("fast"); Record("fast", "success", TaskFileHash.HashOf(path, null));
        var snapshot = Read();
        Assert.AreEqual(0, snapshot.Pending.Count);
        Assert.AreEqual("fast", snapshot.Recent[0].Id);
    }
    [Test] public void ActiveTaskWithTemporarilyMissingJournalCannotBecomeDraggable()
    {
        Request("active");
        var snapshot = Read("active");
        Assert.IsEmpty(snapshot.Pending);
        Assert.AreEqual("active", snapshot.Active[0].Id);
    }
    [Test] public void BrokenJournalDoesNotFreezeOtherRows()
    {
        Request("waiting"); Record("active", "running");
        File.WriteAllText(Path.Combine(_journal, "broken.json"), "not-json");
        var snapshot = Read();
        Assert.AreEqual(1, snapshot.Errors.Count);
        Assert.AreEqual("waiting", snapshot.Pending[0].Id);
        Assert.AreEqual("active", snapshot.Active[0].Id);
    }
    [Test] public void ChangedRequestReappearsWithoutOldTerminalDuplicate()
    {
        Request("retry"); Record("retry", "rejected", "older-hash");
        var snapshot = Read();
        Assert.AreEqual("retry", snapshot.Pending[0].Id);
        Assert.AreEqual(0, snapshot.Recent.Count);
    }
    [Test] public void UnreadableInboxEntryIsReportedAndNextRefreshRecovers()
    {
        string path = Request("waiting");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.AreEqual(1, Read().Errors.Count);
        Assert.AreEqual("waiting", Read().Pending[0].Id);
    }
    [Test] public void ObservingDoesNotRejectOrRewriteSubmittedTasks()
    {
        string path = Request("invalid-window");
        File.WriteAllText(path, JsonUtility.ToJson(new TaskRequest
        { Id = "invalid-window", Kind = "compile", CoordinationWindowToken = "expired", CoordinationStepId = "step" }));
        string before = File.ReadAllText(path);
        Assert.AreEqual(1, Read().Pending.Count);
        Assert.AreEqual(before, File.ReadAllText(path));
        Assert.IsEmpty(Directory.GetFiles(_journal));
    }
    [Test] public void CoordinationQueueIncludesWaitingAndGrantedWithoutSubmittedTask()
    {
        var state = new AgentBridge.Coordination.CoordinationState();
        state.Requests.Add(new AgentBridge.Coordination.CoordinationRequest { Id = "R0044", Session = "d35-flora", Ticket = 44, State = "waiting" });
        state.Requests.Add(new AgentBridge.Coordination.CoordinationRequest { Id = "R0043", Session = "d115-reflections", Ticket = 43, State = "granted" });
        state.Requests.Add(new AgentBridge.Coordination.CoordinationRequest { Id = "R0042", Ticket = 42, State = "closed" });
        var snapshot = Read();
        snapshot.IncludeCoordination(state);
        Assert.IsEmpty(snapshot.Pending);
        Assert.AreEqual(2, snapshot.CoordinationRequests.Count);
        Assert.AreEqual("R0043", snapshot.CoordinationRequests[0].Id);
        Assert.AreEqual("R0044", snapshot.CoordinationRequests[1].Id);
        state.Requests[0].State = "granted";
        var next = Read(); next.IncludeCoordination(state);
        Assert.AreEqual("granted", next.CoordinationRequests[1].State);
    }
}
