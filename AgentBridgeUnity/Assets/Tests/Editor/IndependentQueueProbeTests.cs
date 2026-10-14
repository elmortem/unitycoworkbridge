using System;
using System.IO;
using System.Threading;
using AgentBridge;
using NUnit.Framework;

public class IndependentQueueProbeTests
{
    // The external acceptance script performs queue operations between these two markers.
    // An ordinary synchronous NUnit test deliberately prevents every EditorApplication tick.
    [Test]
    public void BlockEditorMainThread()
    {
        string root = Path.Combine(BridgePaths.ProjectRoot, "Temp", "AgentBridge", "IndependentQueueProbe");
        Directory.CreateDirectory(root);
        File.Delete(Path.Combine(root, "finished"));
        File.WriteAllText(Path.Combine(root, "started"), DateTime.UtcNow.ToString("o"));
        Thread.Sleep(20000);
        File.WriteAllText(Path.Combine(root, "finished"), DateTime.UtcNow.ToString("o"));
    }
}
