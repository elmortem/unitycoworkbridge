param(
	[string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
	[string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe')
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project)
function Invoke-Bridge([string[]]$Arguments) {
	$result = (& $Cli @Arguments --project $Project --format json | Out-String) | ConvertFrom-Json
	if ($LASTEXITCODE -eq 3) { throw ($result | ConvertTo-Json) }
	return $result
}
function Check($Condition, $Message) { if (!$Condition) { throw $Message } }
$status = Invoke-Bridge @('status')
Check (!$status.bridge.activeTaskId -and !$status.bridge.isPlaying -and !$status.bridge.queuedTasks.Count) 'Use an idle test project'
$warm = Invoke-Bridge @('tests','--mode','EditMode','--test','TestCancellationPolicyTests','--fresh','--wait','45')
Check ($warm.Status -eq 'success') 'Warm test failed'
$warmCompile = Invoke-Bridge @('compile','--wait','45')
Check ($warmCompile.Status -eq 'success') 'Warm compile failed'
$id = 'Task_' + (Get-Date -Format yyyyMMdd_HHmmss_fff) + '_immediate'
$path = Join-Path $Project "Temp/AgentBridge/$id.cs"
@"
using System.Threading;
using System.Threading.Tasks;
public static class $id {
 public static async Task<string> Run(CancellationToken token) {
  await Task.Delay(45000, token); return "finished";
 }
}
"@ | Set-Content $path
$active = Invoke-Bridge @('csharp',$path,'--wait','1')
try {
	$hit = Invoke-Bridge @('tests','--mode','EditMode','--test','TestCancellationPolicyTests','--wait','5')
	Check ($hit.Status -eq 'success' -and $hit.Cached) 'Test cache must bypass active C#'
	$compile = Invoke-Bridge @('compile','--wait','5')
	Check ($compile.Status -eq 'success' -and $compile.Cached) 'Compile cache must bypass active C#'
	$miss = Invoke-Bridge @('compile','--fresh','--wait','1')
	Check ($miss.Status -eq 'queued') 'Fresh compile must stay queued'
	$stop = Invoke-Bridge @('stopplay','--wait','5')
	Check ($stop.Status -eq 'success') 'stopplay must bypass active C# and queued compile'
	$status = Invoke-Bridge @('status')
	Check ($status.bridge.activeTaskId -eq $id) 'Control/cache request lost the active executor'
	Write-Output 'PASS: cache hits bypass active work; fresh miss waits; stopplay bypasses both'
} finally {
	$null = Invoke-Bridge @('cancel',$id,'--wait','5')
	$null = Invoke-Bridge @('wait',$id,'--wait','15')
}
if ($miss.Id) { $null = Invoke-Bridge @('wait',$miss.Id,'--wait','30') }
$run = Invoke-Bridge @('tests','--mode','PlayMode','--test','AgentBridgeCancellationPlayModeTests.ResponsiveLongRun','--fresh','--wait','1')
$deadline = [DateTime]::UtcNow.AddSeconds(25)
do {
	$status = Invoke-Bridge @('status')
	if ($status.bridge.isPlaying) { break }
	Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $deadline)
Check $status.bridge.isPlaying 'PlayMode test did not start'
$stop = Invoke-Bridge @('stopplay','--session','foreign-control','--wait','10')
Check ($stop.Status -eq 'success') 'Foreign stopplay did not finish promptly'
$status = Invoke-Bridge @('status')
Check (!$status.bridge.isPlaying) 'stopplay reported success while still playing'
$finished = Invoke-Bridge @('wait',$run.Id,'--wait','30')
Check ($finished.Status -eq 'canceled') 'Test owner was not canceled'
$next = Invoke-Bridge @('compile','--fresh','--wait','30')
Check ($next.Status -eq 'success') 'Queue did not recover after stopplay'
Write-Output 'PASS: stopplay exits active PlayMode test, cancels its owner, and queue resumes'
