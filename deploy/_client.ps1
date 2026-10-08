<#
.SYNOPSIS
    deploy/ 下各脚本共用的客户端目录发现逻辑。

.DESCRIPTION
    build.ps1 和 install.ps1 共用，避免两处各写一份而漂移。详见 ARCHITECTURE.md。
#>

<#
.SYNOPSIS
    找最新的坚果云客户端目录（含 NutstoreClient.exe 的 bin-* 目录）。
.PARAMETER Root
    客户端安装根目录，默认 C:\Program Files\Nutstore。参数化是为了可测试。
.OUTPUTS
    目录路径；找不到返回 $null。
#>
function Find-NutstoreClientDir {
    [CmdletBinding()]
    param(
        [string]$Root = 'C:\Program Files\Nutstore'
    )

    if (-not (Test-Path $Root)) {
        return $null
    }

    # 按**版本号**倒序，不是按字符串。
    # 字符串倒序是错的：'bin-7.2.9' > 'bin-7.2.13'（因为 '9' > '1'），
    # 于是客户端从 7.2.9 升级到 7.2.13 之后，这里会选中**旧**目录。
    $candidates = Get-ChildItem $Root -Directory -Filter 'bin-*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'NutstoreClient.exe') } |
        Sort-Object { ConvertTo-VersionKey $_.Name } -Descending

    if ($candidates) {
        return $candidates[0].FullName
    }

    # 没有 bin-* 结构时退回递归找（老版本布局），同样取最新的
    $exe = Get-ChildItem $Root -Recurse -Filter 'NutstoreClient.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($exe) {
        return $exe.DirectoryName
    }

    return $null
}

<#
.SYNOPSIS
    把 bin-7.2.12 这样的目录名转成可比较的版本键。
.DESCRIPTION
    解析不出数字时返回 [version]'0.0'，让它在排序里沉底 ——
    宁可漏选也不要选错。
#>
function ConvertTo-VersionKey {
    [CmdletBinding()]
    param([string]$Name)

    $m = [regex]::Match($Name, '(\d+(?:\.\d+)+)')
    if (-not $m.Success) {
        return [version]'0.0'
    }

    try {
        return [version]$m.Groups[1].Value
    }
    catch {
        return [version]'0.0'
    }
}

<#
.SYNOPSIS
    列出所有含 NutstoreClient.exe 的目录（用于发现「客户端更新后旧目录还在」）。
#>
function Get-AllNutstoreClientDirs {
    [CmdletBinding()]
    param(
        [string]$Root = 'C:\Program Files\Nutstore'
    )

    if (-not (Test-Path $Root)) {
        return @()
    }

    return @(
        Get-ChildItem $Root -Directory -Filter 'bin-*' -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'NutstoreClient.exe') } |
            Sort-Object { ConvertTo-VersionKey $_.Name } -Descending |
            ForEach-Object { $_.FullName }
    )
}