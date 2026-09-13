param([string]$Project = (Join-Path $PSScriptRoot '../AgentBridgeUnity'),
      [string]$Cli = (Join-Path $PSScriptRoot '../AgentBridgeCli/bin/Release/net8.0/agentbridge.exe'))
$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path -LiteralPath $Project).Path
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$report = [Collections.Generic.List[object]]::new()
function Start-Bridge([string[]]$Command) {
    $info = [Diagnostics.ProcessStartInfo]::new($Cli)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in ($Command + @('--project', $Project, '--wait', '60'))) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    return @{ Process = $process; Output = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync(); Started = [DateTime]::UtcNow }
}
function Finish-Bridge($run) {
    if (!$run.Process.WaitForExit(65000)) { throw 'CLI did not finish; inspect and wait for its existing task, do not resubmit.' }
    $value = $run.Output.GetAwaiter().GetResult() | ConvertFrom-Json
    $report.Add(@{ Result = $value; ElapsedSeconds = ([DateTime]::UtcNow - $run.Started).TotalSeconds })
    return $value
}
function Check($condition, [string]$message) { if (!$condition) { throw $message } }
$status = Finish-Bridge (Start-Bridge @('status'))
Check ($status.bridgeReady -and !$status.bridge.isPlaying -and !$status.bridge.activeTaskId -and !$status.bridge.coordinationActive -and @($status.bridge.queuedTasks).Count -eq 0) 'Use an idle, uncoordinated test host.'
$probe = Join-Path $Project ('Assets/CompileRegression_' + [guid]::NewGuid().ToString('N') + '.cs')
Check ($probe.StartsWith((Join-Path $Project 'Assets') + [IO.Path]::DirectorySeparatorChar)) 'Probe must stay inside test Assets.'
try {
    $warm = Finish-Bridge (Start-Bridge @('compile'))
    Check ($warm.Status -eq 'success') 'Initial compilation failed.'
    $runs = @(1..3 | ForEach-Object { Start-Bridge @('compile', '--fresh', '--note', 'Regression: simultaneous diagnostic requests share one completed cycle') })
    $results = @($runs | ForEach-Object { Finish-Bridge $_ })
    Check (@($results | Where-Object Status -ne 'success').Count -eq 0) 'Concurrent compile failed.'
    $sources = @($results | ForEach-Object { if ($_.Cached) { $_.SourceTaskId } else { $_.Id } } | Select-Object -Unique)
    Check ($sources.Count -eq 1) 'Concurrent fresh requests triggered multiple cycles.'
    Write-Output 'PASS concurrent fresh requests share one cycle'

    [IO.File]::WriteAllText($probe, '#error AB_COMPILE_REGRESSION')
    $broken = Finish-Bridge (Start-Bridge @('compile'))
    Check ($broken.Status -eq 'compiler_error') 'The deliberate source error was not reported.'
    Check (@($broken.Diagnostics | Where-Object Message -match 'AB_COMPILE_REGRESSION').Count -gt 0) 'Probe diagnostic missing.'
    Check ($report[$report.Count - 1].ElapsedSeconds -lt 20) 'Error waited for the old 20-second watchdog.'
    $again = Finish-Bridge (Start-Bridge @('compile'))
    Check ($again.Status -eq 'compiler_error' -and $again.Cached) 'Unchanged compiler errors ran again instead of using cache.'
    Write-Output 'PASS compiler error completes promptly and is reused'

    [IO.File]::WriteAllText($probe, '// repaired regression probe')
    $fixed = Finish-Bridge (Start-Bridge @('compile'))
    Check ($fixed.Status -eq 'success') 'Repair did not invalidate the cached error.'
    $repeat = Finish-Bridge (Start-Bridge @('compile'))
    Check ($repeat.Status -eq 'success' -and $repeat.Cached) 'Repaired sources did not reuse success.'
    Write-Output 'PASS repair invalidates errors and repeated success is cached'
}
finally {
    foreach ($file in @($probe, ($probe + '.meta'))) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    $cleanup = Finish-Bridge (Start-Bridge @('compile'))
    $reportDir = Join-Path $Project 'Temp/AgentBridge'
    [IO.Directory]::CreateDirectory($reportDir) | Out-Null
    $reportPath = Join-Path $reportDir ('compile-regression-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.json')
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Output "Report: $reportPath"
    Check ($cleanup.Status -eq 'success') 'Host did not compile after removing the owned probe.'
}
Write-Output 'Compilation live regression: PASS'
