<#
.SYNOPSIS
    _config.ps1 的自测。拿临时文件跑，不碰 Program Files。
.DESCRIPTION
    这段代码是仓库里唯一能弄坏客户端本身的，所以失败路径比成功路径更值得测：
    真正要证明的是「写不进去的时候，线上配置没被动过」。

    临时目录默认建在 %LOCALAPPDATA%\NutstoreMcp\cfgtest-<guid>，脚本结束前自己删掉；
    该位置不可写时（文件沙箱）自动退回系统临时目录，可用 NUTSTORE_MCP_CFGTEST_ROOT 指定。

    .\deploy\_config.tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_config.ps1')

$passed = 0
$failed = [System.Collections.Generic.List[string]]::new()

function Check($name, [scriptblock]$body) {
    try {
        & $body
        Write-Host "  ✅ $name" -ForegroundColor Green
        $script:passed++
    }
    catch {
        Write-Host "  ❌ $name" -ForegroundColor Red
        Write-Host "       $($_.Exception.Message)" -ForegroundColor DarkGray
        $script:failed.Add($name)
    }
}

function Assert-True($cond, $msg) {
    if (-not $cond) { throw $msg }
}

$Assembly = 'NutstoreMcp.Bridge'
$Type = 'NutstoreMcp.Bridge.BridgeDomainManager'

$sampleConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
  <runtime>
    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
      <dependentAssembly>
        <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" />
        <bindingRedirect oldVersion="0.0.0.0-4.0.2.0" newVersion="4.0.2.0" />
      </dependentAssembly>
    </assemblyBinding>
  </runtime>
  <appSettings>
    <add key="AppServer" value="https://example.invalid" />
  </appSettings>
</configuration>
'@

# 测试用的临时目录统一登记，脚本结束前删掉。
# 以前只建不删 —— 跑几轮 verify.ps1 就在 %LOCALAPPDATA%\NutstoreMcp 下攒出几百个
# cfgtest-* 目录（实测攒到 510 个）。测试产物不该留在用户目录里。
$script:scratch = [System.Collections.Generic.List[string]]::new()

<#
    临时目录的根：优先 %LOCALAPPDATA%\NutstoreMcp，不可写时退回系统临时目录。

    为什么要探测可写性：DSH 之类的文件沙箱只放行工作区和平台临时目录，
    %LOCALAPPDATA% 是**拒绝**的。以前直接建目录，在这种环境里 11 项里红 10 项，
    而且报的是 "Access to the path ... is denied" —— 看起来像测试坏了，
    实际上是环境不放行。探测一次、退回可写位置，测试才在任何环境都有意义。

    可用 NUTSTORE_MCP_CFGTEST_ROOT 显式指定（CI 或需要固定位置时用）。
#>
function Resolve-ScratchRoot {
    if ($env:NUTSTORE_MCP_CFGTEST_ROOT) {
        return $env:NUTSTORE_MCP_CFGTEST_ROOT
    }

    $preferred = Join-Path $env:LOCALAPPDATA 'NutstoreMcp'
    try {
        New-Item -ItemType Directory -Force -Path $preferred -ErrorAction Stop | Out-Null
        $probe = Join-Path $preferred ('.write-probe-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $probe -ErrorAction Stop | Out-Null
        Remove-Item -LiteralPath $probe -Recurse -Force -ErrorAction SilentlyContinue
        return $preferred
    }
    catch {
        return [System.IO.Path]::GetTempPath().TrimEnd('\')
    }
}

$script:scratchRoot = Resolve-ScratchRoot
Write-Host "  临时目录根: $script:scratchRoot" -ForegroundColor DarkGray

function New-ScratchDir {
    $dir = Join-Path $script:scratchRoot ("cfgtest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir -ErrorAction Stop | Out-Null
    $script:scratch.Add($dir)
    return $dir
}

function Remove-Scratch {
    foreach ($dir in $script:scratch) {
        if (Test-Path -LiteralPath $dir) {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    $script:scratch.Clear()
}

# 上一轮被 Ctrl+C 打断留下的残骸：正常一轮只跑几秒，超过一小时的一定是弃尸。
# 不无脑全清，是怕撞上并发跑的另一次测试。
Get-ChildItem $script:scratchRoot -Directory -Filter 'cfgtest-*' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-1) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

function New-TempConfig {
    $dir = New-ScratchDir
    $path = Join-Path $dir 'NutstoreClient.exe.config'
    Set-Content -Path $path -Value $sampleConfig -Encoding UTF8
    return $path
}

Write-Host ''
Write-Host '── 补丁 ──' -ForegroundColor Cyan

Check '加补丁：两个元素都写入且值正确' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    $changed = Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type
    Save-ClientConfigAtomically -Xml $xml -ConfigPath $p -ExpectedValues @{
        appDomainManagerAssembly = $Assembly
        appDomainManagerType     = $Type
    }

    Assert-True $changed '应报告有改动'

    $after = Read-ClientConfig -ConfigPath $p
    Assert-True ($after.configuration.runtime.appDomainManagerAssembly.value -eq $Assembly) 'assembly 值不对'
    Assert-True ($after.configuration.runtime.appDomainManagerType.value -eq $Type) 'type 值不对'
}

Check '加补丁：原有内容一个不少' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type | Out-Null
    Save-ClientConfigAtomically -Xml $xml -ConfigPath $p -ExpectedValues @{
        appDomainManagerAssembly = $Assembly
        appDomainManagerType     = $Type
    }

    $after = Read-ClientConfig -ConfigPath $p
    Assert-True ($after.SelectNodes("//*[local-name()='dependentAssembly']").Count -eq 1) 'assemblyBinding 丢了'
    # 用 XPath 而不是 $after.appSettings.add —— PowerShell 的 XmlDocument
    # 适配器对这种访问会返回 null（实测），换成 XPath 才可靠
    $appSetting = $after.SelectSingleNode("//*[local-name()='add']")
    Assert-True ($appSetting -and $appSetting.value -eq 'https://example.invalid') 'appSettings 丢了'
    $supported = $after.SelectSingleNode("//*[local-name()='supportedRuntime']")
    Assert-True ($supported -and $supported.version -eq 'v4.0') 'startup 丢了'
}

Check '加补丁：插在 <runtime> 最前面' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type | Out-Null
    Save-ClientConfigAtomically -Xml $xml -ConfigPath $p -ExpectedValues @{
        appDomainManagerAssembly = $Assembly
        appDomainManagerType     = $Type
    }

    $after = Read-ClientConfig -ConfigPath $p
    $first = $after.configuration.runtime.FirstChild.Name
    Assert-True ($first -eq 'appDomainManagerAssembly') "第一个子节点是 $first"
}

Check '幂等：重复加不产生重复元素' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type | Out-Null
    Save-ClientConfigAtomically -Xml $xml -ConfigPath $p -ExpectedValues @{
        appDomainManagerAssembly = $Assembly; appDomainManagerType = $Type
    }

    $xml2 = Read-ClientConfig -ConfigPath $p
    $changed2 = Add-AppDomainManager -Xml $xml2 -AssemblyName $Assembly -TypeName $Type
    Assert-True (-not $changed2) '第二次不该报告改动'
    Assert-True ($xml2.SelectNodes('//appDomainManagerAssembly').Count -eq 1) '出现了重复元素'
}

Check '冲突：已有的 appDomainManager 值不同则拒绝' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    $el = $xml.CreateElement('appDomainManagerAssembly')
    $el.SetAttribute('value', 'SomeOtherTool')
    $xml.configuration.runtime.InsertBefore($el, $xml.configuration.runtime.FirstChild) | Out-Null

    $threw = $false
    try { Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type | Out-Null }
    catch { $threw = $true }
    Assert-True $threw '值冲突时应抛异常而不是覆盖'
}

Write-Host ''
Write-Host '── 失败路径（真正要证明的部分）──' -ForegroundColor Cyan

Check '验证失败时线上文件原封不动' {
    $p = New-TempConfig
    $before = Get-Content $p -Raw

    $xml = Read-ClientConfig -ConfigPath $p
    Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type | Out-Null

    # 故意给一个错误的期望值 —— 模拟「写出来的东西不对」
    $threw = $false
    try {
        Save-ClientConfigAtomically -Xml $xml -ConfigPath $p -ExpectedValues @{
            appDomainManagerAssembly = 'WrongValue'
        }
    }
    catch { $threw = $true }

    Assert-True $threw '期望值不符时应抛异常'

    $after = Get-Content $p -Raw
    Assert-True ($after -eq $before) '线上文件被改动了 —— 这正是要防的'
}

Check '失败后不留临时文件' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type | Out-Null

    try {
        Save-ClientConfigAtomically -Xml $xml -ConfigPath $p -ExpectedValues @{ appDomainManagerAssembly = 'Wrong' }
    }
    catch { }

    Assert-True (-not (Test-Path "$p.nutstore-mcp-tmp")) '临时文件没清掉'
}

Check '缺 <runtime> 时拒绝而不是盲写' {
    $dir = New-ScratchDir
    $p = Join-Path $dir 'x.config'
    Set-Content $p '<configuration><appSettings /></configuration>' -Encoding UTF8

    $threw = $false
    try { Read-ClientConfig -ConfigPath $p | Out-Null } catch { $threw = $true }
    Assert-True $threw '没有 <runtime> 时应抛异常'
}

Check '文件不存在时报错清楚' {
    $threw = $false
    try { Read-ClientConfig -ConfigPath 'C:\definitely\not\here.config' | Out-Null } catch { $threw = $true }
    Assert-True $threw '应抛异常'
}

Write-Host ''
Write-Host '── 卸载 ──' -ForegroundColor Cyan

Check '移除：两个元素消失，其它内容保留' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    Add-AppDomainManager -Xml $xml -AssemblyName $Assembly -TypeName $Type | Out-Null
    Save-ClientConfigAtomically -Xml $xml -ConfigPath $p -ExpectedValues @{
        appDomainManagerAssembly = $Assembly; appDomainManagerType = $Type
    }

    $xml2 = Read-ClientConfig -ConfigPath $p
    $removed = Remove-AppDomainManager -Xml $xml2
    Save-ClientConfigAtomically -Xml $xml2 -ConfigPath $p -ExpectedValues @{}

    Assert-True ($removed -eq 2) "应移除 2 个，实际 $removed"

    $after = Read-ClientConfig -ConfigPath $p
    Assert-True (-not $after.configuration.runtime.appDomainManagerAssembly) 'assembly 还在'
    Assert-True (-not $after.configuration.runtime.appDomainManagerType) 'type 还在'
    Assert-True ($after.SelectNodes("//*[local-name()='dependentAssembly']").Count -eq 1) '原有内容丢了'
}

Check '移除：本来就没有也不报错' {
    $p = New-TempConfig
    $xml = Read-ClientConfig -ConfigPath $p
    $removed = Remove-AppDomainManager -Xml $xml
    Assert-True ($removed -eq 0) '不该移除任何东西'
}

Remove-Scratch

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "══ 全部通过（$passed 项）══" -ForegroundColor Green
    exit 0
}

Write-Host "══ $($failed.Count) 项失败: $($failed -join '; ') ══" -ForegroundColor Red
exit 1
