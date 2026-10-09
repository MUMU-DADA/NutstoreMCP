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
#
# 逐项目构建，不用 `dotnet build` 整个解决方案，有两条实测理由：
#   · 解决方案级构建会先跑 Restore 目标。受限沙箱里 HTTPS 被拦（Schannel 拿不到
#     凭据），restore 起不来，而它只回一句「0 个警告 0 个错误」—— 从输出根本看不出
#     是环境问题还是代码问题。
#   · 逐项目 + --no-restore 是纯离线操作，结果只取决于源码和已还原的包。
# assets 缺失（例如新克隆）时确实需要联网 restore，那种情况单独报出来，不当成构建失败。
Step '编译（逐项目）'
$projects = @(
    'src\NutstoreMcp\NutstoreMcp.csproj'
    'src\NutstoreMcp.Plugin.Contract\NutstoreMcp.Plugin.Contract.csproj'
    'src\NutstoreMcp.Plugin\NutstoreMcp.Plugin.csproj'
    'src\NutstoreMcp.UiHook\NutstoreMcp.UiHook.csproj'
    'src\NutstoreMcp.Bridge\NutstoreMcp.Bridge.csproj'
    'src\NutstoreMcp.Host\NutstoreMcp.Host.csproj'
    'tests\NutstoreMcp.Tests\NutstoreMcp.Tests.csproj'
)

$scratch = Join-Path $root '.test-tmp'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

Push-Location $root
try {
    $needsRestore = @($projects | Where-Object {
            -not (Test-Path (Join-Path (Split-Path $_ -Parent) 'obj\project.assets.json'))
        })

    if ($needsRestore.Count -gt 0) {
        Bad "缺 NuGet assets（$($needsRestore.Count) 个项目）—— 这一步需要联网"
        Write-Host '      先在有网环境跑： dotnet restore NutstoreMCP.slnx' -ForegroundColor DarkGray
        $failures.Add('restore')
    }
    else {
        $failedProjects = [System.Collections.Generic.List[string]]::new()
        $warnTotal = 0
        foreach ($proj in $projects) {
            $out = dotnet build $proj --no-restore -v q --nologo 2>&1
            if ($LASTEXITCODE -ne 0) {
                $failedProjects.Add($proj)
                Write-Host "      $proj" -ForegroundColor DarkGray
                $out | Select-String -Pattern 'error' | Select-Object -First 5 | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkGray }
            }
            else {
                $warnTotal += @($out | Select-String -Pattern 'warning').Count
            }
        }

        if ($failedProjects.Count -eq 0) {
            Ok "构建通过（$($projects.Count) 个项目，$warnTotal 条警告）"
        }
        else {
            Bad "$($failedProjects.Count) 个项目构建失败"
            $failures.Add('build')
        }
    }
}
finally { Pop-Location }

# ── 2. 主测试套件 ──
#
# 直接跑编译出来的 exe，不用 `dotnet run`：后者会隐式 restore（受限环境里会挂住），
# 而且这里**必须带超时** —— 有一个测试挂起时，整轮验证会永远停在这一步，
# 拿不到任何摘要，也看不出是谁挂的（实测踩过）。
Step '主测试套件 (xUnit v3)'
Push-Location $root
try {
    $testLog = Join-Path $scratch 'verify-tests.log'
    $testBuild = dotnet build tests\NutstoreMcp.Tests\NutstoreMcp.Tests.csproj --no-restore -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Bad '测试项目构建失败'
        $testBuild | Select-String -Pattern 'error' | Select-Object -First 5 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
        $failures.Add('tests-build')
    }
    else {
        $testExe = Join-Path $root 'tests\NutstoreMcp.Tests\bin\Debug\net10.0-windows\NutstoreMcp.Tests.exe'
        $proc = Start-Process -FilePath $testExe -ArgumentList '-noColor' -WorkingDirectory $root `
            -RedirectStandardOutput $testLog -RedirectStandardError "$testLog.err" -PassThru -NoNewWindow

        if (-not $proc.WaitForExit(600000)) {
            try { $proc.Kill() } catch { }
            Bad '测试 10 分钟没跑完（有挂起的测试）'
            Write-Host "      日志: $testLog" -ForegroundColor DarkGray
            Write-Host '      定位挂起项：给测试 exe 加 -longRunning 15 -diagnostics' -ForegroundColor DarkGray
            $failures.Add('tests-timeout')
        }
        else {
            $test = Get-Content $testLog
            $summary = $test | Select-String -Pattern 'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+)'

            if ($summary) {
                $m = $summary.Matches[0].Groups
                $total, $errors, $failed = [int]$m[1].Value, [int]$m[2].Value, [int]$m[3].Value

                if ($errors -eq 0 -and $failed -eq 0) {
                    Ok "$total 个测试全部通过"
                }
                else {
                    Bad "$total 个测试中 $errors 错误 / $failed 失败"
                    $test | Select-String -Pattern '\[FAIL\]' | Select-Object -First 10 | ForEach-Object { Write-Host "      $($_.Line)" -ForegroundColor DarkGray }
                    $failures.Add('tests')
                }
            }
            else {
                Bad '拿不到测试摘要（测试可能提前退出了）'
                $test | Select-Object -Last 10 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
                $failures.Add('tests')
            }
        }
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
        # -m:1 不是性能选择，是**必需**的：这个项目引用了两个兄弟项目，SDK 会通过
        # MSBuild 任务跨节点调 GetTargetFrameworks；并行节点之间走命名管道，
        # 而文件沙箱挡掉命名管道 —— 现象是「生成失败 / 0 个警告 0 个错误」，
        # 什么线索都没有。串行（单节点）就能在同一进程里完成。
        $probeBuild = dotnet build recon/UiHookProbe --no-restore -m:1 -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Bad '探针构建失败'
            $probeBuild | Select-String -Pattern 'error' | Select-Object -First 5 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
            $failures.Add('probe-build')
        }
        else {
            # 必须从工作区外运行：DSH 沙箱对工作区启动的进程有限制。
            # 但沙箱同时也挡掉 %LOCALAPPDATA%（只放行工作区和平台临时目录），
            # 所以先探测可写性，不可写就退回系统临时目录 —— 两者都在工作区之外。
            $stage = Join-Path $env:LOCALAPPDATA 'NutstoreMcp\verify-probe'
            try {
                New-Item -ItemType Directory -Force -Path $stage -ErrorAction Stop | Out-Null
                $probeDir = Join-Path $stage ('.w-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $probeDir -ErrorAction Stop | Out-Null
                Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            catch {
                $stage = Join-Path ([System.IO.Path]::GetTempPath().TrimEnd('\')) 'nutstore-mcp-verify-probe'
            }

            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            Copy-Item 'recon/UiHookProbe/bin/Debug/*' $stage -Recurse -Force

            # 探针也要带超时：它是 WPF 进程，挂住时同样会拖死整轮验证。
            $probeLog = Join-Path $scratch 'verify-probe.log'
            $probeProc = Start-Process -FilePath (Join-Path $stage 'UiHookProbe.exe') -WorkingDirectory $stage `
                -RedirectStandardOutput $probeLog -RedirectStandardError "$probeLog.err" -PassThru -NoNewWindow

            if (-not $probeProc.WaitForExit(300000)) {
                try { $probeProc.Kill() } catch { }
                Bad '探针 5 分钟没结束'
                $failures.Add('probe-timeout')
            }
            else {
                $probe = Get-Content $probeLog
                $probe | Where-Object { $_ -match '✅|❌' } | ForEach-Object { Write-Host "   $_" }

                # 不能看 ExitCode：文件沙箱拒绝打开子进程句柄，Process.ExitCode 拿不到值
                # （实测返回空）。探针自己打印结论行，按它判断 —— 这也是它在任何环境
                # 都成立的判据。
                $verdict = $probe | Where-Object { $_ -match '全部通过|项失败' } | Select-Object -Last 1

                if ($verdict -match '全部通过') {
                    Ok '探针全部通过'
                }
                else {
                    Bad "探针失败（$($verdict -replace '══', '')）"
                    Get-Content "$probeLog.err" -ErrorAction SilentlyContinue | Select-Object -Last 5 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
                    $failures.Add('probe')
                }
            }
        }
    }
    finally { Pop-Location }
}

# ── 4. 客户端配置补丁测试 ──
Step '部署脚本自测（客户端配置补丁）'
Push-Location $root
try {
    # 这段代码是仓库里唯一能弄坏客户端本身的 —— 写坏 NutstoreClient.exe.config
    # 意味着坚果云起不来。所以它的失败路径必须每次都被跑一遍。
    # 测试用临时文件，不碰 Program Files。
    $cfg = & (Join-Path $root 'deploy\_config.tests.ps1') 2>&1
    $cfg | Where-Object { $_ -match '✅|❌' } | ForEach-Object { Write-Host "   $_" }

    if ($LASTEXITCODE -eq 0) {
        Ok '配置补丁测试全部通过'
    }
    else {
        Bad "配置补丁测试失败（退出码 $LASTEXITCODE）"
        Write-Host '      提示：若报「Access to the path ... is denied」且信息里带 Replace，' -ForegroundColor DarkGray
        Write-Host '      那是 File.Replace 被文件沙箱拒绝（它需要对目标文件的删除权限）。' -ForegroundColor DarkGray
        Write-Host '      这一步要在沙箱外的普通 PowerShell 里跑，或者提权跑。' -ForegroundColor DarkGray
        $failures.Add('config-tests')
    }
}
finally { Pop-Location }

# ── 5. 客户端绑定登记与检查器自测（不需要安装客户端）──
Step '客户端绑定登记与检查器离线自测'
Push-Location $root
try {
    & (Join-Path $root 'deploy\_bindings.tests.ps1')
    $bindingTests = & dotnet run --project recon/ClientBindingCheck --no-restore -- --self-test 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ($bindingTests -join "`n")
    }
    Ok '绑定登记和元数据检查器自测通过'
}
catch {
    Bad $_.Exception.Message
    $failures.Add('binding-tests')
}
finally { Pop-Location }

# ── 汇总 ──
Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host '══ 全部通过 ══' -ForegroundColor Green
    exit 0
}

Write-Host "══ $($failures.Count) 项失败: $($failures -join ', ') ══" -ForegroundColor Red
exit 1
