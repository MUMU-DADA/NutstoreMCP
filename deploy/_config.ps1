<#
.SYNOPSIS
    NutstoreClient.exe.config 的补丁与还原逻辑。

.DESCRIPTION
    按路径操作，便于拿临时文件测试（含失败路径）。详见 ARCHITECTURE.md。
#>

$script:AppDomainManagerTags = @('appDomainManagerAssembly', 'appDomainManagerType')

<#
.SYNOPSIS
    读入配置文件并确认它结构与预期一致。
.OUTPUTS
    XmlDocument
#>
function Read-ClientConfig {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ConfigPath)

    if (-not (Test-Path $ConfigPath)) {
        throw "找不到配置文件: $ConfigPath"
    }

    $xml = New-Object System.Xml.XmlDocument
    # PreserveWhitespace：不保留的话重写会把原有缩进全部压掉，
    # 产生一个巨大的无意义 diff，将来排查问题时很难看出真正改了什么。
    $xml.PreserveWhitespace = $true
    $xml.Load($ConfigPath)

    if (-not $xml.configuration) {
        throw "配置文件结构异常：没有 <configuration> 根节点"
    }

    if (-not $xml.configuration.runtime) {
        throw "配置文件结构异常：没有 <runtime> 节点"
    }

    return $xml
}

<#
.SYNOPSIS
    原子地写回配置：临时文件 → 重新解析验证 → File.Replace。
.DESCRIPTION
    不能直接 $xml.Save($ConfigPath)。XmlDocument.Save 会**整体重写**目标文件，
    中途被打断（崩溃、断电、杀软锁文件、磁盘满）会留下截断的 XML ——
    那就是「客户端起不来」。

    任何一步失败都抛异常，且线上文件原封不动。
#>
function Save-ClientConfigAtomically {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Xml.XmlDocument]$Xml,
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][hashtable]$ExpectedValues
    )

    $tmp = "$ConfigPath.nutstore-mcp-tmp"
    $beforeCount = $Xml.SelectNodes('//*').Count

    try {
        $Xml.Save($tmp)

        # 验证 1：写出来的东西必须能重新解析（能抓出写了一半就被截断的情况）
        $check = New-Object System.Xml.XmlDocument
        $check.Load($tmp)

        $runtime = $check.configuration.runtime
        if (-not $runtime) { throw '临时文件里没有 <runtime> 节点' }

        # 验证 2：每个期望的键都在，且值正确
        foreach ($key in $ExpectedValues.Keys) {
            $node = $runtime.SelectSingleNode($key)
            if (-not $node) { throw "临时文件里缺少 <$key>" }
            if ($node.value -ne $ExpectedValues[$key]) {
                throw "<$key> 的值不对: 期望 $($ExpectedValues[$key])，实际 $($node.value)"
            }
        }

        # 验证 3：原有内容没被吃掉。节点数只应增加（加了两个元素）。
        $afterCount = $check.SelectNodes('//*').Count
        if ($afterCount -lt $beforeCount) {
            throw "写出的文件节点数变少了（$beforeCount → $afterCount），拒绝替换"
        }

        # File.Replace 在同一卷上是原子操作：要么全新内容，要么全旧内容，
        # 不存在「写了一半」的中间态。
        #
        # 第三个参数必须用 [NullString]::Value —— PowerShell 会把 $null
        # 转成空字符串，而 File.Replace 认为空路径是非法的
        # （"The path is empty. (Parameter 'path')"）。这里不需要备份文件，
        # 因为调用方已经单独做过备份。
        [System.IO.File]::Replace($tmp, $ConfigPath, [NullString]::Value)
    }
    finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

<#
.SYNOPSIS
    把 appDomainManager 两个元素插到 <runtime> 最前面。
.DESCRIPTION
    幂等：已存在且值相同则不动；已存在但值不同则拒绝（说明别的工具在用这个扩展点，
    覆盖会造成冲突且难以排查）。
#>
function Add-AppDomainManager {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Xml.XmlDocument]$Xml,
        [Parameter(Mandatory)][string]$AssemblyName,
        [Parameter(Mandatory)][string]$TypeName
    )

    $runtime = $Xml.configuration.runtime
    $wanted = [ordered]@{
        appDomainManagerAssembly = $AssemblyName
        appDomainManagerType     = $TypeName
    }

    $changed = $false

    # 倒序插入。
    #
    # 每个元素都 InsertBefore(runtime.FirstChild)，所以**后插的会跑到更前面** ——
    # 正序插会得到 [type, assembly]，和文档里写的顺序相反。
    # CLR 按名字取值、顺序其实无所谓，但产出应该和文档一致，
    # 否则将来有人对比配置时会以为哪里不对。
    $keys = @($wanted.Keys)
    [array]::Reverse($keys)

    foreach ($key in $keys) {
        $existing = $runtime.SelectSingleNode($key)

        if ($existing) {
            if ($existing.value -ne $wanted[$key]) {
                throw "<runtime> 里已存在 $key 且值不同（$($existing.value)）—— 已有别的工具在用这个扩展点，放弃以免冲突"
            }

            continue
        }

        $el = $Xml.CreateElement($key)
        $el.SetAttribute('value', $wanted[$key])

        # 插到最前面：部分配置节的顺序有 schema 要求
        if ($runtime.FirstChild) {
            $runtime.InsertBefore($el, $runtime.FirstChild) | Out-Null
        }
        else {
            $runtime.AppendChild($el) | Out-Null
        }

        $changed = $true
    }

    return $changed
}

<#
.SYNOPSIS
    移除 appDomainManager 两个元素（卸载用）。
#>
function Remove-AppDomainManager {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Xml)

    $runtime = $Xml.configuration.runtime
    $removed = 0

    foreach ($tag in $script:AppDomainManagerTags) {
        $node = $runtime.SelectSingleNode($tag)
        if ($node) {
            $runtime.RemoveChild($node) | Out-Null
            $removed++
        }
    }

    return $removed
}