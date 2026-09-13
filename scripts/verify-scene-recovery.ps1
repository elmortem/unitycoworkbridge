param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe')
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
function Invoke-Bridge([string[]]$Arguments, [switch]$AllowTerminalFailure) {
	$json = (& $Cli @Arguments --project $Project --format json | Out-String)
	if ($LASTEXITCODE -ne 0 -and !($AllowTerminalFailure -and $LASTEXITCODE -eq 1)) { throw $json }
	return ($json | ConvertFrom-Json)
}
$health = Invoke-Bridge @('status')
if ($health.bridge.activeTaskId -or $health.bridge.isPlaying -or $health.bridge.queuedTasks.Count -gt 0) {
	throw 'Run only in an idle test editor.'
}
$id = 'Task_' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff') + '_scene_recovery'
$path = Join-Path $Project "Temp/AgentBridge/$id.cs"
$source = @'
using System;
using System.Reflection;
using System.Threading.Tasks;
using AgentBridge;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
public static class PROBE_NAME
{
	static int restores;
	static MethodInfo complete = typeof(PlayModeSceneRecovery).GetMethod("CompleteRecovery", BindingFlags.Static | BindingFlags.NonPublic);
	public static void Restored(Scene[] scenes)
	{
		restores++;
		// Bound the reproduction: broken code restores twice, never infinite recursion.
		if (restores == 1) complete.Invoke(null, null);
	}
	public static Task<string> Run()
	{
		if (PlayModeSceneRecovery.IsPending || TestRunnerCancellation.IsRunning()) throw new Exception("Expected idle editor");
		var evt = typeof(EditorSceneManager).GetEvent("sceneManagerSetupRestored");
		var handler = Delegate.CreateDelegate(evt.EventHandlerType, typeof(PROBE_NAME).GetMethod("Restored"));
		string error;
		if (!PlayModeSceneRecovery.Begin("PROBE_NAME_owner", out error)) throw new Exception(error);
		evt.AddEventHandler(null, handler);
		try
		{
			complete.Invoke(null, null);
			if (restores != 0 || !PlayModeSceneRecovery.IsPending)
				throw new Exception("Recovery ran before a result: restores=" + restores);
			PlayModeSceneRecovery.RecordResult(new TestRunResult { aborted = true, message = "Synthetic recovery fixture" });
			complete.Invoke(null, null);
			if (restores != 1 || PlayModeSceneRecovery.IsPending)
				throw new Exception("Expected exactly one restore with reentrant callback; got " + restores);
			complete.Invoke(null, null);
			if (restores != 1) throw new Exception("Late callback repeated completed recovery");
		}
		finally { evt.RemoveEventHandler(null, handler); PlayModeSceneRecovery.Cancel(); }
		return Task.FromResult("PASS: waits for result; one restore under reentrancy; late callback harmless");
	}
}
'@
[IO.File]::WriteAllText($path, $source.Replace('PROBE_NAME', $id))
$result = Invoke-Bridge @('csharp', $path, '--session', 'scene-recovery-verifier', '--wait', '30')
if ($result.Status -ne 'success') { throw ($result | ConvertTo-Json -Depth 10) }
Write-Output "$($result.ReturnValue) ($($result.Id))"

# Exercise normal RunFinished/domain reload/cleanup, then demand another task.
# A green NUnit result alone must not be accepted as proof that the queue moved.
$invalidResults = @()
foreach ($iteration in 1..2) {
	$run = Invoke-Bridge -Arguments @('tests', '--mode', 'PlayMode', '--test', 'AgentBridgeCancellationPlayModeTests.ShortSuccessfulRun', '--fresh', '--session', 'scene-recovery-verifier', '--wait', '45') -AllowTerminalFailure
	if ($run.Status -ne 'success') { $invalidResults += "$($run.Id): $($run.Status) ($($run.Evidence.Reason))" }
	if ($run.Tests.total -ne 1 -or $run.Tests.passed -ne 1) {
		throw ($run | ConvertTo-Json -Depth 10)
	}
	$nextId = 'Task_' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff') + "_recovery_follower_$iteration"
	$nextPath = Join-Path $Project "Temp/AgentBridge/$nextId.cs"
	[IO.File]::WriteAllText($nextPath, @"
using System;
using System.Threading.Tasks;
using AgentBridge;
public static class $nextId {
	public static Task<string> Run() {
		if (PlayModeSceneRecovery.IsPending || TestRunnerCancellation.IsRunning() || !string.IsNullOrEmpty(TestRunLifecycle.TaskId))
			throw new Exception("Finished test still owns the editor");
		return Task.FromResult("Queue advanced after successful PlayMode test");
	}
}
"@)
	$follower = Invoke-Bridge @('csharp', $nextPath, '--session', 'scene-recovery-verifier', '--wait', '15')
	if ($follower.Status -ne 'success') { throw ($follower | ConvertTo-Json -Depth 10) }
	Write-Output "PASS queue advanced after PlayMode run $iteration ($($run.Id), $($run.Status)); follower $($follower.Id)"
}
if ($invalidResults.Count -gt 0) { throw ('Queue advanced, but PlayMode evidence is not accepted: ' + ($invalidResults -join '; ')) }
