param(
    [Parameter(Mandatory=$true)][string]$Project,
    [Parameter(Mandatory=$true)][string]$Cli,
    [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path -LiteralPath $Project).Path
$Cli = (Resolve-Path -LiteralPath $Cli).Path
[IO.Directory]::CreateDirectory($Output) | Out-Null
$probe = Join-Path $Project 'Temp/AgentBridge/IndependentQueueProbe'
[IO.Directory]::CreateDirectory($probe) | Out-Null
Remove-Item -LiteralPath (Join-Path $probe 'started'),(Join-Path $probe 'finished') -ErrorAction SilentlyContinue
function Invoke-Bridge([string[]]$Arguments, [switch]$Waiting) {
    $text = & $Cli @Arguments --project $Project 2>> (Join-Path $Output 'stderr.txt')
    if ($LASTEXITCODE -ne 0 -and -not ($Waiting -and $LASTEXITCODE -eq 2)) { throw "CLI failed: $Arguments : $text" }
    return ($text -join "`n" | ConvertFrom-Json)
}
$session = 'IndependentQueue_' + [guid]::NewGuid().ToString('N')
$run = Invoke-Bridge @('tests','--test','IndependentQueueProbeTests.BlockEditorMainThread','--fresh','--wait','0','--session',$session)
$deadline = (Get-Date).AddSeconds(90)
while (-not (Test-Path -LiteralPath (Join-Path $probe 'started'))) {
    if ((Get-Date) -gt $deadline) { throw 'Blocking probe did not start' }
    Start-Sleep -Milliseconds 100
}
$timer = [Diagnostics.Stopwatch]::StartNew()
$a = Invoke-Bridge @('compile','--wait','0','--session',$session)
$b = Invoke-Bridge @('compile','--wait','0','--session',$session)
$moved = Invoke-Bridge @('queue','move',$b.Id,$a.Id)
$cancel = Invoke-Bridge @('cancel',$a.Id,'--session',$session)
$list = Invoke-Bridge @('queue','list')
if ($a.Accepted -ne $true -or $b.Accepted -ne $true -or $moved.Code -ne 'moved' -or $cancel.Status -ne 'canceled') { throw 'Queue operations failed while Unity was blocked' }
if (($list.Tasks | Where-Object Id -eq $a.Id).Status -ne 'canceled') { throw 'Canceled task is still queued' }
$milliseconds = $timer.ElapsedMilliseconds
if (Test-Path -LiteralPath (Join-Path $probe 'finished')) { throw 'Operations did not finish inside the blocked interval' }
if ($milliseconds -gt 10000) { throw "Queue operations took too long: ${milliseconds}ms" }
$result = Invoke-Bridge @('wait',$run.Id,'--wait','90')
if ($result.Status -ne 'success' -or $result.Evidence.Validity -ne 'valid' -or $result.Tests.total -ne 1) { throw "Invalid blocking-test evidence: $($result | ConvertTo-Json -Depth 20)" }
$next = Invoke-Bridge @('wait',$b.Id,'--wait','90')
if ($next.Status -ne 'success') { throw 'Queue did not resume after editor unblocked' }
$canceled = Invoke-Bridge @('queue','status',$a.Id)
if ($canceled.Task.Status -ne 'canceled' -or $canceled.Task.StartedAtUtc) { throw 'Canceled task executed after editor unblocked' }
$report = @{ Passed=$true; OperationsMs=$milliseconds; Probe=$result; Next=$next; Canceled=$canceled }
$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $Output 'result.json') -Encoding utf8
Write-Output "Independent queue live acceptance: PASS (${milliseconds}ms while main thread blocked; execution resumed; canceled task never started)"
