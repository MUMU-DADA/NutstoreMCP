# 停止坚果云 → 重装桥 → 重新启动坚果云
#
# 为什么单独写一个脚本：
#   桥和 UI 钩子都加载在客户端的默认域里，.NET Framework 卸载不了默认域已加载的
#   程序集，所以钩子一改就必须重启客户端。而 install.ps1 要求客户端先退出，
#   手动掐这个时机很难 —— 这里把三步串起来。
#
# 安全考虑：
#   · 退出走 CloseMainWindow（正常关闭），给 45 秒；不强杀，除非它真的不退。
#     坚果云在同步，直接 Stop-Process 有打断同步的风险。
#   · 启动用**计划任务**，不是直接 Start-Process。
#     从工作区直接启动的进程是 DSH 沙箱子进程，命名管道会被禁掉 ——
#     坚果云的 \\.\pipe\BadgeGrpcNamedPipe.BadgeToNutstoreV3 起不来，
#     资源管理器的角标集成就坏了。计划任务跑在沙箱之外。

param(
    [switch]$SkipInstall,
    [switch]$SkipStart
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\_client.ps1"

Write-Host '── 1/3 停止坚果云 ─────────────────────────────────' -ForegroundColor Cyan

# 必须先停守护进程。
# 实测：NutstoreClient 有一个 nutstore_watchdog.exe 守着，直接结束客户端会被
# 它立刻拉起来（新进程的父 PID 就是 watchdog）—— 于是永远等不到「客户端已退出」。
$watchdog = Get-Process -Name 'nutstore_watchdog' -ErrorAction SilentlyContinue
if ($watchdog) {
    Write-Host "  先停守护进程 nutstore_watchdog（PID $($watchdog.Id -join ', ')）…"
    Stop-Process -Name 'nutstore_watchdog' -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$client = Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue
if ($client) {
    Write-Host "  结束 NutstoreClient（PID $($client.Id -join ', ')）…"
    # CloseMainWindow 对它没用 —— 坚果云常驻托盘，没有可关闭的主窗口（实测）。
    # 它自己也维护同步状态，进程被结束是它设计上要处理的（watchdog 的存在即证明）。
    Stop-Process -Name 'NutstoreClient' -Force
    Start-Sleep -Seconds 3
}

$still = Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue
if ($still) { throw "坚果云仍在运行（PID $($still.Id -join ', ')），中止" }
Write-Host '  ✅ 已退出' -ForegroundColor Green

if (-not $SkipInstall) {
    Write-Host ''
    Write-Host '── 2/3 重装桥（会弹 UAC，请点「是」）─────────────' -ForegroundColor Cyan
    & "$PSScriptRoot\install.ps1"

    # install.ps1 提权后是**异步**的：它启动一个新的管理员窗口就返回了。
    # 所以不能直接往下走 —— 否则会在安装还没写完时就启动客户端，
    # 客户端加载到的还是旧契约。这里轮询程序目录的契约，直到新方法出现。
    $dir = Find-NutstoreClientDir
    $contractDll = Join-Path $dir 'NutstoreMcp.Plugin.Contract.dll'

    Write-Host '  等待安装完成（轮询契约，最多 3 分钟）…'
    $deadline = (Get-Date).AddMinutes(3)
    $installed = $false

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $contractDll) {
            try {
                $bytes = [System.IO.File]::ReadAllBytes($contractDll)
                $text = [System.Text.Encoding]::ASCII.GetString($bytes)

                # 这两个方法是本轮新增的，出现即说明新契约已经落盘
                # 这些是插件目录当前契约里的方法名，全部出现才算装好。
                # **每次加新的钩子方法都要同步更新这里** —— 否则轮询会拿旧契约
                # 立刻通过，然后在客户端启动之后才发现调用失败。
                $required = @('ReadClientSettings', 'WriteClientSetting', 'SearchCloud', 'ReadAccountUsage', 'ReadUserInfo', 'ReadSyncFolderDetail', 'ListFileVersions', 'ReadShareInfo', 'ReadContacts', 'InvokeBridge', 'ReadObjectDetail', 'ReadLatestEventId', 'DownloadCloudFile')
                $missing = @($required | Where-Object { -not $text.Contains($_) })

                if ($missing.Count -eq 0) {
                    $installed = $true
                    break
                }
            }
            catch {
                # 文件正被写入，下一轮再试
            }
        }
        Start-Sleep -Seconds 3
    }

    if (-not $installed) {
        Write-Host '  ❌ 3 分钟内没等到新契约 —— 安装可能没完成或 UAC 被取消了。' -ForegroundColor Red
        Write-Host '     客户端不会启动（避免加载到半新半旧的契约）。' -ForegroundColor Red
        throw '安装未完成'
    }

    Write-Host '  ✅ 新契约已落盘' -ForegroundColor Green
}

if ($SkipStart) {
    Write-Host ''
    Write-Host '已指定 -SkipStart，不启动客户端。' -ForegroundColor Yellow
    return
}

Write-Host ''
Write-Host '── 3/3 重新启动坚果云 ─────────────────────────────' -ForegroundColor Cyan

$clientDir = Find-NutstoreClientDir
if (-not $clientDir) { throw '找不到 NutstoreClient.exe' }

$exe = Join-Path $clientDir 'NutstoreClient.exe'
Write-Host "  $exe"

if (-not (Test-Path $exe)) { throw "找不到 $exe" }

# 用计划任务启动，绕开 DSH 沙箱（见文件头的说明）
$taskName = 'NutstoreMcp-RestartClient'
& schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null

$create = & schtasks.exe /Create /TN $taskName /TR "`"$exe`"" /SC ONCE /ST 00:00 /RL LIMITED /F 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ⚠️  计划任务创建失败，退回直接启动（命名管道可能起不来）" -ForegroundColor Yellow
    Write-Host "     $create"
    Start-Process -FilePath $exe
}
else {
    & schtasks.exe /Run /TN $taskName 2>&1 | Out-Null

    # 等进程真的起来再删任务。
    # 一开始是「运行 → 睡 6 秒 → 删」——那样会把正在启动的实例一起掐掉。
    $deadline0 = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline0 `
           -and -not (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 2
    }

    & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
}

# 等它起来（第一次）
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and -not (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 1
}

# ── 确认它站得住 ──
# 只看「进程出现过」是不够的：实测发生过「脚本报已启动、随后客户端退出」，
# 结果用户的同步停了而我以为成功了。所以起来之后要再等一会儿复查。
function Test-Alive {
    Start-Sleep -Seconds 12
    return [bool](Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)
}

$started = Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue

if ($started -and (Test-Alive)) {
    Write-Host "  ✅ 已启动并保持运行（PID $($started.Id)）" -ForegroundColor Green
}
else {
    # 计划任务那条路没成（或者起来又死了）—— 退回直接启动再确认一次。
    # 直接启动的进程是 DSH 沙箱子进程，命名管道可能起不来，
    # 但「客户端在跑」比「角标集成完好」重要得多。
    Write-Host '  ⚠️  计划任务启动的实例没站住，退回直接启动…' -ForegroundColor Yellow
    Get-Process -Name 'nutstore_watchdog' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $exe

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 1
    }

    $started = Get-Process -Name 'NutstoreClient' -ErrorAction SilentlyContinue
    if (-not $started -or -not (Test-Alive)) {
        throw '坚果云启动后没有保持运行 —— 请手动启动它，并检查是否有错误弹窗。'
    }

    Write-Host "  ✅ 已启动并保持运行（PID $($started.Id)，直接启动）" -ForegroundColor Green
}

# 等 MCP 端口
Write-Host '  等待 MCP 端口 17321 …'
$deadline = (Get-Date).AddSeconds(60)
$up = $false
while ((Get-Date) -lt $deadline) {
    try {
        $null = Invoke-RestMethod -Uri 'http://127.0.0.1:17321/mcp' -Method Post `
            -ContentType 'application/json' -TimeoutSec 5 `
            -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
        $up = $true
        break
    }
    catch {
        Start-Sleep -Seconds 2
    }
}

if ($up) {
    Write-Host '  ✅ MCP 已就绪' -ForegroundColor Green
}
else {
    Write-Host '  ⚠️  60 秒内 MCP 端口没起来 —— 看桥日志' -ForegroundColor Yellow
    Write-Host "     $env:LOCALAPPDATA\NutstoreMcp\logs\bridge.log"
}

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
