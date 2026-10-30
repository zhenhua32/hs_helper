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
    @{ runtimeOptions = @{ tfm = 'net10.0'; frameworks = @(
        @{ name = 'Microsoft.NETCore.App'; version = '10.0.0' },
        @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' }
    ) } } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFixture 'HsHelper.App.runtimeconfig.json') -Encoding utf8
    @{ runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/win-x64' }; targets = @{ '.NETCoreApp,Version=v10.0/win-x64' = @{
        'HsHelper.App/fixture' = @{ runtime = @{ 'HsHelper.App.dll' = @{} } }
    } } } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFixture 'HsHelper.App.deps.json') -Encoding utf8
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
    param([string]$Directory, [string]$Expected, [string]$ZipPath)
    $taskRejected = $false
    try { & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $Directory -ZipPath $ZipPath | Out-Null }
    catch { if ($_.Exception.Message -notmatch $Expected) { throw }; $taskRejected = $true }
    if (-not $taskRejected) { throw 'A deliberately invalid synthetic package was accepted.' }
}

function Write-SyntheticZipReceipt {
    param([string]$Path)
    ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($Path)) |
        Set-Content -LiteralPath ($Path + '.sha256') -Encoding utf8
}

function New-SyntheticZip {
    param([string]$Directory, [string]$Name)
    $taskArchivePath = Join-Path $taskTestRoot ($Name + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($Directory, $taskArchivePath)
    Write-SyntheticZipReceipt -Path $taskArchivePath
    return $taskArchivePath
}

$taskValid = New-SyntheticPackage 'valid'
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $taskValid
$taskChecks++
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskValidZip = New-SyntheticZip -Directory $taskValid -Name 'valid'
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $taskValid -ZipPath $taskValidZip
$taskChecks++
$taskFixtureZip = New-SyntheticZip -Directory $taskValid -Name 'wrong-receipt'
('0' * 64 + '  ' + [IO.Path]::GetFileName($taskFixtureZip)) | Set-Content -LiteralPath ($taskFixtureZip + '.sha256') -Encoding utf8
Assert-PackageRejected -Directory $taskValid -ZipPath $taskFixtureZip -Expected 'ZIP SHA256 mismatch'
$taskChecks++
foreach ($taskZipCase in @('extra', 'duplicate', 'traversal', 'missing')) {
    $taskFixtureZip = New-SyntheticZip -Directory $taskValid -Name ('zip-' + $taskZipCase)
    $taskArchive = [IO.Compression.ZipFile]::Open($taskFixtureZip, [IO.Compression.ZipArchiveMode]::Update)
    try {
        switch ($taskZipCase) {
            'extra' { $taskArchive.CreateEntry('unexpected.txt') | Out-Null }
            'duplicate' { $taskArchive.CreateEntry('QUICKSTART.zh-CN.md') | Out-Null }
            'traversal' { $taskArchive.CreateEntry('../outside.txt') | Out-Null }
            'missing' { $taskArchive.GetEntry('QUICKSTART.zh-CN.md').Delete() }
        }
    } finally { $taskArchive.Dispose() }
    Write-SyntheticZipReceipt -Path $taskFixtureZip
    $taskExpected = switch ($taskZipCase) {
        'traversal' { 'invalid relative path' }
        'missing' { 'ZIP is incomplete' }
        default { 'ZIP contains missing, duplicate, or additional files' }
    }
    Assert-PackageRejected -Directory $taskValid -ZipPath $taskFixtureZip -Expected $taskExpected
    $taskChecks++
}
foreach ($taskRequiredRuntimeFile in @('HsHelper.App.runtimeconfig.json', 'HsHelper.App.deps.json')) {
    $taskFixture = New-SyntheticPackage ('missing-' + $taskRequiredRuntimeFile)
    Remove-Item -LiteralPath (Join-Path $taskFixture $taskRequiredRuntimeFile)
    Assert-PackageRejected -Directory $taskFixture -Expected 'Missing required package file'
    $taskChecks++
}
$taskFixture = New-SyntheticPackage 'wrong-deployment-mode'
$taskConfiguration = @{ runtimeOptions = @{ tfm = 'net10.0'; includedFrameworks = @(
    @{ name = 'Microsoft.NETCore.App'; version = '10.0.0' },
    @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.0' }
) } }
$taskConfiguration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFixture 'HsHelper.App.runtimeconfig.json') -Encoding utf8
Assert-PackageRejected -Directory $taskFixture -Expected 'framework-dependent package cannot declare included'
$taskChecks++
$taskFixture = New-SyntheticPackage 'wrong-runtime-target'
@{ runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/linux-x64' }; targets = @{} } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFixture 'HsHelper.App.deps.json') -Encoding utf8
Assert-PackageRejected -Directory $taskFixture -Expected 'dependency manifest does not target'
$taskChecks++
$taskFixture = New-SyntheticPackage 'missing-runtime-dependency'
@{ runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/win-x64' }; targets = @{ '.NETCoreApp,Version=v10.0/win-x64' = @{
    'Fixture.Dependency/1.0.0' = @{ runtime = @{ 'lib/net10.0/missing-fixture.dll' = @{} } }
} } } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFixture 'HsHelper.App.deps.json') -Encoding utf8
Assert-PackageRejected -Directory $taskFixture -Expected 'Dependency asset missing'
$taskChecks++
$taskFixture = New-SyntheticPackage 'missing-native-dependency'
@{ runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/win-x64' }; targets = @{ '.NETCoreApp,Version=v10.0/win-x64' = @{
    'Fixture.Dependency/1.0.0' = @{ runtimeTargets = @{ 'runtimes/win-x64/native/missing-fixture.dll' = @{ rid = 'win-x64'; assetType = 'native' } } }
} } } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskFixture 'HsHelper.App.deps.json') -Encoding utf8
Assert-PackageRejected -Directory $taskFixture -Expected 'Dependency asset missing'
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
