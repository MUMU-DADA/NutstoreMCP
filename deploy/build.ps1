<#
.SYNOPSIS
  构建插件与桥，并把插件同步到一个中立位置。

.DESCRIPTION
  插件**不放在程序目录**里。原因有二：
    1. 程序目录要提权才能写，而插件天天重编；
    2. 实测：从工作区目录启动/加载的进程在访问 HttpListener 时会被限制，
       放到 %LOCALAPPDATA% 下才稳定。

  所以：真正跑的是 %LOCALAPPDATA%\NutstoreMcp\plugin\，
  这个脚本负责把它从项目目录同步过去。改完代码跑一次即可，不需要管理员。

.EXAMPLE
  .\deploy\build.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',

    # 插件部署的根目录。实际输出到它下面的 plugin-<时间戳>\，
    # 每次构建一个新目录 —— 详见下面「为什么版本化」的说明。
    [string]$BaseDir = (Join-Path $env:LOCALAPPDATA 'NutstoreMcp')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  ✅ $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  ⚠️  $text" -ForegroundColor Yellow }

Write-Host "构建 NutstoreMCP 插件" -ForegroundColor White
Write-Host "  配置  : $Configuration"
Write-Host "  部署根: $BaseDir"
Write-Host ""

# ── 1. 构建 ──
Step '构建 Contract / Plugin / UiHook / Bridge …'
foreach ($proj in 'NutstoreMcp.Plugin.Contract', 'NutstoreMcp.Plugin', 'NutstoreMcp.UiHook', 'NutstoreMcp.Bridge') {
    $out = & dotnet build (Join-Path $root "src\$proj") -c $Configuration -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host $out
        throw "$proj 构建失败"
    }
}
Ok '四个项目构建通过'

# ── 2. 同步插件到版本化目录 ──
#
# 每次构建输出到**新的** plugin-<时间戳> 目录，然后改 bridge.ini 指过去。
# 为什么不原地覆盖：插件跑在客户端进程里，DLL 被加载着就是锁的 ——
# 原地覆盖会失败，于是每次改代码都得先关客户端。版本化目录让「构建」和
# 「客户端在跑」互不干扰：随时构建，下次重启客户端自动用上新版本。
Step '同步插件 …'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Target = Join-Path $BaseDir "plugin-$stamp"

New-Item -ItemType Directory -Force -Path $Target | Out-Null

$pluginOut = Join-Path $root "src\NutstoreMcp.Plugin\bin\$Configuration"
Copy-Item "$pluginOut\*" $Target -Recurse -Force

# UI 钩子也放插件目录：它要跟着插件一起迭代，而程序目录写入要提权。
# 它只引用 WPF(GAC) 和契约，没有版本冲突风险，桥会把它加载进客户端**默认域**。
$hookOut = Join-Path $root "src\NutstoreMcp.UiHook\bin\$Configuration"
Copy-Item "$hookOut\NutstoreMcp.UiHook.dll" $Target -Force -ErrorAction SilentlyContinue
Copy-Item "$hookOut\NutstoreMcp.Plugin.Contract.dll" $Target -Force -ErrorAction SilentlyContinue

# 桥也复制一份到插件目录：万一子 AppDomain 继承了 AppDomainManager，
# 桥在子域里也能被找到（它靠域名判断直接返回，不会递归）。
$bridgeOut = Join-Path $root "src\NutstoreMcp.Bridge\bin\$Configuration"
Copy-Item "$bridgeOut\NutstoreMcp.Bridge.dll" $Target -Force -ErrorAction SilentlyContinue

$dll = Join-Path $Target 'NutstoreMcp.Plugin.dll'
$cfg = Join-Path $Target 'NutstoreMcp.Plugin.dll.config'
$hook = Join-Path $Target 'NutstoreMcp.UiHook.dll'
if (-not (Test-Path $dll)) { throw "没有产出 $dll" }
if (-not (Test-Path $cfg)) { throw "缺少绑定重定向配置 $cfg（插件必须带自己的配置跑在独立 AppDomain 里）" }
if (-not (Test-Path $hook)) { throw "缺少 UI 钩子 $hook（界面相关的工具会全部不可用）" }

# 日志写到共享位置：版本化目录会被清理，日志不该跟着消失
$logDir = Join-Path $BaseDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
@{
    enabled = $true
    port    = 17321
    logPath = (Join-Path $logDir 'plugin.log')
} | ConvertTo-Json | Set-Content (Join-Path $Target 'plugin.json') -Encoding UTF8

Ok "插件已同步到 plugin-$stamp（$([math]::Round((Get-ChildItem $Target -File | Measure-Object Length -Sum).Sum/1MB,1)) MB）"

# 清理旧版本：被运行中的客户端占着的删不掉，忽略即可
Step '清理旧版本（保留最近 3 个）…'
$all = Get-ChildItem $BaseDir -Directory -Filter 'plugin-*' |
    Sort-Object Name -Descending
$all | Select-Object -Skip 3 | ForEach-Object {
    try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop; Ok "已删除 $($_.Name)" }
    catch { Warn "$($_.Name) 仍被占用（客户端在跑），下次再清" }
}

# ── 3. 写桥的配置 ──
Step '写桥的配置 …'
$configDir = $BaseDir
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
$ini = Join-Path $configDir 'bridge.ini'

@"
# NutstoreMCP 桥的配置
#
# 这个文件在用户目录下，**改它不需要管理员权限**。
# 桥在客户端启动时读它，按 home 指向的目录加载插件。
#
# enabled=false 可以临时停用插件（需要重启客户端）。
# 删除本文件同样会让桥什么都不做。

enabled=true
home=$Target
"@ | Set-Content $ini -Encoding UTF8

Ok "配置已写入 $ini"

Write-Host ""
Write-Host "下一步：" -ForegroundColor White
Write-Host "  以**管理员**身份运行：.\deploy\install.ps1"
Write-Host "  它会备份客户端配置、放入桥、打补丁。之后重启坚果云即可。"
