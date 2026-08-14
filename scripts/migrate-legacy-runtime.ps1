[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [string]$CanonicalRuntime,
    [string]$LegacyRuntime,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$localData = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData')).TrimEnd('\')
$expectedCanonical = Join-Path $localData 'CodexTotalManager\runtime-v3'
$expectedLegacy = Join-Path $localData 'CodexModelManager'
if ([string]::IsNullOrWhiteSpace($CanonicalRuntime)) { $CanonicalRuntime = $expectedCanonical }
if ([string]::IsNullOrWhiteSpace($LegacyRuntime)) { $LegacyRuntime = $expectedLegacy }
$canonicalFull = [IO.Path]::GetFullPath($CanonicalRuntime).TrimEnd('\')
$legacyFull = [IO.Path]::GetFullPath($LegacyRuntime).TrimEnd('\')
if (-not $canonicalFull.Equals($expectedCanonical, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Canonical runtime must be exactly $expectedCanonical"
}
if (-not $legacyFull.Equals($expectedLegacy, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Legacy runtime must be exactly $expectedLegacy"
}
if (-not $Apply) {
    Write-Output 'DRY_RUN_ONLY: pass -Apply to migrate the legacy local runtime; no files were changed.'
    exit 0
}
if (-not (Test-Path -LiteralPath $canonicalFull -PathType Container)) { throw 'Canonical runtime is missing.' }
if (-not (Test-Path -LiteralPath $legacyFull -PathType Container)) { throw 'Legacy runtime is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $canonicalFull '.total-manager-runtime-root') -PathType Leaf)) {
    throw 'Canonical runtime marker is missing.'
}

$managerProcesses = @(Get-CimInstance Win32_Process -Filter "Name='CodexModelManager.exe'" -ErrorAction SilentlyContinue)
if ($managerProcesses.Count -ne 0) { throw 'CodexModelManager is running; migration refused.' }
$managedPorts = [Collections.Generic.HashSet[int]]::new()
[void]$managedPorts.Add(10100)
[void]$managedPorts.Add(10110)
$settingsPath = Join-Path $canonicalFull 'settings.json'
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    $runtimeSettings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($candidate in @($runtimeSettings.NativeEnginePort, $runtimeSettings.UnifiedGatewayPort)) {
        if ($null -ne $candidate -and [int]$candidate -ge 1024 -and [int]$candidate -le 65535) {
            [void]$managedPorts.Add([int]$candidate)
        }
    }
}
$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $managedPorts.Contains([int]$_.LocalPort) })
if ($listeners.Count -ne 0) {
    throw "A configured Manager port is listening ($(@($listeners.LocalPort | Sort-Object -Unique) -join ', ')); migration refused."
}

function Get-LedgerFiles([string]$root) {
    @(Get-ChildItem -LiteralPath $root -File -Force -ErrorAction Stop |
        Where-Object { $_.Name -match '^account-(?:ledger|quota|request|token|usage)-' } |
        Sort-Object Name)
}

$sourceFiles = Get-LedgerFiles $legacyFull
if ($sourceFiles.Count -lt 10) { throw 'Legacy ledger file set is unexpectedly small.' }
$required = @(
    'account-ledger-identity.key',
    'account-ledger-key-domain.json',
    'account-usage-projection-v1.json'
)
foreach ($name in $required) {
    if ($sourceFiles.Name -notcontains $name) { throw "Legacy ledger is missing $name" }
}
$attemptSegments = @($sourceFiles | Where-Object { $_.Name -match '^account-token-attempts-\d{4}-\d{2}\.jsonl$' })
if ($attemptSegments.Count -eq 0) {
    throw 'Legacy ledger has no monthly account-token-attempts-YYYY-MM.jsonl segment.'
}
$sourceHashes = @{}
foreach ($file in $sourceFiles) {
    $sourceHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}

$legacySecretsPath = Join-Path $legacyFull 'secrets.json'
if (-not (Test-Path -LiteralPath $legacySecretsPath -PathType Leaf)) { throw 'Legacy secret store is missing.' }
$legacySecrets = Get-Content -LiteralPath $legacySecretsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$secretNames = @($legacySecrets.PSObject.Properties.Name)
if ($secretNames.Count -ne 1 -or $secretNames[0] -ne 'internal:unified-gateway:client') {
    throw 'Legacy secret store contains unexpected entries; migration refused.'
}

$installRoot = Split-Path -Parent $canonicalFull
$workRoot = Join-Path $installRoot ('migration-work-' + [Guid]::NewGuid().ToString('N'))
$stageRoot = Join-Path $workRoot 'stage'
$backupRoot = Join-Path $workRoot 'rollback'
New-Item -ItemType Directory -Path $stageRoot, $backupRoot -Force | Out-Null

try {
    foreach ($file in $sourceFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stageRoot $file.Name) -Force
    }
    foreach ($file in $sourceFiles) {
        $staged = Join-Path $stageRoot $file.Name
        if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $sourceHashes[$file.Name]) {
            throw "Staged ledger hash mismatch: $($file.Name)"
        }
    }

    $currentFiles = Get-LedgerFiles $canonicalFull
    foreach ($file in $currentFiles) {
        if ($PSCmdlet.ShouldProcess($file.FullName, 'Move old canonical ledger file to rollback staging')) {
            Move-Item -LiteralPath $file.FullName -Destination (Join-Path $backupRoot $file.Name) -Force
        }
    }
    foreach ($file in $sourceFiles) {
        if ($PSCmdlet.ShouldProcess($file.FullName, 'Install verified legacy ledger file into canonical runtime')) {
            Copy-Item -LiteralPath (Join-Path $stageRoot $file.Name) `
                -Destination (Join-Path $canonicalFull $file.Name) -Force
        }
    }
    if ($PSCmdlet.ShouldProcess($legacySecretsPath, 'Replace polluted canonical secret store with verified legacy gateway-only store')) {
        Copy-Item -LiteralPath $legacySecretsPath -Destination (Join-Path $canonicalFull 'secrets.json') -Force
    }

    $installedFiles = Get-LedgerFiles $canonicalFull
    if ($installedFiles.Count -ne $sourceFiles.Count) { throw 'Installed ledger file count mismatch.' }
    foreach ($file in $sourceFiles) {
        $installed = Join-Path $canonicalFull $file.Name
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf) -or
            (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $sourceHashes[$file.Name]) {
            throw "Installed ledger verification failed: $($file.Name)"
        }
    }

    $report = [ordered]@{
        schemaVersion = 1
        migratedAt = (Get-Date).ToUniversalTime().ToString('o')
        source = $legacyFull
        target = $canonicalFull
        ledgerFileCount = $sourceFiles.Count
        ledgerBytes = [long](($sourceFiles | Measure-Object Length -Sum).Sum)
        ledgerIdentitySha256 = $sourceHashes['account-ledger-identity.key']
        preservedSecretEntries = @('internal:unified-gateway:client')
        note = 'Only the verified account ledger and gateway admission secret were migrated.'
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content `
        -LiteralPath (Join-Path $canonicalFull 'runtime-migration-state.json') -Encoding UTF8
    Remove-Item -LiteralPath $workRoot -Recurse -Force
    Write-Output "LOCAL_RUNTIME_MIGRATED ledgerFiles=$($sourceFiles.Count) ledgerBytes=$($report.ledgerBytes)"
} catch {
    foreach ($file in @(Get-LedgerFiles $canonicalFull)) {
        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $backupRoot -File -Force -ErrorAction SilentlyContinue)) {
        Move-Item -LiteralPath $file.FullName -Destination (Join-Path $canonicalFull $file.Name) -Force
    }
    throw
}
