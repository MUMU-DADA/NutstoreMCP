# 验证混淆字面量和版本基线仅位于共享登记目录。
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $repoRoot 'src\NutstoreMcp.ClientBindings'
$symbolFile = Join-Path $sourceRoot 'ClientSymbols.cs'
$symbolText = [IO.File]::ReadAllText($symbolFile)
$expectedPattern = 'public const string BaselineFileName = "([^"]+)";'
$baselineName = [regex]::Match($symbolText, $expectedPattern).Groups[1].Value
if (-not $baselineName -or -not (Test-Path -LiteralPath (Join-Path $sourceRoot $baselineName))) {
    throw '共享登记缺少版本基线。'
}
$leaks = [Collections.Generic.List[string]]::new()
foreach ($scope in @('src', 'tests', 'recon', 'deploy')) {
    Get-ChildItem -LiteralPath (Join-Path $repoRoot $scope) -Recurse -File |
        Where-Object {
            $_.Extension -in @('.cs', '.ps1') -and
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
            -not $_.FullName.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object {
            if ([IO.File]::ReadAllText($_.FullName) -match 'c[0-9a-f]{32}') { $leaks.Add($_.FullName) }
        }
}
if ($leaks.Count) { throw "混淆字面量散落到共享登记之外：$($leaks -join ', ')" }
Write-Host '✅ 混淆字面量全部位于共享登记，版本基线存在。'
