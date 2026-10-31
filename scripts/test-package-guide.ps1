Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskTemplate = Join-Path $taskWorkspace 'doc/user-quickstart.zh-CN.md'
$taskGenerator = Join-Path $PSScriptRoot 'package-guide.ps1'
$taskUtf8 = [Text.UTF8Encoding]::new($false, $true)
$taskGuideChecks = 0
$taskReferenceGuide = $null
$taskOriginalDirectory = (Get-Location).Path
$taskLoadOutput = @(. $taskGenerator 6>&1)
. (Join-Path $PSScriptRoot 'package-common.ps1')
$taskTestRoot = Assert-NewPackagePath -Path (Join-Path $taskWorkspace ('.local/package-guide-tests/' + [Guid]::NewGuid().ToString('N') + ' synthetic paths')) -AllowedRoot (Join-Path $taskWorkspace '.local/package-guide-tests')
New-Item -ItemType Directory -Path $taskTestRoot | Out-Null

function Assert-GuideCheck {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Test-GuideCase {
    param([string]$Name, [scriptblock]$Run)
    & $Run
    $script:taskGuideChecks++
    Write-Host "PASS package guide: $Name"
}
function Get-ExpectedGuideHeader {
    param([string]$Version, [string]$Mode)
    $taskExpectedMode = if ($Mode -eq 'self-contained') { '自包含（self-contained）' } else { '依赖框架（framework-dependent）' }
    $taskExpectedRuntime = if ($Mode -eq 'self-contained') { '本包已内置所需运行时，无需安装 .NET 或 SDK。' }
        else { '先安装 x64 .NET 10 Windows Desktop Runtime；仅安装普通 .NET Runtime 或 ASP.NET Runtime 不够，无需 SDK。' }
    return "# 炉石猎人助手快速开始`n`n版本：$Version`n发布方式：$taskExpectedMode`n运行要求：$taskExpectedRuntime`n平台：Windows x64`n`n"
}
function Read-StrictGuide {
    param([string]$Path)
    $taskBytes = [IO.File]::ReadAllBytes($Path)
    Assert-GuideCheck ($taskBytes.Length -gt 3) 'The generated guide was empty.'
    Assert-GuideCheck (-not ($taskBytes[0] -eq 0xef -and $taskBytes[1] -eq 0xbb -and $taskBytes[2] -eq 0xbf)) 'The generated guide contains a UTF-8 BOM.'
    $taskText = $taskUtf8.GetString($taskBytes)
    Assert-GuideCheck (-not $taskText.Contains("`r") -and $taskText.EndsWith("`n", [StringComparison]::Ordinal) -and -not $taskText.EndsWith("`n`n", [StringComparison]::Ordinal)) 'Guide output must use LF and one terminal newline.'
    return $taskText
}
function Assert-GenerationRejected {
    param([string]$Template, [string]$Version = '1.2.3', [string]$Mode = 'self-contained', [string]$Destination)
    $taskExisted = Test-Path -LiteralPath $Destination
    $taskBefore = if ($taskExisted -and (Test-Path -LiteralPath $Destination -PathType Leaf)) { [IO.File]::ReadAllBytes($Destination) } else { $null }
    $taskFailure = $null
    try { New-PackageQuickStart -TemplatePath $Template -Version $Version -DeploymentMode $Mode -Destination $Destination }
    catch { $taskFailure = $_ }
    Assert-GuideCheck ($null -ne $taskFailure -and -not [string]::IsNullOrWhiteSpace($taskFailure.Exception.Message)) 'Invalid generator input was accepted or failed without an explanation.'
    if (-not $taskExisted) { Assert-GuideCheck (-not (Test-Path -LiteralPath $Destination)) 'A rejected generation created a partial output file.' }
    elseif ($null -ne $taskBefore) {
        Assert-GuideCheck ([Convert]::ToBase64String($taskBefore) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($Destination))) 'A rejected generation changed the existing destination bytes.'
    }
}
function Assert-UserSteps {
    param([string[]]$Required, [string]$Meaning)
    foreach ($taskRequired in $Required) {
        Assert-GuideCheck ($taskReferenceGuide.Contains($taskRequired, [StringComparison]::Ordinal)) "The package guide lost an essential $Meaning step: $taskRequired"
    }
}

Test-GuideCase 'dot-sourcing defines the API without output or changing the working directory' {
    Assert-GuideCheck ($taskLoadOutput.Count -eq 0 -and (Get-Location).Path -eq $taskOriginalDirectory -and
        (Get-Command New-PackageQuickStart).CommandType -eq 'Function' -and (Get-Command Assert-PackageQuickStart).CommandType -eq 'Function') 'Loading the generator executed work or did not expose its API.'
}
$taskTemplateBefore = (Get-FileHash -LiteralPath $taskTemplate -Algorithm SHA256).Hash
$taskTemplateBody = [IO.File]::ReadAllText($taskTemplate, $taskUtf8).Replace("`r`n", "`n").Replace("`r", "`n").Trim([char[]]"`n")
foreach ($taskVersion in @('0.1.0-preview.314', '12.34.56-rc.7')) {
    foreach ($taskMode in @('self-contained', 'framework-dependent')) {
        Test-GuideCase "$taskVersion $taskMode has current facts, reproducible UTF-8 bytes and the intact stable body" {
            $taskDestination = Join-Path $taskTestRoot ("$taskVersion $taskMode first.md")
            $taskRepeat = Join-Path $taskTestRoot ("$taskVersion $taskMode repeated.md")
            New-PackageQuickStart -TemplatePath $taskTemplate -Version $taskVersion -DeploymentMode $taskMode -Destination $taskDestination
            New-PackageQuickStart -TemplatePath $taskTemplate -Version $taskVersion -DeploymentMode $taskMode -Destination $taskRepeat
            $taskGuide = Read-StrictGuide $taskDestination
            $taskExpected = (Get-ExpectedGuideHeader $taskVersion $taskMode) + $taskTemplateBody + "`n"
            Assert-GuideCheck ($taskGuide -ceq $taskExpected) 'A generated header has stale version/mode/runtime facts or the stable user steps were changed.'
            Assert-GuideCheck ([Convert]::ToBase64String([IO.File]::ReadAllBytes($taskDestination)) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($taskRepeat))) 'Identical generation inputs did not produce identical bytes.'
            foreach ($taskField in @('版本', '发布方式', '运行要求', '平台')) {
                Assert-GuideCheck ([regex]::Matches($taskGuide, "(?m)^${taskField}：").Count -eq 1) "The guide repeated or omitted its $taskField header."
            }
            Assert-GuideCheck ($taskGuide -notmatch '(?i)\b[0-9a-f]{40}\b|\b[0-9a-f]{64}\b|\.local[\\/]publish|sourceCommit|sourceDirty|scripts[\\/]publish\.ps1') 'The user guide leaked a source commit, fixed ZIP hash or local build/receipt details.'
            $taskOtherMode = if ($taskMode -eq 'self-contained') { '发布方式：依赖框架' } else { '发布方式：自包含' }
            Assert-GuideCheck (-not $taskGuide.Contains($taskOtherMode, [StringComparison]::Ordinal)) 'Both deployment modes appeared as current package facts.'
            Assert-PackageQuickStart -GuidePath $taskDestination -Version $taskVersion -DeploymentMode $taskMode
            $script:taskReferenceGuide = $taskGuide
        }
    }
}
Test-GuideCase 'generating guides leaves the public template byte-for-byte unchanged' {
    Assert-GuideCheck ((Get-FileHash -LiteralPath $taskTemplate -Algorithm SHA256).Hash -eq $taskTemplateBefore) 'The generator rewrote its source template.'
}
Test-GuideCase 'upgrade and data-location operations survive generation' {
    Assert-UserSteps @('%LOCALAPPDATA%\HsHelper', '备份', 'schema 3', '回退', '--data-dir', 'HSHELPER_DATA_DIR', '完整解压', 'HsHelper.App.exe') 'upgrade/data-location'
}
Test-GuideCase 'safe log setup remains an explicit user workflow' {
    Assert-UserSteps @('Power.log', '日志配置预览', '备份并启用日志', '自行重启炉石', '恢复上次原配置') 'log setup/recovery'
}
Test-GuideCase 'example import and version-pinned recording instructions survive generation' {
    Assert-UserSteps @('samples\decks\beast-hunter.txt', '保存到套牌库', '同步客户端版本卡库', '记录套牌', '开始监听') 'deck/build/recording'
}
Test-GuideCase 'AI consent, fixed historical scope and non-overwriting headless diagnostics survive generation' {
    Assert-UserSteps @('加密保存配置', '检查连接', '主动', '知识截止', '录制时刻', 'Unknown', '--diagnostic-output', '尚不存在', '不连接模型') 'privacy/history/diagnostics'
}
Test-GuideCase 'Chinese templates and space-containing paths normalize UTF-8 and final newlines consistently' {
    $taskUnicodeBody = "中文第一行：保留数据。`n第二行：手动核对日志。"
    $taskResults = @()
    $taskIndex = 0
    foreach ($taskVariant in @($taskUnicodeBody, ($taskUnicodeBody.Replace("`n", "`r`n") + "`r`n`r`n"), ("`n" + $taskUnicodeBody + "`n"))) {
        $taskInput = Join-Path $taskTestRoot ("中文 源模板 $taskIndex.md")
        $taskOutput = Join-Path $taskTestRoot ("中文 目的指南 $taskIndex.md")
        [IO.File]::WriteAllText($taskInput, $taskVariant, [Text.UTF8Encoding]::new($taskIndex -eq 1))
        New-PackageQuickStart -TemplatePath $taskInput -Version '2.3.4' -DeploymentMode 'self-contained' -Destination $taskOutput
        $taskRead = Read-StrictGuide $taskOutput
        Assert-GuideCheck ($taskRead -ceq ((Get-ExpectedGuideHeader '2.3.4' 'self-contained') + $taskUnicodeBody + "`n")) 'Unicode, interior line breaks or final-newline normalization changed the stable body.'
        $taskResults += [Convert]::ToBase64String([IO.File]::ReadAllBytes($taskOutput)); $taskIndex++
    }
    Assert-GuideCheck (@($taskResults | Select-Object -Unique).Count -eq 1) 'Equivalent UTF-8/BOM/CRLF template inputs produced different guide bytes.'
}
Test-GuideCase 'missing templates fail before creating output' {
    Assert-GenerationRejected -Template (Join-Path $taskTestRoot 'missing template.md') -Destination (Join-Path $taskTestRoot 'missing output.md')
}
Test-GuideCase 'unsupported deployment modes fail before creating output' {
    Assert-GenerationRejected -Template $taskTemplate -Mode 'portable' -Destination (Join-Path $taskTestRoot 'unsupported mode.md')
}
foreach ($taskInvalidVersion in @('1.2', '../1.2.3', '1.2.3-', "1.2.3`n", "1.2.3`n版本：forged")) {
    $taskVersionCase = [Array]::IndexOf(@('1.2', '../1.2.3', '1.2.3-', "1.2.3`n", "1.2.3`n版本：forged"), $taskInvalidVersion)
    Test-GuideCase "invalid version case $taskVersionCase fails before creating output" {
        Assert-GenerationRejected -Template $taskTemplate -Version $taskInvalidVersion -Destination (Join-Path $taskTestRoot "invalid version $taskVersionCase.md")
    }
}
foreach ($taskEmptyBody in @('', " `r`n`t`n")) {
    Test-GuideCase 'empty or whitespace-only template fails before creating output' {
        $taskPath = Join-Path $taskTestRoot ([Guid]::NewGuid().ToString('N') + ' empty source.md')
        [IO.File]::WriteAllText($taskPath, $taskEmptyBody, $taskUtf8)
        Assert-GenerationRejected -Template $taskPath -Destination ($taskPath + '.output.md')
    }
}
foreach ($taskReservedField in @('版本', '发布方式', '运行要求', '平台')) {
    Test-GuideCase "template cannot redefine the reserved $taskReservedField field" {
        $taskPath = Join-Path $taskTestRoot ($taskReservedField + ' reserved source.md')
        [IO.File]::WriteAllText($taskPath, "用户步骤。`n${taskReservedField}：forged`n", $taskUtf8)
        Assert-GenerationRejected -Template $taskPath -Destination ($taskPath + '.output.md')
    }
}
Test-GuideCase 'malformed UTF-8 templates fail rather than silently replace text' {
    $taskPath = Join-Path $taskTestRoot 'invalid utf8 source.md'
    [IO.File]::WriteAllBytes($taskPath, [byte[]]@(0xc3, 0x28))
    Assert-GenerationRejected -Template $taskPath -Destination ($taskPath + '.output.md')
}
Test-GuideCase 'existing destination files are never overwritten' {
    $taskExisting = Join-Path $taskTestRoot 'existing destination.md'
    [IO.File]::WriteAllText($taskExisting, "既有文件原内容，必须保留。`r`n", $taskUtf8)
    Assert-GenerationRejected -Template $taskTemplate -Destination $taskExisting
    Assert-GenerationRejected -Template $taskTemplate -Version 'invalid' -Destination $taskExisting
}
Test-GuideCase 'a destination that is already a directory is preserved' {
    $taskExisting = Join-Path $taskTestRoot 'existing directory'
    New-Item -ItemType Directory -Path $taskExisting | Out-Null
    Assert-GenerationRejected -Template $taskTemplate -Destination $taskExisting
    Assert-GuideCheck ((Test-Path -LiteralPath $taskExisting -PathType Container) -and @(Get-ChildItem -LiteralPath $taskExisting -Force).Count -eq 0) 'A rejected destination-directory write changed its contents.'
}
Write-Host "$taskGuideChecks package guide generator checks passed; synthetic files: $taskTestRoot"
