<#
.SYNOPSIS
    _config.ps1 的自测。拿临时文件跑，不碰 Program Files。
.DESCRIPTION
    这段代码是仓库里唯一能弄坏客户端本身的，所以失败路径比成功路径更值得测：
    真正要证明的是「写不进去的时候，线上配置没被动过」。

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

function New-TempConfig {
    $dir = Join-Path $env:LOCALAPPDATA ("NutstoreMcp\cfgtest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
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
    $dir = Join-Path $env:LOCALAPPDATA ("NutstoreMcp\cfgtest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
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

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "══ 全部通过（$passed 项）══" -ForegroundColor Green
    exit 0
}

Write-Host "══ $($failed.Count) 项失败: $($failed -join '; ') ══" -ForegroundColor Red
exit 1
