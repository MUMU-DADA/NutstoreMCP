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
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'NutstoreMcp\plugin')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  ✅ $text" -ForegroundColor Green }

Write-Host "构建 NutstoreMCP 插件" -ForegroundColor White
Write-Host "  配置: $Configuration"
Write-Host "  目标: $Target"
Write-Host ""

# ── 1. 构建 ──
Step '构建 Contract / Plugin / Bridge …'
foreach ($proj in 'NutstoreMcp.Plugin.Contract', 'NutstoreMcp.Plugin', 'NutstoreMcp.Bridge') {
    $out = & dotnet build (Join-Path $root "src\$proj") -c $Configuration -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host $out
        throw "$proj 构建失败"
    }
}
Ok '三个项目构建通过'

# ── 2. 同步插件到中立位置 ──
Step '同步插件 …'
if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Target | Out-Null

$pluginOut = Join-Path $root "src\NutstoreMcp.Plugin\bin\$Configuration"
Copy-Item "$pluginOut\*" $Target -Recurse -Force

# 桥也复制一份到插件目录：万一子 AppDomain 继承了 AppDomainManager，
# 桥在子域里也能被找到（它靠域名判断直接返回，不会递归）。
$bridgeOut = Join-Path $root "src\NutstoreMcp.Bridge\bin\$Configuration"
Copy-Item "$bridgeOut\NutstoreMcp.Bridge.dll" $Target -Force -ErrorAction SilentlyContinue

$dll = Join-Path $Target 'NutstoreMcp.Plugin.dll'
$cfg = Join-Path $Target 'NutstoreMcp.Plugin.dll.config'
if (-not (Test-Path $dll)) { throw "没有产出 $dll" }
if (-not (Test-Path $cfg)) { throw "缺少绑定重定向配置 $cfg（插件必须带自己的配置跑在独立 AppDomain 里）" }
Ok "插件已同步（$([math]::Round((Get-ChildItem $Target -File | Measure-Object Length -Sum).Sum/1MB,1)) MB）"

# ── 3. 写桥的配置 ──
Step '写桥的配置 …'
$configDir = Join-Path $env:LOCALAPPDATA 'NutstoreMcp'
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
