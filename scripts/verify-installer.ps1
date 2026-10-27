param([string]$CliDirectory = (Join-Path $PSScriptRoot "../AgentBridgeCli/bin/Release/net8.0"))
$ErrorActionPreference = "Stop"
$installer = Join-Path $PSScriptRoot "install-agentbridge.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agentbridge-installer-test-" + [Guid]::NewGuid().ToString("N"))
$originalModulePath = $env:PSModulePath
$server = $null
New-Item -ItemType Directory -Path $testRoot | Out-Null
function Check($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-Project([string]$Name) {
	$path = Join-Path $testRoot $Name
	foreach ($directory in 'ProjectSettings','Packages','Assets') { [IO.Directory]::CreateDirectory((Join-Path $path $directory)) | Out-Null }
	[IO.File]::WriteAllText((Join-Path $path "ProjectSettings/ProjectVersion.txt"), "m_EditorVersion: 2022.3.62f2")
	[IO.File]::WriteAllText((Join-Path $path "Packages/manifest.json"), "{}")
	return $path
}
function Check-Bytes([string]$Path, [string]$Expected) { Check ([IO.File]::ReadAllText($Path) -eq $Expected) "Unexpected installed bytes: $Path" }
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
	$failed = $false
	try { & $Action } catch { Check ($_.Exception.Message -like $Pattern) "Unexpected failure: $_"; $failed = $true }
	Check $failed "Expected installer failure: $Pattern"
}
try {
	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$source = New-Item -ItemType Directory -Path (Join-Path $testRoot "windows-source")
	[IO.File]::WriteAllText((Join-Path $source.FullName "agentbridge.exe"), "new windows CLI")
	$zip = Join-Path $testRoot "fixture.zip"
	[IO.Compression.ZipFile]::CreateFromDirectory($source.FullName, $zip)
	$linuxSource = New-Item -ItemType Directory -Path (Join-Path $testRoot "linux-source")
	[IO.File]::WriteAllText((Join-Path $linuxSource.FullName "agentbridge"), "new linux CLI")
	$tarball = Join-Path $testRoot "fixture.tar.gz"
	# Git/MSYS tar earlier in PATH treats "C:" as a remote host; the fixture uses the system bsdtar.
	& (Join-Path $env:SystemRoot "System32/tar.exe") -czf $tarball -C $linuxSource.FullName agentbridge
	Check ($LASTEXITCODE -eq 0) "Could not create Linux archive fixture"
	$zipHash = (Get-FileHash -LiteralPath $zip).Hash
	$tarHash = (Get-FileHash -LiteralPath $tarball).Hash
	$fixtureState = [pscustomobject]@{
		Corrupt = ""; Missing = ""; LatestCalls = 0
		Uris = New-Object 'System.Collections.Generic.List[string]'
	}
	$env:PSModulePath = Join-Path $testRoot "no-modules"
	function Get-FileHash { throw "Get-FileHash is unavailable in this regression scenario" }
	function Expand-Archive { throw "Expand-Archive is unavailable in this regression scenario" }
	# The installer extracts tar.gz in-process: any tar found in PATH must stay unused.
	function tar { throw "tar from PATH must not be used by the installer" }
	function Invoke-WebRequest {
		param($Uri, $OutFile, [switch]$UseBasicParsing)
		Check $UseBasicParsing "Basic parsing is required"
		$fixtureState.Uris.Add($Uri)
		if ($Uri -like "https://api.github.com/*") {
			$fixtureState.LatestCalls++
			return [pscustomobject]@{Content='{"tag_name":"agentbridge-v9.8.7"}'}
		}
		$name = ($Uri -split '/')[-1]
		if ($fixtureState.Missing -and $name -eq $fixtureState.Missing) { throw "fixture missing asset" }
		$isLinux = $name -like '*linux*'
		if ($name.EndsWith(".sha256")) {
			$hash = if ($isLinux) { $tarHash } else { $zipHash }
			if ($fixtureState.Corrupt -and $name -like $fixtureState.Corrupt) { $hash = "0" * 64 }
			[IO.File]::WriteAllText($OutFile, "$hash  $($name.Substring(0, $name.Length - 7))")
		} else {
			[IO.File]::Copy($(if ($isLinux) { $tarball } else { $zip }), $OutFile)
		}
	}
	$destination = Join-Path $testRoot "installed"
	& $installer -Version test -Rid win-x64 -InstallDirectory $destination -NoPathUpdate
	Check-Bytes (Join-Path $destination "agentbridge.exe") "new windows CLI"
	$fixtureState.Corrupt = '*win*.sha256'
	$rejected = Join-Path $testRoot "rejected"
	Expect-Failure { & $installer -Version test -Rid win-x64 -InstallDirectory $rejected -NoPathUpdate } 'Checksum mismatch*'
	Check (-not (Test-Path -LiteralPath $rejected)) "Invalid checksum must fail before installation"
	$fixtureState.Corrupt = ''
	# Both real archive formats are handled on Windows, with a single resolved release.
	$project = New-Project 'Unity project with spaces'
	$native = Join-Path $destination "agentbridge.exe"
	$linux = Join-Path $project "Library/AgentBridge/cli/agentbridge"
	& $installer -Rid win-x64 -Project $project -InstallDirectory $destination -NoPathUpdate
	Check-Bytes $native "new windows CLI"
	Check-Bytes $linux "new linux CLI"
	Check ($fixtureState.LatestCalls -eq 1) "Resolve latest only once for both builds"
	Check (($fixtureState.Uris | Where-Object {$_ -like '*agentbridge-v9.8.7/*'}).Count -eq 4) "Both builds must use the resolved release"
	# A normal invocation from a Unity subfolder also updates the sandbox copy.
	[IO.File]::WriteAllText($linux, "stale linux CLI")
	Push-Location (Join-Path $project "Assets")
	try { & $installer -Version test -Rid win-x64 -InstallDirectory $destination -NoPathUpdate }
	finally { Pop-Location }
	Check-Bytes $linux "new linux CLI"
	& $installer -Version test -Rid win-arm64 -Project $project -InstallDirectory $destination -NoPathUpdate
	Check ($fixtureState.Uris -match 'agentbridge-linux-arm64.tar.gz') "ARM64 Windows must select Linux ARM64"
	# Failure of the second download or extraction cannot leave only Windows updated.
	[IO.File]::WriteAllText($native, "old windows CLI")
	[IO.File]::WriteAllText($linux, "old linux CLI")
	$fixtureState.Corrupt = '*linux*.sha256'
	Expect-Failure { & $installer -Version test -Rid win-x64 -Project $project -InstallDirectory $destination -NoPathUpdate } 'Checksum mismatch*'
	$fixtureState.Corrupt = ''
	$fixtureState.Missing = 'agentbridge-linux-x64.tar.gz'
	Expect-Failure { & $installer -Version test -Rid win-x64 -Project $project -InstallDirectory $destination -NoPathUpdate } 'Failed to download*'
	$fixtureState.Missing = ''
	$validTarball = $tarball
	$validTarHash = $tarHash
	$tarball = Join-Path $testRoot "corrupt.tar.gz"
	[IO.File]::WriteAllText($tarball, "not a gzip archive")
	$sha = [Security.Cryptography.SHA256]::Create()
	try { $tarHash = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($tarball))).Replace('-', '') }
	finally { $sha.Dispose() }
	try { Expect-Failure { & $installer -Version test -Rid win-x64 -Project $project -InstallDirectory $destination -NoPathUpdate } 'Failed to extract*' }
	finally { $tarball = $validTarball; $tarHash = $validTarHash }
	Check-Bytes $native "old windows CLI"
	Check-Bytes $linux "old linux CLI"
	# A failure during publication of Linux rolls Windows back too.
	$held = [IO.File]::Open($linux, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
	try { Expect-Failure { & $installer -Version test -Rid win-x64 -Project $project -InstallDirectory $destination -NoPathUpdate } '*' }
	finally { $held.Dispose() }
	Check-Bytes $native "old windows CLI"
	Check-Bytes $linux "old linux CLI"
	# Exercise the actual Windows image lock with a running AgentBridge CLI process.
	$liveDirectory = Join-Path $testRoot "live-client"
	New-Item -ItemType Directory -Path $liveDirectory | Out-Null
	foreach ($name in 'agentbridge.exe','agentbridge.dll','agentbridge.deps.json','agentbridge.runtimeconfig.json') {
		Copy-Item -LiteralPath (Join-Path $CliDirectory $name) -Destination (Join-Path $liveDirectory $name)
	}
	$liveProject = New-Project 'live-server'
	$start = New-Object Diagnostics.ProcessStartInfo
	$start.FileName = Join-Path $liveDirectory 'agentbridge.exe'
	$start.Arguments = 'queue-server --project "' + $liveProject + '"'
	$start.UseShellExecute = $false
	$start.CreateNoWindow = $true
	$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
	$server = [Diagnostics.Process]::Start($start)
	$queue = Join-Path $liveProject 'Library/AgentBridge/Queue'
	$deadline = [DateTime]::UtcNow.AddSeconds(15)
	while (-not (Test-Path -LiteralPath (Join-Path $queue 'heartbeat'))) {
		Check (-not $server.HasExited -and [DateTime]::UtcNow -lt $deadline) "Fixture CLI server did not start"
		Start-Sleep -Milliseconds 50
	}
	# A different, runnable image: the PE overlay is ignored by the loader, while
	# its bytes prove that new invocations use the replacement instead of the old file.
	$replacement = Join-Path $source.FullName 'agentbridge.exe'
	$replacementBytes = [IO.File]::ReadAllBytes((Join-Path $liveDirectory 'agentbridge.exe')) + [Text.Encoding]::UTF8.GetBytes('installer-new-image')
	[IO.File]::WriteAllBytes($replacement, $replacementBytes)
	$zip = Join-Path $testRoot 'live-replacement.zip'
	[IO.Compression.ZipFile]::CreateFromDirectory($source.FullName, $zip)
	$sha = [Security.Cryptography.SHA256]::Create()
	try { $zipHash = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($zip))).Replace('-', '') }
	finally { $sha.Dispose() }
	& $installer -Version test -Rid win-x64 -Project $liveProject -InstallDirectory $liveDirectory -NoPathUpdate
	Check (-not $server.HasExited) "Updating must not stop the existing CLI process"
	Check ((Get-Item -LiteralPath (Join-Path $liveDirectory 'agentbridge.exe')).Length -eq $replacementBytes.Length) 'Replacement image must be installed'
	$newVersion = & (Join-Path $liveDirectory 'agentbridge.exe') --version
	Check ($LASTEXITCODE -eq 0 -and $newVersion -match '^\d+\.\d+\.\d+$') 'New CLI invocations must run after replacement'
	[IO.File]::WriteAllText((Join-Path $queue 'Requests/after-update.json'), '{"Id":"after-update","Op":"list"}')
	$deadline = [DateTime]::UtcNow.AddSeconds(10)
	$reply = Join-Path $queue 'Replies/after-update.json'
	while (-not (Test-Path -LiteralPath $reply)) {
		Check (-not $server.HasExited -and [DateTime]::UtcNow -lt $deadline) "Existing CLI stopped serving after update"
		Start-Sleep -Milliseconds 50
	}
	Check ((Get-Content -LiteralPath $reply -Raw | ConvertFrom-Json).Ok) "Existing CLI must still complete requests"
	[IO.File]::WriteAllText((Join-Path $queue 'Requests/stop.json'), '{"Id":"stop","Op":"shutdown"}')
	Check ($server.WaitForExit(10000)) "Fixture CLI server did not shut down"
	"installer_regression=PASS (Windows+Linux, pinned release, missing modules, checksum/download/extraction failures, rollback, running CLI replacement)"
} finally {
	if ($server) { if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit() }; $server.Dispose() }
	$env:PSModulePath = $originalModulePath
	$prefix = [IO.Path]::Combine([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), 'agentbridge-installer-test-')
	if (-not [IO.Path]::GetFullPath($testRoot).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe fixture cleanup path" }
	Remove-Item -LiteralPath $testRoot -Recurse -Force
}
