# 只读检查客户端绑定与已验证版本的差异。
[CmdletBinding()]
param(
    [string]$ClientDir,
    [string]$ReportPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'recon\ClientBindingCheck\ClientBindingCheck.csproj'
if (-not $SkipBuild) {
    & dotnet build $project -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw '客户端绑定检查器构建失败。' }
}
$checker = Join-Path $repoRoot 'recon\ClientBindingCheck\bin\Debug\net10.0\ClientBindingCheck.dll'
$checkerArgs = @($checker)
if ($ClientDir) { $checkerArgs += @('--client-dir', $ClientDir) }
if ($ReportPath) { $checkerArgs += @('--report', $ReportPath) }
Push-Location $repoRoot
try {
    $json = & dotnet @checkerArgs
    $checkerExitCode = $LASTEXITCODE
    if ($checkerExitCode -eq 2) { throw '客户端绑定检查器无法读取输入，请检查目录或基线。' }
    $report = ($json -join "`n") | ConvertFrom-Json
    if (-not $report.ok) {
        $report.issues | Format-Table code, subject, message -Wrap | Out-Host
        throw '客户端绑定与基线不符。先复核完整语义和守卫，禁止直接刷新基线放行。'
    }
    Write-Host "客户端绑定一致：$($report.current.symbols.Count) 项，$($report.current.assemblies.Count) 个程序集。" -ForegroundColor Green
}
finally { Pop-Location }
