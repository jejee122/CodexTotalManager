[CmdletBinding()]
param(
    [switch]$Silent,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$runtimeRoot = Join-Path $root 'runtime-v3'
$pointerPath = Join-Path $runtimeRoot 'active-release.json'
$configPath = Join-Path $runtimeRoot 'unified-gateway.json'
$logDir = Join-Path $runtimeRoot 'logs'
$mutex = [Threading.Mutex]::new($false, 'Local\CodexTotalManager-ControlPanel')
$ownsMutex = $false
$transcriptStarted = $false
$gateway = $null

function Show-Result([string]$message, [string]$title, [string]$iconName) {
    if ($Silent) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $icon = [Enum]::Parse([Windows.Forms.MessageBoxIcon], $iconName)
        [Windows.Forms.MessageBox]::Show($message, $title, 'OK', $icon) | Out-Null
    } catch {
        Write-Output $message
    }
}

function Assert-PathInsideRoot([string]$candidate, [string]$base) {
    $fullCandidate = [IO.Path]::GetFullPath($candidate)
    $fullBase = ([IO.Path]::GetFullPath($base)).TrimEnd('\') + '\'
    if (-not $fullCandidate.StartsWith($fullBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the Total Manager root: $fullCandidate"
    }
    return $fullCandidate
}

function Get-ManagerGatewayProcesses {
    $trustedPrefix = $root.TrimEnd('\') + '\'
    @(Get-CimInstance Win32_Process -Filter "Name='CodexModelManager.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
            ([string]$_.ExecutablePath).StartsWith($trustedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            $_.CommandLine -match '(?i)(^|\s)--unified-gateway(\s|$)'
        })
}

function Stop-ManagerGatewayProcess($record) {
    $process = Get-Process -Id ([int]$record.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    try {
        if ($process.MainWindowHandle -ne 0) { [void]$process.CloseMainWindow() }
    } catch { }
    try { $process.WaitForExit(5000) } catch { }
    if (-not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(5000)
    }
}

function Get-Listener([int]$port) {
    @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}

function Assert-SupportedCodexIsolation([object]$manifest, [object]$pointer) {
    $mode = [string]$manifest.isolation.codexMode
    if ([string]$pointer.codexIsolationMode -ne $mode) {
        throw 'Active release pointer and payload manifest disagree about Codex isolation mode.'
    }

    $realCodexAccess = [bool]$manifest.isolation.realCodexAccess
    $gatewayCommandEnabled = [bool]$manifest.isolation.gatewayCommandEnabled
    $defaultConnected = [bool]$manifest.isolation.defaultConnected
    $requiresConfirmation = [bool]$manifest.isolation.connectionRequiresInAppConfirmation
    if ($mode -eq 'DETACHED_ONLY') {
        if ($realCodexAccess -or $gatewayCommandEnabled -or $defaultConnected -or $requiresConfirmation) {
            throw 'Detached release has contradictory Codex access flags.'
        }
        return
    }
    if ($mode -eq 'USER_CONTROLLED_DEFAULT_OFF') {
        if (-not $realCodexAccess -or -not $gatewayCommandEnabled -or $defaultConnected -or -not $requiresConfirmation) {
            throw 'User-controlled release must remain disconnected until the user confirms inside the app.'
        }
        return
    }
    throw "Unsupported Codex isolation mode: $mode"
}

function Read-Release {
    if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) {
        throw "Active release pointer is missing: $pointerPath"
    }
    $pointer = Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($pointer.schemaVersion -ne 2 -or $pointer.product -ne 'CodexTotalManager') {
        throw 'Active release pointer has an unsupported schema or product.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$pointer.productVersion)) {
        throw 'Active release pointer has no product version.'
    }

    $exe = Assert-PathInsideRoot (Join-Path $root ([string]$pointer.relativeExecutable)) $root
    $manifestRelative = if ($pointer.payloadManifest) { [string]$pointer.payloadManifest } else { [string]$pointer.relativeManifest }
    $manifestHashExpected = if ($pointer.payloadManifestSha256) { [string]$pointer.payloadManifestSha256 } else { [string]$pointer.manifestSha256 }
    if ([string]::IsNullOrWhiteSpace($manifestRelative) -or
        $manifestHashExpected -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'Active release pointer has no valid payload manifest reference.'
    }
    $manifestPath = Assert-PathInsideRoot (Join-Path $root $manifestRelative) $root
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Release executable is missing: $exe" }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Release manifest is missing: $manifestPath" }

    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if ($manifestHash -ine $manifestHashExpected) {
        throw 'Release manifest hash mismatch.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 2 -or
        $manifest.product -ne 'CodexTotalManager' -or
        [string]$manifest.productVersion -ne [string]$pointer.productVersion) {
        throw 'Release manifest identity does not match the active pointer.'
    }
    Assert-SupportedCodexIsolation $manifest $pointer

    $payloadRoot = Split-Path -Parent $manifestPath
    $manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.files)) {
        $relative = [string]$file.path
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative.StartsWith('/') -or $relative.StartsWith('\') -or
            $relative -match '^(?:[A-Za-z]:|\.\.?[/\\])' -or
            $relative -match '(^|[/\\])\.\.([/\\]|$)' -or
            -not $manifestPaths.Add($relative.Replace('\', '/'))) {
            throw "Release manifest contains an invalid or duplicate path: $relative"
        }
        if ([string]$file.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "Release manifest contains an invalid SHA-256: $relative"
        }
        $payloadPath = Assert-PathInsideRoot (Join-Path $payloadRoot $relative) $payloadRoot
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Release payload file is missing: $relative"
        }
        $item = Get-Item -LiteralPath $payloadPath
        if ([long]$item.Length -ne [long]$file.bytes -or
            (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash -ine [string]$file.sha256) {
            throw "Release payload verification failed: $relative"
        }
    }
    $unlisted = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        ForEach-Object {
            $relative = $_.FullName.Substring($payloadRoot.Length + 1).Replace('\', '/')
            if (-not $manifestPaths.Contains($relative)) { $relative }
        })
    if ($unlisted.Count -gt 0) {
        throw "Release payload contains files not covered by the manifest: $($unlisted -join ', ')"
    }

    $exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ($exeHash -ine [string]$pointer.sha256) { throw 'Release executable hash mismatch.' }
    $exeInfo = (Get-Item -LiteralPath $exe).VersionInfo
    if ($exeInfo.ProductName -ne 'Codex 总管家') {
        throw "Release executable product name $($exeInfo.ProductName) does not match."
    }
    $actualVersion = [string]$exeInfo.ProductVersion
    if (-not ($actualVersion -eq [string]$pointer.productVersion -or
        $actualVersion.StartsWith(([string]$pointer.productVersion) + '+', [StringComparison]::Ordinal))) {
        throw "Release executable version $actualVersion does not match pointer version $($pointer.productVersion)."
    }
    [pscustomobject]@{ Pointer = $pointer; Executable = $exe; Manifest = $manifest }
}

function Get-ConfiguredGatewayPort {
    $settingsPath = Join-Path $runtimeRoot 'settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { return 10110 }
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $port = if ($null -ne $settings.UnifiedGatewayPort) { [int]$settings.UnifiedGatewayPort } else { 10110 }
    if ($port -lt 1024 -or $port -gt 65535) { throw "Configured gateway port is invalid: $port" }
    return $port
}

function Read-GatewayConfiguration([switch]$AllowMissing) {
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        if ($AllowMissing) { return $null }
        throw "Gateway configuration is missing: $configPath"
    }
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $configuredPort = Get-ConfiguredGatewayPort
    if ([int]$config.port -ne $configuredPort) {
        throw "Gateway config port $($config.port) does not match settings port $configuredPort."
    }
    if ([string]$config.service -ne 'codex-unified-gateway') { throw 'Gateway service identity is invalid.' }
    if ([int]$config.schemaVersion -lt 4) { throw 'Gateway configuration is obsolete and must be rebuilt by the control panel.' }
    if ([string]$config.configurationFingerprint -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'Gateway configuration fingerprint is missing or malformed.'
    }
    return $config
}

function Wait-GatewayHealth(
    [int]$expectedPid,
    [int]$expectedRouteCount,
    [int]$port,
    [string]$expectedService,
    [string]$expectedVersion,
    [string]$expectedFingerprint) {
    $deadline = (Get-Date).AddSeconds(30)
    $lastError = 'listener not ready'
    while ((Get-Date) -lt $deadline) {
        $listeners = Get-Listener $port
        if ($listeners.Count -gt 0) {
            $ownerPids = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
            if ($ownerPids.Count -ne 1 -or $ownerPids[0] -ne $expectedPid) {
                throw "Port $port belongs to another process: $($ownerPids -join ',')."
            }
            try {
                $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 3
                if ($health.status -ne 'ok' -or
                    $health.product -ne 'CodexTotalManager' -or
                    -not ([string]$health.productVersion -eq $expectedVersion -or
                        ([string]$health.productVersion).StartsWith($expectedVersion + '+', [StringComparison]::Ordinal)) -or
                    [string]$health.service -ne $expectedService -or
                    [int]$health.pid -ne $expectedPid -or
                    [int]$health.port -ne $port -or
                    [int]$health.routeCount -ne $expectedRouteCount -or
                    [int]$health.routeGuardVersion -lt 3 -or
                    [string]$health.configurationFingerprint -ine $expectedFingerprint) {
                    throw 'health identity, version, PID, port, route count, configuration fingerprint, or guard version mismatch'
                }
                return $health
            } catch {
                $lastError = $_.Exception.Message
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Gateway health check timed out: $lastError"
}

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) {
        Write-Output 'Codex Total Manager control panel is already running; duplicate launch skipped.'
        exit 0
    }

    $release = Read-Release
    $detachedOnly = [string]$release.Manifest.isolation.codexMode -eq 'DETACHED_ONLY'
    if ($ValidateOnly) {
        if (-not $detachedOnly) { $null = Read-GatewayConfiguration -AllowMissing }
        Write-Output "MANAGER_RELEASE_VALID productVersion=$($release.Pointer.productVersion) payloadFiles=$(@($release.Manifest.files).Count)"
        exit 0
    }

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $runId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $transcriptPath = Join-Path $logDir "launcher-$runId.transcript.log"
    $gatewayStdout = Join-Path $logDir "gateway-$runId.out.log"
    $gatewayStderr = Join-Path $logDir "gateway-$runId.err.log"
    Start-Transcript -Path $transcriptPath -ErrorAction Stop | Out-Null
    $transcriptStarted = $true

    if (-not $detachedOnly) {
        foreach ($record in @(Get-ManagerGatewayProcesses)) { Stop-ManagerGatewayProcess $record }
        $initialGatewayPort = Get-ConfiguredGatewayPort
        if ((Get-Listener $initialGatewayPort).Count -gt 0) {
            Write-Output "GATEWAY_PORT_BUSY_BEFORE_GUI port=$initialGatewayPort; control panel will still open so the port can be changed."
        }
    }

    $env:CMM_RUNTIME_ROOT = $runtimeRoot
    if ($detachedOnly) {
        $env:CMM_DETACHED_UI = '1'
        $env:CMM_DETACHED_DATA_ROOT = Join-Path $runtimeRoot 'detached-data'
    }
    $gui = Start-Process -FilePath $release.Executable `
        -WorkingDirectory (Split-Path $release.Executable) -WindowStyle Normal -PassThru
    $gui.WaitForExit()
    if ($gui.ExitCode -ne 0) { throw "Control panel exited with code $($gui.ExitCode)." }

    if ($detachedOnly) {
        Write-Output "GATEWAY_SKIPPED_DETACHED_ONLY version=$($release.Pointer.productVersion); Codex network chain was not inspected or changed."
        exit 0
    }

    Start-Sleep -Seconds 2
    foreach ($record in @(Get-ManagerGatewayProcesses)) { Stop-ManagerGatewayProcess $record }
    try {
        $gatewayConfig = Read-GatewayConfiguration -AllowMissing
    } catch {
        Write-Output "GATEWAY_DISABLED_INVALID_CONFIG version=$($release.Pointer.productVersion) reason=$($_.Exception.Message)"
        Show-Result 'Control panel closed. The gateway was not restored because its saved port or configuration is stale. Open the control panel again and use Start / Sync Gateway after checking the new port.' `
            'Codex Total Manager' 'Information'
        exit 0
    }
    if ($null -eq $gatewayConfig) {
        Write-Output "GATEWAY_DISABLED_NO_CONFIG version=$($release.Pointer.productVersion)"
        exit 0
    }
    $gatewayPort = [int]$gatewayConfig.port
    if ((Get-Listener $gatewayPort).Count -gt 0) {
        throw "Gateway port $gatewayPort remains owned by an unknown process after the control panel closed."
    }
    $expectedRouteCount = @($gatewayConfig.routes).Count
    if ($expectedRouteCount -eq 0) {
        Write-Output "GATEWAY_DISABLED_NO_ROUTES version=$($release.Pointer.productVersion)"
        Show-Result 'Control panel closed. The gateway remains disabled because no trusted routes are configured.' `
            'Codex Total Manager' 'Information'
        exit 0
    }

    $gateway = Start-Process -FilePath $release.Executable `
        -ArgumentList '--unified-gateway','--config',$configPath `
        -WorkingDirectory (Split-Path $release.Executable) -WindowStyle Hidden `
        -RedirectStandardOutput $gatewayStdout -RedirectStandardError $gatewayStderr -PassThru
    $null = Wait-GatewayHealth $gateway.Id $expectedRouteCount $gatewayPort `
        ([string]$gatewayConfig.service) ([string]$release.Pointer.productVersion) `
        ([string]$gatewayConfig.configurationFingerprint)
    Write-Output "GATEWAY_RESTORED version=$($release.Pointer.productVersion) routes=$expectedRouteCount port=$gatewayPort pid=$($gateway.Id) fingerprint=$($gatewayConfig.configurationFingerprint)"
    Show-Result "Gateway restored: version $($release.Pointer.productVersion), routes $expectedRouteCount." `
        'Codex Total Manager' 'Information'
} catch {
    $message = "Codex Total Manager launch failed: $($_.Exception.Message)"
    Write-Output $message
    if ($null -ne $gateway) {
        try { if (-not $gateway.HasExited) { $gateway.Kill($true) } } catch { }
    }
    Show-Result $message 'Codex Total Manager' 'Warning'
    exit 1
} finally {
    if ($transcriptStarted) { try { Stop-Transcript | Out-Null } catch { } }
    if ($ownsMutex) { try { [void]$mutex.ReleaseMutex() } catch { } }
    $mutex.Dispose()
}
