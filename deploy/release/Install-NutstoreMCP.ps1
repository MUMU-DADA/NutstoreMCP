<#
.SYNOPSIS
    NutstoreMCP 一键安装（发布包版）。

.DESCRIPTION
    把发布包里的桥与插件装进已安装的坚果云客户端：
      1. 停掉坚果云（含 watchdog），桥 DLL 被加载时无法替换；
      2. 复制 NutstoreMcp.Bridge.dll / NutstoreMcp.Plugin.Contract.dll 到客户端目录；
      3. 备份并原子地给 NutstoreClient.exe.config 加 appDomainManager 两项；
      4. 把插件解到 %LOCALAPPDATA%\NutstoreMcp\plugin-<版本>，写 bridge.ini 指过去；
      5. 启动客户端，等 MCP 端点就绪并读出工具数量。

    需要管理员权限（写 Program Files + 改客户端配置），脚本会自己弹 UAC。

.PARAMETER Uninstall
    卸载：移除桥 DLL，还原客户端配置，删除 bridge.ini。插件目录默认保留。

.PARAMETER Purge
    与 -Uninstall 连用：同时删除本版本的插件目录（不动日志与台账）。

.PARAMETER Force
    客户端不是 7.2.12.0 时仍继续（默认拒绝，避免符号漂移导致怪异行为）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-NutstoreMCP.ps1
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$Force,

    # 内部参数：提权后的自己。
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$PackageRoot = $PSScriptRoot
$ExpectedClientVersion = '7.2.12'

function Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  ✅ $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  ⚠️  $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host "  ❌ $text" -ForegroundColor Red }

function Test-Admin {
    ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Wait-IfElevated {
    if (-not $Elevated) { return }
    Write-Host ''
    Write-Host '按 Enter 关闭此窗口…' -ForegroundColor DarkGray
    try { Read-Host | Out-Null } catch { Start-Sleep -Seconds 3 }
}

# ── 自提权 ──
if (-not (Test-Admin) -and -not $Elevated) {
    Write-Host '需要管理员权限，正在通过 UAC 提权…' -ForegroundColor Yellow

    $passthrough = @()
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Key -in @('Elevated')) { continue }
        if ($kv.Value -is [switch]) {
            if ($kv.Value.IsPresent) { $passthrough += "-$($kv.Key)" }
        }
    }

    $hostExe = $null
    try { $hostExe = (Get-Process -Id $PID).Path } catch { }
    if (-not $hostExe) { $hostExe = 'powershell.exe' }

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated') + $passthrough
    try {
        Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $argList -WorkingDirectory $PackageRoot | Out-Null
    }
    catch {
        Fail '提权被取消或失败。'
        Write-Host '   也可以自己开一个「以管理员身份运行」的 PowerShell 再跑本脚本。' -ForegroundColor DarkGray
        exit 1
    }

    Write-Host '已在新的管理员窗口中继续运行，请看那个窗口的输出。' -ForegroundColor Green
    exit 0
}

if (-not (Test-Admin)) {
    Fail '仍没有管理员权限 —— 提权未生效。请手动以管理员身份运行 PowerShell 再试。'
    Wait-IfElevated
    exit 1
}

# 共用逻辑（客户端目录发现、配置补丁）
. (Join-Path $PackageRoot 'tools\_client.ps1')
. (Join-Path $PackageRoot 'tools\_config.ps1')

$BridgeFiles = @('NutstoreMcp.Bridge.dll', 'NutstoreMcp.Plugin.Contract.dll')
$AppDomainAssembly = 'NutstoreMcp.Bridge'
$AppDomainType = 'NutstoreMcp.Bridge.BridgeDomainManager'
$BackupSuffix = '.nutstore-mcp-backup'
$WorkRoot = Join-Path $env:LOCALAPPDATA 'NutstoreMcp'
$BridgeIni = Join-Path $WorkRoot 'bridge.ini'
$EnabledState = Join-Path $WorkRoot 'enabled.state'

$VersionFile = Join-Path $PackageRoot 'VERSION'
$Version = if (Test-Path $VersionFile) { (Get-Content $VersionFile -Raw).Trim() } else { 'unknown' }

Write-Host ''
Write-Host $(if ($Uninstall) { "卸载 NutstoreMCP $Version" } else { "安装 NutstoreMCP $Version" }) -ForegroundColor White
Write-Host "  发布包: $PackageRoot"
Write-Host ''

# ── 找客户端 ──
$ClientDir = Find-NutstoreClientDir
if (-not $ClientDir) {
    Fail '找不到坚果云客户端（C:\Program Files\Nutstore 下没有 bin-* 目录）。'
    Write-Host '   请先安装坚果云桌面客户端并至少启动一次。' -ForegroundColor DarkGray
    Wait-IfElevated
    exit 1
}
Ok "客户端目录: $ClientDir"

$clientVersionOk = (Split-Path $ClientDir -Leaf) -match [regex]::Escape($ExpectedClientVersion)
if (-not $clientVersionOk) {
    if (-not $Force) {
        Fail "当前客户端不是 $ExpectedClientVersion（目录 $((Split-Path $ClientDir -Leaf))）。"
        Write-Host '   本发布包按 7.2.12.0 的混淆符号构建；客户端升级后符号可能变化。' -ForegroundColor Yellow
        Write-Host '   确认要继续请加 -Force；否则请安装 7.2.12 客户端。' -ForegroundColor Yellow
        Wait-IfElevated
        exit 1
    }
    Warn "客户端版本不是 $ExpectedClientVersion，按 -Force 继续（行为可能不符合预期）。"
}

$configPath = Join-Path $ClientDir 'NutstoreClient.exe.config'
if (-not (Test-Path $configPath)) {
    Fail "找不到客户端配置: $configPath"
    Wait-IfElevated
    exit 1
}
$backupPath = "$configPath$BackupSuffix"

# ── 停客户端 ──
function Stop-NutstoreClient {
    $watchdog = Get-Process -Name 'nutstore_watchdog' -ErrorAction SilentlyContinue
    if ($watchdog) {
        Stop-Process -Name 'nutstore_watchdog' -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    $client = Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue
    if ($client) {
        Stop-Process -Name 'NutstoreClient' -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 1
        }
    }

    if (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue) {
        throw '坚果云仍在运行，无法安全替换 DLL。请手动退出后重试。'
    }
}

function Start-NutstoreClient {
    $exe = Join-Path $ClientDir 'NutstoreClient.exe'
    Start-Process -FilePath $exe

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 10
    if (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue) {
        return $true
    }

    # 兜底：直接 Start-Process 没站住时，用计划任务在用户会话里启动。
    # （提权上下文里偶尔会出现「起来了又立刻退出」，计划任务更稳。）
    $taskName = 'NutstoreMcp-StartClient'
    & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
    & schtasks.exe /Create /TN $taskName /TR "`"$exe`"" /SC ONCE /ST 00:00 /RL LIMITED /F 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & schtasks.exe /Run /TN $taskName 2>&1 | Out-Null
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline -and -not (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 2
        }
        & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
        Start-Sleep -Seconds 8
    }

    return [bool](Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)
}

Step '停止坚果云 …'
Stop-NutstoreClient
Ok '已退出'

# ── 卸载 ──
if ($Uninstall) {
    Step '移除桥文件 …'
    foreach ($f in $BridgeFiles) {
        $p = Join-Path $ClientDir $f
        if (Test-Path $p) { Remove-Item $p -Force; Ok "已删除 $f" }
    }

    Step '还原客户端配置 …'
    if (Test-Path $backupPath) {
        $restoreTmp = "$configPath.nutstore-mcp-restore"
        try {
            Copy-Item $backupPath $restoreTmp -Force
            $verify = New-Object System.Xml.XmlDocument
            $verify.Load($restoreTmp)
            if (-not $verify.configuration) { throw '备份文件没有 <configuration> 根节点，拒绝还原' }
            [System.IO.File]::Replace($restoreTmp, $configPath, [NullString]::Value)
            Ok '已从备份原子还原'
        }
        finally {
            Remove-Item $restoreTmp -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        $xml = Read-ClientConfig -ConfigPath $configPath
        $removed = Remove-AppDomainManager -Xml $xml
        Save-ClientConfigAtomically -Xml $xml -ConfigPath $configPath -ExpectedValues @{}
        Ok "已摘除 $removed 个元素（没有备份）"
    }

    if (Test-Path $BridgeIni) { Remove-Item $BridgeIni -Force; Ok '已删除 bridge.ini' }

    if ($Purge) {
        $pluginDir = Get-ChildItem $WorkRoot -Directory -Filter "plugin-$Version*" -ErrorAction SilentlyContinue
        foreach ($d in $pluginDir) { Remove-Item $d.FullName -Recurse -Force; Ok "已删除插件目录 $($d.Name)" }
    }
    else {
        Warn '插件目录与日志/台账保留（加 -Purge 可删除本版本插件目录）'
    }

    Step '重新启动坚果云 …'
    if (Start-NutstoreClient) {
        Ok '已启动（bridge 已卸载，MCP 端点不会再监听）'
    }
    else {
        Warn '客户端没有保持运行，请手动启动坚果云'
    }

    Write-Host ''
    Write-Host '卸载完成。' -ForegroundColor Green
    Wait-IfElevated
    return
}

# ── 安装：检查发布包内容 ──
Step '检查发布包 …'
$payloads = @(
    (Join-Path $PackageRoot 'bridge\NutstoreMcp.Bridge.dll'),
    (Join-Path $PackageRoot 'bridge\NutstoreMcp.Plugin.Contract.dll'),
    (Join-Path $PackageRoot 'plugin\NutstoreMcp.Plugin.dll'),
    (Join-Path $PackageRoot 'plugin\NutstoreMcp.UiHook.dll')
)
foreach ($p in $payloads) {
    if (-not (Test-Path $p)) { Fail "发布包缺少文件: $p"; Wait-IfElevated; exit 1 }
}
Ok "发布包完整（版本 $Version）"

# ── 安装：部署插件目录 ──
Step "部署插件到 $WorkRoot …"
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null
$pluginHome = Join-Path $WorkRoot "plugin-$Version"
if (Test-Path $pluginHome) { Remove-Item $pluginHome -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pluginHome | Out-Null
Copy-Item (Join-Path $PackageRoot 'plugin\*') $pluginHome -Recurse -Force
Ok "插件目录: $pluginHome"

$iniText = @"
# NutstoreMCP 桥的配置
#
# 这个文件在用户目录下，改它不需要管理员权限。
# 桥在客户端启动时读它，按 home 指向的目录加载插件。
# enabled=false 可以临时停用插件（需要重启客户端）。

enabled=true
home=$pluginHome
"@
[System.IO.File]::WriteAllText($BridgeIni, $iniText, (New-Object System.Text.UTF8Encoding($false)))
Ok "已写入 $BridgeIni"

if (-not (Test-Path $EnabledState)) {
    [System.IO.File]::WriteAllText($EnabledState, 'true', (New-Object System.Text.UTF8Encoding($false)))
    Ok '已创建 enabled.state（默认启用）'
}

# ── 安装：桥 DLL + 配置补丁 ──
Step '安装桥 …'
foreach ($f in $BridgeFiles) {
    Copy-Item (Join-Path $PackageRoot "bridge\$f") (Join-Path $ClientDir $f) -Force
    Ok "已放入 $f"
}

Step '备份并修改客户端配置 …'
if (Test-Path $backupPath) {
    Warn '备份已存在，保留原备份不动'
}
else {
    Copy-Item $configPath $backupPath -Force
    Ok '已备份 NutstoreClient.exe.config'
}

$xml = Read-ClientConfig -ConfigPath $configPath
$changed = Add-AppDomainManager -Xml $xml -AssemblyName $AppDomainAssembly -TypeName $AppDomainType
if ($changed) {
    Save-ClientConfigAtomically -Xml $xml -ConfigPath $configPath -ExpectedValues @{
        appDomainManagerAssembly = $AppDomainAssembly
        appDomainManagerType     = $AppDomainType
    }
    Ok '配置已原子写入（appDomainManager 两项）'
}
else {
    Ok '配置里已存在一致的两项，无需改动'
}

# ── 启动并验证 ──
Step '启动坚果云 …'
if (-not (Start-NutstoreClient)) {
    Warn '客户端没有保持运行 —— 请手动启动坚果云，并检查是否有错误弹窗'
    Wait-IfElevated
    exit 1
}
Ok "客户端已启动（PID $((Get-Process -Name 'NutstoreClient' | Select-Object -First 1).Id)）"

Step '等待 MCP 端点 …'
$mcpUp = $false
$toolCount = 0
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    try {
        $body = '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
        $response = Invoke-RestMethod -Uri 'http://127.0.0.1:17321/mcp' -Method Post `
            -ContentType 'application/json' -TimeoutSec 5 -Body $body
        if ($response.result.tools) {
            $toolCount = @($response.result.tools).Count
            $mcpUp = $true
            break
        }
    }
    catch {
        Start-Sleep -Seconds 2
    }
}

if ($mcpUp) {
    Ok "MCP 端点已就绪：http://127.0.0.1:17321/mcp（tools/list = $toolCount 项）"
}
else {
    Warn '90 秒内没等到 MCP 端点。'
    Write-Host "     桥日志: $WorkRoot\logs\bridge.log" -ForegroundColor DarkGray
    Write-Host "     插件日志: $pluginHome\logs\plugin.log" -ForegroundColor DarkGray
    Write-Host '     常见原因：客户端没有加载桥（配置未生效/被安全软件拦截），重启客户端再看日志。' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '安装完成。把下面这段加到你的 MCP 客户端配置里：' -ForegroundColor White
Write-Host ''
Write-Host '  transport: streamable-http' -ForegroundColor Gray
Write-Host '  url: http://127.0.0.1:17321/mcp' -ForegroundColor Gray
Write-Host ''
Write-Host "  插件目录: $pluginHome" -ForegroundColor DarkGray
Write-Host "  卸载: powershell -ExecutionPolicy Bypass -File `"$PackageRoot\Uninstall-NutstoreMCP.ps1`"" -ForegroundColor DarkGray
Wait-IfElevated
