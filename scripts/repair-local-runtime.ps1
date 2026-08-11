[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param(
    [string]$InstallRoot,
    [switch]$RemoveLegacyCandidates,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodexTotalManager'
}
$installFull = [IO.Path]::GetFullPath($InstallRoot)
$localData = ([IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))).TrimEnd('\') + '\'
if (-not ($installFull + '\').StartsWith($localData, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Repair root must remain below LocalApplicationData: $installFull"
}
if (-not $Apply) {
    Write-Output 'DRY_RUN_ONLY: pass -Apply to make local runtime changes; no files or processes were changed.'
    exit 0
}
$runtimeRoot = Join-Path $installFull 'runtime-v3'
if (-not (Test-Path -LiteralPath $runtimeRoot -PathType Container)) { throw 'runtime-v3 is missing.' }

$removedAuthFiles = 0
$removedTemporaryFiles = 0
$removedLegacyBytes = 0L
$removedPollutedPools = 0
$removedPollutedSecrets = 0
$removedStaleRuntimeTrees = 0

function Remove-ExactTree([string]$path, [string]$label) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    $full = [IO.Path]::GetFullPath($path)
    $prefix = $installFull.TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the install root: $full"
    }
    if ($PSCmdlet.ShouldProcess($full, "Remove $label")) {
        try {
            Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
        } catch [System.IO.DirectoryNotFoundException] {
            # Windows PowerShell 5.1 can fail part-way through deep npm trees even
            # after the absolute target has been boundary-checked. The extended
            # path form keeps the deletion in the same .NET/PowerShell process.
            $extended = if ($full.StartsWith('\\?\', [StringComparison]::Ordinal)) {
                $full
            } else {
                '\\?\' + $full
            }
            [IO.Directory]::Delete($extended, $true)
        }
    }
}

$rootPrefix = $installFull.TrimEnd('\') + '\'
$managerProcesses = @(Get-CimInstance Win32_Process -Filter "Name='CodexModelManager.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) })
foreach ($record in $managerProcesses) {
    if ($PSCmdlet.ShouldProcess("PID $($record.ProcessId)", 'Stop local Total Manager process before repair')) {
        Stop-Process -Id ([int]$record.ProcessId) -Force -ErrorAction Stop
    }
}

$cliProxyRoot = Join-Path $runtimeRoot 'cli-proxy'
if (Test-Path -LiteralPath $cliProxyRoot) {
    $removedAuthFiles = @(Get-ChildItem -LiteralPath $cliProxyRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match '[\\/]auth(?:[\\/]|$)' }).Count
}
Remove-ExactTree $cliProxyRoot 'untrusted CLIProxy runtime and plaintext credentials'
Remove-ExactTree (Join-Path $runtimeRoot 'config-validation') 'temporary configuration snapshots'

foreach ($tree in @(Get-ChildItem -LiteralPath $runtimeRoot -Directory -Filter 'opencodex-*' -ErrorAction SilentlyContinue)) {
    Remove-ExactTree $tree.FullName 'stale OpenCodex test runtime'
    $removedStaleRuntimeTrees++
}
if (Test-Path -LiteralPath (Join-Path $runtimeRoot 'logs') -PathType Container) {
    Remove-ExactTree (Join-Path $runtimeRoot 'logs') 'stale runtime logs'
    $removedStaleRuntimeTrees++
}

foreach ($temp in @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Filter '*.resize.tmp' -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess($temp.FullName, 'Remove stale resize file')) {
        Remove-Item -LiteralPath $temp.FullName -Force
        $removedTemporaryFiles++
    }
}
foreach ($stale in @(
    (Join-Path $runtimeRoot 'native-proxy\engine.pid'),
    (Join-Path $installFull 'native-proxy\engine.pid'),
    (Join-Path $runtimeRoot 'launcher-transcript.log'),
    (Join-Path $runtimeRoot 'pools.json.bak-official-pro-20260806'),
    (Join-Path $runtimeRoot 'theme-self-test.txt'),
    (Join-Path $runtimeRoot 'ensure-proxy.txt'),
    (Join-Path $runtimeRoot 'deployment-acceptance-current.json'),
    (Join-Path $runtimeRoot 'active-release.rollback-status.json'),
    (Join-Path $runtimeRoot 'active-release.previous.deleted-candidate.json'))) {
    if ((Test-Path -LiteralPath $stale -PathType Leaf) -and $PSCmdlet.ShouldProcess($stale, 'Remove stale runtime file')) {
        Remove-Item -LiteralPath $stale -Force
        $removedTemporaryFiles++
    }
}
foreach ($stalePid in @(Get-ChildItem -LiteralPath $runtimeRoot -File -Filter 'stale-native-proxy-engine.pid.*' -ErrorAction SilentlyContinue)) {
    if ($PSCmdlet.ShouldProcess($stalePid.FullName, 'Remove stale native-engine PID marker')) {
        Remove-Item -LiteralPath $stalePid.FullName -Force
        $removedTemporaryFiles++
    }
}

$poolsPath = Join-Path $runtimeRoot 'pools.json'
if (Test-Path -LiteralPath $poolsPath -PathType Leaf) {
    $catalog = Get-Content -LiteralPath $poolsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $originalPools = @($catalog.Pools)
    $catalog.Pools = @($originalPools | Where-Object {
        [string]$_.Id -in @('official-pro', 'plus-api-1')
    })
    $removedPollutedPools = $originalPools.Count - @($catalog.Pools).Count
    if (@($catalog.Pools | Where-Object { [string]$_.Id -eq 'official-pro' }).Count -ne 1 -or
        @($catalog.Pools | Where-Object { [string]$_.Id -eq 'plus-api-1' }).Count -ne 1) {
        throw 'The clean catalog must contain exactly one official Pro and one native Plus pool.'
    }
    $activePool = @($catalog.Pools | Where-Object { $_.Id -eq $catalog.Active.PoolId }) | Select-Object -First 1
    if ($null -eq $activePool -or -not [bool]$activePool.Enabled) {
        $catalog.Active.PoolId = 'official-pro'
        $catalog.Active.Model = 'gpt-5.6-sol'
        $catalog.Active.Verification = 'local-remediation-fail-closed'
    }
    if ($PSCmdlet.ShouldProcess($poolsPath, 'Remove polluted and stale pool entries')) {
        $temp = $poolsPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
        Move-Item -LiteralPath $temp -Destination $poolsPath -Force
    }
}

$secretsPath = Join-Path $runtimeRoot 'secrets.json'
if (Test-Path -LiteralPath $secretsPath -PathType Leaf) {
    $secrets = Get-Content -LiteralPath $secretsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($name in @($secrets.PSObject.Properties.Name | Where-Object {
        $_ -ne 'internal:unified-gateway:client'
    })) {
        [void]$secrets.PSObject.Properties.Remove($name)
        $removedPollutedSecrets++
    }
    if ($PSCmdlet.ShouldProcess($secretsPath, 'Remove polluted or obsolete secret entries')) {
        $temp = $secretsPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $secrets | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
        Move-Item -LiteralPath $temp -Destination $secretsPath -Force
    }
}

$gatewayPath = Join-Path $runtimeRoot 'unified-gateway.json'
if (Test-Path -LiteralPath $gatewayPath -PathType Leaf) {
    $gateway = Get-Content -LiteralPath $gatewayPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $gateway.routes = @()
    if ($PSCmdlet.ShouldProcess($gatewayPath, 'Remove stale gateway routes')) {
        $temp = $gatewayPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $gateway | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temp -Encoding UTF8
        Move-Item -LiteralPath $temp -Destination $gatewayPath -Force
    }
}

if ($RemoveLegacyCandidates) {
    $legacy = Join-Path $installFull 'candidate-freezes'
    if (Test-Path -LiteralPath $legacy) {
        $removedLegacyBytes = [long]((Get-ChildItem -LiteralPath $legacy -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum)
    }
    Remove-ExactTree $legacy 'legacy candidate freezes'
    foreach ($backup in @(Get-ChildItem -LiteralPath $installFull -File -Filter 'Open-New-Manager-ControlPanel.ps1.bak*' -ErrorAction SilentlyContinue)) {
        if ($PSCmdlet.ShouldProcess($backup.FullName, 'Remove legacy launcher backup')) {
            Remove-Item -LiteralPath $backup.FullName -Force
        }
    }
}

$acl = [IO.Directory]::GetAccessControl(
    $installFull,
    [Security.AccessControl.AccessControlSections]::Access)
$acl.SetAccessRuleProtection($true, $true)
$sandboxRules = @($acl.Access | Where-Object { $_.IdentityReference.Value -match '[\\]CodexSandboxUsers$' })
foreach ($rule in $sandboxRules) { [void]$acl.PurgeAccessRules($rule.IdentityReference) }
if ($PSCmdlet.ShouldProcess($installFull, 'Remove CodexSandboxUsers read access')) {
    [IO.Directory]::SetAccessControl($installFull, $acl)
}
$remainingSandboxRules = @((Get-Acl -LiteralPath $installFull).Access |
    Where-Object { $_.IdentityReference.Value -match '[\\]CodexSandboxUsers$' }).Count
if ($remainingSandboxRules -ne 0) { throw 'CodexSandboxUsers access rule remains after ACL repair.' }

$remainingAuthFiles = @(Get-ChildItem -LiteralPath $installFull -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -match '[\\/]auth(?:[\\/]|$)' }).Count
$report = [ordered]@{
    schemaVersion = 1
    remediatedAt = (Get-Date).ToUniversalTime().ToString('o')
    plaintextAuthFilesRemoved = $removedAuthFiles
    remainingAuthFiles = $remainingAuthFiles
    staleTemporaryFilesRemoved = $removedTemporaryFiles
    staleRuntimeTreesRemoved = $removedStaleRuntimeTrees
    legacyCandidateBytesRemoved = $removedLegacyBytes
    pollutedPoolsRemoved = $removedPollutedPools
    pollutedSecretEntriesRemoved = $removedPollutedSecrets
    gatewayRoutesCleared = $true
    sandboxGroupReadAccessRemoved = ($remainingSandboxRules -eq 0)
    note = 'External OAuth revocation is intentionally not attempted by this local-only repair.'
}
$reportPath = Join-Path $runtimeRoot 'remediation-state.json'
if ($PSCmdlet.ShouldProcess($reportPath, 'Write remediation state')) {
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
Write-Output "LOCAL_RUNTIME_REPAIRED authRemoved=$removedAuthFiles remainingAuth=$remainingAuthFiles pollutedPools=$removedPollutedPools pollutedSecrets=$removedPollutedSecrets staleTrees=$removedStaleRuntimeTrees legacyBytesRemoved=$removedLegacyBytes"
