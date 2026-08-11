[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('ApplyInstalled', 'Pause', 'PrepareSession', 'ImportZip')]
  [string]$Action,
  [string]$ThemeId = '',
  [string]$ArchivePath = '',
  [int]$Port = 9335,
  [switch]$AllowRestart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Complete-ManagerOperation {
  param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Success', 'NeedsRestart', 'Failed', 'Canceled')]
    [string]$Status,
    [Parameter(Mandatory = $true)][string]$Message,
    [bool]$Recovered = $false,
    [AllowNull()][string]$BackupPath = $null,
    [int]$ExitCode = 0
  )
  [pscustomobject][ordered]@{
    status = $Status
    message = $Message
    recovered = $Recovered
    backupPath = $BackupPath
  } | ConvertTo-Json -Compress
  exit $ExitCode
}

function Test-ManagerPathWithin {
  param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Root)
  $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
  $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
  return $fullPath.Equals($fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $fullPath.StartsWith($fullRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-ManagerNoReparsePath {
  param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Root)
  if (-not (Test-ManagerPathWithin -Path $Path -Root $Root)) {
    throw '皮肤路径越过了 Dream Skin 的受控目录。'
  }
  $current = [System.IO.Path]::GetFullPath($Path)
  $stop = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
  while ($true) {
    if (Test-Path -LiteralPath $current) {
      $item = Get-Item -LiteralPath $current -Force
      if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw '皮肤目录含有链接或重定向，已经拒绝运行。'
      }
    }
    $trimmed = $current.TrimEnd('\')
    if ($trimmed.Equals($stop, [System.StringComparison]::OrdinalIgnoreCase)) { break }
    $parent = [System.IO.Path]::GetDirectoryName($trimmed)
    if (-not $parent) { throw '无法验证皮肤目录边界。' }
    $current = $parent
  }
}

function Get-ManagerLastLine {
  param([object[]]$Lines)
  $last = @($Lines | ForEach-Object { "$_".Trim() } | Where-Object { $_ } | Select-Object -Last 1)
  if ($last.Count -eq 0) { return '' }
  return "$($last[0])"
}

function Get-ManagerDetail {
  param([AllowNull()][string]$Value, [string]$Fallback = '没有返回原因')
  if ([string]::IsNullOrWhiteSpace($Value)) { return $Fallback }
  return $Value
}

$stateRoot = if ($env:CMM_SANDBOX_DREAMSKIN) { $env:CMM_SANDBOX_DREAMSKIN } else { Join-Path $env:LOCALAPPDATA 'CodexDreamSkin' }
$bundledEngineRoot = Join-Path $PSScriptRoot '..\CodexDreamSkin'
$bundledEngineRoot = [System.IO.Path]::GetFullPath($bundledEngineRoot)
if (Test-Path -LiteralPath (Join-Path $bundledEngineRoot 'VERSION') -PathType Leaf) {
  $engineRoot = $bundledEngineRoot
} else {
  $engineRoot = Join-Path $stateRoot 'engine'
}
$scriptsRoot = Join-Path $engineRoot 'scripts'
$commonScript = Join-Path $scriptsRoot 'common-windows.ps1'
$themeScript = Join-Path $scriptsRoot 'theme-windows.ps1'
$startScript = Join-Path $scriptsRoot 'start-dream-skin.ps1'
$restoreScript = Join-Path $scriptsRoot 'restore-dream-skin.ps1'

try {
  foreach ($required in @($engineRoot, $scriptsRoot, $commonScript, $themeScript, $startScript, $restoreScript)) {
    if (-not (Test-Path -LiteralPath $required)) {
      throw '没有找到完整的 Codex Dream Skin 引擎。'
    }
    if ($required.StartsWith($stateRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
      Assert-ManagerNoReparsePath -Path $required -Root $stateRoot
    }
  }

  . $commonScript
  . $themeScript
  Assert-DreamSkinPort -Port $Port
  $paths = Get-DreamSkinThemePaths -StateRoot $stateRoot

  function Invoke-ManagerStart {
    param([bool]$Restart, [bool]$FullTheme, [bool]$RequireUnpaused = $true)
    $powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
    $arguments = @(
      '-NoLogo', '-NoProfile', '-STA', '-WindowStyle', 'Hidden',
      '-ExecutionPolicy', 'RemoteSigned', '-File', $startScript,
      '-Port', "$Port", '-OperationLockTimeoutMilliseconds', '30000'
    )
    if ($Restart) { $arguments += '-RestartExisting' }
    if ($FullTheme) { $arguments += '-FullTheme' }
    if ($RequireUnpaused) { $arguments += '-RequireUnpaused' }
    $env:CMM_SANDBOX_DREAMSKIN = $stateRoot
    try {
      $output = @(& $powershell @arguments 2>&1)
    } finally {
      Remove-Item Env:CMM_SANDBOX_DREAMSKIN -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{
      ExitCode = [int]$LASTEXITCODE
      Detail = Get-ManagerLastLine -Lines $output
    }
  }

  function Invoke-ManagerRestoreOfficial {
    $powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
    $arguments = @(
      '-NoLogo', '-NoProfile', '-STA', '-WindowStyle', 'Hidden',
      '-ExecutionPolicy', 'RemoteSigned', '-File', $restoreScript,
      '-Port', "$Port", '-ForceRestart'
    )
    $env:CMM_SANDBOX_DREAMSKIN = $stateRoot
    try {
      $output = @(& $powershell @arguments 2>&1)
    } finally {
      Remove-Item Env:CMM_SANDBOX_DREAMSKIN -ErrorAction SilentlyContinue
    }
    $exitCode = [int]$LASTEXITCODE
    if ($exitCode -eq 0) {
      Set-DreamSkinPaused -Paused $true -StateRoot $stateRoot | Out-Null
    }
    return [pscustomobject]@{
      ExitCode = $exitCode
      Detail = Get-ManagerLastLine -Lines $output
    }
  }

  function New-ManagerThemeBackup {
    $backupRoot = Join-Path $stateRoot 'manager-theme-backups'
    Ensure-DreamSkinManagedDirectory -Path $backupRoot -Root $stateRoot
    $backupPath = Join-Path $backupRoot ((Get-Date).ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N'))
    Assert-ManagerNoReparsePath -Path $backupPath -Root $stateRoot
    $activeExists = Test-Path -LiteralPath $paths.Active -PathType Container
    $fingerprint = $null
    if ($activeExists) {
      $null = Read-DreamSkinTheme -ThemeDirectory $paths.Active
      $fingerprint = Get-DreamSkinThemeRuntimeContentFingerprint -ThemeDirectory $paths.Active
      Copy-Item -LiteralPath $paths.Active -Destination $backupPath -Recurse -ErrorAction Stop
    } else {
      New-Item -ItemType Directory -Path $backupPath -ErrorAction Stop | Out-Null
    }
    $state = $null
    try { $state = Read-DreamSkinState -Path $paths.State } catch {}
    $nativeShell = $false
    if ($null -ne $state -and @($state.PSObject.Properties.Name) -contains 'nativeShell') {
      $nativeShell = [bool]$state.nativeShell
    }
    return [pscustomobject]@{
      Path = $backupPath
      ActiveExisted = $activeExists
      Fingerprint = $fingerprint
      Paused = Test-DreamSkinPaused -StateRoot $stateRoot
      NativeShell = $nativeShell
    }
  }

  function Restore-ManagerThemeBackup {
    param([Parameter(Mandatory = $true)][object]$Backup)
    if (-not $Backup.ActiveExisted) {
      throw '原活动主题不存在，无法做自动恢复。'
    }
    Assert-ManagerNoReparsePath -Path $Backup.Path -Root $stateRoot
    $loaded = Read-DreamSkinTheme -ThemeDirectory $Backup.Path
    $cssPath = Join-Path $Backup.Path 'theme.css'
    if (-not (Test-Path -LiteralPath $cssPath -PathType Leaf)) { $cssPath = $null }
    if ($cssPath) { Assert-DreamSkinSafeCssFile -Path $cssPath }
    $theme = $loaded.Theme | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $null = Set-DreamSkinActiveTheme -ImagePath $loaded.ImagePath -Theme $theme `
      -SafeCssPath $cssPath -StateRoot $stateRoot
    Set-DreamSkinPaused -Paused ([bool]$Backup.Paused) -StateRoot $stateRoot | Out-Null
    $restored = Get-DreamSkinThemeRuntimeContentFingerprint -ThemeDirectory $paths.Active
    if ($Backup.Fingerprint -and $restored -cne $Backup.Fingerprint) {
      throw '旧主题文件已写回，但完整性校验没有通过。'
    }
  }

  if ($Action -eq 'ImportZip') {
    if (-not $ArchivePath) { throw '没有选择主题 ZIP。' }
    $archive = [System.IO.Path]::GetFullPath($ArchivePath)
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or
      [System.IO.Path]::GetExtension($archive) -cne '.zip') {
      throw '请选择一个真实的 .zip 主题包。'
    }
    if (((Get-Item -LiteralPath $archive -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw '主题 ZIP 是链接文件，已经拒绝导入。'
    }
    $imported = Import-DreamSkinThemeZip -ArchivePath $archive -StateRoot $stateRoot
    $message = if ($imported.Status -ceq 'Duplicate') {
      "主题已存在：$($imported.Name)，没有重复写入。"
    } else {
      "主题已安全导入：$($imported.Name)。"
    }
    Complete-ManagerOperation -Status Success -Message $message
  }

  if ($Action -eq 'PrepareSession') {
    $prepareWasPaused = Test-DreamSkinPaused -StateRoot $stateRoot
    $session = Get-DreamSkinLiveSessionContext -StateRoot $stateRoot
    if ($null -ne $session) {
      if (Test-DreamSkinPaused -StateRoot $stateRoot) {
        $lock = Enter-DreamSkinOperationLock
        try {
          Set-DreamSkinPaused -Paused $false -StateRoot $stateRoot | Out-Null
          $live = Invoke-DreamSkinLiveApply -StateRoot $stateRoot
        } finally {
          Exit-DreamSkinOperationLock -Mutex $lock
        }
        if (-not $live.Applied) {
          Set-DreamSkinPaused -Paused $true -StateRoot $stateRoot | Out-Null
          if (-not $AllowRestart) {
            Complete-ManagerOperation -Status NeedsRestart -Message '安全换肤通道需要重启 Codex 才能恢复。'
          }
        } else {
          Complete-ManagerOperation -Status Success -Message '安全换肤通道已连接。'
        }
      } else {
        Complete-ManagerOperation -Status Success -Message '安全换肤通道已经连接。'
      }
    }

    $codex = $null
    $codexRunning = $false
    try {
      $codex = Get-DreamSkinCodexInstall
      $codexRunning = (Get-DreamSkinCodexProcesses -Codex $codex).Count -gt 0
    } catch {}
    if ($codexRunning -and -not $AllowRestart) {
      Complete-ManagerOperation -Status NeedsRestart `
        -Message 'Codex 正以普通模式运行；连接安全换肤通道需要关闭并重新打开一次。'
    }
    Set-DreamSkinPaused -Paused $false -StateRoot $stateRoot | Out-Null
    $started = Invoke-ManagerStart -Restart:([bool]($AllowRestart -and $codexRunning)) -FullTheme $true
    if ($started.ExitCode -eq 0) {
      Complete-ManagerOperation -Status Success -Message '安全换肤通道已连接。'
    }
    Set-DreamSkinPaused -Paused ([bool]$prepareWasPaused) -StateRoot $stateRoot | Out-Null
    Complete-ManagerOperation -Status Failed `
      -Message ("安全换肤通道没有启动：" + (Get-ManagerDetail -Value $started.Detail)) -ExitCode 1
  }

  if ($Action -eq 'Pause') {
    $session = Get-DreamSkinLiveSessionContext -StateRoot $stateRoot
    if ($null -ne $session) {
      $lock = Enter-DreamSkinOperationLock
      try {
        Set-DreamSkinPaused -Paused $true -StateRoot $stateRoot | Out-Null
        $removed = Invoke-DreamSkinLiveRemove -StateRoot $stateRoot -Quiet
      } finally {
        Exit-DreamSkinOperationLock -Mutex $lock
      }
      if ($removed.Removed) {
        Complete-ManagerOperation -Status Success -Message '已实时卸下皮肤，Codex 已恢复官方外观。'
      }
      if (-not $AllowRestart) {
        Complete-ManagerOperation -Status NeedsRestart `
          -Message '暂停请求已记住，但当前窗口无法实时卸下皮肤；重启后可恢复官方外观。'
      }
    } else {
      Set-DreamSkinPaused -Paused $true -StateRoot $stateRoot | Out-Null
      $codex = $null
      $codexRunning = $false
      $ownsPort = $false
      try {
        $codex = Get-DreamSkinCodexInstall
        $codexRunning = (Get-DreamSkinCodexProcesses -Codex $codex).Count -gt 0
        $ownsPort = Test-DreamSkinCodexPortOwner -Port $Port -Codex $codex
      } catch {}
      if (-not $codexRunning -or -not $ownsPort) {
        Complete-ManagerOperation -Status Success `
          -Message '当前已经是官方外观；同时记住了暂停皮肤。'
      }
      if (-not $AllowRestart) {
        Complete-ManagerOperation -Status NeedsRestart `
          -Message '当前皮肤会话无法热卸载，需要关闭并重新打开 Codex。'
      }
    }

    $restored = Invoke-ManagerRestoreOfficial
    if ($restored.ExitCode -eq 0) {
      Complete-ManagerOperation -Status Success -Message 'Codex 已重新打开，并恢复官方外观。'
    }
    Complete-ManagerOperation -Status Failed `
      -Message ("恢复官方外观没有完成：" + (Get-ManagerDetail -Value $restored.Detail)) -ExitCode 1
  }

  if ($ThemeId -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,79}\z' -or $ThemeId.EndsWith('.')) {
    throw '主题标识不安全，已经拒绝切换。'
  }
  $savedRoot = [System.IO.Path]::GetFullPath($paths.Saved).TrimEnd('\')
  $themeDirectory = [System.IO.Path]::GetFullPath((Join-Path $savedRoot $ThemeId)).TrimEnd('\')
  if (-not [System.IO.Path]::GetDirectoryName($themeDirectory).Equals(
      $savedRoot,
      [System.StringComparison]::OrdinalIgnoreCase
    ) -or -not (Test-Path -LiteralPath $themeDirectory -PathType Container)) {
    throw '主题不在 Dream Skin 的受控主题库中。'
  }
  Assert-ManagerNoReparsePath -Path $themeDirectory -Root $stateRoot
  $selected = Read-DreamSkinTheme -ThemeDirectory $themeDirectory

  $session = Get-DreamSkinLiveSessionContext -StateRoot $stateRoot
  $codex = $null
  $codexRunning = $false
  try {
    $codex = Get-DreamSkinCodexInstall
    $codexRunning = (Get-DreamSkinCodexProcesses -Codex $codex).Count -gt 0
  } catch {}
  if ($null -eq $session -and $codexRunning -and -not $AllowRestart) {
    Complete-ManagerOperation -Status NeedsRestart `
      -Message 'Codex 正以普通模式运行；换肤需要关闭并重新打开一次。当前主题没有改动。'
  }

  $backup = $null
  $candidateFingerprint = $null
  $lock = Enter-DreamSkinOperationLock
  try {
    $backup = New-ManagerThemeBackup
    $null = Use-DreamSkinSavedTheme -ThemeDirectory $themeDirectory -StateRoot $stateRoot
    Set-DreamSkinPaused -Paused $false -StateRoot $stateRoot | Out-Null
    $candidateFingerprint = Get-DreamSkinThemeRuntimeContentFingerprint -ThemeDirectory $paths.Active
    if ($null -ne $session) {
      $live = Invoke-DreamSkinLiveApply -StateRoot $stateRoot
      if ($live.Applied) {
        Complete-ManagerOperation -Status Success `
          -Message "已实时切换为“$($selected.Theme.name)”，没有重启 Codex。" `
          -BackupPath $backup.Path
      }
      if (-not $AllowRestart) {
        Restore-ManagerThemeBackup -Backup $backup
        if ($backup.Paused) {
          $null = Invoke-DreamSkinLiveRemove -StateRoot $stateRoot -Quiet
        } else {
          $oldLive = Invoke-DreamSkinLiveApply -StateRoot $stateRoot -NativeShell:([bool]$backup.NativeShell)
          if (-not $oldLive.Applied) { throw '热切换失败，旧主题文件已恢复，但旧界面复核没有通过。' }
        }
        Complete-ManagerOperation -Status NeedsRestart `
          -Message '实时切换没有通过显示校验，旧主题已恢复；如需继续，可确认重启 Codex。' `
          -Recovered $true -BackupPath $backup.Path
      }
    }
  } finally {
    Exit-DreamSkinOperationLock -Mutex $lock
  }

  $restart = [bool]($AllowRestart -and $codexRunning)
  $started = Invoke-ManagerStart -Restart:$restart -FullTheme $true
  if ($started.ExitCode -eq 0) {
    $mode = if ($restart) { 'Codex 已安全重启' } else { 'Codex 已启动' }
    Complete-ManagerOperation -Status Success `
      -Message "$mode，并应用“$($selected.Theme.name)”。" -BackupPath $backup.Path
  }

  $recovered = $false
  $recoveryDetail = ''
  try {
    $rollbackLock = Enter-DreamSkinOperationLock
    try {
      $currentFingerprint = Get-DreamSkinThemeRuntimeContentFingerprint -ThemeDirectory $paths.Active
      if ($currentFingerprint -cne $candidateFingerprint) {
        throw '另一个换肤操作已经生效，因此没有覆盖它。'
      }
      Restore-ManagerThemeBackup -Backup $backup
    } finally {
      Exit-DreamSkinOperationLock -Mutex $rollbackLock
    }
    if ($restart) {
      if ($backup.Paused) {
        $recovery = Invoke-ManagerRestoreOfficial
      } else {
        $recovery = Invoke-ManagerStart -Restart $true -FullTheme:(-not [bool]$backup.NativeShell) `
          -RequireUnpaused $true
      }
      if ($recovery.ExitCode -ne 0) {
        throw ("旧主题已写回，但重新显示失败：" + (Get-ManagerDetail -Value $recovery.Detail))
      }
    }
    $recovered = $true
    $recoveryDetail = '旧主题已经自动恢复。'
  } catch {
    $recoveryDetail = "自动恢复未完整验证：$($_.Exception.Message)"
  }
  Complete-ManagerOperation -Status Failed `
    -Message ("新主题没有通过验证。$recoveryDetail 原因：" + (Get-ManagerDetail -Value $started.Detail)) `
    -Recovered $recovered -BackupPath $backup.Path -ExitCode 1
} catch {
  Complete-ManagerOperation -Status Failed -Message $_.Exception.Message -ExitCode 1
}
