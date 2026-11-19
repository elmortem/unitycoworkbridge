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
#   4. a progress bar on a busy main thread is never reported as a dialog;
#   5. `dialog answer` refuses an unknown button, then clicks a named one (dialog-answer-v1).
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
# @(): Windows PowerShell 5.1 gives a single PSCustomObject no Count.
Check (@($neighbor.BlockedBy | Where-Object { $_.Title -eq $dialogTitle }).Count -ge 1) 'The early exit must carry BlockedBy'
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

# dialog-answer-v1: the agent answers a DisplayDialogComplex with Alpha/Beta/Gamma by itself.
$answerTitle = 'AgentBridge answer probe'
Check ($health.bridge.capabilities -contains 'dialog-answer-v1') 'Update the Unity package: dialog-answer-v1 is missing'
$answerProbe = Bridge @('tests', '--test', 'ModalDialogProbeTests.AnswerPressesNamedButton', '--fresh', '--wait', '0', '--session', $session)
Save 'answer-submit' $answerProbe
Check $answerProbe.Id 'The answer probe was not accepted'
$answerBlocked = $null
$limit = [DateTime]::UtcNow.AddSeconds(45)
do {
	$status = Bridge @('status')
	if ($status.code -eq 'blocked_by_dialog' -and ($status.blockedBy | Where-Object { $_.title -eq $answerTitle })) { $answerBlocked = $status; break }
	Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $limit)
Save 'answer-blocked' $answerBlocked
Check $answerBlocked 'status never reported the answer probe dialog'
$answerDialog = $answerBlocked.blockedBy | Where-Object { $_.title -eq $answerTitle } | Select-Object -First 1
# Unity lays the buttons out as ok, alt, cancel, so only the set is fixed.
Check (((@($answerDialog.buttons) | Sort-Object) -join '|') -eq 'Alpha|Beta|Gamma') "The scan must read Alpha, Beta, Gamma, got: $(@($answerDialog.buttons) -join ', ')"
$answerNeighbor = Bridge @('compile', '--wait', '0', '--session', "$session-answer-neighbor")
Save 'answer-neighbor-submit' $answerNeighbor

$refused = Bridge @('dialog', 'answer', $answerDialog.id, '--button', 'Delta', '--reason', 'verification', '--session', $session)
$refusedExit = $LASTEXITCODE
Save 'answer-refused' $refused
Check ($refusedExit -eq 1 -and $refused.code -eq 'button_not_found') "An unknown button must be refused with exit 1, got $refusedExit $($refused.code)"
$still = Bridge @('status')
Check ($still.code -eq 'blocked_by_dialog') "A refused answer must leave the dialog open, status: $($still.code)"
Write-Output 'PASS: an unknown button is refused with exit 1 and the dialog stays open'

$answered = Bridge @('dialog', 'answer', $answerDialog.id, '--button', 'gamma', '--reason', 'verification', '--session', $session)
$answeredExit = $LASTEXITCODE
Save 'answer-answered' $answered
Check ($answeredExit -eq 0 -and $answered.code -eq 'answered' -and $answered.button -eq 'Gamma') "dialog answer must click Gamma, got $answeredExit $($answered | ConvertTo-Json -Depth 6)"
$answerResult = Wait-Done $answerProbe.Id 180
Save 'answer-result' $answerResult
Check ($answerResult.Status -eq 'success') "AnswerPressesNamedButton failed (DisplayDialogComplex must return 2): $($answerResult | ConvertTo-Json -Depth 8)"
Check (@($answerResult.Logs | Where-Object { $_ -like "modal dialog: `"$answerTitle`"*answered `"Gamma`" by ${session}: verification" }).Count -ge 1) 'The probe record must log who answered the dialog'
$answerAfter = Bridge @('status')
Save 'answer-status-after' $answerAfter
Check ($answerAfter.code -ne 'blocked_by_dialog') 'status still reports the answered dialog'
$answerNeighborResult = Wait-Done $answerNeighbor.Id 300
Save 'answer-neighbor-result' $answerNeighborResult
Check ($answerNeighborResult.Status -eq 'success') "The queued neighbour must run after the answer: $($answerNeighborResult.Status)"
Write-Output 'PASS: dialog answer clicks Gamma, DisplayDialogComplex returns 2, the queue moves on and the log names the answer'
Write-Output "Evidence: $evidence"
