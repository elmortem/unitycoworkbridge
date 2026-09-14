using AgentBridge;
using UnityEditor;
using UnityEditor.SceneManagement;

// Executes production recovery code with deterministic editor callbacks and I/O.
// The live companion scripts/verify-scene-recovery.ps1 covers real Unity transitions.
var root = Path.Combine(Path.GetTempPath(), "bridge-recovery-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
BridgePaths.PlayModeSceneStateFile = Path.Combine(root, "scene-state.json");
int failures = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Case(string name, Action body)
{
	PlayModeSceneRecovery.Stop();
	PlayModeSceneRecovery.Cancel();
	EditorApplication.Reset();
	SessionState.Clear();
	TestRunnerCancellation.Requests = AgentTestRunner.CancellationFinalizations = 0;
	EditorSceneManager.Restores = 0;
	EditorSceneManager.OnRestore = null;
	TestRunnerCancellation.Running = false;
	TaskJournal.Owner = new TaskRecord { Status = "running" };
	AgentTestRunner.OnFinalize = () => TaskJournal.Owner.Status = "success";
	PlayModeSceneRecovery.Start();
	try
	{
		Check(PlayModeSceneRecovery.Begin("owner", out var error), error);
		body();
		Console.WriteLine("PASS " + name);
	}
	catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
	finally { PlayModeSceneRecovery.Stop(); PlayModeSceneRecovery.Cancel(); }
}
void Result() => PlayModeSceneRecovery.RecordResult(new TestRunResult());
Case("startup waits for result", () =>
{
	PlayModeSceneRecovery.Start(); // Domain reload/startup with pending preparation.
	EditorApplication.Frame();
	Check(EditorSceneManager.Restores == 0, "restored a scene before RunFinished");
	Check(PlayModeSceneRecovery.IsPending, "lost pending owner before result");
});
Case("result waits for active framework cleanup", () =>
{
	TestRunnerCancellation.Running = true;
	Result(); EditorApplication.Frame();
	Check(EditorSceneManager.Restores == 0 && PlayModeSceneRecovery.IsPending, "interfered with framework cleanup");
	TestRunnerCancellation.Running = false;
	EditorApplication.Frame();
	Check(EditorSceneManager.Restores == 1 && !PlayModeSceneRecovery.IsPending, "did not recover after framework stopped");
});
Case("scene callback cannot reenter recovery", () =>
{
	bool entered = false;
	EditorSceneManager.OnRestore = () => { if (!entered) { entered = true; EditorApplication.Frame(); } };
	Result(); EditorApplication.Frame();
	Check(EditorSceneManager.Restores == 1, "nested callback restored " + EditorSceneManager.Restores + " times");
	Check(!PlayModeSceneRecovery.IsPending, "recovery did not finish");
	EditorApplication.Frame();
	Check(EditorSceneManager.Restores == 1, "late callback repeated restoration");
});
Case("finalizer failure retries without restoring scenes again", () =>
{
	int attempts = 0;
	AgentTestRunner.OnFinalize = () => { if (++attempts == 1) throw new IOException("injected finalizer failure"); TaskJournal.Owner.Status = "success"; };
	Result(); EditorApplication.Frame(); EditorApplication.Frame();
	Check(attempts >= 2 && !PlayModeSceneRecovery.IsPending, "finalizer did not recover");
	Check(EditorSceneManager.Restores == 1, "finalizer retry reloaded the scene");
});
Case("deferred finalizer retains owner across restart", () =>
{
	AgentTestRunner.OnFinalize = () => { }; // Async finalizer has not written terminal status.
	Result(); EditorApplication.Frame();
	Check(PlayModeSceneRecovery.IsPending, "dropped owner before finalization");
	PlayModeSceneRecovery.Stop(); PlayModeSceneRecovery.Start();
	EditorApplication.Frame();
	Check(EditorSceneManager.Restores == 1, "restart repeated persisted restoration");
	TaskJournal.Owner.Status = "success";
	EditorApplication.Frame();
	Check(!PlayModeSceneRecovery.IsPending, "terminal owner did not release recovery");
});
Case("legacy expired deadline does not stop a long run", () =>
{
	SessionState.SetString("AgentBridge_TestRunLifecycle", """{"Id":"owner","Deadline":1,"Submitted":true}""");
	TestRunnerCancellation.Running = true;
	TestRunLifecycle.Tick();
	Check(TaskJournal.Owner.Status == "running", "expired legacy deadline stopped the run");
	Check(TestRunnerCancellation.Requests == 0 && AgentTestRunner.CancellationFinalizations == 0, "stopped without explicit cancellation");
	Check(TestRunLifecycle.TaskId == "owner", "lost persisted ownership");
});
Case("preemption reason survives reload reads and repeated cancel", () =>
{
	TestRunLifecycle.Begin("owner");
	TestRunLifecycle.Submitted("job");
	TestRunnerCancellation.Running = true;
	const string reason = "preempted_after_300s: Canceled by neighbor after 427 seconds";
	Check(!TestRunLifecycle.RequestStop("other-task", "canceled", "wrong"), "canceled the wrong task");
	Check(TestRunLifecycle.RequestStop("owner", "canceled", reason), "did not accept stop");
	TestRunLifecycle.RequestStop("owner", "canceled", "second requester");
	TestRunLifecycle.Tick();
	Check(TaskJournal.Owner.Status == "canceling" && AgentTestRunner.CancellationFinalizations == 0, "published terminal while executor runs");
	Check(TestRunLifecycle.IsStopping("owner") && TestRunnerCancellation.Requests == 1, "lost persisted stop state");
	TestRunnerCancellation.Running = false;
	AgentTestRunner.OnFinalize = () => { };
	TestRunLifecycle.Tick();
	Check(AgentTestRunner.CancellationFinalizations == 0, "published terminal before restoring scenes");
	EditorApplication.Frame();
	Check(EditorSceneManager.Restores == 1 && !PlayModeSceneRecovery.IsPending, "recovery did not finish");
	TestRunLifecycle.Tick();
	Check(TaskJournal.Owner.Status == "canceled" && AgentTestRunner.CancellationReason == reason, "lost first initiator or reason");
	Check(TestRunLifecycle.TaskId == "", "did not release lifecycle after recovery");
	TestRunLifecycle.Tick();
	Check(AgentTestRunner.CancellationFinalizations == 1, "finalized cancellation twice");
});
Console.WriteLine(failures == 0 ? "Scene recovery and cancellation lifecycle: PASS" : $"Scene recovery and cancellation lifecycle: FAIL ({failures})");
Environment.ExitCode = failures == 0 ? 0 : 1;
