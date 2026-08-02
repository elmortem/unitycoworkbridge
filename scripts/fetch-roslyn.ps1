$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packages = @(
	[pscustomobject]@{ Id = "microsoft.codeanalysis.common"; Version = "4.12.0"; Entry = "lib/netstandard2.0/Microsoft.CodeAnalysis.dll"; File = "Microsoft.CodeAnalysis.dll" }
	[pscustomobject]@{ Id = "microsoft.codeanalysis.csharp"; Version = "4.12.0"; Entry = "lib/netstandard2.0/Microsoft.CodeAnalysis.CSharp.dll"; File = "Microsoft.CodeAnalysis.CSharp.dll" }
	[pscustomobject]@{ Id = "system.collections.immutable"; Version = "8.0.0"; Entry = "lib/netstandard2.0/System.Collections.Immutable.dll"; File = "System.Collections.Immutable.dll" }
	[pscustomobject]@{ Id = "system.reflection.metadata"; Version = "8.0.0"; Entry = "lib/netstandard2.0/System.Reflection.Metadata.dll"; File = "System.Reflection.Metadata.dll" }
	[pscustomobject]@{ Id = "system.runtime.compilerservices.unsafe"; Version = "6.0.0"; Entry = "lib/netstandard2.0/System.Runtime.CompilerServices.Unsafe.dll"; File = "System.Runtime.CompilerServices.Unsafe.dll" }
)

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$targetDirectory = Join-Path $repositoryRoot "AgentBridgeUnity/Packages/com.elmortem.agentbridge/Roslyn~"
$temporaryDirectory = Join-Path $env:TEMP "roslyn-fetch"

if (-not (Test-Path $targetDirectory)) {
	New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
}

if (Test-Path $temporaryDirectory) {
	Remove-Item -Recurse -Force $temporaryDirectory
}

New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null

$hashes = @{}

try {
	foreach ($package in $packages) {
		$nupkgName = "$($package.Id).$($package.Version).nupkg"
		$nupkgPath = Join-Path $temporaryDirectory $nupkgName
		$uri = "https://api.nuget.org/v3-flatcontainer/$($package.Id)/$($package.Version)/$nupkgName"

		Write-Host "Downloading $($package.Id) $($package.Version)"
		Invoke-WebRequest -Uri $uri -UseBasicParsing -OutFile $nupkgPath

		$archive = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
		try {
			$entry = $null
			foreach ($candidate in $archive.Entries) {
				if ($candidate.FullName.Replace("\", "/").Equals($package.Entry, [StringComparison]::OrdinalIgnoreCase)) {
					$entry = $candidate
					break
				}
			}

			if ($null -eq $entry) {
				[Console]::Error.WriteLine("Entry '$($package.Entry)' was not found in package '$($package.Id)' $($package.Version).")
				exit 1
			}

			$destination = Join-Path $targetDirectory $package.File
			[System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
		} finally {
			$archive.Dispose()
		}

		$destination = Join-Path $targetDirectory $package.File
		$hashes[$package.Id] = (Get-FileHash -Algorithm SHA256 $destination).Hash.ToLowerInvariant()
	}
} finally {
	if (Test-Path $temporaryDirectory) {
		Remove-Item -Recurse -Force $temporaryDirectory
	}
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("{")
$lines.Add("  `"packages`": [")

for ($index = 0; $index -lt $packages.Count; $index++) {
	$package = $packages[$index]
	$separator = if ($index -eq $packages.Count - 1) { "" } else { "," }

	$lines.Add("    {")
	$lines.Add("      `"id`": `"$($package.Id)`",")
	$lines.Add("      `"version`": `"$($package.Version)`",")
	$lines.Add("      `"entry`": `"$($package.Entry)`",")
	$lines.Add("      `"file`": `"$($package.File)`",")
	$lines.Add("      `"sha256`": `"$($hashes[$package.Id])`"")
	$lines.Add("    }$separator")
}

$lines.Add("  ]")
$lines.Add("}")

$lockPath = Join-Path $targetDirectory "roslyn.lock.json"
[System.IO.File]::WriteAllText($lockPath, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

$notices = New-Object System.Collections.Generic.List[string]
$notices.Add("# Third-party notices")
$notices.Add("")
$notices.Add("The assemblies in this folder are redistributed unmodified from NuGet. Each is licensed under the MIT License.")
$notices.Add("")

foreach ($package in $packages) {
	$notices.Add("- ``$($package.File)`` - $($package.Id) $($package.Version) - https://www.nuget.org/packages/$($package.Id)/$($package.Version)")
}

$notices.Add("")
$notices.Add("## MIT License")
$notices.Add("")
$notices.Add("Copyright (c) .NET Foundation and Contributors")
$notices.Add("")
$notices.Add("Permission is hereby granted, free of charge, to any person obtaining a copy")
$notices.Add("of this software and associated documentation files (the ""Software""), to deal")
$notices.Add("in the Software without restriction, including without limitation the rights")
$notices.Add("to use, copy, modify, merge, publish, distribute, sublicense, and/or sell")
$notices.Add("copies of the Software, and to permit persons to whom the Software is")
$notices.Add("furnished to do so, subject to the following conditions:")
$notices.Add("")
$notices.Add("The above copyright notice and this permission notice shall be included in all")
$notices.Add("copies or substantial portions of the Software.")
$notices.Add("")
$notices.Add("THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR")
$notices.Add("IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,")
$notices.Add("FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE")
$notices.Add("AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER")
$notices.Add("LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,")
$notices.Add("OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE")
$notices.Add("SOFTWARE.")

$noticesPath = Join-Path $targetDirectory "THIRD-PARTY-NOTICES.md"
[System.IO.File]::WriteAllText($noticesPath, ($notices -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Vendored $($packages.Count) assemblies into $targetDirectory"
