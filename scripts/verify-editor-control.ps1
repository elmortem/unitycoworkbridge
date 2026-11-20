param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe')
)
# Live acceptance of `agentbridge editor open|close` on an idle Windows host editor with clean scenes:
#   1. a graceful close exits the editor process and a repeated close is not_running;
#   2. a compile accepted while the editor is closed finishes success after `editor open`;
#   3. a second open reports already_running;
#   4. --force kills the editor, and after another open the project compiles.
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
$Cli = [IO.Path]::GetFullPath($Cli)
$runId = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$session = "editor-control-$runId"
$evidence = Join-Path ([IO.Path]::GetTempPath()) "AgentBridgeEditorControl/$runId"
New-Item -ItemType Directory -Force $evidence | Out-Null
function Bridge([string[]]$Arguments) {
	$json = (& $Cli @Arguments --project $Project --format json | Out-String)
	$script:exit = $LASTEXITCODE
	return $json | ConvertFrom-Json
}
function Save($Name, $Value) { $Value | ConvertTo-Json -Depth 40 | Set-Content (Join-Path $evidence "$Name.json") }
function Check($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Expect($Result, $Exit, $Code, $Step) {
	Save $Step $Result
	Check ($script:exit -eq $Exit -and $Result.code -eq $Code) "${Step}: expected $Code/$Exit, got $($Result.code)/$($script:exit): $($Result | ConvertTo-Json -Depth 6)"
	Write-Output "PASS: $Step -> $Code/$Exit"
}
function Wait-Ready($Step) {
	$limit = [DateTime]::UtcNow.AddSeconds(300)
	do {
		$status = Bridge @('status')
		if ($status.code -eq 'ready') { Save $Step $status; Write-Output "PASS: $Step -> ready"; return $status }
		Start-Sleep -Seconds 2
	} while ([DateTime]::UtcNow -lt $limit)
	throw "${Step}: the editor did not become ready within 300 s (status: $($status.code))"
}
function Wait-Gone([int]$EditorPid) {
	Check (-not (Get-Process -Id $EditorPid -ErrorAction SilentlyContinue)) "The editor process $EditorPid is still alive"
}

$health = Bridge @('status')
Save 'status-before' $health
Check ($health.code -eq 'ready') "Use an idle, ready test editor (status: $($health.code))"
Check ($health.bridge.editorPath) 'status.json has no editorPath: update the Unity package before this verification'
Check (-not $health.bridge.activeTaskId -and -not $health.bridge.isPlaying -and $health.bridge.queuedTasks.Count -eq 0) 'Use an idle test editor'
$editorPid = $health.bridge.editorPid

$closed = Bridge @('editor', 'close', '--reason', 'verification', '--session', $session)
Expect $closed 0 'closed' 'close'
Wait-Gone $editorPid
Expect (Bridge @('editor', 'close', '--reason', 'verification', '--session', $session)) 0 'not_running' 'close-again'

$compile = Bridge @('compile', '--wait', '0', '--session', $session, '--note', 'editor control verification')
Save 'compile-offline' $compile
Check ($script:exit -eq 0 -and $compile.Accepted -and $compile.Id) "compile must be accepted while the editor is closed: $($compile | ConvertTo-Json -Depth 6)"
Write-Output "PASS: compile accepted offline ($($compile.Id))"

$opened = Bridge @('editor', 'open', '--session', $session)
Expect $opened 0 'launched' 'open'
Wait-Ready 'ready-after-open' | Out-Null
Expect (Bridge @('editor', 'open', '--session', $session)) 0 'already_running' 'open-again'

$result = Bridge @('wait', $compile.Id, '--wait', '600')
Save 'compile-offline-result' $result
Check ($result.Status -eq 'success') "The task accepted while closed must finish success: $($result | ConvertTo-Json -Depth 8)"
Write-Output 'PASS: the offline compile finished success after open'

$health = Bridge @('status')
$editorPid = $health.bridge.editorPid
Expect (Bridge @('editor', 'close', '--force', '--reason', 'verification', '--session', $session)) 0 'killed' 'force-close'
Wait-Gone $editorPid

Expect (Bridge @('editor', 'open', '--session', $session)) 0 'launched' 'open-after-kill'
Wait-Ready 'ready-after-kill' | Out-Null
$final = Bridge @('compile', '--wait', '600', '--session', $session, '--note', 'editor control verification')
Save 'compile-after-kill' $final
Check ($final.Status -eq 'success') "The project must compile after the forced close: $($final | ConvertTo-Json -Depth 8)"
Write-Output 'PASS: the project compiles after the forced close'
& $Cli release --session $session --project $Project --wait 30 | Out-Null
Write-Output "Evidence: $evidence"
