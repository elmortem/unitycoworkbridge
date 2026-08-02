param(
	[string]$Version = "",
	[string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "AgentBridge\bin"),
	[string]$Rid = "",
	[switch]$NoPathUpdate
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$architecture = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
$nativeRid = "win-$architecture"

if ([string]::IsNullOrWhiteSpace($Rid)) {
	$Rid = $nativeRid
}

if ($Rid -notin @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")) {
	throw "Unsupported runtime identifier: $Rid"
}

if ($Rid -like "win-*") {
	$assetName = "agentbridge-$Rid.zip"
	$binaryName = "agentbridge.exe"
} else {
	$assetName = "agentbridge-$Rid.tar.gz"
	$binaryName = "agentbridge"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
	$releaseBase = "https://github.com/elmortem/unitycoworkbridge/releases/latest/download"
	$releasePage = "https://github.com/elmortem/unitycoworkbridge/releases/latest"
	$releaseName = "the latest release"
} else {
	$releaseBase = "https://github.com/elmortem/unitycoworkbridge/releases/download/agentbridge-v$Version"
	$releasePage = "https://github.com/elmortem/unitycoworkbridge/releases/tag/agentbridge-v$Version"
	$releaseName = "release agentbridge-v$Version"
}

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
			throw "AgentBridge release asset '$Name' was not found. $releaseName may be incomplete: $releasePage"
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
	New-Item -ItemType Directory -Force -Path $expandedPath | Out-Null

	if ($assetName -like "*.zip") {
		Expand-Archive $archivePath -DestinationPath $expandedPath -Force
	} else {
		& tar -xzf $archivePath -C $expandedPath
		if ($LASTEXITCODE -ne 0) {
			throw "Failed to extract $assetName. The tar command is required (Windows 10 1803 or newer)."
		}
	}

	New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
	Copy-Item (Join-Path $expandedPath $binaryName) (Join-Path $InstallDirectory $binaryName) -Force

	if (-not $NoPathUpdate -and $Rid -eq $nativeRid) {
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

	Write-Output "Installed agentbridge ($Rid) to $InstallDirectory"
	if ($Rid -eq $nativeRid) {
		Write-Output "Open a new terminal or restart the agent application, then run: agentbridge --version"
	}
} finally {
	if (Test-Path -LiteralPath $temporaryDirectory) {
		Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
	}
}
