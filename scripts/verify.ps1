param([string]$ArtifactsPath = '.local/build')
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $taskWorkspace
try {
    & (Join-Path $PSScriptRoot 'test-package-guide.ps1')
    dotnet build HsHelper.slnx --artifacts-path $ArtifactsPath -p:UseSharedCompilation=false -m:1 --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    foreach ($taskSuite in @('Core','Ingestion','Cards','Agent','Configuration','Storage','Rules','App','Integration')) {
        dotnet run --no-build --project "tests/HsHelper.$taskSuite.Tests" --artifacts-path $ArtifactsPath
        if ($LASTEXITCODE -ne 0) { throw "$taskSuite checks failed: $LASTEXITCODE" }
    }
} finally { Pop-Location }
