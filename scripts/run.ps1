param([string]$DataDirectory, [string]$ArtifactsPath = '.local/build')
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $taskWorkspace
try {
    if ($DataDirectory) { $env:HSHELPER_DATA_DIR = [System.IO.Path]::GetFullPath($DataDirectory) }
    dotnet run --project src/HsHelper.App --artifacts-path $ArtifactsPath -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Application exited: $LASTEXITCODE" }
} finally { Pop-Location }
