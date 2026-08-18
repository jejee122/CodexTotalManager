[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$Interactive,
    [switch]$PurgeRuntimeData,
    [string]$ConfirmPurgeRuntimeData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NoReparsePointBelow([string]$BasePath, [string]$TargetPath, [string]$Label) {
    $baseFull = [IO.Path]::GetFullPath($BasePath).TrimEnd('\')
    $targetFull = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
    if (-not ($targetFull + '\').StartsWith($baseFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside its trusted base directory: $targetFull"
    }
    $relative = $targetFull.Substring($baseFull.Length).TrimStart('\')
    $cursor = $baseFull
    foreach ($part in @($relative -split '\\' | Where-Object { $_ })) {
        $cursor = Join-Path $cursor $part
        if (-not (Test-Path -LiteralPath $cursor)) { break }
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Label contains a symbolic link, junction, or other reparse point: $cursor"
        }
    }
}

function Assert-TreeHasNoReparsePoints([string]$RootPath, [string]$Label) {
    $rootItem = Get-Item -LiteralPath $RootPath -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label is a symbolic link, junction, or other reparse point: $RootPath"
    }
    $reparse = @(Get-ChildItem -LiteralPath $RootPath -Force -Recurse -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1)
    if ($reparse.Count -gt 0) {
        throw "$Label contains a symbolic link, junction, or other reparse point: $($reparse[0].FullName)"
    }
}

function Test-ManagerJsonMarker([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $marker = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        return [string]$marker.product -ceq 'CodexTotalManager'
    } catch {
        return $false
    }
}

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
Assert-NoReparsePointBelow $localDataFull $installFull '卸载目录'
Assert-TreeHasNoReparsePoints $installFull '卸载目录'
$runtimeRoot = Join-Path $installFull 'runtime-v3'
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexTotalManager'
$registeredRoot = if (Test-Path -LiteralPath $uninstallRegistryPath) {
    [string](Get-ItemPropertyValue -LiteralPath $uninstallRegistryPath -Name 'InstallLocation' -ErrorAction SilentlyContinue)
} else { '' }
$registryOwnsRoot = $false
if (-not [string]::IsNullOrWhiteSpace($registeredRoot)) {
    try {
        $registryOwnsRoot = [IO.Path]::GetFullPath($registeredRoot).TrimEnd('\').Equals(
            $installFull,
            [StringComparison]::OrdinalIgnoreCase)
    } catch { }
}
$ownedInstallation = $registryOwnsRoot -or
    (Test-ManagerJsonMarker (Join-Path $runtimeRoot 'active-release.json')) -or
    (Test-ManagerJsonMarker (Join-Path $runtimeRoot 'uninstall-state.json'))
if (-not $ownedInstallation) {
    throw "目标目录没有总管家安装登记或自有状态标记，为防止删错其他软件，已拒绝卸载：$installFull"
}
if ($Interactive -and -not $PurgeRuntimeData) {
    Add-Type -AssemblyName System.Windows.Forms
    $choice = [Windows.Forms.MessageBox]::Show(
        "要卸载 AI 中转站总管家吗？`n`n是：只删除软件，保留账号、号池、账本、密钥、皮肤和设置。`n否：继续确认是否连用户数据一起彻底删除。`n取消：不做任何改动。",
        '卸载 AI 中转站总管家',
        [Windows.Forms.MessageBoxButtons]::YesNoCancel,
        [Windows.Forms.MessageBoxIcon]::Question,
        [Windows.Forms.MessageBoxDefaultButton]::Button1)
    if ($choice -eq [Windows.Forms.DialogResult]::Cancel) {
        Write-Output 'UNINSTALL_CANCELLED'
        return
    }
    if ($choice -eq [Windows.Forms.DialogResult]::No) {
        $purgeChoice = [Windows.Forms.MessageBox]::Show(
            "彻底删除后，本机账号、号池、账本、密钥、皮肤、设置和历史状态都无法从总管家恢复。`n`n确定连用户数据一起删除吗？",
            '确认彻底删除总管家数据',
            [Windows.Forms.MessageBoxButtons]::YesNo,
            [Windows.Forms.MessageBoxIcon]::Warning,
            [Windows.Forms.MessageBoxDefaultButton]::Button2)
        if ($purgeChoice -ne [Windows.Forms.DialogResult]::Yes) {
            Write-Output 'UNINSTALL_CANCELLED'
            return
        }
        $PurgeRuntimeData = $true
        $ConfirmPurgeRuntimeData = 'DELETE_RUNTIME_DATA'
    }
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

$shortcutShell = New-Object -ComObject WScript.Shell
function Remove-OwnedShortcut([string]$ShortcutPath) {
    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) { return }
    try {
        $resolved = $shortcutShell.CreateShortcut($ShortcutPath)
        $targetPath = [string]$resolved.TargetPath
        $arguments = [string]$resolved.Arguments
        $owned = ($targetPath -and $targetPath.StartsWith($installFull + '\', [StringComparison]::OrdinalIgnoreCase)) -or
                 ($arguments -and $arguments.IndexOf($installFull + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0)
        if ($owned) { Remove-Item -LiteralPath $ShortcutPath -Force }
    } catch {
        Write-Warning "无法核对快捷方式，已保留：$ShortcutPath"
    }
}

$programsFolder = [Environment]::GetFolderPath('Programs')
foreach ($startMenuSpec in @(
    [pscustomobject]@{ Folder = 'AI 中转站总管家'; Open = 'AI 中转站总管家.lnk'; Uninstall = '卸载 AI 中转站总管家.lnk' },
    [pscustomobject]@{ Folder = 'Codex 总管家'; Open = 'Codex 总管家.lnk'; Uninstall = '卸载 Codex 总管家.lnk' }
)) {
    $startMenuFolder = Join-Path $programsFolder $startMenuSpec.Folder
    Remove-OwnedShortcut (Join-Path $startMenuFolder $startMenuSpec.Open)
    Remove-OwnedShortcut (Join-Path $startMenuFolder $startMenuSpec.Uninstall)
    if ((Test-Path -LiteralPath $startMenuFolder -PathType Container) -and
        (Get-ChildItem -LiteralPath $startMenuFolder -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $startMenuFolder -Force
    }
}

if (Test-Path -LiteralPath $uninstallRegistryPath) {
    $registeredRoot = [string](Get-ItemPropertyValue -LiteralPath $uninstallRegistryPath -Name 'InstallLocation' -ErrorAction SilentlyContinue)
    if ($registeredRoot -and [IO.Path]::GetFullPath($registeredRoot).TrimEnd('\').Equals(
        $installFull,
        [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $uninstallRegistryPath -Recurse -Force
    }
}

$runRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path -LiteralPath $runRegistryPath) {
    $runValue = [string](Get-ItemPropertyValue -LiteralPath $runRegistryPath -Name 'CodexTotalManager' -ErrorAction SilentlyContinue)
    if ($runValue -and $runValue.IndexOf($installFull + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Remove-ItemProperty -LiteralPath $runRegistryPath -Name 'CodexTotalManager' -ErrorAction SilentlyContinue
    }
}

$programTargets = @(
    (Join-Path $installFull 'releases'),
    (Join-Path $installFull 'Open-New-Manager-ControlPanel.ps1'),
    (Join-Path $installFull 'Launch-Manager-Hidden.vbs'),
    (Join-Path $installFull 'Uninstall-Manager.vbs'),
    (Join-Path $installFull 'uninstall-local-release.ps1')
)
foreach ($target in $programTargets) {
    $targetFull = [IO.Path]::GetFullPath($target)
    if (-not $targetFull.StartsWith($installFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "卸载目标越过安装目录：$targetFull"
    }
    if (Test-Path -LiteralPath $targetFull) { Remove-Item -LiteralPath $targetFull -Recurse -Force }
}

# Remove only new-name or legacy-name shortcuts that resolve back to this exact
# installation root. A similarly named user shortcut to another program is kept.
foreach ($desktopFolder in @(
    [Environment]::GetFolderPath('DesktopDirectory'),
    [Environment]::GetFolderPath('CommonDesktopDirectory')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
    $desktopFull = [IO.Path]::GetFullPath($desktopFolder).TrimEnd('\')
    foreach ($shortcutPattern in @('AI 中转站总管家*.lnk', 'Codex 总管家*.lnk')) {
        foreach ($shortcut in @(Get-ChildItem -LiteralPath $desktopFull -Filter $shortcutPattern -File -ErrorAction SilentlyContinue)) {
            $shortcutFull = [IO.Path]::GetFullPath($shortcut.FullName)
            if (-not $shortcutFull.StartsWith($desktopFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw "快捷方式越过桌面目录：$shortcutFull"
            }
            Remove-OwnedShortcut $shortcutFull
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
