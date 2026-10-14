using System;
using System.Collections.Generic;
using System.Collections;
using AgentBridge.Queueing;
using AgentBridge.Coordination;
using UnityEngine.TestTools;
using System.IO;
using AgentBridge;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class TaskQueueWindowTests
{
    private string _savedOrder;
    private string OrderPath => Path.Combine(QueueFiles.Root(BridgePaths.ProjectRoot), "order.txt");
    private readonly List<string> _ids = new List<string>();
    [SetUp] public void Setup()
    {
        _savedOrder = SharedFile.TryReadAllText(OrderPath, out var order) ? order : "";
        QueueFiles.Write(OrderPath, "");
    }
    [TearDown] public void Cleanup()
    {
        QueueFiles.Write(OrderPath, _savedOrder);
        foreach (string id in _ids)
        {
            QueueEditorAdapter.Cancel(id);
            File.Delete(Path.Combine(BridgePaths.Inbox, id + ".task.json"));
            File.Delete(Path.Combine(BridgePaths.Journal, id + ".json"));
        }
        _ids.Clear();
    }
    private string Submit()
    {
        string id = "queue-window-test-" + Guid.NewGuid().ToString("N");
        _ids.Add(id);
        File.WriteAllText(Path.Combine(BridgePaths.Inbox, id + ".task.json"),
            JsonUtility.ToJson(new TaskRequest { Id = id, Kind = "compile", Fresh = true, Note = "queue window fixture; must not use cache" }));
        return id;
    }
    [Test] public void DragChangesRealSchedulerPickAndReportedPosition()
    {
        var a = new PendingTaskInfo { Id = "a", EffectiveSessionId = "a", CreatedUtc = DateTime.UtcNow.AddMinutes(-1) };
        var b = new PendingTaskInfo { Id = "b", EffectiveSessionId = "b", CreatedUtc = DateTime.UtcNow };
        var tasks = new List<PendingTaskInfo> { a, b };
        QueueFiles.Write(OrderPath, "b\na");
        Assert.IsTrue(AgentSessionScheduler.TryPick(tasks, DateTime.UtcNow, out var next, out _, out _));
        Assert.AreEqual("b", next.Id);
        Assert.AreEqual("b", AgentSessionScheduler.BuildQueue(tasks)[0].Id);
        Assert.AreEqual("a", TaskQueueOrder.First(new List<PendingTaskInfo> { a }).Id);
        QueueFiles.Write(OrderPath, "");
        Assert.IsNull(TaskQueueOrder.First(tasks));
    }
    [UnityTest] public IEnumerator MoveThenCancelPreservesRequestAndDoesNotRequeue()
    {
        string a = Submit(), b = Submit();
        string path = Path.Combine(BridgePaths.Inbox, b + ".task.json");
        string original = File.ReadAllText(path);
        Assert.IsTrue(TaskCoordinator.MoveQueuedTask(b, a));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var queue = TaskCoordinator.GetQueueSnapshot();
            if (queue.FindIndex(t => t.Id == b) < queue.FindIndex(t => t.Id == a)) break;
            Assert.Less(DateTime.UtcNow, deadline, "Dispatcher must apply move");
            yield return null;
        }
        Assert.IsTrue(TaskCoordinator.CancelFromQueueWindow(b));
        TaskRecord record;
        while (!TaskJournal.TryRead(b, out record) || record.Status != "canceled")
        {
            Assert.Less(DateTime.UtcNow, deadline, "Dispatcher must apply cancel");
            yield return null;
        }
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
