. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $taskWorkspace
try {
    dotnet build HsHelper.slnx -p:UseSharedCompilation=false -m:1
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    foreach ($taskSuite in @('Core','Ingestion','Cards','Agent')) {
        dotnet run --no-build --project "tests/HsHelper.$taskSuite.Tests"
        if ($LASTEXITCODE -ne 0) { throw "$taskSuite checks failed: $LASTEXITCODE" }
    }
} finally { Pop-Location }
