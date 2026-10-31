param(
    [string]$Version,
    [string]$OutputDirectory,
    [switch]$FrameworkDependent,
    [switch]$SkipZip,
    [switch]$RunDiagnostics
)
. (Join-Path $PSScriptRoot 'environment.ps1')
. (Join-Path $PSScriptRoot 'package-common.ps1')
. (Join-Path $PSScriptRoot 'package-guide.ps1')

if (-not $Version) {
    [xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskWorkspace 'src/HsHelper.App/HsHelper.App.csproj') -Raw
    $Version = @($taskProject.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z') { throw 'Use a three-part version with an optional prerelease suffix.' }
$taskMode = if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }
$taskOutputRoot = Join-Path $taskWorkspace '.local/publish'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskOutputRoot "$Version-$taskMode" }
$taskPackage = Assert-NewPackagePath -Path $OutputDirectory -AllowedRoot $taskOutputRoot
$taskZip = $taskPackage + '.zip'
if (-not $SkipZip -and ((Test-Path -LiteralPath $taskZip) -or (Test-Path -LiteralPath ($taskZip + '.sha256')))) {
    throw 'The ZIP or its checksum already exists; choose a new directory.'
}
$taskArtifacts = Assert-NewPackagePath -Path (Join-Path $taskWorkspace ('.local/publish-build/' + [Guid]::NewGuid().ToString('N'))) -AllowedRoot (Join-Path $taskWorkspace '.local/publish-build')
New-Item -ItemType Directory -Path $taskPackage | Out-Null
$taskCommit = (@(& git -C $taskWorkspace rev-parse HEAD 2>$null) -join '').Trim()
if ($taskCommit -notmatch '^[0-9a-f]{40}$') { $taskCommit = 'unknown' }
$taskDirty = [bool](& git -C $taskWorkspace status --porcelain 2>$null)
Push-Location $taskWorkspace
try {
    $taskSelfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    & dotnet publish src/HsHelper.App/HsHelper.App.csproj -c Release -r win-x64 --self-contained $taskSelfContained `
        --artifacts-path $taskArtifacts -o $taskPackage -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -m:1 --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE). The partial output is retained for inspection; use a new directory on retry." }
    # Only these public repository files are added. Never copy a workspace directory recursively.
    New-PackageQuickStart -TemplatePath (Join-Path $taskWorkspace 'doc/user-quickstart.zh-CN.md') -Version $Version `
        -DeploymentMode $taskMode -Destination (Join-Path $taskPackage 'QUICKSTART.zh-CN.md')
    $taskFiles = @(Get-CheckedPackageFiles -Directory $taskPackage)
    $taskEntries = @($taskFiles | ForEach-Object {
        [ordered]@{ path = $_.RelativePath; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $taskManifest = [ordered]@{
        schemaVersion = 1
        product = 'HsHelper'
        version = $Version
        sourceCommit = $taskCommit
        sourceDirty = $taskDirty
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        runtimeIdentifier = 'win-x64'
        deploymentMode = $taskMode
        entryPoint = 'HsHelper.App.exe'
        dataDirectory = '%LOCALAPPDATA%\HsHelper'
        dpiManifest = 'PerMonitorV2,PerMonitor'
        files = $taskEntries
    }
    $taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskPackage 'package-manifest.json') -Encoding utf8
    $taskChecksums = @(Get-CheckedPackageFiles -Directory $taskPackage | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.RelativePath
    })
    $taskChecksums | Set-Content -LiteralPath (Join-Path $taskPackage 'SHA256SUMS') -Encoding utf8
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $taskPackage -RunDiagnostics:$RunDiagnostics
    if (-not $SkipZip) {
        Compress-Archive -Path (Join-Path $taskPackage '*') -DestinationPath $taskZip -CompressionLevel Optimal
        ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($taskZip)) |
            Set-Content -LiteralPath ($taskZip + '.sha256') -Encoding utf8
        & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageDirectory $taskPackage -ZipPath $taskZip
        Write-Host "ZIP: $taskZip"
    }
    Write-Host "Package: $taskPackage"
} finally { Pop-Location }
