#Requires -Version 5.1
# No Pester dependency. Execute production functions against isolated temporary files.
# Profile env vars (USERPROFILE, APPDATA, LOCALAPPDATA) are redirected to a
# sandbox under the test root for the whole run, so a function that ignores its
# fixture path cannot reach real user data.
[CmdletBinding()]
param([string]$ResultPath, [string]$TestRootParent = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $TestRootParent ('rvt-uninstaller-tests-' + [guid]::NewGuid().ToString('N'))
$sandboxUserProfile = Join-Path $testRoot 'profile\userprofile'
$sandboxAppData = Join-Path $testRoot 'profile\appdata'
$sandboxLocalAppData = Join-Path $testRoot 'profile\localappdata'
New-Item -ItemType Directory -Path $sandboxUserProfile, $sandboxAppData, $sandboxLocalAppData -Force | Out-Null
$savedUserProfile = $env:USERPROFILE
$savedAppData = $env:APPDATA
$savedLocalAppData = $env:LOCALAPPDATA
$env:USERPROFILE = $sandboxUserProfile
$env:APPDATA = $sandboxAppData
$env:LOCALAPPDATA = $sandboxLocalAppData
if ($env:USERPROFILE -ne $sandboxUserProfile -or $env:APPDATA -ne $sandboxAppData -or $env:LOCALAPPDATA -ne $sandboxLocalAppData) {
    throw 'Profile env redirection failed'
}
$uninstaller = Join-Path $PSScriptRoot '../../scripts/uninstall-all.ps1'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $uninstaller), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
# Dot-source only function definitions; the script body prompts and calls exit.
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
# Step4 must take the fixture root explicitly; without it the function falls
# back to the real profile and the tests would delete user data.
if (-not (Get-Command Invoke-Step4-Discovery).Parameters.ContainsKey('Root')) {
    throw 'Invoke-Step4-Discovery does not accept -Root; refusing to run'
}
# The main sweep without its confirmation prompt's caller-facing exit: from the
# step4 plan through the try/finally that runs the steps (the trailing `exit`
# is excluded so the test host keeps running).
$mainStart = $ast.EndBlock.Statements | Where-Object { $_.Extent.Text.StartsWith('$step4Plan =') } | Select-Object -First 1
$mainEnd = $ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] } | Select-Object -Last 1
if (-not $mainStart -or -not $mainEnd) { throw 'Could not locate the uninstall main body' }
$mainBody = [scriptblock]::Create((Get-Content -LiteralPath $uninstaller -Raw).Substring($mainStart.Extent.StartOffset, $mainEnd.Extent.EndOffset - $mainStart.Extent.StartOffset))
$results = New-Object System.Collections.Generic.List[object]
$script:handled = @()
$script:skipped = @()
$script:failed  = @()
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Test([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{name=$Name;passed=$true}); Write-Host "PASS $Name" }
    catch { $results.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.Message}); Write-Host "FAIL $Name : $_" }
}
$discoveryNames = @('revit-2024.json','revit-2027.json','revit-2024-12345.json','revit-2027-23456.json','revit-2027-34567.json')
$removableNames = @('rvt','server','spill') + $discoveryNames
$keptNames = @('rvtmcp.config.json','locales','bake.db','bake.db-wal','baked','usage.jsonl','bake-audit.jsonl','firm-profiles','shared-parameters.txt','.migrated-from-bimwright-rvt-mcp','logs','revit-mcp.log','mcp-calls.jsonl','send-code-journal.jsonl','journal','captures','mcp-calls.version','notes.txt','custom')
function New-RootFixture {
    $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    # Both server layouts: nested rvt\server (legacy root) and flat server\
    # (current root) must be swept.
    New-Item -ItemType Directory -Path "$root/locales", "$root/baked", "$root/firm-profiles", "$root/logs", "$root/rvt/server/0.6.2", "$root/server/current", "$root/spill", "$root/journal", "$root/captures", "$root/custom" -Force | Out-Null
    Set-Content "$root/rvtmcp.config.json" '{}'
    Set-Content "$root/locales/strings.vi.json" '{}'
    Set-Content "$root/bake.db" 'db'
    Set-Content "$root/bake.db-wal" 'wal'
    Set-Content "$root/baked/tool.cs" 'cs'
    Set-Content "$root/usage.jsonl" '{}'
    Set-Content "$root/bake-audit.jsonl" '{}'
    Set-Content "$root/firm-profiles/f.json" '{}'
    Set-Content "$root/shared-parameters.txt" 'p'
    Set-Content "$root/.migrated-from-bimwright-rvt-mcp" 'm'
    Set-Content "$root/revit-mcp.log" 'l'
    Set-Content "$root/mcp-calls.jsonl" 'l'
    Set-Content "$root/send-code-journal.jsonl" 'l'
    Set-Content "$root/logs/a.log" 'l'
    Set-Content "$root/rvt/server/0.6.2/rvt-mcp.exe" 'exe'
    Set-Content "$root/server/current/tbc-revit-mcp.exe" 'exe'
    foreach ($name in $discoveryNames) { Set-Content -LiteralPath (Join-Path $root $name) '{}' }
    Set-Content "$root/spill/x.json" '{}'
    Set-Content "$root/journal/j.json" '{}'
    Set-Content "$root/captures/c.png" 'png'
    Set-Content "$root/mcp-calls.version" 'v'
    Set-Content "$root/notes.txt" 'n'
    Set-Content "$root/custom/x.txt" 'x'
    return $root
}
function Invoke-Step4 {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param([string]$Root, [switch]$KeepLogs, [switch]$Purge)
    Invoke-Step4-Discovery -Root $Root
}
function Invoke-UninstallMain {
    [CmdletBinding(SupportsShouldProcess=$true)]
    param([switch]$Purge, [switch]$KeepLogs)
    $Yes = $true
    # Steps 1-2 reach Revit add-ins and the global dotnet tool list; they are
    # exercised elsewhere. Only the data-root selection runs for real here.
    function Invoke-Step1-Plugin { $script:handled += 'step1-plugin' }
    function Invoke-Step2-DotnetTool { $script:skipped += 'step2-dotnet-tool' }
    & $mainBody
}
function Assert-RootContents($Root, [string[]]$Kept) {
    $actual = @((Get-ChildItem -LiteralPath $Root -Force | Select-Object -ExpandProperty Name) | Sort-Object)
    $expected = @($Kept | Sort-Object)
    Assert (($actual -join ',') -eq ($expected -join ',')) ("Root contents differ. Expected [{0}] got [{1}]" -f ($expected -join ','), ($actual -join ','))
}
try {
    Test 'Uninstall default removes only server copies, discovery and spill' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert-RootContents $root $keptNames
        Assert ($script:handled -contains 'step4-discovery') 'step4 not reported'
        Assert ($script:handled -notcontains 'step5-toolbaker (contained)') 'Kept ToolBaker data reported as removed'
    }
    Test 'Uninstall -KeepLogs matches default (logs already kept)' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -KeepLogs
        Assert-RootContents $root $keptNames
    }
    Test 'Uninstall -Purge removes the whole root including ToolBaker data' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -Purge
        Assert (-not (Test-Path $root)) 'Root still present after -Purge'
        Assert ($script:handled -contains 'step5-toolbaker (contained)') 'Removed ToolBaker data not reported'
    }
    Test 'Uninstall -Purge -KeepLogs keeps only logs' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -Purge -KeepLogs
        Assert-RootContents $root (@('logs','revit-mcp.log','mcp-calls.jsonl','send-code-journal.jsonl','usage.jsonl','bake-audit.jsonl'))
        Assert ($script:handled -contains 'step5-toolbaker (contained)') 'Removed ToolBaker data not reported'
    }
    Test 'Uninstall -WhatIf removes nothing' {
        $root = New-RootFixture
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -WhatIf
        Assert-RootContents $root ($keptNames + $removableNames)
    }
    Test 'Uninstall removes both descriptor formats from the product root without touching siblings' {
        $fam = Join-Path $testRoot "fam-$([guid]::NewGuid().ToString('N'))"
        $root = "$fam/NourRouas/tbc-revit-mcp"
        New-Item -ItemType Directory -Path $root, "$fam/Bimwright/rvt-mcp", "$fam/Bimwright/ipt-mcp" -Force | Out-Null
        Set-Content -LiteralPath "$fam/Bimwright/ipt-mcp/revit-2027-23456.json" '{}'
        Set-Content -LiteralPath "$fam/Bimwright/rvt-mcp/revit-2027-23456.json" '{}'
        foreach ($name in $discoveryNames) { Set-Content -LiteralPath (Join-Path $root $name) '{}' }
        Set-Content -LiteralPath "$root/rvtmcp.config.json" '{}'
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert-RootContents $root @('rvtmcp.config.json')
        Assert ($script:handled -contains 'step4-discovery' -and $script:failed.Count -eq 0) 'Cleanup not reported as successful'
        Assert (Test-Path -LiteralPath "$fam/Bimwright/ipt-mcp/revit-2027-23456.json") 'Sibling discovery file removed'
        Assert (Test-Path -LiteralPath "$fam/Bimwright/rvt-mcp/revit-2027-23456.json") 'Upstream discovery file removed'
    }
    Test 'Product data root is NourRouas\tbc-revit-mcp; upstream roots are reported, not swept' {
        Assert ((Get-ProductDataRoot -LocalAppData 'C:\x') -eq 'C:\x\NourRouas\tbc-revit-mcp') "Unexpected product root: $(Get-ProductDataRoot -LocalAppData 'C:\x')"
        $upstream = @(Get-UpstreamDataRoots -LocalAppData 'C:\x')
        Assert (($upstream -join ';') -eq 'C:\x\Bimwright\rvt-mcp;C:\x\RvtMcp') "Unexpected upstream roots: $($upstream -join ';')"
        Assert ($upstream -notcontains (Get-ProductDataRoot -LocalAppData 'C:\x')) 'Product root listed as upstream'
    }
    Test 'Uninstall keeps lookalike filenames and directories instead of using a broad discovery glob' {
        $root = New-RootFixture
        $lookalikes = @('revit-2027-notes.json','revit-2027-12345.json.bak','revit-202-12345.json','revit-2027-.json','revit-2027-12345-67890.json','revit-2027-12345.jsonl','revit-2027-45678.json')
        foreach ($name in $lookalikes) {
            if ($name -eq 'revit-2027-45678.json') {
                New-Item -ItemType Directory -Path (Join-Path $root $name) | Out-Null
                Set-Content -LiteralPath (Join-Path $root "$name/keep.txt") 'keep'
            } else { Set-Content -LiteralPath (Join-Path $root $name) 'keep' }
        }
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert-RootContents $root ($keptNames + $lookalikes)
        Assert ((Get-Content -LiteralPath "$root/revit-2027-45678.json/keep.txt" -Raw).Trim() -eq 'keep') 'Lookalike directory contents removed'
    }
    Test 'Uninstall removes a root containing only legacy and per-instance descriptors' {
        $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root | Out-Null
        foreach ($name in $discoveryNames) { Set-Content -LiteralPath (Join-Path $root $name) '{}' }
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert (-not (Test-Path -LiteralPath $root)) 'Descriptor-only root survived cleanup'
    }
    Test 'Uninstall missing root skips step4' {
        $root = Join-Path $testRoot 'missing-root'
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert ($script:skipped -contains 'step4-discovery') 'Missing root not reported as skipped'
        Assert (-not (Test-Path $root)) 'Missing root was created'
    }
    Test 'Uninstall root holding only removable items is removed' {
        $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path "$root/rvt/server/0.6.2", "$root/spill" -Force | Out-Null
        Set-Content "$root/rvt/server/0.6.2/rvt-mcp.exe" 'exe'
        Set-Content "$root/spill/x.json" '{}'
        Set-Content "$root/revit-2027.json" '{}'
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert (-not (Test-Path $root)) 'Empty root still present'
    }
    Test 'Uninstall -Purge -KeepLogs deletes exactly the named entry' {
        $root = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($root) | Out-Null
        [IO.File]::WriteAllText((Join-Path $root 'a.log'), 'l')
        [IO.File]::WriteAllText((Join-Path $root 'a[.]log'), 'x')
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root -Purge -KeepLogs
        Assert (Test-Path -LiteralPath (Join-Path $root 'a.log')) 'a.log was deleted'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'a[.]log'))) 'a[.]log survived'
    }
    Test 'Uninstall handles a root path containing brackets' {
        $root = Join-Path $testRoot 'root[1]'
        [IO.Directory]::CreateDirectory((Join-Path $root 'rvt\server\0.6.2')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $root 'rvt\server\0.6.2\rvt-mcp.exe'), 'exe')
        [IO.File]::WriteAllText((Join-Path $root 'revit-2027.json'), '{}')
        [IO.File]::WriteAllText((Join-Path $root 'bake.db'), 'db')
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'rvt'))) 'rvt not removed'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'revit-2027.json'))) 'discovery file not removed'
        Assert (Test-Path -LiteralPath (Join-Path $root 'bake.db')) 'bake.db not kept'
    }
    Test 'Uninstall keeps a server copy that is still running and removes the rest' {
        $root = New-RootFixture
        Copy-Item -LiteralPath "$env:WINDIR\System32\PING.EXE" -Destination "$root/rvt/server/0.6.2/rvt-mcp.exe" -Force
        $proc = Start-Process -FilePath "$root\rvt\server\0.6.2\rvt-mcp.exe" -ArgumentList '-n','30','127.0.0.1' -WindowStyle Hidden -PassThru
        try {
            Start-Sleep -Milliseconds 500
            $script:handled=@(); $script:skipped=@(); $script:failed=@()
            Invoke-Step4 -Root $root 3>$null 6>$null
            Assert (Test-Path -LiteralPath "$root/rvt/server/0.6.2/rvt-mcp.exe") 'Running server copy was deleted'
            Assert ($script:failed -contains 'step4-discovery') 'In-use copy not reported as failure'
            Assert (-not (Test-Path -LiteralPath "$root/spill")) 'Spill cache was not removed'
            foreach ($name in $discoveryNames) { Assert (-not (Test-Path -LiteralPath (Join-Path $root $name))) "Discovery file $name survived alongside an in-use server" }
            Assert (Test-Path -LiteralPath "$root/rvtmcp.config.json") 'Personal data removed'
        } finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; $null = $proc.WaitForExit(5000) }
        Start-Sleep -Milliseconds 300
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root 6>$null
        Assert (-not (Test-Path -LiteralPath "$root/rvt")) 'Server copy not removed after the client exited'
    }
    Test 'Uninstall -Purge keeps the root while a server copy is still running' {
        $root = New-RootFixture
        Copy-Item -LiteralPath "$env:WINDIR\System32\PING.EXE" -Destination "$root/rvt/server/0.6.2/rvt-mcp.exe" -Force
        $proc = Start-Process -FilePath "$root\rvt\server\0.6.2\rvt-mcp.exe" -ArgumentList '-n','30','127.0.0.1' -WindowStyle Hidden -PassThru
        try {
            Start-Sleep -Milliseconds 500
            $script:handled=@(); $script:skipped=@(); $script:failed=@()
            Invoke-Step4 -Root $root -Purge 3>$null 6>$null
            Assert (Test-Path -LiteralPath "$root/rvt/server/0.6.2/rvt-mcp.exe") 'Running server copy was deleted'
            Assert (-not (Test-Path -LiteralPath "$root/rvtmcp.config.json") -and -not (Test-Path -LiteralPath "$root/logs")) '-Purge did not remove the rest'
            Assert ($script:failed -contains 'step4-discovery') 'In-use copy not reported as failure'
        } finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; $null = $proc.WaitForExit(5000) }
    }
    Test 'Uninstall -Purge removes the product root but never the NourRouas parent or siblings' {
        $fam = Join-Path $testRoot "fam-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path "$fam/NourRouas/tbc-revit-mcp/server/current", "$fam/NourRouas/other" -Force | Out-Null
        Set-Content -LiteralPath "$fam/NourRouas/tbc-revit-mcp/server/current/tbc-revit-mcp.exe" 'exe'
        Set-Content -LiteralPath "$fam/NourRouas/tbc-revit-mcp/rvtmcp.config.json" '{}'
        Set-Content -LiteralPath "$fam/NourRouas/other/keep.txt" 'k'
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root "$fam/NourRouas/tbc-revit-mcp" -Purge
        Assert (-not (Test-Path -LiteralPath "$fam/NourRouas/tbc-revit-mcp")) 'product root survived -Purge'
        Assert (Test-Path -LiteralPath "$fam/NourRouas") 'NourRouas family parent removed'
        Assert ((Get-Content -LiteralPath "$fam/NourRouas/other/keep.txt" -Raw).Trim() -eq 'k') 'sibling touched'
    }
    Test 'Uninstall -Purge main sweep purges only the new root and leaves upstream data untouched' {
        $la = $sandboxLocalAppData
        $new = Join-Path $la 'NourRouas\tbc-revit-mcp'
        $upstream = Join-Path $la 'Bimwright\rvt-mcp'
        $legacy = Join-Path $la 'RvtMcp'
        New-Item -ItemType Directory -Path "$new/server/current", "$upstream/server/current", "$la/Bimwright/ipt-mcp", "$legacy/rvt/server/current" -Force | Out-Null
        Set-Content -LiteralPath "$new/server/current/tbc-revit-mcp.exe" 'exe'
        Set-Content -LiteralPath "$new/rvtmcp.config.json" '{}'
        Set-Content -LiteralPath "$new/revit-2027.json" '{}'
        Set-Content -LiteralPath "$upstream/server/current/rvt-mcp.exe" 'upstream-exe'
        Set-Content -LiteralPath "$upstream/rvtmcp.config.json" '{"upstream":true}'
        Set-Content -LiteralPath "$upstream/revit-2027.json" '{}'
        Set-Content -LiteralPath "$la/Bimwright/ipt-mcp/keep.txt" 'k'
        Set-Content -LiteralPath "$legacy/rvtmcp.config.json" '{"legacy":true}'
        Set-Content -LiteralPath "$legacy/rvt/server/current/rvt-mcp.exe" 'legacy-exe'
        $upstreamHashes = @(Get-ChildItem -LiteralPath "$la/Bimwright", $legacy -File -Recurse | ForEach-Object {
            [pscustomobject]@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}
        })
        try {
            $script:handled=@(); $script:skipped=@(); $script:failed=@()
            $output = Invoke-UninstallMain -Purge 3>&1 6>&1 | Out-String -Width 4096
            Assert (-not (Test-Path -LiteralPath $new)) "new root survived -Purge: $output"
            Assert (Test-Path -LiteralPath (Join-Path $la 'NourRouas')) 'NourRouas parent removed'
            foreach ($file in $upstreamHashes) {
                Assert ((Test-Path -LiteralPath $file.Path) -and (Get-FileHash -LiteralPath $file.Path).Hash -eq $file.Hash) "upstream data changed: $($file.Path)"
            }
            Assert ($output -match [regex]::Escape($upstream) + '.*belongs to upstream rvt-mcp') "upstream root not reported: $output"
            Assert ($output -match 'tbc-revit-mcp') 'client guidance does not name the tbc-revit-mcp entry'
            Assert ($script:failed.Count -eq 0) "sweep failed: $($script:failed -join ', ')"
        } finally {
            Remove-Item -LiteralPath (Join-Path $la 'NourRouas'), (Join-Path $la 'Bimwright'), $legacy -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Test 'Uninstall of the current layout removes server\ and discovery files, keeps data' {
        $fam = Join-Path $testRoot "fam-$([guid]::NewGuid().ToString('N'))"
        $root = "$fam/NourRouas/tbc-revit-mcp"
        New-Item -ItemType Directory -Path "$root/server/current", "$root/server/0.6.2", "$root/spill", "$fam/Bimwright/rvt-mcp" -Force | Out-Null
        Set-Content -LiteralPath "$root/server/current/tbc-revit-mcp.exe" 'exe'
        Set-Content -LiteralPath "$root/server/0.6.2/rvt-mcp.exe" 'exe'
        Set-Content -LiteralPath "$root/revit-2027.json" '{}'
        Set-Content -LiteralPath "$root/spill/x.json" '{}'
        Set-Content -LiteralPath "$root/rvtmcp.config.json" '{}'
        Set-Content -LiteralPath "$fam/Bimwright/rvt-mcp/keep.txt" 'k'
        $script:handled=@(); $script:skipped=@(); $script:failed=@()
        Invoke-Step4 -Root $root
        Assert (-not (Test-Path -LiteralPath "$root/server")) 'server dir not removed'
        Assert (-not (Test-Path -LiteralPath "$root/revit-2027.json")) 'discovery file not removed'
        Assert (-not (Test-Path -LiteralPath "$root/spill")) 'spill not removed'
        Assert (Test-Path -LiteralPath "$root/rvtmcp.config.json") 'personal data removed'
        Assert ((Get-Content -LiteralPath "$fam/Bimwright/rvt-mcp/keep.txt" -Raw).Trim() -eq 'k') 'upstream product touched'
    }
} finally {
    $env:USERPROFILE = $savedUserProfile
    $env:APPDATA = $savedAppData
    $env:LOCALAPPDATA = $savedLocalAppData
    $report = [ordered]@{powershell=$PSVersionTable.PSVersion.ToString();uninstallerSha256=(Get-FileHash $uninstaller -Algorithm SHA256).Hash;results=@($results.ToArray())}
    if ($ResultPath) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    # Only remove the uniquely created test directory under the resolved temp root.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath($TestRootParent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if (@($results | Where-Object { -not $_.passed }).Count) { throw 'Uninstaller regression tests failed' }
