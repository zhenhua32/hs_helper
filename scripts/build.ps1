param([string]$ArtifactsPath = '.local/build')
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $taskWorkspace
try {
    dotnet build HsHelper.slnx --artifacts-path $ArtifactsPath -p:UseSharedCompilation=false -m:1 --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
} finally { Pop-Location }
