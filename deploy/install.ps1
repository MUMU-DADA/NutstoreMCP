<#
.SYNOPSIS
  把桥装进坚果云客户端目录，或卸载还原。会自动请求管理员权限。

.DESCRIPTION
  改动只有三处，全部可回滚：
    1. 备份 NutstoreClient.exe.config
    2. 在 <runtime> 里加 appDomainManagerAssembly / appDomainManagerType
    3. 复制 NutstoreMcp.Bridge.dll 与 NutstoreMcp.Plugin.Contract.dll 到客户端目录

  没有管理员权限时会**自动通过 UAC 提权**（新开一个窗口跑）。

  出任何问题：.\deploy\install.ps1 -Uninstall 即可完全还原。

.EXAMPLE
  .\deploy\install.ps1              # 安装（会弹 UAC）
  .\deploy\install.ps1 -Uninstall   # 卸载还原（会弹 UAC）
  .\deploy\install.ps1 -WhatIf      # 只看会改什么（不需要管理员，不弹 UAC）
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Configuration = 'Debug',
    [switch]$Uninstall,
    [string]$ClientDir,

    # 内部参数：标记「本次是提权后重启的自己」。
    # 没有它的话，万一提权后仍不是管理员就会无限弹 UAC。
    [switch]$Elevated
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

function Test-Admin {
    ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 提权时新开的窗口跑完就关，用户根本来不及看输出 —— 停一下等他读完。
# 只在 -Elevated（我们自己弹出来的那个窗口）里停，正常手跑不会莫名其妙卡住。
function Wait-IfElevated {
    if (-not $Elevated) { return }

    Write-Host ''
    Write-Host '按 Enter 关闭此窗口…' -ForegroundColor DarkGray
    try { Read-Host | Out-Null } catch { Start-Sleep -Seconds 3 }
}

# ── 自提权 ──
# 放在最前面：让提权后的进程从头完整跑一遍，而不是跑到一半才发现权限不够
# （那样会留下半成品状态 —— 文件拷了、配置没改，最难排查）。
# -WhatIf 是纯预演，不需要管理员，也不该弹 UAC。
if (-not (Test-Admin) -and -not $WhatIfPreference -and -not $Elevated) {
    Write-Host '需要管理员权限，正在通过 UAC 提权…' -ForegroundColor Yellow

    # 用 $PSBoundParameters 而不是 $args —— 脚本声明了 param 块，$args 里是空的
    $passthrough = @()
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Key -in @('WhatIf', 'Confirm', 'Elevated')) { continue }

        if ($kv.Value -is [switch]) {
            if ($kv.Value.IsPresent) { $passthrough += "-$($kv.Key)" }
        }
        else {
            $passthrough += "-$($kv.Key)"
            $passthrough += "`"$($kv.Value)`""
        }
    }

    $selfPath = $PSCommandPath
    if (-not $selfPath) { $selfPath = $MyInvocation.MyCommand.Path }

    # 用当前宿主可执行文件重新拉起自己 —— 用户用什么跑的就用什么提权，
    # 不会出现「pwsh 里跑却弹出 powershell.exe」这种割裂。
    $hostExe = $null
    try { $hostExe = (Get-Process -Id $PID).Path } catch { }
    if (-not $hostExe) {
        try { $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName } catch { }
    }
    if (-not $hostExe) { $hostExe = 'powershell.exe' }

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$selfPath`"", '-Elevated') + $passthrough

    try {
        Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList $argList -WorkingDirectory $root | Out-Null
    }
    catch {
        Write-Host ''
        Write-Host '❌ 提权被取消或失败。' -ForegroundColor Red
        Write-Host '   也可以自己开一个「以管理员身份运行」的 PowerShell 再跑本脚本：' -ForegroundColor Yellow
        Write-Host "     cd `"$root`"" -ForegroundColor Gray
        Write-Host "     .\deploy\install.ps1" -ForegroundColor Gray
        exit 1
    }

    Write-Host ''
    Write-Host '已在新的管理员窗口中继续运行，请看那个窗口的输出。' -ForegroundColor Green
    exit 0
}

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

# ── 权限兜底 ──
# 上面已经自提权过；走到这里还不是管理员，说明提权没生效（策略限制等）。
# 与其写坏一半再失败，不如现在停下。
if (-not (Test-Admin) -and -not $WhatIfPreference) {
    throw '仍没有管理员权限 —— 提权未生效。请手动开一个「以管理员身份运行」的 PowerShell 再跑。'
}

# ── 客户端必须先关掉 ──
# 桥 DLL 被运行中的客户端加载着，文件是锁的，Copy-Item 会失败。
# 而且改配置也要等重启才生效 —— 与其拷到一半失败留下不一致状态，不如直接说清楚。
if (-not $WhatIfPreference) {
    $running = Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host ''
        Write-Host "❌ 坚果云客户端正在运行（PID $($running.Id -join ', ')）" -ForegroundColor Red
        Write-Host ''
        Write-Host '   桥的 DLL 被它加载着，现在替换会失败。请先退出坚果云，再重跑本脚本。' -ForegroundColor Yellow
        Write-Host '   （退出后不需要手动启动 —— 装完再启动就行）' -ForegroundColor DarkGray
        Wait-IfElevated
        exit 1
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
    Wait-IfElevated
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

Wait-IfElevated
