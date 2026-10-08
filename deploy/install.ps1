<#
.SYNOPSIS
  把桥装进坚果云客户端目录（需要管理员），或卸载还原。

.DESCRIPTION
  改动只有三处，全部可回滚：
    1. 备份 NutstoreClient.exe.config
    2. 在 <runtime> 里加 appDomainManagerAssembly / appDomainManagerType
    3. 复制 NutstoreMcp.Bridge.dll 与 NutstoreMcp.Plugin.Contract.dll 到客户端目录

  出任何问题：.\deploy\install.ps1 -Uninstall 即可完全还原。

.EXAMPLE
  .\deploy\install.ps1              # 安装
  .\deploy\install.ps1 -Uninstall   # 卸载还原
  .\deploy\install.ps1 -WhatIf      # 只看会改什么
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Configuration = 'Debug',
    [switch]$Uninstall,
    [string]$ClientDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$AssemblyName = 'NutstoreMcp.Bridge'
$TypeName = 'NutstoreMcp.Bridge.BridgeDomainManager'
$BridgeFiles = @('NutstoreMcp.Bridge.dll', 'NutstoreMcp.Plugin.Contract.dll')
$BackupSuffix = '.nutstore-mcp-backup'

function Step($t) { Write-Host "  $t" -ForegroundColor Cyan }
function Ok($t) { Write-Host "  ✅ $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  ⚠️  $t" -ForegroundColor Yellow }

# ── 找客户端目录 ──
if (-not $ClientDir) {
    $exe = Get-ChildItem 'C:\Program Files\Nutstore' -Recurse -Filter 'NutstoreClient.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $exe) { throw '找不到 NutstoreClient.exe' }
    $ClientDir = $exe.DirectoryName
}

$configPath = Join-Path $ClientDir 'NutstoreClient.exe.config'
if (-not (Test-Path $configPath)) { throw "找不到客户端配置: $configPath" }
$backupPath = "$configPath$BackupSuffix"

Write-Host $(if ($Uninstall) { '卸载 NutstoreMCP 桥' } else { '安装 NutstoreMCP 桥' }) -ForegroundColor White
Write-Host "  客户端目录: $ClientDir"
Write-Host ""

# ── 管理员检查（-WhatIf 预演时跳过，方便先看会改什么）──
if (-not $WhatIfPreference) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw '需要管理员权限。请用「以管理员身份运行」的 PowerShell 重跑。'
    }
}

# ── 卸载 ──
if ($Uninstall) {
    Step '移除桥文件 …'
    foreach ($f in $BridgeFiles) {
        $p = Join-Path $ClientDir $f
        if (Test-Path $p) {
            if ($PSCmdlet.ShouldProcess($p, 'Remove')) { Remove-Item $p -Force }
            Ok "已删除 $f"
        }
    }

    Step '还原客户端配置 …'
    if (Test-Path $backupPath) {
        if ($PSCmdlet.ShouldProcess($configPath, 'Restore from backup')) {
            Copy-Item $backupPath $configPath -Force
        }
        Ok '已从备份还原'
    }
    else {
        # 没有备份（例如备份被删）时退而求其次：只摘掉我们加的两个元素
        if ($PSCmdlet.ShouldProcess($configPath, 'Strip appDomainManager elements')) {
            [xml]$xml = Get-Content $configPath -Raw
            $runtime = $xml.configuration.runtime
            foreach ($tag in 'appDomainManagerAssembly', 'appDomainManagerType') {
                $node = $runtime.SelectSingleNode($tag)
                if ($node) { $runtime.RemoveChild($node) | Out-Null }
            }
            $xml.Save($configPath)
        }
        Warn '没有备份，已按元素摘除（其余内容未动）'
    }

    Write-Host ''
    Write-Host '卸载完成。重启坚果云后生效。' -ForegroundColor Green
    return
}

# ── 安装 ──
# 1. 桥文件
Step '检查桥文件 …'
$bridgeOut = Join-Path $root "src\NutstoreMcp.Bridge\bin\$Configuration"
foreach ($f in $BridgeFiles) {
    $src = Join-Path $bridgeOut $f
    if (-not (Test-Path $src)) {
        throw "缺少 $f —— 先跑 .\deploy\build.ps1"
    }
}

foreach ($f in $BridgeFiles) {
    $src = Join-Path $bridgeOut $f
    $dst = Join-Path $ClientDir $f
    if ($PSCmdlet.ShouldProcess($dst, 'Copy')) { Copy-Item $src $dst -Force }
    Ok "已放入 $f"
}

# 2. 备份配置（只在第一次备份，避免把已打补丁的配置当成原始配置）
Step '备份客户端配置 …'
if (Test-Path $backupPath) {
    Warn "备份已存在，保留原备份不动: $BackupSuffix"
}
else {
    if ($PSCmdlet.ShouldProcess($backupPath, 'Create backup')) {
        Copy-Item $configPath $backupPath -Force
    }
    Ok "已备份到 NutstoreClient.exe.config$BackupSuffix"
}

# 3. 打补丁
Step '写入 appDomainManager 配置 …'
[xml]$xml = Get-Content $configPath -Raw
$runtime = $xml.configuration.runtime
if (-not $runtime) { throw '<runtime> 节点不存在 —— 客户端配置结构异常，放弃' }

foreach ($pair in @{ appDomainManagerAssembly = $AssemblyName; appDomainManagerType = $TypeName }.GetEnumerator()) {
    $existing = $runtime.SelectSingleNode($pair.Key)
    if ($existing) {
        if ($existing.value -ne $pair.Value) {
            throw "<runtime> 里已存在 $($pair.Key) 且值不同（$($existing.value)）—— 已有别的工具在用这个扩展点，放弃以免冲突"
        }
        Ok "$($pair.Key) 已存在且一致"
        continue
    }

    $el = $xml.CreateElement($pair.Key)
    $el.SetAttribute('value', $pair.Value)
    # 插到 <runtime> 最前面：部分配置节的顺序有 schema 要求，放前面最安全
    if ($runtime.FirstChild) { $runtime.InsertBefore($el, $runtime.FirstChild) | Out-Null }
    else { $runtime.AppendChild($el) | Out-Null }
    Ok "已写入 $($pair.Key) = $($pair.Value)"
}

if ($PSCmdlet.ShouldProcess($configPath, 'Save')) { $xml.Save($configPath) }

# 4. 检查桥的配置是否有指向
$ini = Join-Path $env:LOCALAPPDATA 'NutstoreMcp\bridge.ini'
Write-Host ''
if (Test-Path $ini) {
    $homeLine = (Get-Content $ini | Where-Object { $_ -match '^\s*home\s*=' }) -replace '^\s*home\s*=\s*', ''
    if ($homeLine -and (Test-Path $homeLine)) {
        Ok "插件目录: $homeLine"
    }
    else {
        Warn "bridge.ini 里的 home 指向不存在: $homeLine"
        Warn '跑一次 .\deploy\build.ps1 重新生成'
    }
}
else {
    Warn "缺少 $ini —— 桥会什么都不做。先跑 .\deploy\build.ps1"
}

Write-Host ''
Write-Host '安装完成。' -ForegroundColor Green
Write-Host ''
Write-Host '现在重启坚果云客户端。启动后检查：' -ForegroundColor White
Write-Host "  Get-Content `"$env:LOCALAPPDATA\NutstoreMcp\logs\bridge.log`" -Tail 20"
Write-Host "  Get-Content `"$env:LOCALAPPDATA\NutstoreMcp\logs\plugin.log`" -Tail 20"
Write-Host ''
Write-Host '出问题就还原：.\deploy\install.ps1 -Uninstall' -ForegroundColor Yellow
