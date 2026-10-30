param([Parameter(Mandatory = $true)][string]$PackageDirectory, [string]$ZipPath, [switch]$RunDiagnostics)
. (Join-Path $PSScriptRoot 'package-common.ps1')
$taskPackage = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
$taskFiles = @(Get-CheckedPackageFiles -Directory $taskPackage)
$taskByPath = @{}
foreach ($taskFile in $taskFiles) { $taskByPath[$taskFile.RelativePath] = $taskFile }
foreach ($taskRequired in @('package-manifest.json', 'SHA256SUMS', 'HsHelper.App.exe', 'HsHelper.App.dll', 'HsHelper.App.runtimeconfig.json', 'HsHelper.App.deps.json', 'QUICKSTART.zh-CN.md', 'samples/decks/beast-hunter.txt')) {
    if (-not $taskByPath.ContainsKey($taskRequired)) { throw "Missing required package file: $taskRequired" }
}
$taskManifest = Get-Content -LiteralPath $taskByPath['package-manifest.json'].FullName -Raw | ConvertFrom-Json
if ($taskManifest.schemaVersion -ne 1 -or $taskManifest.product -ne 'HsHelper' -or $taskManifest.runtimeIdentifier -ne 'win-x64' -or
    $taskManifest.version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$' -or
    $taskManifest.entryPoint -ne 'HsHelper.App.exe' -or $taskManifest.dataDirectory -ne '%LOCALAPPDATA%\HsHelper' -or
    $taskManifest.deploymentMode -notin @('self-contained', 'framework-dependent') -or $taskManifest.dpiManifest -ne 'PerMonitorV2,PerMonitor') {
    throw 'Unsupported or inconsistent package manifest.'
}
$taskProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskByPath['HsHelper.App.dll'].FullName).ProductVersion
if (-not $taskProductVersion -or -not ($taskProductVersion -ceq $taskManifest.version -or $taskProductVersion.StartsWith($taskManifest.version + '+', [StringComparison]::Ordinal))) {
    throw 'The application assembly version does not match the package manifest.'
}
$taskRuntimeOptions = (Get-Content -LiteralPath $taskByPath['HsHelper.App.runtimeconfig.json'].FullName -Raw | ConvertFrom-Json).runtimeOptions
if ($taskRuntimeOptions.tfm -ne 'net10.0') { throw 'The application runtime configuration must target .NET 10.' }
$taskSharedFrameworks = @($taskRuntimeOptions.framework) + @($taskRuntimeOptions.frameworks) | Where-Object { $null -ne $_ }
$taskIncludedFrameworks = @($taskRuntimeOptions.includedFrameworks) | Where-Object { $null -ne $_ }
if ($taskManifest.deploymentMode -eq 'self-contained') {
    if (@($taskSharedFrameworks).Count -ne 0) { throw 'A self-contained package cannot require an installed shared framework.' }
    $taskFrameworks = @($taskIncludedFrameworks)
} else {
    if (@($taskIncludedFrameworks).Count -ne 0) { throw 'A framework-dependent package cannot declare included frameworks.' }
    $taskFrameworks = @($taskSharedFrameworks)
}
foreach ($taskFrameworkName in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $taskFramework = @($taskFrameworks | Where-Object { $_.name -ceq $taskFrameworkName })
    if ($taskFramework.Count -ne 1 -or $taskFramework[0].version -notmatch '^10\.0\.\d+$') {
        throw 'The runtime configuration does not declare the required .NET 10 Windows Desktop frameworks.'
    }
}
$taskDependencies = Get-Content -LiteralPath $taskByPath['HsHelper.App.deps.json'].FullName -Raw | ConvertFrom-Json
$taskRuntimeTarget = $taskDependencies.runtimeTarget.name
if ($taskRuntimeTarget -cne '.NETCoreApp,Version=v10.0/win-x64' -or
    $null -eq $taskDependencies.targets.PSObject.Properties[$taskRuntimeTarget]) {
    throw 'The dependency manifest does not target .NET 10 / win-x64.'
}
$taskTargetLibraries = $taskDependencies.targets.PSObject.Properties[$taskRuntimeTarget].Value
foreach ($taskLibrary in $taskTargetLibraries.PSObject.Properties) {
    $taskDeclaredAssets = New-Object 'Collections.Generic.List[string]'
    foreach ($taskAssetKind in @('runtime', 'native')) {
        $taskAssets = $taskLibrary.Value.PSObject.Properties[$taskAssetKind].Value
        if ($null -eq $taskAssets) { continue }
        foreach ($taskAsset in $taskAssets.PSObject.Properties) {
            $taskDeclaredAssets.Add($taskAsset.Name)
        }
    }
    $taskRidAssets = $taskLibrary.Value.PSObject.Properties['runtimeTargets'].Value
    if ($null -ne $taskRidAssets) {
        foreach ($taskAsset in $taskRidAssets.PSObject.Properties) {
            if ($taskAsset.Value.rid -in @('win-x64', 'win', 'any') -and $taskAsset.Value.assetType -in @('runtime', 'native')) {
                $taskDeclaredAssets.Add($taskAsset.Name)
            }
        }
    }
    foreach ($taskAssetPath in $taskDeclaredAssets) {
        Assert-PackageRelativePath -Path $taskAssetPath
        $taskAssetName = [IO.Path]::GetFileName($taskAssetPath)
        if ($taskAssetName -eq '_._') { continue }
        # Publish normally flattens lib/<tfm> and RID native assets beside the entry point.
        if (-not $taskByPath.ContainsKey($taskAssetPath) -and -not $taskByPath.ContainsKey($taskAssetName)) {
            throw "Dependency asset missing from the package: $taskAssetPath"
        }
    }
}
if ($taskManifest.deploymentMode -eq 'self-contained') {
    foreach ($taskRuntimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'System.Private.CoreLib.dll')) {
        if (-not $taskByPath.ContainsKey($taskRuntimeFile)) { throw "Self-contained runtime file missing: $taskRuntimeFile" }
    }
}
$taskEmbeddedPolicy = Get-EmbeddedAppPolicy -Executable $taskByPath['HsHelper.App.exe'].FullName
if ($taskEmbeddedPolicy.DpiAwareness -ne $taskManifest.dpiManifest -or $taskEmbeddedPolicy.ExecutionLevel -ne 'asInvoker' -or $taskEmbeddedPolicy.UiAccess -ne 'false') {
    throw 'The executable embedded manifest does not match the declared DPI or ordinary-user policy.'
}
$taskListed = @{}
foreach ($taskEntry in @($taskManifest.files)) {
    Assert-PackageRelativePath -Path $taskEntry.path
    if ($taskListed.ContainsKey($taskEntry.path) -or $taskEntry.path -in @('package-manifest.json', 'SHA256SUMS')) { throw 'Duplicate or reserved manifest entry.' }
    if (-not $taskByPath.ContainsKey($taskEntry.path) -or $taskEntry.sha256 -notmatch '^[0-9a-f]{64}$' -or $taskEntry.bytes -ne $taskByPath[$taskEntry.path].Length) {
        throw "Invalid manifest entry: $($taskEntry.path)"
    }
    if ((Get-FileHash -LiteralPath $taskByPath[$taskEntry.path].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskEntry.sha256) {
        throw "SHA256 mismatch: $($taskEntry.path)"
    }
    $taskListed[$taskEntry.path] = $true
}
if ($taskListed.Count + 2 -ne $taskFiles.Count) { throw 'The package has unlisted files.' }
$taskChecksummed = @{}
foreach ($taskLine in Get-Content -LiteralPath $taskByPath['SHA256SUMS'].FullName) {
    if ($taskLine -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Malformed SHA256SUMS line.' }
    $taskHash = $Matches[1]; $taskRelative = $Matches[2]
    Assert-PackageRelativePath -Path $taskRelative
    if ($taskRelative -eq 'SHA256SUMS' -or $taskChecksummed.ContainsKey($taskRelative) -or -not $taskByPath.ContainsKey($taskRelative)) { throw 'Invalid SHA256SUMS entry.' }
    if ((Get-FileHash -LiteralPath $taskByPath[$taskRelative].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskHash) { throw "Checksum mismatch: $taskRelative" }
    $taskChecksummed[$taskRelative] = $taskHash
}
if ($taskChecksummed.Count + 1 -ne $taskFiles.Count) { throw 'SHA256SUMS is incomplete.' }
if ($ZipPath) {
    $taskZipPath = [IO.Path]::GetFullPath($ZipPath)
    $taskZipReceipt = Get-Content -LiteralPath ($taskZipPath + '.sha256') -Raw
    if ($taskZipReceipt.TrimEnd() -notmatch '^([0-9a-f]{64})  (.+)$' -or $Matches[2] -cne [IO.Path]::GetFileName($taskZipPath)) { throw 'Invalid ZIP checksum receipt.' }
    if ((Get-FileHash -LiteralPath $taskZipPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Matches[1]) { throw 'ZIP SHA256 mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskArchive = [IO.Compression.ZipFile]::OpenRead($taskZipPath)
    $taskZipFiles = @{}
    try {
        foreach ($taskZipEntry in $taskArchive.Entries) {
            Assert-PackageRelativePath -Path $taskZipEntry.FullName
            if ($taskZipEntry.FullName.EndsWith('/')) {
                if (-not (Test-Path -LiteralPath (Join-Path $taskPackage $taskZipEntry.FullName.TrimEnd('/')) -PathType Container)) { throw 'The ZIP contains an additional directory.' }
                continue
            }
            if ($taskZipFiles.ContainsKey($taskZipEntry.FullName) -or -not $taskByPath.ContainsKey($taskZipEntry.FullName) -or
                $taskZipEntry.Length -ne $taskByPath[$taskZipEntry.FullName].Length) { throw 'The ZIP contains missing, duplicate, or additional files.' }
            $taskExpectedZipHash = if ($taskZipEntry.FullName -eq 'SHA256SUMS') {
                (Get-FileHash -LiteralPath $taskByPath['SHA256SUMS'].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            } else { $taskChecksummed[$taskZipEntry.FullName] }
            $taskStream = $taskZipEntry.Open()
            $taskSha = [Security.Cryptography.SHA256]::Create()
            try { $taskZipHash = [BitConverter]::ToString($taskSha.ComputeHash($taskStream)).Replace('-', '').ToLowerInvariant() }
            finally { $taskSha.Dispose(); $taskStream.Dispose() }
            if ($taskZipHash -cne $taskExpectedZipHash) { throw "ZIP entry SHA256 mismatch: $($taskZipEntry.FullName)" }
            $taskZipFiles[$taskZipEntry.FullName] = $true
        }
    } finally { $taskArchive.Dispose() }
    if ($taskZipFiles.Count -ne $taskFiles.Count) { throw 'The ZIP is incomplete.' }
    Write-Host 'ZIP: checksum and every archived file verified.'
}
if ($RunDiagnostics) {
    $taskWorkspace = Split-Path -Parent $PSScriptRoot
    $taskDiagnosticDirectory = Assert-NewPackagePath -Path (Join-Path $taskWorkspace ('.local/package-check/' + [Guid]::NewGuid().ToString('N'))) -AllowedRoot (Join-Path $taskWorkspace '.local/package-check')
    New-Item -ItemType Directory -Path $taskDiagnosticDirectory | Out-Null
    $taskDiagnosticPath = Join-Path $taskDiagnosticDirectory 'diagnostic.json'
    $taskStart = New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName = $taskByPath['HsHelper.App.exe'].FullName
    $taskStart.Arguments = '--diagnostic-output "' + $taskDiagnosticPath + '"'
    $taskStart.WorkingDirectory = [IO.Path]::GetTempPath()
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $taskStart.EnvironmentVariables.Remove('HSHELPER_DATA_DIR')
    foreach ($taskExternalConfiguration in @('DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE')) {
        $taskStart.EnvironmentVariables.Remove($taskExternalConfiguration)
    }
    if ($taskManifest.deploymentMode -eq 'self-contained') {
        $taskUnavailableRuntime = Join-Path $taskDiagnosticDirectory 'no-installed-runtime'
        $taskStart.EnvironmentVariables['DOTNET_ROOT'] = $taskUnavailableRuntime
        $taskStart.EnvironmentVariables['DOTNET_ROOT_X64'] = $taskUnavailableRuntime
        $taskStart.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    }
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        if (-not $taskProcess.WaitForExit(30000)) { $taskProcess.Kill(); throw 'Package diagnostic timed out.' }
        if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskDiagnosticPath -PathType Leaf)) { throw 'Package diagnostic failed.' }
    } finally { $taskProcess.Dispose() }
    $taskDiagnostic = Get-Content -LiteralPath $taskDiagnosticPath -Raw | ConvertFrom-Json
    $taskExpectedData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HsHelper'
    if (-not $taskDiagnostic.is64Bit -or $taskDiagnostic.dataDirectory -ne $taskExpectedData -or
        [IO.Path]::GetFullPath($taskDiagnostic.baseDirectory).TrimEnd('\', '/') -ne $taskPackage -or
        -not ($taskDiagnostic.version -ceq $taskManifest.version -or $taskDiagnostic.version.StartsWith($taskManifest.version + '+', [StringComparison]::Ordinal)) -or
        -not $taskDiagnostic.dpiManifest.StartsWith('PerMonitorV2', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package diagnostic settings do not match the manifest.' }
    Write-Host 'Diagnostic: passed without opening the main window.'
}
Write-Host "Verified: $($taskManifest.version) / $($taskManifest.deploymentMode) / $($taskFiles.Count) files / SHA256"
