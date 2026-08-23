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
$skillNameLimit = 64
$skillDescriptionLimit = 1024
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

function Get-YamlScalar {
	param([string]$Value)
	$trimmed = $Value.Trim()
	if ($trimmed.Length -ge 2 -and $trimmed.StartsWith('"') -and $trimmed.EndsWith('"')) {
		return $trimmed.Substring(1, $trimmed.Length - 2).Replace('\"', '"').Replace('\\', '\')
	}
	if ($trimmed.Length -ge 2 -and $trimmed.StartsWith("'") -and $trimmed.EndsWith("'")) {
		return $trimmed.Substring(1, $trimmed.Length - 2).Replace("''", "'")
	}
	return $trimmed
}

function Get-SkillFrontmatter {
	param([string]$Entry, [string]$Source)

	$lines = @([System.IO.File]::ReadAllText($Source) -split "\r?\n")
	if ($lines.Count -lt 3 -or $lines[0].Trim() -ne "---") {
		throw "Skill '$Entry' must start with a YAML frontmatter block"
	}

	$closing = -1
	for ($index = 1; $index -lt $lines.Count; $index++) {
		if ($lines[$index].Trim() -eq "---") {
			$closing = $index
			break
		}
	}
	if ($closing -lt 0) {
		throw "Skill '$Entry' has no closing '---' for its frontmatter"
	}

	$fields = @{}
	for ($index = 1; $index -lt $closing; $index++) {
		$line = $lines[$index]
		if ([string]::IsNullOrWhiteSpace($line)) {
			continue
		}
		$match = [regex]::Match($line, "^(?<key>[A-Za-z0-9_-]+):(?<value>.*)$")
		if (-not $match.Success) {
			throw "Skill '$Entry': frontmatter line $($index + 1) is not a single-line 'key: value' pair. Multi-line and block scalars are not allowed here, because their length cannot be validated: $line"
		}
		$key = $match.Groups["key"].Value
		if ($fields.ContainsKey($key)) {
			throw "Skill '$Entry': duplicate frontmatter field '$key'"
		}
		$value = Get-YamlScalar $match.Groups["value"].Value
		if ([string]::IsNullOrWhiteSpace($value)) {
			throw "Skill '$Entry': frontmatter field '$key' must have a non-empty single-line value"
		}
		$fields[$key] = $value
	}

	return $fields
}

function Assert-SkillFrontmatter {
	param([object[]]$Items)

	$skillFiles = @($Items | Where-Object { $_.Entry -match "^skills/[^/]+/SKILL\.md$" })
	if ($skillFiles.Count -eq 0) {
		throw "Plugin contains no skills/<name>/SKILL.md"
	}

	foreach ($file in $skillFiles) {
		$entry = $file.Entry
		$directoryName = ($entry -split "/")[1]
		$fields = Get-SkillFrontmatter $entry $file.Source

		foreach ($required in @("name", "description")) {
			if (-not $fields.ContainsKey($required)) {
				throw "Skill '$entry': frontmatter has no '$required' field"
			}
		}

		$name = $fields["name"]
		$description = $fields["description"]

		if ($name -cne $directoryName) {
			throw "Skill '$entry': field 'name' is '$name' but its directory is '$directoryName'"
		}
		if ($name -cnotmatch "^[a-z0-9]+(-[a-z0-9]+)*$") {
			throw "Skill '$entry': field 'name' must be lowercase kebab-case: $name"
		}
		if ($name.Length -gt $skillNameLimit) {
			throw "Skill '$entry': field 'name' must be at most $skillNameLimit characters, actual $($name.Length)"
		}
		if ($description.Length -gt $skillDescriptionLimit) {
			throw "Skill '$entry': field 'description' must be at most $skillDescriptionLimit characters, actual $($description.Length). Shorten the description; do not ship a skill the agent host will refuse to load."
		}

		Write-Output "skill_frontmatter=$entry name=$($name.Length)/$skillNameLimit description=$($description.Length)/$skillDescriptionLimit"
	}

	Write-Output "frontmatter_validation=PASS"
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
Assert-SkillFrontmatter $items
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
