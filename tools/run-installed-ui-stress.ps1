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
$realConfigExists = Test-Path -LiteralPath $realConfig -PathType Leaf
$beforeHash = if ($realConfigExists) { (Get-FileHash -LiteralPath $realConfig -Algorithm SHA256).Hash } else { $null }
$beforeTime = if ($realConfigExists) { (Get-Item -LiteralPath $realConfig).LastWriteTimeUtc } else { $null }

try {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $manager
    $info.Arguments = "--ui-stress $Cycles"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.EnvironmentVariables['CMM_DETACHED_NO_EXTERNAL_NETWORK'] = '1'
    $info.EnvironmentVariables['CMM_DETACHED_DATA_ROOT'] = $runRoot
    $info.EnvironmentVariables['HTTP_PROXY'] = 'http://127.0.0.1:1'
    $info.EnvironmentVariables['HTTPS_PROXY'] = 'http://127.0.0.1:1'
    $info.EnvironmentVariables['ALL_PROXY'] = 'socks5://127.0.0.1:1'
    $info.EnvironmentVariables['NO_PROXY'] = '127.0.0.1,localhost'

    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(180000)) {
        if (-not $process.HasExited) {
            # Windows PowerShell 5.1 runs on .NET Framework, whose Process type
            # does not have Kill(bool). The stress target is a single-process,
            # detached-only executable, so the compatible parameterless overload
            # is the correct cleanup path here.
            $process.Kill()
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

    $afterExists = Test-Path -LiteralPath $realConfig -PathType Leaf
    $afterHash = if ($afterExists) { (Get-FileHash -LiteralPath $realConfig -Algorithm SHA256).Hash } else { $null }
    $afterTime = if ($afterExists) { (Get-Item -LiteralPath $realConfig).LastWriteTimeUtc } else { $null }
    if ($realConfigExists -ne $afterExists -or $beforeHash -ne $afterHash -or $beforeTime -ne $afterTime) {
        throw 'Real Codex config changed during isolated UI stress testing.'
    }

    $report | Select-Object Marker, Cycles, PageTransitions, ElapsedMs,
        MaxCycleLatencyMs, EnabledActionButtonCount,
        ExternalStatusConnectionsAllowed, LocalServiceRows, ServerMonitorRunning
    Write-Output "REAL_CODEX_CONFIG_UNCHANGED=True"
    Write-Output "REAL_CODEX_CONFIG_PRESENT=$afterExists"
    if ($afterHash) { Write-Output "REAL_CODEX_CONFIG_SHA256=$afterHash" }
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
