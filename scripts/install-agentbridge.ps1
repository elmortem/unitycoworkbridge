param(
	[string]$Version = "",
	[string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "AgentBridge\bin"),
	[string]$Rid = "",
	[string]$Project = "",
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

# The Editor supplies -Project. A native install run from inside a Unity project
# has the same contract; an explicit foreign -Rid remains a single-build install.
if ([string]::IsNullOrWhiteSpace($Project) -and $Rid -like "win-*") {
	$candidate = (Get-Location).ProviderPath
	while ($candidate) {
		if (Test-Path -LiteralPath (Join-Path $candidate "ProjectSettings/ProjectVersion.txt") -PathType Leaf) {
			$Project = $candidate
			break
		}
		$candidate = [IO.Path]::GetDirectoryName($candidate)
	}
}
if ($Project) {
	$Project = [IO.Path]::GetFullPath($Project)
	if ($Rid -notlike "win-*" -or -not (Test-Path -LiteralPath (Join-Path $Project "ProjectSettings/ProjectVersion.txt") -PathType Leaf)) {
		throw "-Project requires a Unity project and a Windows runtime identifier."
	}
}

# Resolve latest once: both architectures must come from the same release.
if ([string]::IsNullOrWhiteSpace($Version)) {
	$release = Invoke-WebRequest "https://api.github.com/repos/elmortem/unitycoworkbridge/releases/latest" -UseBasicParsing
	$tag = ($release.Content | ConvertFrom-Json).tag_name
	if ($tag -notmatch '^agentbridge-v([0-9]+\.[0-9]+\.[0-9]+)$') { throw "Invalid AgentBridge release tag: $tag" }
	$Version = $Matches[1]
}
$releaseBase = "https://github.com/elmortem/unitycoworkbridge/releases/download/agentbridge-v$Version"
$releasePage = "https://github.com/elmortem/unitycoworkbridge/releases/tag/agentbridge-v$Version"
$releaseName = "release agentbridge-v$Version"

function New-InstallTarget {
	param([string]$Runtime, [string]$Directory)
	$binary = if ($Runtime -like "win-*") { "agentbridge.exe" } else { "agentbridge" }
	$extension = if ($Runtime -like "win-*") { "zip" } else { "tar.gz" }
	$path = Join-Path ([IO.Path]::GetFullPath($Directory)) $binary
	$nonce = [Guid]::NewGuid().ToString("N")
	return [pscustomobject]@{
		Rid = $Runtime; Asset = "agentbridge-$Runtime.$extension"; Binary = $binary
		Path = $path; Stage = "$path.update-$nonce"; Backup = "$path.previous-$nonce"
		OldMoved = $false; Installed = $false
	}
}
$targets = @(New-InstallTarget $Rid $InstallDirectory)
if ($Project) {
	$targets += New-InstallTarget ("linux-" + $Rid.Substring(4)) (Join-Path $Project "Library/AgentBridge/cli")
}

function Get-Sha256 {
	param([string]$Path)
	# Unity can inherit a PSModulePath without the optional Get-FileHash module.
	$sha256 = [Security.Cryptography.SHA256]::Create()
	try {
		$stream = [IO.File]::OpenRead($Path)
		try { return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "").ToLowerInvariant() }
		finally { $stream.Dispose() }
	} finally { $sha256.Dispose() }
}

function Get-ReleaseAsset {
	param(
		[string]$Name,
		[string]$Destination
	)

	$uri = "$releaseBase/$Name"
	try {
		Invoke-WebRequest $uri -UseBasicParsing -OutFile $Destination
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

function Read-TarBytes {
	param($Stream, [byte[]]$Buffer, [int]$Count)
	$offset = 0
	while ($offset -lt $Count) {
		$read = $Stream.Read($Buffer, $offset, $Count - $offset)
		if ($read -le 0) { break }
		$offset += $read
	}
	return $offset
}

function Get-TarString {
	param([byte[]]$Header, [int]$Offset, [int]$Length)
	$end = [Array]::IndexOf($Header, [byte]0, $Offset, $Length)
	if ($end -lt 0) { $end = $Offset + $Length }
	return [Text.Encoding]::UTF8.GetString($Header, $Offset, $end - $Offset)
}

function Expand-TarGzEntry {
	param([string]$Archive, [string]$EntryName, [string]$Destination)
	# PATH may resolve tar to Git/MSYS GNU tar, which reads "C:\..." as a remote host.
	# Reading the archive in-process removes the dependency on whichever tar comes first.
	$file = [IO.File]::OpenRead($Archive)
	try {
		$gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionMode]::Decompress)
		try {
			$header = New-Object byte[] 512
			$buffer = New-Object byte[] 81920
			$longName = $null
			while ($true) {
				$read = Read-TarBytes $gzip $header 512
				if ($read -eq 0) { break }
				if ($read -ne 512) { throw "Truncated tar header." }
				if (@($header | Where-Object { $_ -ne 0 }).Count -eq 0) { break }
				$sizeText = (Get-TarString $header 124 12).Trim([char]0, [char]32)
				if ($sizeText -notmatch '^[0-7]+$') { throw "Invalid tar entry size '$sizeText'." }
				$size = [Convert]::ToInt64($sizeText, 8)
				$type = [char]$header[156]
				$name = Get-TarString $header 0 100
				if ((Get-TarString $header 257 5) -eq "ustar") {
					$prefix = Get-TarString $header 345 155
					if ($prefix) { $name = "$prefix/$name" }
				}
				if ($longName) { $name = $longName; $longName = $null }
				while ($name.StartsWith("./")) { $name = $name.Substring(2) }

				$output = $null
				$nameBytes = $null
				if ($type -eq 'L') { $nameBytes = New-Object IO.MemoryStream }
				elseif (($type -eq '0' -or $type -eq [char]0) -and $name -eq $EntryName) {
					$output = [IO.File]::Create($Destination)
				}
				try {
					$remaining = $size
					while ($remaining -gt 0) {
						$chunk = [int][Math]::Min([long]$buffer.Length, $remaining)
						$got = Read-TarBytes $gzip $buffer $chunk
						if ($got -ne $chunk) { throw "Truncated tar entry '$name'." }
						if ($output) { $output.Write($buffer, 0, $got) }
						if ($nameBytes) { $nameBytes.Write($buffer, 0, $got) }
						$remaining -= $got
					}
				} finally { if ($output) { $output.Dispose() } }
				if ($nameBytes) { $longName = [Text.Encoding]::UTF8.GetString($nameBytes.ToArray()).TrimEnd([char]0) }
				$padding = [int]((512 - ($size % 512)) % 512)
				if ($padding -gt 0 -and (Read-TarBytes $gzip $buffer $padding) -ne $padding) { throw "Truncated tar padding." }
				if ($output) { return $true }
			}
		} finally { $gzip.Dispose() }
	} finally { $file.Dispose() }
	return $false
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("agentbridge-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
$committed = $false

try {
	# Download, verify and stage every build before changing an installed executable.
	foreach ($target in $targets) {
		$archivePath = Join-Path $temporaryDirectory $target.Asset
		$checksumPath = $archivePath + ".sha256"
		Get-ReleaseAsset $target.Asset $archivePath
		Get-ReleaseAsset ($target.Asset + ".sha256") $checksumPath
		$expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split "\s+")[0].ToLowerInvariant()
		if ((Get-Sha256 $archivePath) -ne $expectedHash) { throw "Checksum mismatch for $($target.Asset)" }
		$expandedPath = Join-Path $temporaryDirectory $target.Rid
		New-Item -ItemType Directory -Path $expandedPath | Out-Null
		if ($target.Asset.EndsWith(".zip")) {
			Add-Type -AssemblyName System.IO.Compression.FileSystem
			[IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $expandedPath)
		} else {
			try { Expand-TarGzEntry $archivePath $target.Binary (Join-Path $expandedPath $target.Binary) | Out-Null }
			catch { throw "Failed to extract $($target.Asset). $($_.Exception.Message)" }
		}
		$source = Join-Path $expandedPath $target.Binary
		if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing $($target.Binary) in $($target.Asset)" }
		New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target.Path)) | Out-Null
		[IO.File]::Copy($source, $target.Stage)
		if ((Get-Sha256 $source) -ne (Get-Sha256 $target.Stage)) { throw "Staged binary checksum mismatch: $($target.Path)" }
	}

	try {
		foreach ($target in $targets) {
			# Windows allows renaming an executing image, but not overwriting its bytes.
			# Existing clients keep that image; new invocations use the staged replacement.
			if ([IO.File]::Exists($target.Path)) {
				[IO.File]::Move($target.Path, $target.Backup)
				$target.OldMoved = $true
			}
			[IO.File]::Move($target.Stage, $target.Path)
			$target.Installed = $true
		}
	} catch {
		$failure = $_
		for ($i = $targets.Count - 1; $i -ge 0; $i--) {
			$target = $targets[$i]
			try {
				if ($target.Installed) { [IO.File]::Move($target.Path, $target.Stage) }
				if ($target.OldMoved) { [IO.File]::Move($target.Backup, $target.Path) }
			} catch { Write-Warning "Rollback failed for $($target.Path): $($_.Exception.Message). Previous binary: $($target.Backup)" }
		}
		throw $failure
	}
	$committed = $true

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

	foreach ($target in $targets) { Write-Output "Installed agentbridge $Version ($($target.Rid)) to $($target.Path)" }
	if ($Rid -eq $nativeRid) {
		Write-Output "New commands use the installed version; running commands can finish normally. Reopen your terminal only if PATH changed. Verify with: agentbridge --version"
	}
} finally {
	foreach ($target in $targets) {
		# A running old image may remain locked until its process exits. Never stop it.
		foreach ($path in @($target.Stage) + $(if ($committed) { @($target.Backup) } else { @() })) {
			try { if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) } }
			catch { Write-Warning "Binary is still in use and was retained: $path" }
		}
	}
	if (Test-Path -LiteralPath $temporaryDirectory) {
		$tempPrefix = [IO.Path]::Combine([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), "agentbridge-install-")
		if (-not [IO.Path]::GetFullPath($temporaryDirectory).StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe installer cleanup path" }
		Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
	}
}
