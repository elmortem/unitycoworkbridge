$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('bridge-plugin-eol-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build-plugin.ps1') -Destination (Join-Path $fixture 'scripts/build-plugin.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'unity-bridge-plugin') -Destination $fixture -Recurse
$build = Join-Path $fixture 'scripts/build-plugin.ps1'
$source = Join-Path $fixture 'unity-bridge-plugin/skills/unity-bridge/SKILL.md'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$canonical = $utf8.GetString([IO.File]::ReadAllBytes($source)).Replace("`r`n", "`n")
# Mixed line endings reproduce an editor changing only part of an LF file.
$mixed = $canonical.Replace("`n", "`r`n")
$first = $mixed.IndexOf("`r`n")
$mixed = $mixed.Remove($first, 1)
[IO.File]::WriteAllBytes($source, $utf8.GetBytes($mixed))
function Expect-Failure([string]$Message) {
	try { & $build -SkipVersionCheck -ValidateOnly | Out-Null }
	catch { if ($_.Exception.Message.Contains($Message)) { return }; throw }
	throw "Expected validation failure: $Message"
}
Expect-Failure 'Plugin source has CRLF'
if ($utf8.GetString([IO.File]::ReadAllBytes($source)) -ne $mixed) { throw 'Validation modified the source' }
& $build -SkipVersionCheck | Out-Null
if ($utf8.GetString([IO.File]::ReadAllBytes($source)) -cne $canonical) { throw 'Build did not normalize to exact LF content' }
& $build -SkipVersionCheck -ValidateOnly | Out-Null
[IO.File]::AppendAllText($source, "Changed content`n", $utf8)
Expect-Failure 'ZIP content differs from source'
Write-Output 'PASS: mixed CRLF/LF rejected without writes; build normalizes; strict validation accepts LF and rejects changed content'
Write-Output "Fixture: $fixture"
