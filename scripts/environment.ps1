$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskLocalSdk = Join-Path $taskWorkspace '.local\sdk'
if (Test-Path -LiteralPath (Join-Path $taskLocalSdk 'dotnet.exe') -PathType Leaf) {
    $env:DOTNET_ROOT = $taskLocalSdk
    $env:PATH = $taskLocalSdk + [IO.Path]::PathSeparator + $env:PATH
}
$env:DOTNET_CLI_HOME = Join-Path $taskWorkspace '.local\dotnet'
$env:NUGET_PACKAGES = Join-Path $taskWorkspace '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
