[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$PurgeRuntimeData,
    [string]$ConfirmPurgeRuntimeData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodexTotalManager'
}
$installFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$localDataFull = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData')).TrimEnd('\')
if ($installFull.Equals($localDataFull, [StringComparison]::OrdinalIgnoreCase) -or
    -not ($installFull + '\').StartsWith($localDataFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "卸载目录必须严格位于 LocalApplicationData 下面：$installFull"
}
if (-not (Test-Path -LiteralPath $installFull -PathType Container)) {
    Write-Output "PROGRAM_NOT_INSTALLED path=$installFull"
    return
}
if ($PurgeRuntimeData -and $ConfirmPurgeRuntimeData -cne 'DELETE_RUNTIME_DATA') {
    throw '彻底删除运行数据必须同时传入 -ConfirmPurgeRuntimeData DELETE_RUNTIME_DATA。默认卸载会保留账号、号池、账本、密钥和皮肤状态。'
}

$running = @()
foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
    try {
        $path = [string]$process.Path
        if ($path -and $path.StartsWith($installFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $running += "$($process.ProcessName)($($process.Id))"
        }
    } catch { }
}
if ($running.Count -gt 0) {
    throw "总管家程序仍在运行，请先正常退出再卸载：$($running -join ', ')"
}

$runtimeRoot = Join-Path $installFull 'runtime-v3'
if (-not $PurgeRuntimeData -and (Test-Path -LiteralPath $runtimeRoot -PathType Container)) {
    $historyRoot = Join-Path $runtimeRoot 'release-history'
    New-Item -ItemType Directory -Path $historyRoot -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    foreach ($name in @('active-release.json', 'active-release.previous.json', 'deployment-acceptance-v1.json')) {
        $source = Join-Path $runtimeRoot $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Move-Item -LiteralPath $source -Destination (Join-Path $historyRoot "$stamp-$name") -Force
        }
    }
    [ordered]@{
        schemaVersion = 1
        product = 'CodexTotalManager'
        programRemovedAt = (Get-Date).ToUniversalTime().ToString('o')
        runtimeDataPreserved = $true
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $runtimeRoot 'uninstall-state.json') -Encoding UTF8
}

$programTargets = @(
    (Join-Path $installFull 'releases'),
    (Join-Path $installFull 'Open-New-Manager-ControlPanel.ps1'),
    (Join-Path $installFull 'Launch-Manager-Hidden.vbs'),
    (Join-Path $installFull 'uninstall-local-release.ps1')
)
foreach ($target in $programTargets) {
    $targetFull = [IO.Path]::GetFullPath($target)
    if (-not $targetFull.StartsWith($installFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "卸载目标越过安装目录：$targetFull"
    }
    if (Test-Path -LiteralPath $targetFull) { Remove-Item -LiteralPath $targetFull -Recurse -Force }
}

# Remove only shortcuts that both use the Total Manager name and resolve back to this
# exact installation root. A similarly named user shortcut to another program is kept.
$shortcutShell = New-Object -ComObject WScript.Shell
foreach ($desktopFolder in @(
    [Environment]::GetFolderPath('DesktopDirectory'),
    [Environment]::GetFolderPath('CommonDesktopDirectory')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
    $desktopFull = [IO.Path]::GetFullPath($desktopFolder).TrimEnd('\')
    foreach ($shortcut in @(Get-ChildItem -LiteralPath $desktopFull -Filter 'Codex 总管家*.lnk' -File -ErrorAction SilentlyContinue)) {
        $shortcutFull = [IO.Path]::GetFullPath($shortcut.FullName)
        if (-not $shortcutFull.StartsWith($desktopFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "快捷方式越过桌面目录：$shortcutFull"
        }
        try {
            $resolvedShortcut = $shortcutShell.CreateShortcut($shortcutFull)
            $targetPath = [string]$resolvedShortcut.TargetPath
            $arguments = [string]$resolvedShortcut.Arguments
            $owned = ($targetPath -and $targetPath.StartsWith($installFull + '\', [StringComparison]::OrdinalIgnoreCase)) -or
                     ($arguments -and $arguments.IndexOf($installFull + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0)
            if ($owned) { Remove-Item -LiteralPath $shortcutFull -Force }
        } catch {
            Write-Warning "无法核对快捷方式，已保留：$shortcutFull"
        }
    }
}

if ($PurgeRuntimeData) {
    if (Test-Path -LiteralPath $runtimeRoot) { Remove-Item -LiteralPath $runtimeRoot -Recurse -Force }
    if ((Get-ChildItem -LiteralPath $installFull -Force -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $installFull -Force
    }
    Write-Output "PROGRAM_AND_RUNTIME_DATA_REMOVED path=$installFull recoverable=false"
} else {
    Write-Output "PROGRAM_REMOVED_RUNTIME_DATA_PRESERVED path=$installFull runtime=$runtimeRoot"
}
