param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe'),
	[ValidateSet('Both', 'CSharp', 'PlayMode')][string]$Stage = 'Both'
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
$Cli = [IO.Path]::GetFullPath($Cli)
$runId = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$owner = "long-owner-$runId"
$neighbor = "long-neighbor-$runId"
$evidence = Join-Path $Project "Temp/AgentBridge/long-running/$runId"
New-Item -ItemType Directory -Force $evidence | Out-Null
function Bridge([string[]]$Arguments) {
	$json = (& $Cli @Arguments --project $Project --format json | Out-String)
	$value = $json | ConvertFrom-Json
	if ($LASTEXITCODE -eq 3 -and $value.Code -ne 'task_not_found' -and
		-not ($Arguments[0] -eq 'status' -and $value.Code -eq 'heartbeat_stale')) { throw $json }
	return $value
}
function Save($Name, $Value) { $Value | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $evidence "$Name.json") }
function Check($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Write-Script($Suffix, $Body) {
	$id = "Task_${runId}_$Suffix"
	$path = Join-Path $evidence "$id.cs"
	[IO.File]::WriteAllText($path, "using System; using System.Threading; using System.Threading.Tasks; using AgentBridge; public static class $id { public static async Task<string> Run(CancellationToken token) { $Body } }")
	return $path
}
function Wait-Running($Id) {
	$limit = [DateTime]::UtcNow.AddSeconds(60)
	do {
		$result = Bridge @('wait', $Id, '--wait', '1')
		if ($result.Code -eq 'task_not_found') { Start-Sleep -Milliseconds 500; continue }
		if ($result.Status -eq 'running') { return $result }
		Check ($result.Status -in @('queued', 'compiling')) "Task failed before running: $($result | ConvertTo-Json -Depth 8)"
	} while ([DateTime]::UtcNow -lt $limit)
	throw 'Task did not start within 60 seconds'
}
function Assert-Protected($Id) {
	$early = Bridge @('cancel', $Id, '--session', $neighbor, '--wait', '5')
	Save 'early-foreign-cancel' $early
	Check ($early.Status -eq 'rejected' -and ($early.Logs -join ' ') -match 'task_protected') 'Foreign cancellation was not rejected during protection'
}
$health = Bridge @('status')
Check ($health.bridge.capabilities -contains 'long-running-tasks-v1') 'Update the Unity package before this verification'
Check (-not $health.bridge.activeTaskId -and -not $health.bridge.isPlaying -and $health.bridge.queuedTasks.Count -eq 0) 'Use an idle test editor'
$active = $null
$registered = $false
try {
	if ($Stage -in @('Both', 'CSharp')) {
		$path = Write-Script 'natural' 'await Task.Delay(305000, token); return "Completed beyond 300 seconds without cancellation";'
		$submitted = Bridge @('csharp', $path, '--session', $owner, '--wait', '1')
		$active = $submitted.Id
		Save 'csharp-submit' $submitted
		$null = Wait-Running $active
		Assert-Protected $active
		$limit = [DateTime]::UtcNow.AddSeconds(360)
		do {
			$result = Bridge @('wait', $active, '--wait', '30')
			Write-Output "CSharp: $($result.Status)"
			if ($result.Status -ne 'running') { break }
		} while ([DateTime]::UtcNow -lt $limit)
		Save 'csharp-final' $result
		Check ($result.Status -eq 'success' -and $result.Timing.TotalMs -ge 300000) 'C# did not finish naturally beyond 300 seconds'
		$active = $null
		Write-Output 'PASS: C# survives 300 seconds and completes naturally'
	}
	if ($Stage -in @('Both', 'PlayMode')) {
		$scope = Join-Path $evidence 'scope.json'
		'{"Paths":["AgentBridgeUnity/Assets/Tests/"]}' | Set-Content $scope
		$repo = [IO.Path]::GetFullPath((Join-Path $Project '..'))
		$registration = Bridge @('coord', 'register', '--session', $owner, '--spec', 'long-running-verification', '--repo', $repo, '--scope', $scope, '--owner', $owner)
		Check $registration.Ok "Registration failed: $($registration.Message)"
		$registered = $true
		$plan = Join-Path $evidence 'plan.json'
		@{ Steps = @(
			@{ Id = 'long'; Kind = 'tests'; Mode = 'PlayMode'; Tests = @('AgentBridgeCancellationPlayModeTests.SurvivesProtectedPeriod'); Fresh = $true },
			@{ Id = 'forbidden'; Kind = 'tests'; Mode = 'PlayMode'; Tests = @('AgentBridgeCancellationPlayModeTests.ShortSuccessfulRun'); Fresh = $true }
		)} | ConvertTo-Json -Depth 10 | Set-Content $plan
		$requestId = "long-$runId"
		$batch = Bridge @('coord', 'submit', '--session', $owner, '--request', $requestId, '--kind', 'validation', '--plan', $plan, '--seconds', '600')
		Save 'batch-submit' $batch
		Check $batch.Ok "Batch submission failed: $($batch.Message)"
		$active = $batch.TaskIds[0]
		$null = Wait-Running $active
		Assert-Protected $active
		$limit = [DateTime]::UtcNow.AddSeconds(600)
		do {
			$health = Bridge @('status')
			if ($health.Code -eq 'heartbeat_stale') {
				$null = Bridge @('wait', $active, '--wait', '30')
				continue
			}
			Check ($health.bridge.activeTaskId -eq $active) 'Long test ended before preemption'
			Write-Output "PlayMode elapsed: $($health.bridge.activeTaskElapsedSeconds)s"
			if ($health.bridge.activeTaskCancelableByOtherAgents) { break }
			Start-Sleep -Seconds 10
		} while ([DateTime]::UtcNow -lt $limit)
		Save 'preemptible-status' $health
		Check $health.bridge.activeTaskCancelableByOtherAgents 'Task never became preemptible'
		$window = Bridge @('coord', 'status', '--session', $owner, '--request', $requestId)
		Save 'window-before-cancel' $window
		Check ($window.State -eq 'granted') 'Expected live 600-second window'
		$cancel = Bridge @('cancel', $active, '--session', $neighbor, '--wait', '10')
		Save 'late-foreign-cancel' $cancel
		Check ($cancel.Status -eq 'success') 'Late foreign cancellation was refused'
		$result = Bridge @('wait', $active, '--wait', '60')
		Save 'play-final' $result
		Check ($result.Status -eq 'canceled') 'Target did not finish canceled'
		Check (($result.Logs -join ' ') -match 'preempted_after_300s' -and ($result.Logs -join ' ') -match [regex]::Escape($neighbor)) 'Owner notification lacks reason or initiator'
		$active = $null
		$marker = Write-Script 'after' 'if (TestRunnerCancellation.IsRunning() || PlayModeSceneRecovery.IsPending || !string.IsNullOrEmpty(TestRunLifecycle.TaskId)) throw new Exception("Executor or recovery still active"); await Task.Yield(); return "Executor stopped; scenes recovered; next agent can run";'
		$next = Bridge @('csharp', $marker, '--session', $neighbor, '--wait', '30')
		Save 'next-agent' $next
		Check ($next.Status -eq 'success') 'Next agent could not use the editor'
		$closed = Bridge @('coord', 'status', '--session', $owner, '--request', $requestId)
		Save 'batch-final' $closed
		Check ($closed.State -eq 'closed' -and $closed.Reason -match 'canceled') 'Canceled package did not close with its reason'
		$unstarted = Bridge @('wait', $batch.TaskIds[1], '--wait', '1')
		Save 'unstarted-second-step' $unstarted
		Check ($unstarted.Code -eq 'task_not_found') 'Canceled package dispatched its remaining step'
		Write-Output 'PASS: PlayMode preemption after 300s inside live window, notification, recovery, batch stop, next agent'
	}
	Write-Output "Evidence: $evidence"
} finally {
	if ($active) {
		$null = Bridge @('cancel', $active, '--session', $owner, '--wait', '5')
		$cleanup = Bridge @('wait', $active, '--wait', '30')
		if ($cleanup.Status -in @('queued', 'compiling', 'running', 'canceling')) {
			Write-Warning "Cleanup is still pending for $active (owner $owner). Keep waiting through Bridge; do not delete queue files."
		}
	}
	if ($registered) {
		$leave = Bridge @('coord', 'leave', '--session', $owner)
		if (-not $leave.Ok) { Write-Warning "Registration $owner still needs cleanup: $($leave.Message)" }
	}
}
