#Requires -Version 5.1
# Upgrade a sandboxed installation from a real release package to a new one:
# real payloads, the real installer control flow and the real server smoke
# check (tbc-revit-mcp.exe --help). Profile env vars (USERPROFILE, APPDATA,
# LOCALAPPDATA) are redirected to a sandbox under $Sandbox\profile for the whole
# run, so a function that ignores its fixture path cannot reach real user data.
#
# -OldLayout:
#   Legacy   - an upstream pre-rename %LOCALAPPDATA%\RvtMcp\ profile (old package
#              is an upstream rvt-mcp release); the installer moves it to
#              %LOCALAPPDATA%\NourRouas\tbc-revit-mcp\.
#   Upstream - an upstream %LOCALAPPDATA%\Bimwright\rvt-mcp\ profile (old package
#              is an upstream rvt-mcp release); the installer leaves it and the
#              client's rvt-mcp entry untouched and adds tbc-revit-mcp.
#   Current  - a tbc-revit-mcp profile (old package is a tbc-revit-mcp
#              release); upgraded in place and its client entry repointed.
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$OldPackage,
    [Parameter(Mandatory=$true)][string]$NewPackage,
    [Parameter(Mandatory=$true)][string]$Sandbox,
    [Parameter(Mandatory=$true)][string]$ResultPath,
    [ValidateSet('Legacy','Upstream','Current')][string]$OldLayout = 'Legacy'
)
$ErrorActionPreference='Stop'
$OldPackage=(Resolve-Path -LiteralPath $OldPackage).Path
$NewPackage=(Resolve-Path -LiteralPath $NewPackage).Path
$Sandbox=[IO.Path]::GetFullPath($Sandbox)
if (Test-Path -LiteralPath $Sandbox) { throw 'Use a new sandbox directory.' }
$sandboxUserProfile=Join-Path $Sandbox 'profile\userprofile'
$sandboxAppData=Join-Path $Sandbox 'profile\appdata'
$sandboxLocalAppData=Join-Path $Sandbox 'profile\localappdata'
New-Item -ItemType Directory -Path $sandboxUserProfile,$sandboxAppData,$sandboxLocalAppData -Force | Out-Null
$savedUserProfile=$env:USERPROFILE
$savedAppData=$env:APPDATA
$savedLocalAppData=$env:LOCALAPPDATA
$env:USERPROFILE=$sandboxUserProfile
$env:APPDATA=$sandboxAppData
$env:LOCALAPPDATA=$sandboxLocalAppData
if ($env:USERPROFILE -ne $sandboxUserProfile -or $env:APPDATA -ne $sandboxAppData -or $env:LOCALAPPDATA -ne $sandboxLocalAppData) {
    throw 'Profile env redirection failed'
}
$installer=Join-Path $NewPackage 'install.ps1'
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($fn in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
$start=$ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('if (-not $Years -or') } | Select-Object -First 1
$main=[scriptblock]::Create((Get-Content $installer -Raw).Substring($start.Extent.StartOffset))

# Only these OS boundaries differ from a real user installation. The real
# Revit process and the machine-wide add-in folders are never touched.
function Get-AddinsRoot([int]$year) { Join-Path $Sandbox "addins/$year" }
function Get-MachineAddinsRoot([int]$year) { Join-Path $Sandbox "machine/$year" }
function Get-Process { param($Name,$ErrorAction) } # Isolated host has no Revit process.
try {
$oldManifest=Get-Content (Join-Path $OldPackage 'manifest.json') -Raw | ConvertFrom-Json
Assert-SetupManifest -Root $OldPackage -Manifest $oldManifest
$oldExeName=Split-Path -Leaf (Find-ServerSourceExe (Join-Path $OldPackage 'server'))
$newRoot=Join-Path $sandboxLocalAppData 'NourRouas\tbc-revit-mcp'
# Seed the pre-upgrade layout, including a retained version and the current
# server used by recent releases. All files are inside the sandbox profile.
$oldRoot=switch ($OldLayout) {
    'Legacy'   { Join-Path $sandboxLocalAppData 'RvtMcp' }
    'Upstream' { Join-Path $sandboxLocalAppData 'Bimwright\rvt-mcp' }
    'Current'  { $newRoot }
}
$oldServerFolder=if ($OldLayout -eq 'Legacy') { 'rvt/server' } else { 'server' }
$oldServer=Join-Path $oldRoot "$oldServerFolder/$($oldManifest.version)"
New-Item -ItemType Directory -Path (Split-Path -Parent $oldServer) | Out-Null
Copy-Item -LiteralPath (Join-Path $OldPackage 'server') -Destination $oldServer -Recurse
Copy-Item -LiteralPath (Join-Path $OldPackage 'server') -Destination (Join-Path $oldRoot "$oldServerFolder/current") -Recurse
Set-Content -LiteralPath (Join-Path $oldRoot 'rvtmcp.config.json') '{"toolsets":["query"],"custom":true}'
New-Item -ItemType Directory -Path (Join-Path $oldRoot 'baked/personal') | Out-Null
Set-Content -LiteralPath (Join-Path $oldRoot 'baked/personal/code.cs') 'return "preserved personal source";'
Set-Content -LiteralPath (Join-Path $oldRoot 'baked/registry.json') '{"personal":{"source":"personal/code.cs"}}'
Set-Content -LiteralPath (Join-Path $oldRoot 'bake-audit.jsonl') '{"event":"fixture-preserved"}'
# Installer preservation is byte-level: these are fixture bytes, not a runnable
# SQLite database or a claim that a legacy baked tool executes in Revit.
foreach ($name in 'bake.db', 'bake.db-wal', 'bake.db-shm') {
    [IO.File]::WriteAllBytes((Join-Path $oldRoot $name), [byte[]](0, 1, 128, 255))
}
$dataHashes=@(Get-ChildItem -LiteralPath $oldRoot -File -Recurse | Where-Object { $_.FullName -notlike '*\server\*' } | ForEach-Object {
    @{path=$_.FullName.Substring($oldRoot.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
# Upstream releases register 'rvt-mcp'; tbc-revit-mcp releases register 'tbc-revit-mcp'.
$oldEntryKey=if ($OldLayout -eq 'Current') { 'tbc-revit-mcp' } else { 'rvt-mcp' }
$oldEnv=if ($OldLayout -eq 'Current') { @{TBC_REVIT_MCP_TARGET='2024'} } else { @{BIMWRIGHT_TARGET='2024'} }
$clientConfig=Join-Path $sandboxUserProfile '.cursor/mcp.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $clientConfig) | Out-Null
@{mcpServers=@{$oldEntryKey=@{command=(Join-Path $oldServer $oldExeName);args=@('--read-only','--toolsets','query');env=$oldEnv};other=@{command='other.exe'}}} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $clientConfig
$clientHash=(Get-FileHash -LiteralPath $clientConfig).Hash
$oldEntryJson=(Get-Content -LiteralPath $clientConfig -Raw | ConvertFrom-Json).mcpServers.$oldEntryKey | ConvertTo-Json -Depth 6 -Compress
foreach ($year in 2022..2027) {
    $root=Get-AddinsRoot $year
    Expand-Archive -LiteralPath (Join-Path $OldPackage "plugins/RvtMcp.Plugin.R$($year-2000).zip") -DestinationPath "$root/RvtMcp"
    Move-Item -LiteralPath "$root/RvtMcp/RvtMcp.R$($year-2000).addin" -Destination $root
}
$oldServerHash=(Get-FileHash (Join-Path $oldServer $oldExeName)).Hash

$SourceDir=$NewPackage; $pluginSourceDir=Join-Path $NewPackage 'plugins'; $serverSourceDir=Join-Path $NewPackage 'server'
$manifest=Get-Content (Join-Path $NewPackage 'manifest.json') -Raw | ConvertFrom-Json
$setupVersion=[string]$manifest.version
$newExeName=Split-Path -Leaf (Find-ServerSourceExe $serverSourceDir)
$ServerInstallRoot=Join-Path $newRoot 'server\current'
$Years=@(2022..2027); $Client='cursor'; $Uninstall=$false; $WireClient=$null
& $main

$pluginResults=@()
foreach ($year in $Years) {
    $root=Get-AddinsRoot $year
    $reference=Join-Path $Sandbox "reference/$year"
    Expand-Archive -LiteralPath (Join-Path $NewPackage "plugins/RvtMcp.Plugin.R$($year-2000).zip") -DestinationPath $reference
    $files=@(Get-ChildItem $reference -File -Recurse)
    foreach ($file in $files) {
        $relative=$file.FullName.Substring($reference.Length+1)
        $target=if ($relative.EndsWith('.addin')) { Join-Path $root $relative } else { Join-Path "$root/RvtMcp" $relative }
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Installed payload differs: $target" }
    }
    $pluginResults += @{year=$year;filesVerified=$files.Count;passed=$true}
}
# Legacy is moved into the new root; Upstream stays where it was; Current is in place.
$dataRoot=if ($OldLayout -eq 'Upstream') { $oldRoot } else { $newRoot }
$keptServer=Join-Path $dataRoot "server/$($oldManifest.version)/$oldExeName"
if ((Get-FileHash -LiteralPath $keptServer).Hash -ne $oldServerHash) { throw 'Legacy server version changed' }
if ($OldLayout -eq 'Legacy' -and (Test-Path -LiteralPath $oldRoot)) { throw 'Old product root survived relocation' }
foreach ($file in $dataHashes) {
    if ((Get-FileHash -LiteralPath (Join-Path $dataRoot $file.path)).Hash -ne $file.sha256) { throw "Personal data changed: $($file.path)" }
}
if ($OldLayout -eq 'Upstream' -and (Test-Path -LiteralPath (Join-Path $newRoot 'baked'))) { throw 'Installer copied upstream data; the runtime owns that copy' }
$installedExe=Join-Path $ServerInstallRoot $newExeName
if ((Get-FileHash $installedExe).Hash -ne (Get-FileHash (Join-Path $serverSourceDir $newExeName)).Hash) { throw 'New server mismatch' }
$client=Get-Content -LiteralPath $clientConfig -Raw | ConvertFrom-Json
$entry=$client.mcpServers.'tbc-revit-mcp'
if (-not $entry -or [IO.Path]::GetFullPath($entry.command) -ne [IO.Path]::GetFullPath($installedExe)) { throw 'Client tbc-revit-mcp entry does not point at the current server' }
if ($OldLayout -eq 'Current') {
    if (($entry.args -join ',') -ne '--read-only,--toolsets,query' -or $entry.env.TBC_REVIT_MCP_TARGET -ne '2024') { throw 'Client args/env changed' }
} elseif (($client.mcpServers.'rvt-mcp' | ConvertTo-Json -Depth 6 -Compress) -ne $oldEntryJson) {
    throw 'Upstream rvt-mcp client entry changed'
}
if ($client.mcpServers.other.command -ne 'other.exe') { throw 'Unrelated client entry changed' }
if ((Get-FileHash -LiteralPath "$clientConfig.bak").Hash -ne $clientHash) { throw 'Client config backup differs' }
$report=[ordered]@{testedAtUtc=(Get-Date).ToUniversalTime().ToString('o');fromVersion=$oldManifest.version;toVersion=$manifest.version;
    installerSha256=(Get-FileHash $installer).Hash;oldPackageCommit=$oldManifest.commit;candidateCommit=$manifest.commit;candidateDirty=$manifest.dirty;
    isolation='Real payloads, real installer control flow and real server smoke check (--help); sandbox paths, a fixture Cursor config and simulated closed host; no real deployment or client/native acceptance.';
    oldLayout=$OldLayout;pluginResults=$pluginResults;serverVerified=$true;legacyServerKept=$true;dataRootRelocated=($OldLayout -eq 'Legacy');upstreamDataUntouched=($OldLayout -eq 'Upstream');customConfigPreserved=$true;personalDataPreserved=$true;
    preservedFiles=$dataHashes;clientPathRepointed=($OldLayout -eq 'Current');clientEntryAdded=($OldLayout -ne 'Current');upstreamClientEntryUntouched=($OldLayout -ne 'Current');clientArgsEnvPreserved=$true;clientBackupPreserved=$true;smokeCheck='passed';passed=$true}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
$report | ConvertTo-Json -Depth 10
} finally {
    $env:USERPROFILE=$savedUserProfile
    $env:APPDATA=$savedAppData
    $env:LOCALAPPDATA=$savedLocalAppData
}
