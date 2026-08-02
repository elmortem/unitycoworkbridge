param(
	[string]$Version = "1.3.0",
	[string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "AgentBridge\bin"),
	[switch]$NoPathUpdate
)

$ErrorActionPreference = "Stop"

$architecture = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$assetName = "agentbridge-win-$architecture.zip"
$releaseBase = "https://github.com/elmortem/unitycoworkbridge/releases/download/agentbridge-v$Version"

function Get-ReleaseAsset {
	param(
		[string]$Name,
		[string]$Destination
	)

	$uri = "$releaseBase/$Name"
	try {
		Invoke-WebRequest $uri -OutFile $Destination
	} catch {
		$statusCode = $null
		if ($null -ne $_.Exception.Response -and $null -ne $_.Exception.Response.StatusCode) {
			$statusCode = [int]$_.Exception.Response.StatusCode
		}

		if ($statusCode -eq 404) {
			throw "AgentBridge release asset '$Name' was not found. Release agentbridge-v$Version may be incomplete: https://github.com/elmortem/unitycoworkbridge/releases/tag/agentbridge-v$Version"
		}

		throw "Failed to download AgentBridge release asset '$Name' from $uri. $($_.Exception.Message)"
	}
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("agentbridge-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null

try {
	$archivePath = Join-Path $temporaryDirectory $assetName
	$checksumPath = $archivePath + ".sha256"
	Get-ReleaseAsset $assetName $archivePath
	Get-ReleaseAsset "$assetName.sha256" $checksumPath

	$expectedHash = ((Get-Content $checksumPath -Raw).Trim() -split "\s+")[0].ToLowerInvariant()
	$actualHash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
	if ($actualHash -ne $expectedHash) {
		throw "Checksum mismatch for $assetName"
	}

	$expandedPath = Join-Path $temporaryDirectory "expanded"
	Expand-Archive $archivePath -DestinationPath $expandedPath
	New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
	Copy-Item (Join-Path $expandedPath "agentbridge.exe") (Join-Path $InstallDirectory "agentbridge.exe") -Force

	if (-not $NoPathUpdate) {
		$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
		$entries = @($userPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
		if ($entries -notcontains $InstallDirectory) {
			$newPath = (@($entries) + $InstallDirectory) -join ";"
			[Environment]::SetEnvironmentVariable("Path", $newPath, "User")
		}

		if (($env:Path -split ";") -notcontains $InstallDirectory) {
			$env:Path = $env:Path + ";" + $InstallDirectory
		}
	}

	Write-Output "Installed agentbridge to $InstallDirectory"
	Write-Output "Open a new terminal or restart the agent application, then run: agentbridge --version"
} finally {
	if (Test-Path -LiteralPath $temporaryDirectory) {
		Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
	}
}
