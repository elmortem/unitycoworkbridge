using System;
using System.Collections.Generic;
using System.IO;
using AgentBridge;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class TaskQueueWindowTests
{
    private string _savedOrder;
    private readonly List<string> _ids = new List<string>();
    [SetUp] public void Setup()
    {
        _savedOrder = SessionState.GetString("AgentBridge.ManualQueueOrder", "");
        TaskQueueOrder.Clear();
    }
    [TearDown] public void Cleanup()
    {
        SessionState.SetString("AgentBridge.ManualQueueOrder", _savedOrder);
        foreach (string id in _ids)
        {
            File.Delete(Path.Combine(BridgePaths.Inbox, id + ".task.json"));
            TaskJournal.Delete(id);
        }
        _ids.Clear();
    }
    private string Submit()
    {
        string id = "queue-window-test-" + Guid.NewGuid().ToString("N");
        _ids.Add(id);
        File.WriteAllText(Path.Combine(BridgePaths.Inbox, id + ".task.json"),
            JsonUtility.ToJson(new TaskRequest { Id = id, Kind = "compile" }));
        return id;
    }
    [Test] public void DragChangesRealSchedulerPickAndReportedPosition()
    {
        var a = new PendingTaskInfo { Id = "a", EffectiveSessionId = "a", CreatedUtc = DateTime.UtcNow.AddMinutes(-1) };
        var b = new PendingTaskInfo { Id = "b", EffectiveSessionId = "b", CreatedUtc = DateTime.UtcNow };
        var tasks = new List<PendingTaskInfo> { a, b };
        TaskQueueOrder.Set(new List<string> { "b", "a" });
        Assert.IsTrue(AgentSessionScheduler.TryPick(tasks, DateTime.UtcNow, out var next, out _, out _));
        Assert.AreEqual("b", next.Id);
        Assert.AreEqual("b", AgentSessionScheduler.BuildQueue(tasks)[0].Id);
        Assert.AreEqual("a", TaskQueueOrder.First(new List<PendingTaskInfo> { a }).Id);
        TaskQueueOrder.Clear();
        Assert.IsNull(TaskQueueOrder.First(tasks));
    }
    [Test] public void MoveThenCancelPreservesRequestAndDoesNotRequeue()
    {
        string a = Submit(), b = Submit();
        string path = Path.Combine(BridgePaths.Inbox, b + ".task.json");
        string original = File.ReadAllText(path);
        Assert.IsTrue(TaskCoordinator.MoveQueuedTask(b, a));
        var queue = TaskCoordinator.GetQueueSnapshot();
        Assert.Less(queue.FindIndex(t => t.Id == b), queue.FindIndex(t => t.Id == a));
        Assert.IsTrue(TaskCoordinator.CancelFromQueueWindow(b));
        Assert.IsTrue(TaskJournal.TryRead(b, out var record));
        Assert.AreEqual("canceled", record.Status);
        Assert.AreEqual(original, File.ReadAllText(path));
        Assert.IsFalse(TaskCoordinator.GetQueueSnapshot().Exists(t => t.Id == b));
        Assert.IsFalse(TaskCoordinator.MoveQueuedTask(b, a));
        Assert.IsFalse(TaskCoordinator.CancelFromQueueWindow(b));
    }
    [Test] public void RunningTaskCannotBeDragged()
    {
        Assert.IsFalse(TaskCoordinator.MoveQueuedTask(TaskCoordinator.ActiveTaskId, null));
    }
}
