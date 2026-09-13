param(
	[Parameter(Mandatory=$true)][string]$Project,
	[Parameter(Mandatory=$true)][string]$Cli,
	[Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$Project = (Resolve-Path -LiteralPath $Project).Path
$Cli = (Resolve-Path -LiteralPath $Cli).Path
[IO.Directory]::CreateDirectory($Output) | Out-Null
$scratch = Join-Path $Project ('Temp/AgentBridge/BatchAcceptance_' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Json($path, $value) { [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 30), $utf8) }
function Invoke-Bridge([string[]]$Arguments, [switch]$AllowFailure) {
	$previous = $ErrorActionPreference
	try {
		$ErrorActionPreference = 'Continue'
		$text = & $Cli @Arguments --project $Project 2>> (Join-Path $Output 'stderr.txt')
		$code = $LASTEXITCODE
	} finally { $ErrorActionPreference = $previous }
	if ($code -ne 0 -and -not $AllowFailure) { throw "CLI failed: $Arguments : $text" }
	return ($text -join "`n" | ConvertFrom-Json)
}
$sessions = @()
$submitted = @()
$fence = $null
$repairFile = $null
$createdProbeRoot = -not (Test-Path -LiteralPath (Join-Path $Project 'Assets/BatchAcceptance'))
$stamp = [guid]::NewGuid().ToString('N').Substring(0,12)
try {
	foreach ($name in @('Fence','A','B','C')) {
		$session = "Batch_${stamp}_$name"
		$scope = Join-Path $scratch "$name.scope.json"
		Write-Json $scope @{ Paths = @("Assets/BatchAcceptance/$stamp/$name/") }
		Invoke-Bridge @('coord','register','--session',$session,'--spec','batch-acceptance','--repo',$Project,'--scope',$scope,'--owner','batch-acceptance') | Out-Null
		$sessions += $session
	}
	$fence = Invoke-Bridge @('coord','edit-begin','--session',$sessions[0],'--request',"fence-$stamp",'--seconds','120')
	if ($fence.Code -ne 'granted') { throw 'Acceptance needs a free input-writing interval' }
	for ($index = 1; $index -le 3; $index++) {
		$steps = @()
		for ($step = 1; $step -le 2; $step++) {
			$class = "Task_Batch_${stamp}_${index}_$step"
			$source = Join-Path $scratch "$class.cs"
			$body = if ($index -eq 2 -and $step -eq 1) { 'throw new System.Exception("expected-batch-failure");' } else { "return `"frozen-$index-$step`";" }
			[IO.File]::WriteAllText($source, "using System.Threading.Tasks; public static class $class { public static async Task<string> Run() { await Task.Delay(100); $body } }", $utf8)
			$steps += @{ Id = "S$step"; Kind = 'csharp'; PayloadFile = "$class.cs" }
		}
		$plan = Join-Path $scratch "$index.plan.json"
		Write-Json $plan @{ Steps = $steps }
		$result = Invoke-Bridge @('coord','submit','--session',$sessions[$index],'--request',"request-$stamp-$index",'--kind','editor','--plan',$plan,'--seconds','60')
		if ($result.State -ne 'waiting' -or $result.TaskIds.Count -ne 2) { throw 'Batch must enter FIFO with both task ids' }
		$submitted += $result
		# Mutating the original after acceptance must have no effect on queued work.
		foreach ($entry in $steps) { [IO.File]::WriteAllText((Join-Path $scratch $entry.PayloadFile), 'this is no longer valid C#', $utf8) }
	}
	Invoke-Bridge @('coord','edit-end','--session',$sessions[0],'--token',$fence.Token) | Out-Null
	$fence = $null
	# No agent dispatch, finish, renewal or polling while the editor drains all three packages.
	Start-Sleep -Seconds 15
	$results = @()
	for ($index = 0; $index -lt 3; $index++) {
		$status = Invoke-Bridge @('coord','status','--session',$sessions[$index+1],'--request',"request-$stamp-$($index+1)")
		if ($status.State -ne 'closed') { throw "Batch did not finish unattended: $($status | ConvertTo-Json -Compress)" }
		if ($index -eq 1) {
			if ($status.Reason -notlike 'failed:S1:*') { throw 'Expected fail-fast outcome' }
		} elseif ($status.Reason -ne 'completed') { throw "Unexpected outcome: $($status.Reason)" }
		$results += $status
		$count = if ($index -eq 1) { 1 } else { 2 }
		for ($step = 0; $step -lt $count; $step++) {
			$taskId = $submitted[$index].TaskIds[$step]
			$record = Get-Content -Raw (Join-Path $Project "Library/AgentBridge/Journal/$taskId.json") | ConvertFrom-Json
			if ($index -ne 1 -and ($record.Status -ne 'success' -or $record.ReturnValue -ne "frozen-$($index+1)-$($step+1)")) { throw 'Frozen payload did not execute successfully' }
			Write-Json (Join-Path $Output "$taskId.json") $record
		}
	}
	$skipped = $submitted[1].TaskIds[1]
	if (Test-Path (Join-Path $Project "Library/AgentBridge/Inbox/$skipped.task.json")) { throw 'Failed package submitted its remaining step' }
	$validationPlan = Join-Path $scratch 'validation.plan.json'
	Write-Json $validationPlan @{ Steps = @(
		@{ Id = 'Compile'; Kind = 'compile' },
		@{ Id = 'EditMode'; Kind = 'tests'; Mode = 'EditMode'; Tests = @('AgentBridgeTestCacheTests') },
		@{ Id = 'PlayMode'; Kind = 'tests'; Mode = 'PlayMode'; Tests = @('AgentBridgePlayModeProbeTests.PassingTest') },
		@{ Id = 'AfterReload'; Kind = 'compile' }
	) }
	$validation = Invoke-Bridge @('coord','submit','--session',$sessions[1],'--request',"validation-$stamp",'--kind','validation','--plan',$validationPlan,'--seconds','180')
	$deadline = (Get-Date).AddSeconds(180)
	do {
		# Read-only result observation; the editor continues even when this process exits.
		$observed = Invoke-Bridge @('coord','status','--session',$sessions[1],'--request',"validation-$stamp")
		if ($observed.State -eq 'closed') { break }
		Start-Sleep -Milliseconds 500
	} while ((Get-Date) -lt $deadline)
	if ($observed.State -ne 'closed' -or $observed.Reason -ne 'completed') { throw "Validation batch failed: $($observed | ConvertTo-Json -Compress)" }
	foreach ($taskId in $validation.TaskIds) {
		$record = Get-Content -Raw (Join-Path $Project "Library/AgentBridge/Journal/$taskId.json") | ConvertFrom-Json
		if ($record.Status -ne 'success') { throw "Validation task failed: $taskId" }
		if ($record.Kind -eq 'tests' -and ($null -eq $record.Tests -or $record.Tests.Total -le 0)) { throw 'Empty test run is not acceptance' }
		Write-Json (Join-Path $Output "$taskId.json") $record
	}
	$results += $observed
	# Reproduce the user's double -> float mistake under an owned, disposable input scope.
	$repairDir = Join-Path $Project "Assets/BatchAcceptance/$stamp/Fence"
	$repairFile = Join-Path $repairDir 'BatchRepairProbe.cs'
	$fence = Invoke-Bridge @('coord','edit-begin','--session',$sessions[0],'--request',"break-$stamp",'--seconds','120')
	if ($fence.Code -ne 'granted') { throw 'Could not obtain owned repair input scope' }
	[IO.Directory]::CreateDirectory($repairDir) | Out-Null
	$broken = "public static class BatchRepair_$stamp { static void Animate(float time) {} static void Check() { Animate(1.0); } }"
	[IO.File]::WriteAllText($repairFile, $broken, $utf8)
	Invoke-Bridge @('coord','edit-end','--session',$sessions[0],'--token',$fence.Token) | Out-Null
	$fence = $null
	$brokenResult = Invoke-Bridge -Arguments @('compile','--wait','60') -AllowFailure
	if ($brokenResult.Status -ne 'compiler_error') { throw "Expected compiler_error, got $($brokenResult.Status)" }
	Write-Json (Join-Path $Output 'expected-compiler-error.json') $brokenResult
	$repairPlan = Join-Path $scratch 'repair-validation.json'
	Write-Json $repairPlan @{ Steps = @(@{ Id = 'Check'; Kind = 'compile' }) }
	$neighbors = @()
	foreach ($index in @(2,3)) {
		$neighbors += Invoke-Bridge @('coord','submit','--session',$sessions[$index],'--request',"repair-neighbor-$stamp-$index",'--kind','validation','--plan',$repairPlan,'--seconds','120')
	}
	$fence = Invoke-Bridge @('coord','edit-begin','--session',$sessions[0],'--request',"fix-$stamp",'--seconds','120')
	if ($fence.Code -ne 'granted') { throw 'Repair waited behind older validation requests' }
	[IO.File]::WriteAllText($repairFile, $broken.Replace('Animate(1.0)', 'Animate(1.0f)'), $utf8)
	Invoke-Bridge @('coord','edit-end','--session',$sessions[0],'--token',$fence.Token) | Out-Null
	$fence = $null
	$deadline = (Get-Date).AddSeconds(120)
	foreach ($index in @(2,3)) {
		do {
			$repaired = Invoke-Bridge @('coord','status','--session',$sessions[$index],'--request',"repair-neighbor-$stamp-$index")
			if ($repaired.State -eq 'closed') { break }
			Start-Sleep -Milliseconds 500
		} while ((Get-Date) -lt $deadline)
		if ($repaired.Reason -ne 'completed') { throw "Neighbor did not resume after repair: $($repaired | ConvertTo-Json -Compress)" }
		$results += $repaired
	}
	$reportData = @{ Verdict = 'PASS'; Packages = $results; NoClientActionsSeconds = 15; RepairPriority = 'PASS' }
} finally {
	if ($null -ne $fence) { Invoke-Bridge @('coord','edit-end','--session',$sessions[0],'--token',$fence.Token) | Out-Null }
	if ($null -ne $repairFile -and (Test-Path -LiteralPath $repairFile)) {
		$cleanup = Invoke-Bridge @('coord','edit-begin','--session',$sessions[0],'--request',"cleanup-$stamp",'--seconds','120')
		if ($cleanup.Code -ne 'granted') { throw "Cleanup is waiting; owned probe remains at $repairFile" }
		# Exact owned files only; never recursively delete a computed Assets path.
		Remove-Item -LiteralPath $repairFile
		if (Test-Path -LiteralPath "$repairFile.meta") { Remove-Item -LiteralPath "$repairFile.meta" }
		# These are exact owned empty directories; non-recursive removal refuses unexpected contents.
		Remove-Item -LiteralPath $repairDir
		if (Test-Path -LiteralPath "$repairDir.meta") { Remove-Item -LiteralPath "$repairDir.meta" }
		$stampDir = Split-Path -Parent $repairDir
		Remove-Item -LiteralPath $stampDir
		if (Test-Path -LiteralPath "$stampDir.meta") { Remove-Item -LiteralPath "$stampDir.meta" }
		if ($createdProbeRoot) {
			$baseDir = Split-Path -Parent $stampDir
			Remove-Item -LiteralPath $baseDir
			if (Test-Path -LiteralPath "$baseDir.meta") { Remove-Item -LiteralPath "$baseDir.meta" }
		}
		Invoke-Bridge @('coord','edit-end','--session',$sessions[0],'--token',$cleanup.Token) | Out-Null
	}
	foreach ($session in $sessions) { Invoke-Bridge @('coord','leave','--session',$session) | Out-Null }
}
Write-Json (Join-Path $Output 'report.json') $reportData
Write-Output 'Unattended coordination batches and compiler repair: PASS'
