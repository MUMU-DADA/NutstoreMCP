<#
.SYNOPSIS
    卸载 NutstoreMCP（发布包版）。

.DESCRIPTION
    从坚果云客户端移除桥：停客户端 → 还原 NutstoreClient.exe.config →
    删除桥 DLL 与 bridge.ini → 重新启动客户端。

    默认保留插件目录、日志与台账；加 -Purge 同时删除本版本的插件目录。

    需要管理员权限（写 Program Files + 改客户端配置），脚本会自己弹 UAC。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Uninstall-NutstoreMCP.ps1
    powershell -ExecutionPolicy Bypass -File .\Uninstall-NutstoreMCP.ps1 -Purge
#>
[CmdletBinding()]
param([switch]$Purge)

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-NutstoreMCP.ps1'

if (-not (Test-Path $installer)) {
    Write-Host "找不到安装脚本: $installer" -ForegroundColor Red
    exit 1
}

$installArgs = @('-Uninstall')
if ($Purge) { $installArgs += '-Purge' }

& $installer @installArgs
exit $LASTEXITCODE
