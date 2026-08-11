[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManagerPath,
    [ValidateRange(1, 20000)]
    [int]$Cycles = 2000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$manager = [IO.Path]::GetFullPath($ManagerPath)
if (-not (Test-Path -LiteralPath $manager -PathType Leaf)) {
    throw "Manager file is missing: $manager"
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$runRoot = [IO.Path]::GetFullPath(
    (Join-Path $tempRoot ('cmm-ui-stress-' + [Guid]::NewGuid().ToString('N'))))
$runPrefix = $runRoot.TrimEnd('\') + '\'
if (-not $runPrefix.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Test root must be below the system temporary directory: $runRoot"
}

New-Item -ItemType Directory -Path $runRoot | Out-Null
$marker = [Guid]::NewGuid().ToString('N')
$markerPath = Join-Path $runRoot '.owned-ui-stress'
Set-Content -LiteralPath $markerPath -Value $marker -Encoding UTF8 -NoNewline
$realConfig = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.codex\config.toml'
$beforeHash = (Get-FileHash -LiteralPath $realConfig -Algorithm SHA256).Hash
$beforeTime = (Get-Item -LiteralPath $realConfig).LastWriteTimeUtc

try {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $manager
    $info.Arguments = "--ui-stress $Cycles"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.Environment['CMM_DETACHED_NO_EXTERNAL_NETWORK'] = '1'
    $info.Environment['CMM_DETACHED_DATA_ROOT'] = $runRoot
    $info.Environment['HTTP_PROXY'] = 'http://127.0.0.1:1'
    $info.Environment['HTTPS_PROXY'] = 'http://127.0.0.1:1'
    $info.Environment['ALL_PROXY'] = 'socks5://127.0.0.1:1'
    $info.Environment['NO_PROXY'] = '127.0.0.1,localhost'

    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(180000)) {
        if (-not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
        }
        throw 'Installed UI stress test exceeded 180 seconds.'
    }
    $exitCode = $process.ExitCode
    $process.Dispose()

    $reportPath = Join-Path $runRoot 'runtime\ui-stress-report.json'
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "UI stress report is missing. Exit code: $exitCode"
    }
    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($exitCode -ne 0 -or $report.Marker -ne 'DETACHED_UI_STRESS_OK') {
        throw "Installed UI stress test failed. Exit code: $exitCode"
    }

    $afterHash = (Get-FileHash -LiteralPath $realConfig -Algorithm SHA256).Hash
    $afterTime = (Get-Item -LiteralPath $realConfig).LastWriteTimeUtc
    if ($beforeHash -ne $afterHash -or $beforeTime -ne $afterTime) {
        throw 'Real Codex config changed during isolated UI stress testing.'
    }

    $report | Select-Object Marker, Cycles, PageTransitions, ElapsedMs,
        MaxCycleLatencyMs, EnabledActionButtonCount,
        ExternalStatusConnectionsAllowed, LocalServiceRows, ServerMonitorRunning
    Write-Output "REAL_CODEX_CONFIG_UNCHANGED=True"
    Write-Output "REAL_CODEX_CONFIG_SHA256=$afterHash"
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        $storedMarker = if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8
        } else { $null }
        if (($storedMarker -ne $marker) -or
            (-not $runPrefix.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase))) {
            throw "Refused to remove unverified test root: $runRoot"
        }
        Remove-Item -LiteralPath $runRoot -Recurse -Force
        if (Test-Path -LiteralPath $runRoot) {
            throw "Test root cleanup did not complete: $runRoot"
        }
    }
}
