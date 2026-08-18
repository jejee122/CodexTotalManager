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

if (-not $Apply) {
    Write-Output 'DRY_RUN_ONLY: pass -Apply to migrate the legacy local runtime; no files were changed.'
    exit 0
}
if (-not (Test-Path -LiteralPath $canonicalFull -PathType Container)) { throw 'Canonical runtime is missing.' }
if (-not (Test-Path -LiteralPath $legacyFull -PathType Container)) { throw 'Legacy runtime is missing.' }
Assert-NoReparsePointBelow $localData $canonicalFull 'Canonical runtime'
Assert-NoReparsePointBelow $localData $legacyFull 'Legacy runtime'
Assert-TreeHasNoReparsePoints $canonicalFull 'Canonical runtime'
Assert-TreeHasNoReparsePoints $legacyFull 'Legacy runtime'
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
$legacyGatewaySecret = [string]$legacySecrets.PSObject.Properties['internal:unified-gateway:client'].Value
if ([string]::IsNullOrWhiteSpace($legacyGatewaySecret)) {
    throw 'Legacy gateway secret is empty; migration refused.'
}
try { [void][Convert]::FromBase64String($legacyGatewaySecret) }
catch { throw 'Legacy gateway secret is not valid encrypted Base64 data; migration refused.' }

$canonicalSecretsPath = Join-Path $canonicalFull 'secrets.json'
$mergedSecrets = [ordered]@{}
$mergedSecretNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $canonicalSecretsPath -PathType Leaf) {
    $canonicalSecrets = Get-Content -LiteralPath $canonicalSecretsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($property in @($canonicalSecrets.PSObject.Properties)) {
        if (-not $mergedSecretNames.Add([string]$property.Name)) {
            throw "Canonical secret store contains duplicate names that differ only by case: $($property.Name)"
        }
        $mergedSecrets[[string]$property.Name] = [string]$property.Value
    }
}
$gatewaySecretName = @($mergedSecrets.Keys | Where-Object {
    [string]$_.ToString() -ieq 'internal:unified-gateway:client'
} | Select-Object -First 1)
if ($gatewaySecretName.Count -eq 1) {
    $mergedSecrets[[string]$gatewaySecretName[0]] = $legacyGatewaySecret
} else {
    $mergedSecrets['internal:unified-gateway:client'] = $legacyGatewaySecret
    [void]$mergedSecretNames.Add('internal:unified-gateway:client')
}

$installRoot = Split-Path -Parent $canonicalFull
$workRoot = Join-Path $installRoot ('migration-work-' + [Guid]::NewGuid().ToString('N'))
$stageRoot = Join-Path $workRoot 'stage'
$backupRoot = Join-Path $workRoot 'rollback'
New-Item -ItemType Directory -Path $stageRoot, $backupRoot -Force | Out-Null
$stagedSecretsPath = Join-Path $stageRoot 'secrets.json'
$backupSecretsPath = Join-Path $backupRoot 'secrets.json'
$movedCanonicalLedgerNames = [Collections.Generic.List[string]]::new()
$installedLegacyLedgerNames = [Collections.Generic.List[string]]::new()
$canonicalSecretsBackedUp = $false
$canonicalSecretsInstalled = $false

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
    $mergedSecrets | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $stagedSecretsPath -Encoding UTF8

    $currentFiles = Get-LedgerFiles $canonicalFull
    foreach ($file in $currentFiles) {
        if ($PSCmdlet.ShouldProcess($file.FullName, 'Move old canonical ledger file to rollback staging')) {
            Move-Item -LiteralPath $file.FullName -Destination (Join-Path $backupRoot $file.Name) -Force
            $movedCanonicalLedgerNames.Add($file.Name)
        }
    }
    foreach ($file in $sourceFiles) {
        if ($PSCmdlet.ShouldProcess($file.FullName, 'Install verified legacy ledger file into canonical runtime')) {
            Copy-Item -LiteralPath (Join-Path $stageRoot $file.Name) `
                -Destination (Join-Path $canonicalFull $file.Name) -Force
            $installedLegacyLedgerNames.Add($file.Name)
        }
    }
    if ($PSCmdlet.ShouldProcess($legacySecretsPath, 'Merge the verified legacy gateway secret while preserving current provider API keys')) {
        if (Test-Path -LiteralPath $canonicalSecretsPath -PathType Leaf) {
            Move-Item -LiteralPath $canonicalSecretsPath -Destination $backupSecretsPath -Force
            $canonicalSecretsBackedUp = $true
        }
        Move-Item -LiteralPath $stagedSecretsPath -Destination $canonicalSecretsPath -Force
        $canonicalSecretsInstalled = $true
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
    $installedSecrets = Get-Content -LiteralPath $canonicalSecretsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $mergedSecrets.GetEnumerator()) {
        $installedProperty = $installedSecrets.PSObject.Properties[[string]$entry.Key]
        if ($null -eq $installedProperty -or [string]$installedProperty.Value -cne [string]$entry.Value) {
            throw "Installed secret-store verification failed for entry: $($entry.Key)"
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
        preservedSecretEntries = @($mergedSecrets.Keys)
        note = 'The verified account ledger and gateway admission secret were migrated; existing custom-provider API keys were preserved.'
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content `
        -LiteralPath (Join-Path $canonicalFull 'runtime-migration-state.json') -Encoding UTF8
    Remove-Item -LiteralPath $workRoot -Recurse -Force
    Write-Output "LOCAL_RUNTIME_MIGRATED ledgerFiles=$($sourceFiles.Count) ledgerBytes=$($report.ledgerBytes)"
} catch {
    $failure = $_
    foreach ($name in @($installedLegacyLedgerNames)) {
        $installed = Join-Path $canonicalFull $name
        if (Test-Path -LiteralPath $installed -PathType Leaf) {
            Remove-Item -LiteralPath $installed -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($name in @($movedCanonicalLedgerNames)) {
        $backup = Join-Path $backupRoot $name
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Move-Item -LiteralPath $backup -Destination (Join-Path $canonicalFull $name) -Force
        }
    }
    if ($canonicalSecretsInstalled -and (Test-Path -LiteralPath $canonicalSecretsPath -PathType Leaf)) {
        Remove-Item -LiteralPath $canonicalSecretsPath -Force -ErrorAction SilentlyContinue
    }
    if ($canonicalSecretsBackedUp -and (Test-Path -LiteralPath $backupSecretsPath -PathType Leaf)) {
        Move-Item -LiteralPath $backupSecretsPath -Destination $canonicalSecretsPath -Force
    }
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw $failure
}
