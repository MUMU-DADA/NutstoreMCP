# 发布为自包含可执行文件 —— 目标机器不需要安装 .NET。
#
# 用法：
#   .\publish.ps1              发布到 publish\
#   .\publish.ps1 -Output dist 发布到指定目录
#
# 刻意**不加** -p:IncludeNativeLibrariesForSelfExtract=true：
# 那会把原生 SQLite 压进 exe，运行时解包到 %TEMP%\.net。
# 在 %TEMP% 对进程不可写的机器上会直接启动失败（实测 error code: 5）。
# 两个文件的形态在哪都能跑。

[CmdletBinding()]
param(
    [string]$Output = 'publish',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "发布 nutstore-mcp（$Configuration / win-x64 / 自包含）…" -ForegroundColor Cyan

dotnet publish (Join-Path $root 'src/NutstoreMcp.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o (Join-Path $root $Output) `
    -v q --nologo

if ($LASTEXITCODE -ne 0) {
    throw "发布失败（退出码 $LASTEXITCODE）"
}

$exe = Join-Path $root "$Output/nutstore-mcp.exe"
$dll = Join-Path $root "$Output/e_sqlite3.dll"

if (-not (Test-Path $exe)) {
    throw "没有产出 $exe"
}
if (-not (Test-Path $dll)) {
    Write-Warning "缺少 e_sqlite3.dll —— L2（本地只读）会在运行时报错。请检查发布参数。"
}

$totalMb = [math]::Round(((Get-ChildItem (Join-Path $root $Output) -File | Measure-Object Length -Sum).Sum / 1MB), 1)

Write-Host ""
Write-Host "✅ 完成：$Output\（共 $totalMb MB，无需运行时）" -ForegroundColor Green
Get-ChildItem (Join-Path $root $Output) -File |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize

Write-Host "自检一下：" -ForegroundColor Cyan
Write-Host "  .\$Output\nutstore-mcp.exe --doctor"
