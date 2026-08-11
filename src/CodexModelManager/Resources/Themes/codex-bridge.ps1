param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('Discover', 'Processes', 'Listeners', 'Launch', 'Stop', 'OpenCodex')]
  [string]$Action,
  [string]$Executable,
  [int]$Port,
  [string]$AppUserModelId,
  [string]$ArgumentsBase64,
  [string]$ForceStop = 'false'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Result([AllowNull()][object]$Value) {
  if ($null -eq $Value) { [Console]::Out.Write('null'); return }
  [Console]::Out.Write(($Value | ConvertTo-Json -Depth 8 -Compress))
}

function Resolve-ProcessPath([object]$ProcessInfo) {
  if ($ProcessInfo.ExecutablePath) { return "$($ProcessInfo.ExecutablePath)" }
  try { return (Get-Process -Id ([int]$ProcessInfo.ProcessId) -ErrorAction Stop).Path } catch { return $null }
}

function Path-Equals([string]$Left, [string]$Right) {
  if (-not $Left -or -not $Right) { return $false }
  try { return [System.IO.Path]::GetFullPath($Left) -ieq [System.IO.Path]::GetFullPath($Right) } catch { return $false }
}

function Convert-Package([object]$Package) {
  if ("$($Package.Name)" -ine 'OpenAI.Codex' -or "$($Package.SignatureKind)" -ine 'Store' -or
      [bool]$Package.IsDevelopmentMode -or -not $Package.InstallLocation) { return $null }
  $root = "$($Package.InstallLocation)"
  $exe = Join-Path $root 'app\ChatGPT.exe'
  if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return $null }
  $manifest = Get-AppxPackageManifest -Package $Package -ErrorAction Stop
  $apps = @($manifest.Package.Applications.Application | Where-Object {
    "$($_.Executable)".Replace('/', '\') -ieq 'app\ChatGPT.exe'
  })
  if ($apps.Count -ne 1) { return $null }
  $applicationId = "$($apps[0].Id)"
  $family = "$($Package.PackageFamilyName)"
  if ($family -cnotmatch '^[A-Za-z0-9._-]{1,128}$' -or $applicationId -cnotmatch '^[A-Za-z0-9._-]{1,64}$') {
    return $null
  }
  return [ordered]@{
    platform = 'win32'; bundle = $root; executable = $exe; version = "$($Package.Version)"
    appUserModelId = "$family!$applicationId"; packageFamilyName = $family; applicationId = $applicationId
  }
}

function Discover-Codex {
  $packages = @(Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction SilentlyContinue | Sort-Object Version -Descending)
  foreach ($package in $packages) {
    $candidate = Convert-Package $package
    if ($null -ne $candidate) { return $candidate }
  }

  # Restricted hosts sometimes cannot enumerate AppX registration. A running
  # official package is still discoverable by its exact protected path and
  # manifest. This fallback never scans or launches arbitrary executables.
  $running = @(Get-Process -Name 'ChatGPT' -ErrorAction SilentlyContinue)
  foreach ($process in $running) {
    $path = try { $process.Path } catch { $null }
    if (-not $path -or $path -notmatch '(?i)^(?<root>[A-Z]:\\Program Files\\WindowsApps\\OpenAI\.Codex_(?<version>[0-9.]+)_(?:x64|arm64)__2p2nqsd0c76g0)\\app\\ChatGPT\.exe$') { continue }
    $root = $Matches.root
    $version = $Matches.version
    $manifestPath = Join-Path $root 'AppxManifest.xml'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }
    try { [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath } catch { continue }
    if ("$($manifest.Package.Identity.Name)" -ine 'OpenAI.Codex') { continue }
    $apps = @($manifest.Package.Applications.Application | Where-Object {
      "$($_.Executable)".Replace('/', '\') -ieq 'app\ChatGPT.exe'
    })
    if ($apps.Count -ne 1) { continue }
    $applicationId = "$($apps[0].Id)"
    if ($applicationId -cnotmatch '^[A-Za-z0-9._-]{1,64}$') { continue }
    $family = 'OpenAI.Codex_2p2nqsd0c76g0'
    return [ordered]@{
      platform = 'win32'; bundle = $root; executable = $path; version = $version
      appUserModelId = "$family!$applicationId"; packageFamilyName = $family; applicationId = $applicationId
    }
  }
  return $null
}

function Get-ExactProcesses([string]$Path) {
  if (-not [System.IO.Path]::IsPathRooted($Path)) { throw 'Executable must be an absolute path.' }
  return @(Get-Process -Name 'ChatGPT' -ErrorAction SilentlyContinue | Where-Object {
    $processPath = try { $_.Path } catch { $null }
    Path-Equals $processPath $Path
  })
}

function Initialize-PackageLauncher {
  if ('CodexThemes.PackageLauncher' -as [type]) { return }
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace CodexThemes {
  [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IApplicationActivationManager {
    [PreserveSig] int ActivateApplication(
      [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
      [MarshalAs(UnmanagedType.LPWStr)] string arguments,
      uint options,
      out uint processId);
  }
  [ComImport, Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
  internal class ApplicationActivationManager {}
  public static class PackageLauncher {
    public static uint Launch(string id, string arguments) {
      var manager = (IApplicationActivationManager)new ApplicationActivationManager();
      try {
        uint pid;
        int result = manager.ActivateApplication(id, arguments ?? string.Empty, 0, out pid);
        Marshal.ThrowExceptionForHR(result);
        return pid;
      } finally {
        if (Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager);
      }
    }
  }
}
'@
}

function Quote-Argument([string]$Value) {
  if ($Value.Contains('"')) { throw 'Launch arguments containing a quote are not allowed.' }
  if ($Value.Length -eq 0) { return '""' }
  if ($Value -notmatch '\s') { return $Value }
  $escaped = [regex]::Replace($Value, '(\\+)$', '$1$1')
  return '"' + $escaped + '"'
}

switch ($Action) {
  'Discover' { Write-Result (Discover-Codex); break }
  'Processes' {
    Write-Result @((Get-ExactProcesses $Executable) | ForEach-Object { [int]$_.Id })
    break
  }
  'Listeners' {
    if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port is outside the allowed range.' }
    $rows = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | ForEach-Object {
      $p = Get-Process -Id ([int]$_.OwningProcess) -ErrorAction SilentlyContinue
      $path = if ($p) { try { $p.Path } catch { $null } } else { $null }
      [ordered]@{ localAddress = "$($_.LocalAddress)"; owningProcess = [int]$_.OwningProcess; executablePath = $path }
    })
    Write-Result $rows
    break
  }
  'Launch' {
    if ($AppUserModelId -cnotmatch '^[A-Za-z0-9._-]{1,128}![A-Za-z0-9._-]{1,64}$') { throw 'Invalid Codex AppUserModelId.' }
    $arguments = @()
    if ($ArgumentsBase64) {
      $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64))
      $arguments = @($json | ConvertFrom-Json)
      foreach ($value in $arguments) {
        if ($value -isnot [string] -or $value.Length -gt 1024) { throw 'Invalid Codex launch argument.' }
      }
    }
    Initialize-PackageLauncher
    $line = (($arguments | ForEach-Object { Quote-Argument "$_" }) -join ' ')
    $pid = [CodexThemes.PackageLauncher]::Launch($AppUserModelId, $line)
    if ($pid -le 0) { throw 'Windows did not return a Codex process id.' }
    Write-Result ([int]$pid)
    break
  }
  'Stop' {
    $items = @(Get-ExactProcesses $Executable)
    foreach ($item in $items) { try { [void](Get-Process -Id $item.Id -ErrorAction Stop).CloseMainWindow() } catch {} }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-ExactProcesses $Executable).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    $remaining = @(Get-ExactProcesses $Executable)
    if ($remaining.Count -gt 0 -and $ForceStop -ne 'true') { throw 'Codex is still open; forced stop was not authorized.' }
    foreach ($item in $remaining) {
      $current = Get-Process -Id ([int]$item.Id) -ErrorAction SilentlyContinue
      $currentPath = if ($current) { try { $current.Path } catch { $null } } else { $null }
      if ($current -and (Path-Equals $currentPath $Executable)) {
        Stop-Process -Id $item.Id -Force -ErrorAction Stop
      }
    }
    Start-Sleep -Milliseconds 400
    if ((Get-ExactProcesses $Executable).Count -gt 0) { throw 'Codex could not be stopped safely.' }
    Write-Result $true
    break
  }
  'OpenCodex' {
    Start-Process -FilePath 'codex://threads/new' -ErrorAction Stop
    Write-Result $true
    break
  }
}
