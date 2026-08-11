[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SshConfigPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$SshConfigSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sshExe = 'C:\Program Files\Git\usr\bin\ssh.exe'
$configPath = [IO.Path]::GetFullPath($SshConfigPath).Replace('\', '/')

if (-not (Test-Path -LiteralPath $sshExe)) {
    throw "Git OpenSSH not found: $sshExe"
}
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "SSH config not found: $configPath"
}
$expectedConfigHash = $SshConfigSha256.Trim().ToUpperInvariant()
$actualConfigHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualConfigHash -ne $expectedConfigHash) {
    throw 'SSH config SHA-256 mismatch; health check was not started.'
}

function Get-MainServerAliases {
    param([Parameter(Mandatory)][string]$Path)

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $aliases = [Collections.Generic.List[string]]::new()
    $text = [IO.File]::ReadAllText($Path)
    foreach ($match in [regex]::Matches($text, '(?m)^\s*Host\s+([^\r\n#]+)')) {
        foreach ($alias in ($match.Groups[1].Value -split '\s+')) {
            if ([string]::IsNullOrWhiteSpace($alias)) { continue }
            if ($alias -notmatch '^[A-Za-z0-9._-]+$') { continue }
            if ($alias.EndsWith('-Public', [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($alias.EndsWith('-WG', [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($seen.Add($alias)) { $aliases.Add($alias) }
        }
    }
    return $aliases.ToArray()
}

function Invoke-SshCommand {
    param(
        [Parameter(Mandatory)][string]$HostAlias,
        [Parameter(Mandatory)][string]$RemoteCommand
    )

    if ($HostAlias -notmatch '^[A-Za-z0-9._-]+$') {
        throw "Unsafe SSH alias: $HostAlias"
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $sshExe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $escapedCommand = $RemoteCommand.Replace('"', '\"')
    $start.Arguments = "-F `"$configPath`" -o BatchMode=yes -o ConnectionAttempts=1 -o ConnectTimeout=8 -o ServerAliveInterval=5 -o ServerAliveCountMax=1 $HostAlias `"$escapedCommand`""
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "SSH process did not start for $HostAlias" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit() } catch { }
        $process.Dispose()
        throw "SSH check timed out for $HostAlias after 15 seconds"
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) {
        throw "SSH check failed for $HostAlias with exit code $exitCode"
    }
    return $stdout.TrimEnd()
}

function Invoke-RemoteScript {
    param(
        [Parameter(Mandatory)][string]$HostAlias,
        [Parameter(Mandatory)][string]$Script
    )

    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Script))
    $remoteCommand = "printf '%s' '$encoded' | base64 -d | bash"
    $latencyClock = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-SshCommand -HostAlias $HostAlias -RemoteCommand $remoteCommand
    $latencyClock.Stop()
    Write-Output "metric:latency_ms=$($latencyClock.ElapsedMilliseconds)"
    Write-Output $result
}

$telemetryScript = @'
set -u
printf 'role=__CMM_ALIAS__\n'

cpu_total_1=0; cpu_idle_1=0; cpu_total_2=0; cpu_idle_2=0
if [ -r /proc/stat ]; then
  read -r cpu_total_1 cpu_idle_1 < <(awk '/^cpu / {idle=$5+$6; total=0; for(i=2;i<=NF;i++) total+=$i; print total,idle; exit}' /proc/stat)
fi
net_rx_1=0; net_tx_1=0
if [ -r /proc/net/dev ]; then
  read -r net_rx_1 net_tx_1 < <(awk 'NR>2 {gsub(":","",$1); if($1!="lo"){rx+=$2; tx+=$10}} END {print rx+0,tx+0}' /proc/net/dev)
fi
sleep 1
if [ -r /proc/stat ]; then
  read -r cpu_total_2 cpu_idle_2 < <(awk '/^cpu / {idle=$5+$6; total=0; for(i=2;i<=NF;i++) total+=$i; print total,idle; exit}' /proc/stat)
fi
net_rx_2=0; net_tx_2=0
if [ -r /proc/net/dev ]; then
  read -r net_rx_2 net_tx_2 < <(awk 'NR>2 {gsub(":","",$1); if($1!="lo"){rx+=$2; tx+=$10}} END {print rx+0,tx+0}' /proc/net/dev)
fi

cpu_percent=$(awk -v t1="$cpu_total_1" -v t2="$cpu_total_2" -v i1="$cpu_idle_1" -v i2="$cpu_idle_2" 'BEGIN {d=t2-t1; if(d<=0) print "0.0"; else printf "%.1f", 100*(d-(i2-i1))/d}')
mem_total=$(awk '/^MemTotal:/ {print $2*1024}' /proc/meminfo 2>/dev/null || printf '0')
mem_available=$(awk '/^MemAvailable:/ {print $2*1024}' /proc/meminfo 2>/dev/null || printf '0')
mem_used=$((mem_total-mem_available))
mem_percent=$(awk -v u="$mem_used" -v t="$mem_total" 'BEGIN {if(t<=0) print "0.0"; else printf "%.1f", 100*u/t}')
read -r disk_total disk_used disk_percent < <(df -B1 -P / 2>/dev/null | awk 'NR==2 {gsub(/%/,"",$5); print $2,$3,$5}')
read -r load1 load5 load15 _ < /proc/loadavg

printf 'metric:cpu_percent=%s\n' "$cpu_percent"
printf 'metric:memory_percent=%s\n' "$mem_percent"
printf 'metric:memory_used_bytes=%s\n' "$mem_used"
printf 'metric:memory_total_bytes=%s\n' "$mem_total"
printf 'metric:disk_percent=%s\n' "$disk_percent"
printf 'metric:disk_used_bytes=%s\n' "$disk_used"
printf 'metric:disk_total_bytes=%s\n' "$disk_total"
printf 'metric:download_bps=%s\n' "$((net_rx_2-net_rx_1))"
printf 'metric:upload_bps=%s\n' "$((net_tx_2-net_tx_1))"
printf 'metric:load1=%s\nmetric:load5=%s\nmetric:load15=%s\n' "$load1" "$load5" "$load15"
printf 'metric:uptime_seconds=%s\n' "$(cut -d. -f1 /proc/uptime 2>/dev/null || printf '0')"

if command -v systemctl >/dev/null 2>&1; then
  for unit in ssh sshd cron crond docker caddy nginx fail2ban xray cloudflared; do
    load_state=$(systemctl show "$unit.service" --property=LoadState --value 2>/dev/null || true)
    if [ -n "$load_state" ] && [ "$load_state" != "not-found" ]; then
      state=$(systemctl is-active "$unit.service" 2>/dev/null || true)
      printf 'service:%s=%s\n' "$unit" "${state:-unknown}"
    fi
  done
fi
'@

$serverAliases = @(Get-MainServerAliases -Path $configPath)
Write-Output "discovery:count=$($serverAliases.Count)"
if ($serverAliases.Count -eq 0) {
    throw 'No main server aliases were discovered in the SSH config.'
}

foreach ($serverAlias in $serverAliases) {
    Write-Output "discovery:alias=$serverAlias"
    Write-Output "--- SERVER $serverAlias ---"
    try {
        $script = $telemetryScript.Replace('__CMM_ALIAS__', $serverAlias)
        Invoke-RemoteScript -HostAlias $serverAlias -Script $script
    } catch {
        Write-Output "role=$serverAlias"
        Write-Output "error=$($_.Exception.Message)"
    }
}

Write-Output '--- PUBLIC ENDPOINTS ---'
$expectations = [ordered]@{}
if ($env:CMM_PUBLIC_DASHBOARD) {
    $expectations['dashboard'] = @{ Url = $env:CMM_PUBLIC_DASHBOARD; Expected = '401' }
}
if ($env:CMM_PUBLIC_MODELS) {
    $expectations['models-api'] = @{ Url = $env:CMM_PUBLIC_MODELS; Expected = '401' }
}
if ($env:CMM_PUBLIC_RELAY) {
    $expectations['relay-status'] = @{ Url = $env:CMM_PUBLIC_RELAY; Expected = '401' }
}

foreach ($entry in $expectations.GetEnumerator()) {
    $code = & curl.exe -sS --retry 1 --retry-all-errors --retry-delay 1 --max-time 12 -o NUL -w '%{http_code}' $entry.Value.Url
    $state = if ($code -eq $entry.Value.Expected) { 'ok' } else { 'unexpected' }
    Write-Output "public:$($entry.Key)=$code expected=$($entry.Value.Expected) state=$state"
}
