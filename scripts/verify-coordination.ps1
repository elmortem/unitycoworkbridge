<#
.SYNOPSIS
	End-to-end acceptance for coordination-v1 and evidence-v1 through the real CLI.

.DESCRIPTION
	Registers two sessions of its own, drives a writer and a window against each other, takes the
	window, runs one real bounded validation step, and reports what actually happened with real
	task ids and counters.

	It never touches state it does not own: no foreign session is abandoned, no foreign grant is
	closed, and if somebody else holds the editor the script waits and reports instead of taking it.
	Every registration and grant it created is closed again before it exits.
#>
param(
	[Parameter(Mandatory = $true)][string]$Project,
	[Parameter(Mandatory = $true)][string]$Cli,
	[Parameter(Mandatory = $true)][string]$Output,
	[int]$WaitSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Cli)) {
	throw "CLI not found: $Cli. Build it first: dotnet build AgentBridgeCli/AgentBridgeCli.csproj -c Release"
}
if (-not (Test-Path -LiteralPath $Project)) {
	throw "Unity project not found: $Project"
}
if (-not [System.IO.Path]::IsPathRooted($Output)) {
	throw "-Output must be an absolute path"
}

[System.IO.Directory]::CreateDirectory($Output) | Out-Null

$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddHHmmss")
$sessionA = "AB_COORD_ACC_A_$stamp"
$sessionB = "AB_COORD_ACC_B_$stamp"
$report = [ordered]@{
	StartedUtc      = (Get-Date).ToUniversalTime().ToString("o")
	Cli             = $Cli
	Project         = $Project
	SessionA        = $sessionA
	SessionB        = $sessionB
	Checks          = @()
	TaskIds         = @()
	Counters        = [ordered]@{ RealRuns = 0; CacheHits = 0; Refusals = 0 }
	Verdict         = "UNKNOWN"
}

$script:ownedGrants = @()
$script:registered = @()

function Invoke-Cli {
	param([string[]]$Arguments, [switch]$AllowFailure)

	# stdout carries exactly one JSON answer and stderr carries queue progress. Merging them would
	# make every progress line unparseable, and with ErrorActionPreference=Stop it would also turn
	# ordinary progress into a terminating error. So stderr goes to its own file.
	$errorFile = Join-Path $Output ("stderr-" + [guid]::NewGuid().ToString("N") + ".txt")
	$previous = $ErrorActionPreference
	try {
		$ErrorActionPreference = "Continue"
		$text = & $Cli @Arguments 2>$errorFile
		$code = $LASTEXITCODE
	} finally {
		$ErrorActionPreference = $previous
	}

	$progress = ""
	if (Test-Path -LiteralPath $errorFile) {
		$progress = (Get-Content -LiteralPath $errorFile -Raw)
		Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
	}

	$joined = ($text | Out-String).Trim()
	$value = $null
	try { $value = $joined | ConvertFrom-Json } catch { $value = $null }

	if (-not $AllowFailure -and $code -ne 0) {
		throw "agentbridge $($Arguments -join ' ') failed with exit $code`n$joined`n$progress"
	}

	return [pscustomobject]@{ ExitCode = $code; Text = $joined; Json = $value; Progress = $progress }
}

function Add-Check {
	param([string]$Id, [string]$Name, [bool]$Passed, [string]$Detail)
	$report.Checks += [pscustomobject]@{ Id = $Id; Name = $Name; Passed = $Passed; Detail = $Detail }
	$status = if ($Passed) { "PASS" } else { "FAIL" }
	Write-Output "$status $Id $Name - $Detail"
}

function Write-Scope {
	param([string]$Path, [string[]]$Paths)
	$document = [pscustomobject]@{ Paths = $Paths }
	$document | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Write-Plan {
	param([string]$Path, [object[]]$Steps)
	$document = [pscustomobject]@{
		Steps         = $Steps
		ArtifactRoots = @()
		FixtureRoots  = @()
	}
	$document | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Close-Own {
	# Only the rights this run created, and each one with the verb that actually closes it.
	foreach ($grant in $script:ownedGrants) {
		Invoke-Cli -AllowFailure -Arguments @("coord", $grant.Verb, "--project", $Project,
			"--session", $grant.Session, "--token", $grant.Token, "--format", "json") | Out-Null
	}
	$script:ownedGrants = @()

	foreach ($session in $script:registered) {
		Invoke-Cli -AllowFailure -Arguments @("coord", "leave", "--project", $Project, "--session", $session,
			"--format", "json") | Out-Null
	}
	$script:registered = @()
}

try {
	# ---------------------------------------------------------------- capabilities
	$capabilities = Invoke-Cli -AllowFailure -Arguments @("coord", "capabilities", "--project", $Project, "--format", "json")
	$cliOk = $null -ne $capabilities.Json -and $capabilities.Json.Cli -contains "coordination-v1"
	$packageOk = $null -ne $capabilities.Json -and $capabilities.Json.PackageSupportsCoordination
	Add-Check "CAP" "both sides declare coordination-v1" ($cliOk -and $packageOk) `
		"cli=$cliOk package=$packageOk"
	if (-not ($cliOk -and $packageOk)) {
		# A capability mismatch stops every dependent check rather than producing a fake verdict.
		$report.Verdict = "SKIPPED_CAPABILITY_MISMATCH"
		throw "coordination-v1 is not declared by both sides; nothing below would mean anything"
	}

	# ---------------------------------------------------------------- foreign work
	$status = Invoke-Cli -Arguments @("coord", "status", "--project", $Project, "--format", "json")
	$foreign = $status.Json.Blockers | Where-Object { $_ -like "window_active:*" -or $_ -like "edit_active:*" }
	if ($foreign) {
		# Somebody else is working. Waiting is the whole point of the protocol; taking it would be
		# the exact failure this contract exists to prevent.
		Add-Check "FOREIGN" "another session holds a right" $false "blockers: $($foreign -join ',')"
		$report.Verdict = "WAITING_FOR_FOREIGN_WORK"
		throw "foreign coordination work is in progress; rerun once it finishes"
	}

	# ---------------------------------------------------------------- C02 / C09
	$scopeA = Join-Path $Output "scope-a.json"
	$scopeB = Join-Path $Output "scope-b.json"
	Write-Scope $scopeA @("AgentBridgeUnity/Assets/AgentBridgeCoordinationFixtures/")
	Write-Scope $scopeB @("Docs/notes/")

	Invoke-Cli -Arguments @("coord", "register", "--project", $Project, "--session", $sessionA,
		"--spec", "bridge_coordination_v1", "--repo", $Project, "--scope", $scopeA,
		"--owner", "verify-coordination/a", "--format", "json") | Out-Null
	$script:registered += $sessionA

	Invoke-Cli -Arguments @("coord", "register", "--project", $Project, "--session", $sessionB,
		"--spec", "bridge_coordination_v1", "--repo", $Project, "--scope", $scopeB,
		"--owner", "verify-coordination/b", "--format", "json") | Out-Null
	$script:registered += $sessionB

	$overlap = Invoke-Cli -AllowFailure -Arguments @("coord", "register", "--project", $Project,
		"--session", "$sessionB`_dup", "--spec", "x", "--repo", $Project, "--scope", $scopeA,
		"--owner", "verify/dup", "--format", "json")
	Add-Check "C01" "an overlapping scope is refused" `
		($overlap.ExitCode -eq 1 -and $overlap.Json.Code -eq "scope_conflict") `
		"exit=$($overlap.ExitCode) code=$($overlap.Json.Code)"
	if ($overlap.ExitCode -eq 1) { $report.Counters.Refusals++ }

	$editB = Invoke-Cli -Arguments @("coord", "edit-begin", "--project", $Project, "--session", $sessionB,
		"--request", [guid]::NewGuid().ToString("N"), "--seconds", "60", "--format", "json")
	$tokenB = $editB.Json.Token
	$script:ownedGrants += [pscustomobject]@{ Verb = "edit-end"; Session = $sessionB; Token = $tokenB }

	$planPath = Join-Path $Output "plan.json"
	Write-Plan $planPath @(
		[pscustomobject]@{
			Id = "V1"; Kind = "tests"; Mode = "EditMode"
			# A dedicated probe, not the V3 fixtures: AgentBridgeCoordinationTests and
			# AgentBridgeEvidenceTests assert that no registration and no window exist, which is
			# exactly what this script creates. Running them here would be self-contradictory.
			Assemblies = @(); Tests = @("AgentBridgeTestCacheTests"); Categories = @()
			PayloadSha256 = ""; Fresh = $false
		}
	)

	$windowUuid = [guid]::NewGuid().ToString("N")
	$requestA = Invoke-Cli -Arguments @("coord", "request", "--project", $Project, "--session", $sessionA,
		"--request", $windowUuid, "--kind", "validation", "--plan", $planPath, "--seconds", "300", "--format", "json")
	Add-Check "C02a" "a window queues behind a live writer" `
		($requestA.Json.State -eq "waiting") "state=$($requestA.Json.State) blockers=$($requestA.Json.Blockers -join ',')"

	$pausedB = Invoke-Cli -AllowFailure -Arguments @("coord", "renew", "--project", $Project,
		"--session", $sessionB, "--token", $tokenB, "--format", "json")
	Add-Check "C02b" "the waiting window pauses the writer" `
		($pausedB.Json.Code -eq "pause_requested") "code=$($pausedB.Json.Code)"

	Invoke-Cli -Arguments @("coord", "edit-end", "--project", $Project, "--session", $sessionB,
		"--token", $tokenB, "--format", "json") | Out-Null
	$script:ownedGrants = @($script:ownedGrants | Where-Object { $_.Token -ne $tokenB })

	# ---------------------------------------------------------------- C09: the editor confirms
	$deadline = (Get-Date).AddSeconds($WaitSeconds)
	$token = ""
	$revision = $requestA.Json.Revision
	while ((Get-Date) -lt $deadline -and -not $token) {
		$wait = Invoke-Cli -AllowFailure -Arguments @("coord", "wait", "--project", $Project,
			"--session", $sessionA, "--request", $windowUuid, "--after", "$revision", "--wait", "15", "--format", "json")
		if ($null -ne $wait.Json) {
			$revision = $wait.Json.Revision
			if ($wait.Json.State -eq "granted" -and $wait.Json.Token) {
				$token = $wait.Json.Token
			}
		}
	}

	$tokenDetail = if ($token) { "issued" } else { "not issued within ${WaitSeconds}s" }
	Add-Check "C09" "the editor confirmed the window" ([bool]$token) "token=$tokenDetail"
	if (-not $token) {
		$report.Verdict = "EDITOR_DID_NOT_CONFIRM"
		throw "no window was granted within $WaitSeconds seconds; the editor may be busy or asleep"
	}

	$script:ownedGrants += [pscustomobject]@{ Verb = "finish"; Session = $sessionA; Token = $token }

	# ---------------------------------------------------------------- C11: no token, no run
	$refused = Invoke-Cli -AllowFailure -Arguments @("tests", "--project", $Project, "--mode", "EditMode",
		"--test", "AgentBridgeTestCacheTests", "--session", "$sessionA`_legacy", "--wait", "30", "--format", "json")
	$refusedCode = if ($null -ne $refused.Json -and $refused.Json.PSObject.Properties.Name -contains "Logs") {
		($refused.Json.Logs -join ' ')
	} else { $refused.Text }
	Add-Check "C11" "a legacy submission is refused while coordination is active" `
		($refused.ExitCode -ne 0 -and $refusedCode -like "*coordination_required*") `
		"exit=$($refused.ExitCode) reason=$refusedCode"
	if ($refused.ExitCode -ne 0) { $report.Counters.Refusals++ }

	# ---------------------------------------------------------------- the real step
	$run = Invoke-Cli -AllowFailure -Arguments @("tests", "--project", $Project, "--mode", "EditMode",
		"--test", "AgentBridgeTestCacheTests", "--session", $sessionA,
		"--coord-window", $token, "--coord-step", "V1", "--wait", "$WaitSeconds", "--format", "json")
	if ($null -ne $run.Json -and $run.Json.PSObject.Properties.Name -contains "Id") {
		$report.TaskIds += $run.Json.Id
	}

	$ranClean = $run.ExitCode -eq 0
	if ($ranClean) { $report.Counters.RealRuns++ }
	$runId = if ($null -ne $run.Json) { [string]$run.Json.Id } else { "" }
	$runStatus = if ($null -ne $run.Json) { [string]$run.Json.Status } else { "" }
	Add-Check "C09b" "the planned step ran inside the window" $ranClean `
		"exit=$($run.ExitCode) id=$runId status=$runStatus"

	$validity = if ($null -ne $run.Json -and $run.Json.PSObject.Properties.Name -contains "Evidence" -and $run.Json.Evidence) {
		$run.Json.Evidence.Validity
	} else { "missing" }
	$digest = ""
	if ($null -ne $run.Json -and $run.Json.Evidence -and $run.Json.Evidence.InputDigest) {
		$full = [string]$run.Json.Evidence.InputDigest
		$digest = $full.Substring(0, [Math]::Min(16, $full.Length))
	}
	Add-Check "C14" "the result carries evidence-v1" ($validity -eq "valid") "validity=$validity digest=$digest"

	# C03: the same step with a different task id must not run twice.
	$second = Invoke-Cli -AllowFailure -Arguments @("tests", "--project", $Project, "--mode", "EditMode",
		"--test", "AgentBridgeTestCacheTests", "--session", $sessionA,
		"--coord-window", $token, "--coord-step", "V1", "--wait", "30", "--format", "json")
	$secondText = ($second.Text)
	Add-Check "C03" "a consumed step refuses a second task" `
		($second.ExitCode -ne 0 -and $secondText -like "*step_consumed*") `
		"exit=$($second.ExitCode)"
	if ($second.ExitCode -ne 0) { $report.Counters.Refusals++ }

	# ---------------------------------------------------------------- C20: finish
	$finish = Invoke-Cli -AllowFailure -Arguments @("coord", "finish", "--project", $Project,
		"--session", $sessionA, "--token", $token, "--format", "json")
	Add-Check "C20" "the window closes only after terminal tasks" `
		($finish.Json.Code -in @("ok", "draining")) "code=$($finish.Json.Code)"
	if ($finish.Json.Code -eq "ok") {
		$script:ownedGrants = @($script:ownedGrants | Where-Object { $_.Token -ne $token })
	}
}
catch {
	$report.Error = $_.Exception.Message
	Write-Warning $_.Exception.Message
}
finally {
	Close-Own

	# An aborted run is never a pass, however many checks it managed to tick off before it died.
	$failed = @($report.Checks | Where-Object { -not $_.Passed })
	$aborted = $report.Contains("Error")
	if ($report.Verdict -eq "UNKNOWN") {
		$report.Verdict = if ($report.Checks.Count -gt 0 -and $failed.Count -eq 0 -and -not $aborted) {
			"PASS"
		} else {
			"FAIL"
		}
	}

	$report.FinishedUtc = (Get-Date).ToUniversalTime().ToString("o")
	$reportPath = Join-Path $Output "coordination-acceptance.json"
	$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

	Write-Output "report=$reportPath"
	Write-Output "real_runs=$($report.Counters.RealRuns) cache_hits=$($report.Counters.CacheHits) refusals=$($report.Counters.Refusals)"
	Write-Output "task_ids=$($report.TaskIds -join ',')"
	Write-Output "coordination_acceptance=$($report.Verdict)"
}

if ($report.Verdict -ne "PASS") {
	exit 1
}
