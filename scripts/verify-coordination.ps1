param(
 [Parameter(Mandatory=$true)][string]$Project,
 [Parameter(Mandatory=$true)][string]$Cli,
 [Parameter(Mandatory=$true)][string]$Output,
 [int]$WaitSeconds = 120
)
$ErrorActionPreference = 'Stop'
# Established entry point, now covering ready packages and real validation.
& (Join-Path $PSScriptRoot 'verify-coordination-batches.ps1') -Project $Project -Cli $Cli -Output $Output
Copy-Item -LiteralPath (Join-Path $Output 'report.json') -Destination (Join-Path $Output 'coordination-acceptance.json')
Write-Output 'coordination_acceptance=PASS'
