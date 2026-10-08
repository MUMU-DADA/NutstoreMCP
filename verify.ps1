<#
.SYNOPSIS
    跑完整验证：编译 + 主测试套件 + WPF 探针。

.DESCRIPTION
    存在的理由：仓库里有两套测试，跑的地方不一样。

      · tests/NutstoreMcp.Tests  —— dotnet test，net10，覆盖 WebDAV / 本地数据 / L3 安全逻辑
      · recon/UiHookProbe        —— 独立 exe，net48 + WPF，覆盖控件树遍历 + L3 守卫

    探针**不在解决方案里**（它要 net48 + WPF，主套件跑不了），所以
    `dotnet test` 变绿并不能说明遍历逻辑没问题 —— 而遍历这块已经出过四个 bug。
    这个脚本把两套都跑掉，消除「忘了跑探针」这个失败模式。

.EXAMPLE
    .\verify.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipProbe
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Step($text) {
    Write-Host ''
    Write-Host "── $text " -ForegroundColor Cyan -NoNewline
    Write-Host ('─' * [Math]::Max(1, 60 - $text.Length)) -ForegroundColor DarkGray
}

function Ok($text) { Write-Host "   ✅ $text" -ForegroundColor Green }
function Bad($text) { Write-Host "   ❌ $text" -ForegroundColor Red }

# ── 1. 编译 ──
Step '编译解决方案'
Push-Location $root
try {
    $build = dotnet build -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Bad '构建失败'
        $build | Select-String -Pattern 'error' | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        $failures.Add('build')
    }
    else {
        $warn = ($build | Select-String -Pattern 'warning').Count
        Ok "构建通过（$warn 条警告）"
    }
}
finally { Pop-Location }

# ── 2. 主测试套件 ──
Step '主测试套件 (dotnet test)'
Push-Location $root
try {
    $test = dotnet run --project tests/NutstoreMcp.Tests -c Debug 2>&1
    $summary = $test | Select-String -Pattern 'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+)'

    if ($summary) {
        $m = $summary.Matches[0].Groups
        $total, $errors, $failed = [int]$m[1].Value, [int]$m[2].Value, [int]$m[3].Value

        if ($errors -eq 0 -and $failed -eq 0) {
            Ok "$total 个测试全部通过"
        }
        else {
            Bad "$total 个测试中 $errors 错误 / $failed 失败"
            $test | Select-String -Pattern '\[FAIL\]' | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
            $failures.Add('tests')
        }
    }
    else {
        Bad '拿不到测试摘要'
        $failures.Add('tests')
    }
}
finally { Pop-Location }

# ── 3. WPF 探针 ──
if ($SkipProbe) {
    Step 'WPF 探针（已跳过）'
    Write-Host '   ⚠️  跳过探针意味着遍历逻辑没有验证' -ForegroundColor Yellow
}
else {
    Step 'WPF 探针（控件树遍历 + L3 守卫）'
    Push-Location $root
    try {
        $probeBuild = dotnet build recon/UiHookProbe -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Bad '探针构建失败'
            $probeBuild | Select-String -Pattern 'error' | Select-Object -First 5 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
            $failures.Add('probe-build')
        }
        else {
            # 必须从工作区外运行：DSH 沙箱对工作区启动的进程有限制
            $stage = Join-Path $env:LOCALAPPDATA 'NutstoreMcp\verify-probe'
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            Copy-Item 'recon/UiHookProbe/bin/Debug/*' $stage -Recurse -Force

            $probe = & (Join-Path $stage 'UiHookProbe.exe') 2>&1
            $probe | Where-Object { $_ -match '✅|❌' } | ForEach-Object { Write-Host "   $_" }

            if ($LASTEXITCODE -eq 0) {
                Ok '探针全部通过'
            }
            else {
                Bad "探针失败（退出码 $LASTEXITCODE）"
                $failures.Add('probe')
            }
        }
    }
    finally { Pop-Location }
}

# ── 汇总 ──
Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host '══ 全部通过 ══' -ForegroundColor Green
    exit 0
}

Write-Host "══ $($failures.Count) 项失败: $($failures -join ', ') ══" -ForegroundColor Red
exit 1
