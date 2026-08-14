[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManagerPath,
    [Parameter(Mandatory)]
    [string]$TestDoublePath,
    [ValidateRange(1, 50000)]
    [int]$Cycles = 5000,
    [string]$RunRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-FreeLoopbackPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

$enginePort = Get-FreeLoopbackPort
do { $gatewayPort = Get-FreeLoopbackPort } while ($gatewayPort -eq $enginePort)
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($null -ne $dotnetCommand) {
    $dotnetCommand.Source
} else {
    Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'dotnet was not found on PATH or under the current user profile.'
}
$managerFull = [IO.Path]::GetFullPath($ManagerPath)
$testDoubleFull = [IO.Path]::GetFullPath($TestDoublePath)
if (-not (Test-Path -LiteralPath $managerFull -PathType Leaf)) { throw "Manager test file is missing: $managerFull" }
if (-not (Test-Path -LiteralPath $testDoubleFull -PathType Leaf)) { throw "Codex test double is missing: $testDoubleFull" }
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path ([IO.Path]::GetTempPath()) ('cmm-codex-test-double-' + [Guid]::NewGuid().ToString('N'))
}
$runFull = [IO.Path]::GetFullPath($RunRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not ($runFull.TrimEnd('\') + '\').StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Test root must be below the system temporary directory: $runFull"
}
if (Test-Path -LiteralPath $runFull) { throw "Test root already exists: $runFull" }

New-Item -ItemType Directory -Path $runFull | Out-Null
$token = [Guid]::NewGuid().ToString('N')
$markerPath = Join-Path $runFull '.cmm-codex-test-double-run'
Set-Content -LiteralPath $markerPath -Value $token -Encoding UTF8 -NoNewline
$fake = $null
$manager = $null

function New-TestProcessInfo([string]$Path, [string]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    if ([IO.Path]::GetExtension($Path).Equals('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw ".NET 10 host is missing: $dotnet" }
        $info.FileName = $dotnet
        $info.Arguments = '"' + $Path + '"' + $(if ($Arguments) { ' ' + $Arguments } else { '' })
    } else {
        $info.FileName = $Path
        $info.Arguments = $Arguments
    }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $false
    $info.RedirectStandardError = $false
$info.EnvironmentVariables['CMM_CODEX_TEST_DOUBLE_TOKEN'] = $token
$info.EnvironmentVariables['CMM_DETACHED_NO_EXTERNAL_NETWORK'] = '1'
$info.EnvironmentVariables['CMM_DETACHED_DATA_ROOT'] = $runFull
$info.EnvironmentVariables['CMM_CODEX_TEST_DOUBLE_ENGINE_URL'] = "http://127.0.0.1:$enginePort/"
$info.EnvironmentVariables['CMM_CODEX_TEST_DOUBLE_GATEWAY_URL'] = "http://127.0.0.1:$gatewayPort/"
$info.EnvironmentVariables['HTTP_PROXY'] = 'http://127.0.0.1:1'
$info.EnvironmentVariables['HTTPS_PROXY'] = 'http://127.0.0.1:1'
$info.EnvironmentVariables['ALL_PROXY'] = 'socks5://127.0.0.1:1'
$info.EnvironmentVariables['NO_PROXY'] = '127.0.0.1,localhost'
    $info.Environment.Remove('CMM_DETACHED_UI') | Out-Null
    return $info
}

function Stop-TestProcess([Diagnostics.Process]$Process, [string]$Label) {
    if ($null -eq $Process) { return }
    $processId = $Process.Id
    try {
        if (-not $Process.HasExited) {
            $Process.Kill()
            if (-not $Process.WaitForExit(5000)) {
                throw "$Label process PID $processId did not exit within five seconds."
            }
        }
        if (-not $Process.HasExited) {
            throw "$Label process PID $processId is still running."
        }
    } finally {
        $Process.Dispose()
    }
}

try {
    $fakeInfo = New-TestProcessInfo $testDoubleFull ''
    $fake = [Diagnostics.Process]::Start($fakeInfo)
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($fake.HasExited) { break }
        try {
            $engine = [Net.Sockets.TcpClient]::new()
            $engine.Connect('127.0.0.1', $enginePort)
            $engine.Dispose()
            $gateway = [Net.Sockets.TcpClient]::new()
            $gateway.Connect('127.0.0.1', $gatewayPort)
            $gateway.Dispose()
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 100
        }
    }
    if (-not $ready) { throw 'Codex test double did not bind both loopback ports in time.' }

    $managerInfo = New-TestProcessInfo $managerFull "--codex-test-double-self-test $Cycles"
    $manager = [Diagnostics.Process]::Start($managerInfo)
    if (-not $manager.WaitForExit(180000)) {
        throw 'Manager test exceeded 180 seconds; only the test process was stopped.'
    }
    $managerExitCode = $manager.ExitCode
    $reportPath = Join-Path $runFull 'runtime\codex-test-double-report.json'
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "Manager did not produce the test-double report. Exit code: $managerExitCode"
    }
    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($managerExitCode -ne 0 -or -not [bool]$report.Success) {
        throw "Manager test-double self-test failed. Exit code: $managerExitCode; report: $reportPath"
    }
    Write-Output "CODEX_TEST_DOUBLE_TEST_OK cycles=$($report.Cycles) requests=$($report.RequestsCompleted) elapsedMs=$($report.ElapsedMs)"
    Write-Output "CODEX_TEST_DOUBLE_REPORT=stdout-temporary-root-cleaned-after-test"
    Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8
}
finally {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    foreach ($entry in @(
        [pscustomobject]@{ Process = $manager; Label = 'Manager self-test' },
        [pscustomobject]@{ Process = $fake; Label = 'Codex test double' }
    )) {
        try {
            Stop-TestProcess $entry.Process $entry.Label
        } catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }
    if ($cleanupErrors.Count -gt 0) {
        throw ('Test process cleanup failed: ' + ($cleanupErrors -join ' | '))
    }
    if (Test-Path -LiteralPath $runFull) {
        $marker = if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
            Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8
        } else {
            $null
        }
        $verifiedTempRoot = ($runFull.TrimEnd('\') + '\').StartsWith(
            $tempRoot,
            [StringComparison]::OrdinalIgnoreCase)
        if ($marker -ne $token -or -not $verifiedTempRoot) {
            throw "Refused to remove unverified test root: $runFull"
        }
        Remove-Item -LiteralPath $runFull -Recurse -Force
        if (Test-Path -LiteralPath $runFull) {
            throw "Test root cleanup did not complete: $runFull"
        }
    }
}
