param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe')
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
$Cli = [IO.Path]::GetFullPath($Cli)
$scratch = Join-Path $Project 'Temp/AgentBridge/repro-queue'
New-Item -ItemType Directory -Force $scratch | Out-Null
$settings = Join-Path $Project 'ProjectSettings/AgentBridge.json'
$original = [IO.File]::ReadAllBytes($settings)
$runId = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$evidence = Join-Path $scratch $runId
New-Item -ItemType Directory -Force $evidence | Out-Null
function Invoke-Bridge([string[]]$Arguments) {
	$json = (& $Cli @Arguments --project $Project --format json | Out-String)
	if ($LASTEXITCODE -eq 3) { throw $json }
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
	$config = [IO.File]::ReadAllText($settings) | ConvertFrom-Json
	$config.TaskTimeoutSeconds = 10
	[IO.File]::WriteAllText($settings, ($config | ConvertTo-Json))
	# SettingsStore refreshes its cache every two seconds.
	Start-Sleep -Seconds 3
	$cases = @(
		@('plain', 'EditMode', 'QueueTimeoutReproTests.ResponsiveTestOutlivesTimeout', 'timeout'),
		@('recovery', 'EditMode', 'QueueTimeoutReproTests.PendingRecoveryOutlivesTimeout', 'timeout'),
		@('play', 'PlayMode', 'AgentBridgeCancellationPlayModeTests.ResponsiveLongRun', 'timeout'),
		@('orphan', 'EditMode', 'QueueTimeoutReproTests.FinishedOwnerLeavesRecovery', 'success')
	)
	foreach ($case in $cases) {
		$submitted = Invoke-Bridge @('tests', '--mode', $case[1], '--test', $case[2], '--fresh', '--wait', '1')
		Save-Result ($case[0] + '-submit') $submitted
		$marker = Invoke-Bridge @('csharp', (New-Marker $case[0]), '--session', 'cancellation-verifier', '--wait', '30')
		Save-Result ($case[0] + '-marker') $marker
		if ($marker.Status -ne 'success') { throw "Marker failed for $($case[0]): $($marker | ConvertTo-Json -Depth 10)" }
		$final = Invoke-Bridge @('wait', $submitted.Id, '--wait', '1')
		Save-Result ($case[0] + '-final') $final
		if ($final.Status -ne $case[3]) { throw "Expected $($case[3]), got $($final.Status) for $($case[0])" }
		Write-Output "PASS $($case[0]): $($final.Status), executor stopped, next task completed"
	}
	$active = Invoke-Bridge @('tests', '--mode', 'EditMode', '--test', 'QueueTimeoutReproTests.ResponsiveTestOutlivesTimeout', '--fresh', '--wait', '1')
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
	Write-Output 'PASS explicit cancellation: queued target, active foreign test, repeated cancel, follower'
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
	$cooperative = Invoke-Bridge @('csharp', $cooperativePath, '--wait', '1')
	$stopScript = Invoke-Bridge @('cancel', $cooperative.Id, '--session', 'cancellation-verifier', '--wait', '3')
	$scriptFinal = Invoke-Bridge @('wait', $cooperative.Id, '--wait', '10')
	Save-Result 'cooperative-cancel' $scriptFinal
	if ($stopScript.Status -ne 'success' -or $scriptFinal.Status -ne 'canceled') { throw 'Cooperative script cancellation failed' }
	Write-Output 'PASS cooperative C# cancellation'
	Write-Output "Evidence: $evidence"
}
finally {
	[IO.File]::WriteAllBytes($settings, $original)
	& $Cli release --session cancellation-verifier --project $Project --wait 2 --format human
}
