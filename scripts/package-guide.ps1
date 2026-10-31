function Get-PackageQuickStartHeader {
    param(
        [Parameter(Mandatory = $true)][ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z')][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet('self-contained', 'framework-dependent')][string]$DeploymentMode
    )
    if ($DeploymentMode -eq 'self-contained') {
        $taskModeText = '自包含（self-contained）'
        $taskRequirement = '本包已内置所需运行时，无需安装 .NET 或 SDK。'
    } else {
        $taskModeText = '依赖框架（framework-dependent）'
        $taskRequirement = '先安装 x64 .NET 10 Windows Desktop Runtime；仅安装普通 .NET Runtime 或 ASP.NET Runtime 不够，无需 SDK。'
    }
    return "# 炉石猎人助手快速开始`n`n版本：$Version`n发布方式：$taskModeText`n运行要求：$taskRequirement`n平台：Windows x64`n`n"
}

function New-PackageQuickStart {
    param(
        [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$TemplatePath,
        [Parameter(Mandatory = $true)][ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?\z')][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet('self-contained', 'framework-dependent')][string]$DeploymentMode,
        [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Destination
    )
    $taskUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $taskBody = [IO.File]::ReadAllText([IO.Path]::GetFullPath($TemplatePath), $taskUtf8).Replace("`r`n", "`n").Replace("`r", "`n").Trim([char[]]"`n")
    if ([string]::IsNullOrWhiteSpace($taskBody) -or $taskBody -match '(?m)^(版本|发布方式|运行要求|平台)：') {
        throw 'The quickstart template must be nonempty and cannot define reserved package header fields.'
    }
    $taskText = (Get-PackageQuickStartHeader -Version $Version -DeploymentMode $DeploymentMode) + $taskBody + "`n"
    $taskBytes = $taskUtf8.GetBytes($taskText)
    $taskOutput = [IO.File]::Open([IO.Path]::GetFullPath($Destination), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $taskOutput.Write($taskBytes, 0, $taskBytes.Length) }
    finally { $taskOutput.Dispose() }
}

function Assert-PackageQuickStart {
    param(
        [Parameter(Mandatory = $true)][string]$GuidePath,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet('self-contained', 'framework-dependent')][string]$DeploymentMode
    )
    $taskHeader = Get-PackageQuickStartHeader -Version $Version -DeploymentMode $DeploymentMode
    $taskGuide = [IO.File]::ReadAllText([IO.Path]::GetFullPath($GuidePath), [Text.UTF8Encoding]::new($false, $true)).Replace("`r`n", "`n").Replace("`r", "`n")
    if (-not $taskGuide.StartsWith($taskHeader, [StringComparison]::Ordinal)) {
        throw 'Package quickstart version or deployment mode does not match the manifest.'
    }
    $taskBody = $taskGuide.Substring($taskHeader.Length)
    if ([string]::IsNullOrWhiteSpace($taskBody) -or $taskBody -match '(?m)^(版本|发布方式|运行要求|平台)：') {
        throw 'Package quickstart version or deployment mode does not match the manifest.'
    }
}
