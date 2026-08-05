param(
	[string]$BaseRef = "HEAD",
	[string]$OutputPath,
	[switch]$ValidateOnly,
	[switch]$SkipVersionCheck
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$pluginRoot = Join-Path $repoRoot "unity-bridge-plugin"
$defaultArchive = Join-Path $pluginRoot "unity-bridge-plugin.zip"
$archivePath = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
	$defaultArchive
} elseif ([System.IO.Path]::IsPathRooted($OutputPath)) {
	[System.IO.Path]::GetFullPath($OutputPath)
} else {
	[System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}

function Invoke-GitText {
	param([string[]]$Arguments)

	$previousErrorAction = $ErrorActionPreference
	try {
		$ErrorActionPreference = "Continue"
		$safeRoot = $repoRoot.Replace("\", "/")
		$output = & git -c "safe.directory=$safeRoot" -C $repoRoot @Arguments 2>$null
		$gitExitCode = $LASTEXITCODE
	} finally {
		$ErrorActionPreference = $previousErrorAction
	}
	if ($gitExitCode -ne 0) {
		throw "git $($Arguments -join ' ') failed with exit code $gitExitCode"
	}

	return ($output -join "`n")
}

function Get-TextAtRef {
	param([string]$Ref, [string]$RepoPath)
	return Invoke-GitText -Arguments @("show", "${Ref}:$RepoPath")
}

function Get-JsonVersion {
	param([string]$Text, [string]$Label)
	$value = ($Text | ConvertFrom-Json).version
	if ([string]::IsNullOrWhiteSpace($value)) {
		throw "$Label has no version"
	}
	return [Version]$value
}

function Get-CliVersion {
	param([string]$Text, [string]$Label)
	$match = [regex]::Match($Text, "<Version>([^<]+)</Version>")
	if (-not $match.Success) {
		throw "$Label has no <Version> element"
	}
	return [Version]$match.Groups[1].Value
}

function Assert-VersionBumped {
	param([string]$Component, [Version]$Current, [Version]$Previous)
	if ($Current -le $Previous) {
		throw "$Component changed but its version was not increased: current=$Current base=$Previous"
	}
	Write-Output "version_bump=$Component $Previous->$Current"
}

function Test-UnsafeEntryName {
	param([string]$Name)
	return $Name.Contains("\") `
		-or $Name.StartsWith("/", [StringComparison]::Ordinal) `
		-or $Name -match "(^|/)\.\.(/|$)" `
		-or $Name -match '[<>:"|?*\x00-\x1F]'
}

function Get-PluginFiles {
	$items = @()
	foreach ($sourceRoot in @((Join-Path $pluginRoot ".claude-plugin"), (Join-Path $pluginRoot "skills"))) {
		foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
			$entryName = $file.FullName.Substring($pluginRoot.Length + 1).Replace("\", "/")
			if (Test-UnsafeEntryName $entryName) {
				throw "Refusing unsafe plugin entry: $entryName"
			}
			$items += [pscustomobject]@{ Source = $file.FullName; Entry = $entryName }
		}
	}
	return @($items | Sort-Object Entry)
}

function Get-Sha256 {
	param([byte[]]$Bytes)
	$sha = [System.Security.Cryptography.SHA256]::Create()
	try {
		return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace("-", "").ToLowerInvariant()
	} finally {
		$sha.Dispose()
	}
}

function Get-StreamSha256 {
	param([System.IO.Stream]$Stream)
	$sha = [System.Security.Cryptography.SHA256]::Create()
	try {
		return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace("-", "").ToLowerInvariant()
	} finally {
		$sha.Dispose()
	}
}

function Assert-Archive {
	param([string]$Path, [object[]]$ExpectedItems)

	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
	try {
		$rawNames = @($zip.Entries | ForEach-Object { $_.FullName })
		$invalid = @($rawNames | Where-Object { Test-UnsafeEntryName $_ })
		if ($invalid.Count -ne 0) {
			throw "ZIP contains invalid raw entry names: $($invalid -join ', ')"
		}
		if ($rawNames.Count -ne ($rawNames | Sort-Object -Unique).Count) {
			throw "ZIP contains duplicate entry names"
		}
		if ($rawNames.Count -ne $ExpectedItems.Count) {
			throw "ZIP entry count mismatch: expected=$($ExpectedItems.Count) actual=$($rawNames.Count)"
		}

		foreach ($item in $ExpectedItems) {
			$entry = $zip.GetEntry($item.Entry)
			if ($null -eq $entry) {
				throw "ZIP is missing entry: $($item.Entry)"
			}
			$sourceHash = Get-Sha256 ([System.IO.File]::ReadAllBytes($item.Source))
			$stream = $entry.Open()
			try {
				$entryHash = Get-StreamSha256 $stream
			} finally {
				$stream.Dispose()
			}
			if ($sourceHash -ne $entryHash) {
				throw "ZIP content differs from source: $($item.Entry)"
			}
			Write-Output "verified=$($item.Entry)"
		}

		Write-Output "raw_entries=$($rawNames -join ',')"
		Write-Output "invalid_entries=0"
		Write-Output "zip_validation=PASS"
	} finally {
		$zip.Dispose()
	}
}

if (-not $SkipVersionCheck) {
	$changedText = Invoke-GitText -Arguments @("diff", "--name-only", $BaseRef, "--")
	$changedPaths = @($changedText -split "`n" | Where-Object { $_ })
	Write-Output "changed_paths=$($changedPaths.Count)"
	$packageChanged = @($changedPaths | Where-Object { $_ -like "AgentBridgeUnity/Packages/com.elmortem.agentbridge/*" }).Count -gt 0
	$pluginChanged = @($changedPaths | Where-Object { $_ -like "unity-bridge-plugin/.claude-plugin/*" -or $_ -like "unity-bridge-plugin/skills/*" }).Count -gt 0
	$cliChanged = @($changedPaths | Where-Object { $_ -like "AgentBridgeCli/*" -or $_ -like "AgentBridgeCli.Tests/*" }).Count -gt 0

	if ($packageChanged) {
		$current = Get-JsonVersion ([System.IO.File]::ReadAllText((Join-Path $repoRoot "AgentBridgeUnity/Packages/com.elmortem.agentbridge/package.json"))) "Unity package"
		$previous = Get-JsonVersion (Get-TextAtRef $BaseRef "AgentBridgeUnity/Packages/com.elmortem.agentbridge/package.json") "base Unity package"
		Assert-VersionBumped "Unity package" $current $previous
	}
	if ($pluginChanged) {
		$current = Get-JsonVersion ([System.IO.File]::ReadAllText((Join-Path $pluginRoot ".claude-plugin/plugin.json"))) "plugin"
		$previous = Get-JsonVersion (Get-TextAtRef $BaseRef "unity-bridge-plugin/.claude-plugin/plugin.json") "base plugin"
		Assert-VersionBumped "plugin" $current $previous
	}
	if ($cliChanged) {
		$current = Get-CliVersion ([System.IO.File]::ReadAllText((Join-Path $repoRoot "AgentBridgeCli/AgentBridgeCli.csproj"))) "CLI"
		$previous = Get-CliVersion (Get-TextAtRef $BaseRef "AgentBridgeCli/AgentBridgeCli.csproj") "base CLI"
		Assert-VersionBumped "CLI" $current $previous
	}
	Write-Output "version_check=PASS"
}

$items = Get-PluginFiles
if (-not $ValidateOnly) {
	$archiveDirectory = Split-Path -Parent $archivePath
	[System.IO.Directory]::CreateDirectory($archiveDirectory) | Out-Null
	$temporaryPath = $archivePath + ".new"
	if (Test-Path -LiteralPath $temporaryPath) {
		Remove-Item -LiteralPath $temporaryPath -Force
	}

	Add-Type -AssemblyName System.IO.Compression
	$fileStream = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::CreateNew,
		[System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
	try {
		$archive = [System.IO.Compression.ZipArchive]::new($fileStream,
			[System.IO.Compression.ZipArchiveMode]::Create, $true)
		try {
			foreach ($item in $items) {
				$entry = $archive.CreateEntry($item.Entry, [System.IO.Compression.CompressionLevel]::Optimal)
				$entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
				$input = [System.IO.File]::OpenRead($item.Source)
				$output = $entry.Open()
				try {
					$input.CopyTo($output)
				} finally {
					$output.Dispose()
					$input.Dispose()
				}
			}
		} finally {
			$archive.Dispose()
		}
	} finally {
		$fileStream.Dispose()
	}

	Assert-Archive $temporaryPath $items
	Move-Item -LiteralPath $temporaryPath -Destination $archivePath -Force
}

Assert-Archive $archivePath $items
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "archive=$archivePath"
Write-Output "sha256=$archiveHash"
