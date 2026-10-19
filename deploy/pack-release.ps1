<#
.SYNOPSIS
    打一个「最终用户可以直接安装」的发布包（zip）。

.DESCRIPTION
    发布包与仓库源码的区别：它不含源码、不需要 .NET SDK ——
    解压后运行 Install-NutstoreMCP.ps1 即可安装到已装好的坚果云客户端。

    产物（默认在 dist\ 下）：
      NutstoreMCP-v<版本>-win-x64\        解压后的目录（可直接用）
      NutstoreMCP-v<版本>-win-x64.zip     给 GitHub Release 上传的压缩包
      NutstoreMCP-v<版本>-win-x64.zip.sha256

    注意：Release 包里的 .ps1 统一带 UTF-8 BOM —— Windows PowerShell 5.1
    在没有 BOM 时会把中文按 ANSI 解码，脚本输出会乱码。

.EXAMPLE
    .\deploy\pack-release.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Configuration = 'Release',
    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$releaseDir = Join-Path $PSScriptRoot 'release'

function Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  ✅ $text" -ForegroundColor Green }

Write-Host "打包 NutstoreMCP v$Version（$Configuration）" -ForegroundColor White

# ── 1. 构建 ──
Step '构建 Contract / Bridge / Plugin / UiHook …'
$projects = @(
    'src\NutstoreMcp.Plugin.Contract\NutstoreMcp.Plugin.Contract.csproj',
    'src\NutstoreMcp.Bridge\NutstoreMcp.Bridge.csproj',
    'src\NutstoreMcp.Plugin\NutstoreMcp.Plugin.csproj',
    'src\NutstoreMcp.UiHook\NutstoreMcp.UiHook.csproj'
)
foreach ($proj in $projects) {
    $out = & dotnet build (Join-Path $root $proj) -c $Configuration -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host $out
        throw "$proj 构建失败"
    }
}
Ok '四个项目构建通过'

# ── 2. 准备目录 ──
$stageRoot = Join-Path $root $OutDir
$name = "NutstoreMCP-v$Version-win-x64"
$stage = Join-Path $stageRoot $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'plugin') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'bridge') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'tools') | Out-Null

# ── 3. 插件（含依赖 + UI 钩子 + 契约） ──
Step '收集插件文件 …'
$pluginOut = Join-Path $root "src\NutstoreMcp.Plugin\bin\$Configuration"
Copy-Item "$pluginOut\*" (Join-Path $stage 'plugin') -Recurse -Force
Copy-Item (Join-Path $root "src\NutstoreMcp.UiHook\bin\$Configuration\NutstoreMcp.UiHook.dll") (Join-Path $stage 'plugin') -Force
Copy-Item (Join-Path $root "src\NutstoreMcp.Plugin.Contract\bin\$Configuration\NutstoreMcp.Plugin.Contract.dll") (Join-Path $stage 'plugin') -Force
$pluginCount = @(Get-ChildItem (Join-Path $stage 'plugin') -File).Count
Ok "插件 $pluginCount 个文件"

# ── 4. 桥（安装时复制进客户端目录） ──
Step '收集桥文件 …'
$bridgeOut = Join-Path $root "src\NutstoreMcp.Bridge\bin\$Configuration"
Copy-Item (Join-Path $bridgeOut 'NutstoreMcp.Bridge.dll') (Join-Path $stage 'bridge') -Force
Copy-Item (Join-Path $bridgeOut 'NutstoreMcp.Plugin.Contract.dll') (Join-Path $stage 'bridge') -Force
Ok '桥 2 个文件'

# ── 5. 安装脚本与公用逻辑（统一转 UTF-8 BOM） ──
Step '收集安装脚本 …'
function Write-Utf8Bom([string]$from, [string]$to) {
    $text = [System.IO.File]::ReadAllText($from)
    [System.IO.File]::WriteAllText($to, $text, (New-Object System.Text.UTF8Encoding($true)))
}

Write-Utf8Bom (Join-Path $releaseDir 'Install-NutstoreMCP.ps1') (Join-Path $stage 'Install-NutstoreMCP.ps1')
Write-Utf8Bom (Join-Path $releaseDir 'Uninstall-NutstoreMCP.ps1') (Join-Path $stage 'Uninstall-NutstoreMCP.ps1')
Write-Utf8Bom (Join-Path $PSScriptRoot '_client.ps1') (Join-Path $stage 'tools\_client.ps1')
Write-Utf8Bom (Join-Path $PSScriptRoot '_config.ps1') (Join-Path $stage 'tools\_config.ps1')
Copy-Item (Join-Path $releaseDir 'README.md') (Join-Path $stage 'README.md') -Force
[System.IO.File]::WriteAllText((Join-Path $stage 'VERSION'), $Version, (New-Object System.Text.UTF8Encoding($false)))
Ok '安装/卸载脚本与工具就绪'

# ── 6. 压缩 + 校验和 ──
Step '压缩 …'
$zip = Join-Path $stageRoot "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$zipLength = (Get-Item $zip).Length
Ok ("压缩包 {0:N1} MB" -f ($zipLength / 1MB))

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$hashFile = "$zip.sha256"
[System.IO.File]::WriteAllText($hashFile, "$hash  $name.zip`n", (New-Object System.Text.UTF8Encoding($false)))
Ok "SHA256: $hash"

Write-Host ''
Write-Host "发布目录: $stage" -ForegroundColor Green
Write-Host "压缩包  : $zip" -ForegroundColor Green
Write-Host "校验和  : $hashFile" -ForegroundColor Green
Write-Host ''
Write-Host '自测建议：解压到临时目录运行 Install-NutstoreMCP.ps1，确认 tools/list 数量正确。' -ForegroundColor DarkGray
