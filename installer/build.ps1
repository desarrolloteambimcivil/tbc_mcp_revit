<#
.SYNOPSIS
  Builds installer\output\TbcRevitMcp_Setup_<version>.exe from a client setup ZIP.
.DESCRIPTION
  The setup ZIP (scripts\package-client-setup.ps1, or the CI artifact) is the single source of
  what gets installed. This script checks it against its .sha256 sidecar when present, extracts
  it to installer\payload, reads the version from its manifest.json and compiles
  tbc-revit-mcp.iss with Inno Setup 6.
.PARAMETER SetupZip
  Path to TbcRevitMcp.Setup-v<version>-win-x64.zip. Defaults to the newest one in build\client-setup.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build.ps1 -SetupZip build\client-setup\TbcRevitMcp.Setup-v1.3.0-win-x64.zip
#>
[CmdletBinding()]
param([string]$SetupZip)

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerDir
$payloadDir = Join-Path $installerDir 'payload'

if (-not $SetupZip) {
    $SetupZip = Get-ChildItem (Join-Path $repoRoot 'build\client-setup') -Filter 'TbcRevitMcp.Setup-v*-win-x64.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $SetupZip) { throw 'No setup ZIP found in build\client-setup. Pass -SetupZip or run scripts\package-client-setup.ps1 first.' }
}
$SetupZip = (Resolve-Path $SetupZip).Path

$sidecar = "$SetupZip.sha256"
if (Test-Path $sidecar) {
    $expected = ((Get-Content $sidecar -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash $SetupZip -Algorithm SHA256).Hash
    if ($actual -ne $expected.ToUpperInvariant()) { throw "Checksum mismatch for $SetupZip" }
    Write-Host "[build] checksum ok"
}

if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
Expand-Archive $SetupZip -DestinationPath $payloadDir
foreach ($required in 'install.ps1', 'uninstall.ps1', 'manifest.json', 'server', 'plugins') {
    if (-not (Test-Path (Join-Path $payloadDir $required))) { throw "Setup ZIP is missing $required" }
}

$version = (Get-Content (Join-Path $payloadDir 'manifest.json') -Raw | ConvertFrom-Json).version
# Inno's VersionInfoVersion only takes numbers, so prerelease suffixes are rejected here.
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unsupported setup version '$version' (expected x.y.z)." }

# Inno Setup is not always in Program Files: winget installs it per user under LOCALAPPDATA.
$isccOnPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
$candidates = @(
    $env:INNO_SETUP_DIR
    if ($isccOnPath) { Split-Path -Parent $isccOnPath }
    "${env:ProgramFiles(x86)}\Inno Setup 6"
    "$env:ProgramFiles\Inno Setup 6"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6"
) | Where-Object { $_ }
$iscc = $candidates | ForEach-Object { Join-Path $_ 'ISCC.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup' }

Write-Host "[build] version $version, compiler $iscc"
& $iscc "/DAppVersion=$version" (Join-Path $installerDir 'tbc-revit-mcp.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$exe = Join-Path $installerDir "output\TbcRevitMcp_Setup_$version.exe"
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$exe.sha256" -Value "$hash *$(Split-Path -Leaf $exe)" -Encoding ascii -NoNewline
Write-Host "[build] $exe"
Write-Host "[build] sha256 $hash"
