param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe')
)
# Live acceptance of dialog-detection-v1 on an idle Windows host editor. The probe tests hold the
# main thread in a real EditorUtility.DisplayDialog and a real progress bar and release it by
# themselves, so nobody has to click anything:
#   1. status reports blocked_by_dialog with the dialog's title and the task that was running;
#   2. a neighbour's wait ends early with exit 2 and Reason editor_blocked_by_dialog;
#   3. after the dialog closes both tasks finish and the probe's record logs the dialog;
#   4. a progress bar on a busy main thread is never reported as a dialog.
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
$Cli = [IO.Path]::GetFullPath($Cli)
$runId = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$session = "modal-$runId"
$dialogTitle = 'AgentBridge modal probe'
$evidence = Join-Path $Project "Temp/AgentBridge/modal-dialog/$runId"
New-Item -ItemType Directory -Force $evidence | Out-Null
function Bridge([string[]]$Arguments) {
	$json = (& $Cli @Arguments --project $Project --format json | Out-String)
	$value = $json | ConvertFrom-Json
	if ($LASTEXITCODE -eq 3 -and -not ($Arguments[0] -eq 'status')) { throw $json }
	return $value
}
function Save($Name, $Value) { $Value | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $evidence "$Name.json") }
function Check($Condition, $Message) { if (-not $Condition) { throw $Message } }
# A nonterminal answer carries Resume; while the probe dialog is open every wait returns at once with
# Reason editor_blocked_by_dialog, so keep asking until the record itself comes back.
function Wait-Done($Id, $Seconds) {
	$limit = [DateTime]::UtcNow.AddSeconds($Seconds)
	do {
		$result = Bridge @('wait', $Id, '--wait', '30')
		if (-not $result.Resume) { return $result }
		Start-Sleep -Seconds 1
	} while ([DateTime]::UtcNow -lt $limit)
	throw "Task $Id did not finish within $Seconds s: $($result | ConvertTo-Json -Depth 8)"
}

$health = Bridge @('status')
Save 'status-before' $health
Check ($health.bridge.capabilities -contains 'dialog-detection-v1') 'Update the Unity package before this verification'
Check ($health.bridge.dialogDetection -like '*win32*') "This check needs the Windows window scan, the editor reports: $($health.bridge.dialogDetection)"
Check ($health.code -eq 'ready') "Use an idle, ready test editor (status: $($health.code))"
Check (-not $health.bridge.activeTaskId -and -not $health.bridge.isPlaying -and $health.bridge.queuedTasks.Count -eq 0) 'Use an idle test editor'

$probe = Bridge @('tests', '--test', 'ModalDialogProbeTests.NativeDialogIsReportedWhileOpen', '--fresh', '--wait', '0', '--session', $session)
Save 'probe-submit' $probe
$probeId = $probe.Id
Check $probeId 'The probe test was not accepted'

$blocked = $null
$limit = [DateTime]::UtcNow.AddSeconds(60)
do {
	$status = Bridge @('status')
	if ($status.code -eq 'blocked_by_dialog' -and ($status.blockedBy | Where-Object { $_.title -eq $dialogTitle })) { $blocked = $status; break }
	Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $limit)
Save 'status-blocked' $blocked
Check $blocked 'status never reported the open dialog'
$dialog = $blocked.blockedBy | Where-Object { $_.title -eq $dialogTitle } | Select-Object -First 1
Check ($dialog.source -eq 'win32' -or $dialog.source -eq 'editor_dialog_events') "Unexpected dialog source: $($dialog.source)"
Check ($dialog.taskId -eq $probeId) "The dialog must be attributed to the probe task, got '$($dialog.taskId)'"
Check (-not $blocked.bridgeReady) 'A blocked editor must not be reported ready'
Write-Output "PASS: status reports blocked_by_dialog ($($dialog.kind), open $($dialog.openMs) ms)"

$watch = [Diagnostics.Stopwatch]::StartNew()
$neighbor = Bridge @('compile', '--wait', '60', '--session', "$session-neighbor")
$neighborExit = $LASTEXITCODE
$watch.Stop()
Save 'neighbor-early' $neighbor
Check ($neighborExit -eq 2) "The neighbour's wait must end with exit 2, got $neighborExit"
Check ($neighbor.Accepted -and $neighbor.EditorState -eq 'blocked_by_dialog' -and $neighbor.Reason -eq 'editor_blocked_by_dialog') 'The early exit must name the dialog block'
Check (($neighbor.BlockedBy | Where-Object { $_.Title -eq $dialogTitle }).Count -ge 1) 'The early exit must carry BlockedBy'
Check ($watch.Elapsed.TotalSeconds -lt 15) "The wait must end early, it took $([int]$watch.Elapsed.TotalSeconds) s"
Write-Output "PASS: a waiting neighbour returns in $([int]$watch.Elapsed.TotalSeconds) s with exit 2"

$probeResult = Wait-Done $probeId 180
Save 'probe-result' $probeResult
Check ($probeResult.Status -eq 'success') "The probe test failed: $($probeResult | ConvertTo-Json -Depth 8)"
Check (($probeResult.Logs | Where-Object { $_ -like "modal dialog: `"$dialogTitle`"*" }).Count -ge 1) 'The probe record must log the dialog it held'
$neighborResult = Wait-Done $neighbor.Id 300
Save 'neighbor-result' $neighborResult
Check ($neighborResult.Status -eq 'success') "The neighbour must run once the dialog is gone: $($neighborResult.Status)"
$after = Bridge @('status')
Save 'status-after' $after
Check ($after.code -ne 'blocked_by_dialog') 'status still reports the closed dialog'
Write-Output 'PASS: both tasks finish after the dialog closes and the probe logs it'

$progress = Bridge @('tests', '--test', 'ModalDialogProbeTests.ProgressBarIsNotReportedAsDialog', '--fresh', '--wait', '0', '--session', $session)
Save 'progress-submit' $progress
$falseAlarm = $null
$limit = [DateTime]::UtcNow.AddSeconds(30)
do {
	$status = Bridge @('status')
	if ($status.code -eq 'blocked_by_dialog') { $falseAlarm = $status; break }
	$task = Bridge @('queue', 'status', $progress.Id)
	if ($task.Task.Status -notin @('queued', 'running', 'compiling')) { break }
	Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $limit)
Save 'progress-false-alarm' $falseAlarm
Check (-not $falseAlarm) "A progress bar was reported as a dialog: $($falseAlarm.blockedBy | ConvertTo-Json -Depth 6)"
$progressResult = Wait-Done $progress.Id 180
Save 'progress-result' $progressResult
Check ($progressResult.Status -eq 'success') "The progress probe failed: $($progressResult | ConvertTo-Json -Depth 8)"
Write-Output 'PASS: a progress bar on a busy main thread is not a dialog'
Write-Output "Evidence: $evidence"
