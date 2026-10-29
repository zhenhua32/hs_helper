. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $taskWorkspace
try {
    dotnet run --project src/HsHelper.App -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Application exited: $LASTEXITCODE" }
} finally { Pop-Location }
