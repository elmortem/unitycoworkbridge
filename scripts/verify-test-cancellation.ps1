param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe')
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
$Cli = [IO.Path]::GetFullPath($Cli)
$scratch = Join-Path $Project 'Temp/AgentBridge/repro-queue'
New-Item -ItemType Directory -Force $scratch | Out-Null
$runId = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$evidence = Join-Path $scratch $runId
New-Item -ItemType Directory -Force $evidence | Out-Null
function Invoke-Bridge([string[]]$Arguments) {
	# status.json is replaced atomically; a reader can catch the swap gap and see exit 3.
	# One retry distinguishes that gap from an editor that is really gone.
	$json = (& $Cli @Arguments --project $Project --format json | Out-String)
	if ($LASTEXITCODE -eq 3) {
		Start-Sleep -Seconds 2
		$json = (& $Cli @Arguments --project $Project --format json | Out-String)
		if ($LASTEXITCODE -eq 3) { throw $json }
	}
	return ($json | ConvertFrom-Json)
}
function Save-Result($Name, $Result) { $Result | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $evidence "$Name.json") }
function New-Marker([string]$Suffix) {
	$id = "Task_${runId}_$Suffix"
	$path = Join-Path $Project "Temp/AgentBridge/$id.cs"
	$source = @"
using System;
using System.Threading.Tasks;
using AgentBridge;
public static class $id
{
 public static Task<string> Run()
 {
  if (TestRunnerCancellation.IsRunning() || PlayModeSceneRecovery.IsPending || !string.IsNullOrEmpty(TestRunLifecycle.TaskId))
   throw new Exception("Previous test executor or recovery is still alive");
  return Task.FromResult("Previous executor stopped; scenes recovered");
 }
}
"@
	[IO.File]::WriteAllText($path, $source)
	return $path
}
try {
	$health = Invoke-Bridge @('status')
	if ($health.bridge.activeTaskId -or $health.bridge.isPlaying -or $health.bridge.queuedTasks.Count -gt 0) { throw 'Run only in an idle test editor.' }
	$cases = @(
		@('plain', 'EditMode', 'QueueTimeoutReproTests.ResponsiveTestOutlivesTimeout', 'canceled'),
		@('recovery', 'EditMode', 'QueueTimeoutReproTests.PendingRecoveryOutlivesTimeout', 'canceled'),
		@('play', 'PlayMode', 'AgentBridgeCancellationPlayModeTests.ResponsiveLongRun', 'canceled'),
		@('orphan', 'EditMode', 'QueueTimeoutReproTests.FinishedOwnerLeavesRecovery', 'success'),
		# No client action at all: the bridge itself must notice the job that never reported back.
		@('lost', 'EditMode', 'QueueTimeoutReproTests.FrameworkStopsWithoutRunFinished', 'runtime_error')
	)
	foreach ($case in $cases) {
		$submitted = Invoke-Bridge @('tests', '--mode', $case[1], '--test', $case[2], '--session', 'cancellation-verifier', '--fresh', '--wait', '1')
		Save-Result ($case[0] + '-submit') $submitted
		if ($case[0] -ne 'orphan' -and $case[0] -ne 'lost') {
			Start-Sleep -Seconds 4
			$stop = Invoke-Bridge @('cancel', $submitted.Id, '--session', 'cancellation-verifier', '--wait', '5')
			if ($stop.Status -ne 'success') { throw 'Owner cancellation failed' }
		}
		$markerWait = if ($case[0] -eq 'lost') { '60' } else { '30' }
		$marker = Invoke-Bridge @('csharp', (New-Marker $case[0]), '--session', 'cancellation-verifier', '--wait', $markerWait)
		Save-Result ($case[0] + '-marker') $marker
		if ($marker.Status -ne 'success') { throw "Marker failed for $($case[0]): $($marker | ConvertTo-Json -Depth 10)" }
		$final = Invoke-Bridge @('wait', $submitted.Id, '--wait', '1')
		Save-Result ($case[0] + '-final') $final
		if ($final.Status -ne $case[3]) { throw "Expected $($case[3]), got $($final.Status) for $($case[0])" }
		if ($case[0] -eq 'lost' -and -not (($final.Logs -join "`n") -match 'ended without RunFinished')) {
			throw "Lost run must explain itself in the logs: $($final | ConvertTo-Json -Depth 10)"
		}
		Write-Output "PASS $($case[0]): $($final.Status), executor stopped, next task completed"
	}
	$active = Invoke-Bridge @('tests', '--mode', 'EditMode', '--test', 'QueueTimeoutReproTests.ResponsiveTestOutlivesTimeout', '--session', 'cancellation-verifier', '--fresh', '--wait', '1')
	Start-Sleep -Seconds 3
	$queued = Invoke-Bridge @('csharp', (New-Marker 'queued_cancel'), '--session', 'cancellation-verifier', '--wait', '1')
	if ($queued.Status -ne 'queued') { throw 'Expected queued follower' }
	$cancelQueued = Invoke-Bridge @('cancel', $queued.Id, '--session', 'cancellation-verifier', '--wait', '3')
	$queuedFinal = Invoke-Bridge @('wait', $queued.Id, '--wait', '1')
	if ($cancelQueued.Status -ne 'success' -or $queuedFinal.Status -ne 'canceled') { throw 'Queued cancellation failed' }
	$cancelActive = Invoke-Bridge @('cancel', $active.Id, '--session', 'cancellation-verifier', '--wait', '3')
	$activeFinal = Invoke-Bridge @('wait', $active.Id, '--wait', '10')
	Save-Result 'explicit-cancel' $activeFinal
	if ($cancelActive.Status -ne 'success' -or $activeFinal.Status -ne 'canceled') { throw 'Active cancellation failed' }
	$after = Invoke-Bridge @('csharp', (New-Marker 'after_cancel'), '--session', 'cancellation-verifier', '--wait', '10')
	if ($after.Status -ne 'success') { throw 'Queue did not recover after cancellation' }
	$again = Invoke-Bridge @('cancel', $active.Id, '--session', 'cancellation-verifier', '--wait', '3')
	if ($again.Status -ne 'success') { throw 'Repeated cancel must be harmless' }
	Write-Output 'PASS explicit cancellation: queued target, active owned test, repeated cancel, follower'
	$cooperativeId = "Task_${runId}_cooperative"
	$cooperativePath = Join-Path $Project "Temp/AgentBridge/$cooperativeId.cs"
	[IO.File]::WriteAllText($cooperativePath, @"
using System.Threading;
using System.Threading.Tasks;
public static class $cooperativeId
{
 public static async Task<string> Run(CancellationToken token)
 {
  await Task.Delay(35000, token);
  return "unexpected completion";
 }
}
"@)
	$cooperative = Invoke-Bridge @('csharp', $cooperativePath, '--session', 'cancellation-verifier', '--wait', '1')
	$stopScript = Invoke-Bridge @('cancel', $cooperative.Id, '--session', 'cancellation-verifier', '--wait', '3')
	$scriptFinal = Invoke-Bridge @('wait', $cooperative.Id, '--wait', '10')
	Save-Result 'cooperative-cancel' $scriptFinal
	if ($stopScript.Status -ne 'success' -or $scriptFinal.Status -ne 'canceled') { throw 'Cooperative script cancellation failed' }
	Write-Output 'PASS cooperative C# cancellation'
	$slowId = "Task_${runId}_slow_stop"
	$slowPath = Join-Path $Project "Temp/AgentBridge/$slowId.cs"
	[IO.File]::WriteAllText($slowPath, @"
using System.Threading.Tasks;
public static class $slowId
{
 public static async Task<string> Run()
 {
  await Task.Delay(35000);
  return "Finished after delayed cancellation";
 }
}
"@)
	$slow = Invoke-Bridge @('csharp', $slowPath, '--session', 'cancellation-verifier', '--wait', '1')
	Start-Sleep -Seconds 3
	$null = Invoke-Bridge @('cancel', $slow.Id, '--session', 'cancellation-verifier', '--wait', '3')
	$stopping = Invoke-Bridge @('wait', $slow.Id, '--wait', '1')
	if ($stopping.Status -ne 'canceling') { throw 'Uncooperative C# lost its canceling status' }
	$blocked = Invoke-Bridge @('csharp', (New-Marker 'slow_follower'), '--session', 'cancellation-verifier', '--wait', '1')
	if ($blocked.Status -ne 'queued') { throw 'Queue released before C# executor stopped' }
	$slowFinal = Invoke-Bridge @('wait', $slow.Id, '--wait', '40')
	Save-Result 'slow-cancel-final' $slowFinal
	if ($slowFinal.Status -ne 'canceled') { throw 'Late C# success overwrote cancellation' }
	$follower = Invoke-Bridge @('wait', $blocked.Id, '--wait', '10')
	if ($follower.Status -ne 'success') { throw 'Follower did not resume after C# stopped' }
	Write-Output 'PASS delayed C# cancellation retains queue and terminal outcome'
	Write-Output "Evidence: $evidence"
}
finally {
	# Control only tasks submitted by this verifier; do not change project timeout settings.
	& $Cli release --session cancellation-verifier --project $Project --wait 2 --format human
}
