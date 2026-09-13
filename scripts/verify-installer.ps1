$ErrorActionPreference = "Stop"
$installer = Join-Path $PSScriptRoot "install-agentbridge.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agentbridge-installer-test-" + [Guid]::NewGuid().ToString("N"))
$originalModulePath = $env:PSModulePath
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$source = New-Item -ItemType Directory -Path (Join-Path $testRoot "source")
	[IO.File]::WriteAllText((Join-Path $source.FullName "agentbridge.exe"), "installer regression fixture")
	$fixture = Join-Path $testRoot "fixture.zip"
	[IO.Compression.ZipFile]::CreateFromDirectory($source.FullName, $fixture)
	# Fixed expected digest is computed independently before hiding optional modules.
	$expected = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
	$env:PSModulePath = Join-Path $testRoot "no-modules"
	# Shadow commands as well: some PowerShell hosts restore built-in module paths.
	function Get-FileHash { throw "Get-FileHash is unavailable in this regression scenario" }
	function Expand-Archive { throw "Expand-Archive is unavailable in this regression scenario" }
	function Invoke-WebRequest {
		param($Uri, $OutFile, [switch]$UseBasicParsing)
		if (-not $UseBasicParsing) { throw "Basic parsing is required" }
		if ($Uri.EndsWith(".sha256")) {
			[IO.File]::WriteAllText($OutFile, "$expected  agentbridge-win-x64.zip")
		} else {
			[IO.File]::Copy($fixture, $OutFile)
		}
	}
	$destination = Join-Path $testRoot "installed"
	& $installer -Version "test" -Rid win-x64 -InstallDirectory $destination -NoPathUpdate
	if ([IO.File]::ReadAllText((Join-Path $destination "agentbridge.exe")) -ne "installer regression fixture") {
		throw "Installed bytes differ"
	}
	$expected = "0" * 64
	$rejectedDestination = Join-Path $testRoot "rejected"
	$rejected = $false
	try {
		& $installer -Version "test" -Rid win-x64 -InstallDirectory $rejectedDestination -NoPathUpdate
	} catch {
		if ($_.Exception.Message -notlike "Checksum mismatch*") { throw }
		$rejected = $true
	}
	if (-not $rejected -or (Test-Path $rejectedDestination)) { throw "Bad checksum was not rejected before installation" }
	"installer_regression=PASS (missing modules, ZIP installation, checksum rejection)"
} finally {
	$env:PSModulePath = $originalModulePath
	Remove-Item -LiteralPath $testRoot -Recurse -Force
}
