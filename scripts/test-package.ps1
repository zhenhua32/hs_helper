param([Parameter(Mandatory = $true)][string]$PackageDirectory)
. (Join-Path $PSScriptRoot 'package-common.ps1')
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $PackageDirectory
$taskSource = [IO.Path]::GetFullPath($PackageDirectory)
$taskSourceManifest = Get-Content -LiteralPath (Join-Path $taskSource 'package-manifest.json') -Raw | ConvertFrom-Json
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskTestRoot = Assert-NewPackagePath -Path (Join-Path $taskWorkspace ('.local/package-safety/' + [Guid]::NewGuid().ToString('N'))) -AllowedRoot (Join-Path $taskWorkspace '.local/package-safety')
New-Item -ItemType Directory -Path $taskTestRoot | Out-Null
$taskChecks = 0

function New-SyntheticPackage {
    param([string]$Name)
    $taskFixture = Assert-NewPackagePath -Path (Join-Path $taskTestRoot $Name) -AllowedRoot $taskTestRoot
    New-Item -ItemType Directory -Path (Join-Path $taskFixture 'samples/decks') | Out-Null
    foreach ($taskBinary in @('HsHelper.App.exe', 'HsHelper.App.dll')) {
        Copy-Item -LiteralPath (Join-Path $taskSource $taskBinary) -Destination (Join-Path $taskFixture $taskBinary)
    }
    Set-Content -LiteralPath (Join-Path $taskFixture 'samples/decks/beast-hunter.txt') -Value 'SYNTHETIC PACKAGE CHECK: not a real deck.' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $taskFixture 'QUICKSTART.zh-CN.md') -Value 'Synthetic package validation fixture, never run as an app.' -Encoding utf8
    $taskFixtureFiles = @(Get-CheckedPackageFiles -Directory $taskFixture)
    [ordered]@{
        schemaVersion = 1; product = 'HsHelper'; version = $taskSourceManifest.version; runtimeIdentifier = 'win-x64'
        deploymentMode = 'framework-dependent'; entryPoint = 'HsHelper.App.exe'; dataDirectory = '%LOCALAPPDATA%\HsHelper'; dpiManifest = 'PerMonitorV2,PerMonitor'
        files = @($taskFixtureFiles | ForEach-Object { [ordered]@{ path = $_.RelativePath; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFixture 'package-manifest.json') -Encoding utf8
    @(Get-CheckedPackageFiles -Directory $taskFixture | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.RelativePath
    }) | Set-Content -LiteralPath (Join-Path $taskFixture 'SHA256SUMS') -Encoding utf8
    return $taskFixture
}

function Assert-PackageRejected {
    param([string]$Directory, [string]$Expected)
    $taskRejected = $false
    try { & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $Directory | Out-Null }
    catch { if ($_.Exception.Message -notmatch $Expected) { throw }; $taskRejected = $true }
    if (-not $taskRejected) { throw 'A deliberately invalid synthetic package was accepted.' }
}

$taskValid = New-SyntheticPackage 'valid'
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $taskValid
$taskChecks++
foreach ($taskForbidden in @('.env.synthetic', 'private.db', 'model-config.json', 'Power.log')) {
    $taskFixture = New-SyntheticPackage ('prohibited-' + $taskChecks)
    Set-Content -LiteralPath (Join-Path $taskFixture $taskForbidden) -Value 'synthetic, no credentials or user data' -Encoding utf8
    Assert-PackageRejected -Directory $taskFixture -Expected 'prohibited'
    $taskChecks++
}
$taskFixture = New-SyntheticPackage 'tampered'
Add-Content -LiteralPath (Join-Path $taskFixture 'QUICKSTART.zh-CN.md') -Value 'changed'
Assert-PackageRejected -Directory $taskFixture -Expected 'Invalid manifest entry|SHA256 mismatch'
$taskChecks++
$taskFixture = New-SyntheticPackage 'extra'
Set-Content -LiteralPath (Join-Path $taskFixture 'extra.txt') -Value 'synthetic'
Assert-PackageRejected -Directory $taskFixture -Expected 'unlisted'
$taskChecks++
$taskFixture = New-SyntheticPackage 'traversal'
$taskInvalid = Get-Content -LiteralPath (Join-Path $taskFixture 'package-manifest.json') -Raw | ConvertFrom-Json
$taskInvalid.files[0].path = '../outside.txt'
$taskInvalid | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFixture 'package-manifest.json') -Encoding utf8
Assert-PackageRejected -Directory $taskFixture -Expected 'invalid relative path'
$taskChecks++
$taskFixture = New-SyntheticPackage 'duplicate-checksum'
$taskFirstChecksum = Get-Content -LiteralPath (Join-Path $taskFixture 'SHA256SUMS') -TotalCount 1
Add-Content -LiteralPath (Join-Path $taskFixture 'SHA256SUMS') -Value $taskFirstChecksum
Assert-PackageRejected -Directory $taskFixture -Expected 'Invalid SHA256SUMS entry'
$taskChecks++
foreach ($taskBadOutput in @($taskTestRoot, $taskValid, (Join-Path $taskTestRoot '../escape'))) {
    $taskRejected = $false
    try { Assert-NewPackagePath -Path $taskBadOutput -AllowedRoot $taskTestRoot | Out-Null } catch { $taskRejected = $true }
    if (-not $taskRejected) { throw 'An existing, root, or escaping output path was accepted.' }
    $taskChecks++
}
$taskFixture = New-SyntheticPackage 'junction'
New-Item -ItemType Junction -Path (Join-Path $taskFixture 'linked') -Target $taskValid | Out-Null
Assert-PackageRejected -Directory $taskFixture -Expected 'link'
$taskChecks++
$taskRejected = $false
try { Assert-NewPackagePath -Path (Join-Path $taskFixture 'linked/new-output') -AllowedRoot $taskTestRoot | Out-Null } catch { $taskRejected = $true }
if (-not $taskRejected) { throw 'An output through a junction ancestor was accepted.' }
$taskChecks++
$taskRejected = $false
try { Get-CheckedPackageFiles -Directory (Join-Path $taskFixture 'linked/samples') | Out-Null } catch { $taskRejected = $true }
if (-not $taskRejected) { throw 'A package through a junction ancestor was accepted.' }
$taskChecks++
Write-Host "Package safety checks: $taskChecks passed (synthetic fixtures retained under $taskTestRoot)."
