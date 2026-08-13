using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class DashboardStatusService
{
    private const string ServerScriptHash = "ED3F5CC78C5975E8A58E4C833B1C815DB5BE1AA210080DEDF97E21F7E8440FCC";
    private const string ThemeBridgeHash = "B0EDC5DC12B37E7828234264219AA6014E7BFED63BD28FF63BC09810B1BF2BCA";

    public string ThemeProjectRoot { get; }
    public string ServerHealthScript { get; }
    public IReadOnlyList<string> DiscoveredServerAliases { get; private set; } = Array.Empty<string>();
    public int ExpectedServerCount => DiscoveredServerAliases.Count;

    private string ThemeBridge => Path.Combine(ThemeProjectRoot, "codex-bridge.ps1");
    private string? ServerSshConfig { get; set; }
    private string? ServerSshConfigPinnedHash { get; set; }
    private int V2rayProxyPort { get; }

    public DashboardStatusService(
        string? resourceRoot = null,
        string? serverSshConfigPath = null,
        string? serverSshConfigSha256 = null,
        IReadOnlyList<string>? serverAliases = null,
        int v2rayProxyPort = 10808)
    {
        if (v2rayProxyPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(v2rayProxyPort));
        V2rayProxyPort = v2rayProxyPort;
        var root = resourceRoot ?? Path.Combine(AppContext.BaseDirectory, "Resources");
        ThemeProjectRoot = Path.Combine(root, "Themes");
        ServerHealthScript = Path.Combine(root, "Server", "health-check.ps1");

        if (!string.IsNullOrWhiteSpace(serverSshConfigPath)
            || !string.IsNullOrWhiteSpace(serverSshConfigSha256)
            || serverAliases is { Count: > 0 })
            ConfigureServerMonitoring(serverSshConfigPath, serverSshConfigSha256, serverAliases);
    }

    public void ConfigureServerMonitoring(
        string? serverSshConfigPath,
        string? serverSshConfigSha256,
        IReadOnlyList<string>? serverAliases)
    {
        if (string.IsNullOrWhiteSpace(serverSshConfigPath)
            || string.IsNullOrWhiteSpace(serverSshConfigSha256)
            || serverAliases is null)
            throw new ArgumentException("服务器监控必须同时提供 SSH 配置、固定指纹和五台服务器名称。");
        var normalizedHash = serverSshConfigSha256.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalizedHash, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("服务器连接配置 SHA-256 格式无效。", nameof(serverSshConfigSha256));
        var fullPath = Path.GetFullPath(serverSshConfigPath);
        var aliases = AppSettingsService.ValidateServerAliases(fullPath, serverAliases);
        ServerSshConfig = fullPath;
        ServerSshConfigPinnedHash = normalizedHash;
        DiscoveredServerAliases = aliases.ToArray();
    }

    public void DisableServerMonitoring()
    {
        ServerSshConfig = null;
        ServerSshConfigPinnedHash = null;
        DiscoveredServerAliases = Array.Empty<string>();
    }

    public bool ThemeProjectExists => File.Exists(ThemeBridge);
    public bool ServerCheckExists => File.Exists(ServerHealthScript)
                                     && ServerSshConfig is not null
                                     && File.Exists(ServerSshConfig)
                                     && ExpectedServerCount > 0;

    public Task<bool> IsV2rayReadyAsync(CancellationToken cancellationToken = default) =>
        IsPortOpenAsync(V2rayProxyPort, cancellationToken);

    public async Task<ThemeSafetyResult> CheckThemeSafetyAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsDetachedUi)
            return new ThemeSafetyResult(true, false, "独立模式没有扫描或连接 Codex 皮肤通道。");
        if (!ThemeProjectExists)
            return new ThemeSafetyResult(false, false, "没有找到皮肤组件。模型和账号功能不受影响。");
        if (!HashMatches(ThemeBridge, ThemeBridgeHash))
            return new ThemeSafetyResult(true, false, "皮肤安全组件被改过，已经锁住，不会运行。");

        var result = await RunPowerShellAsync(
            new[] { "-File", ThemeBridge, "-Action", "Discover" },
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output) || result.Output.Trim() == "null")
            return new ThemeSafetyResult(true, false, "没有认出官方 Codex 安装，已禁止应用皮肤。");

        try
        {
            using var json = JsonDocument.Parse(result.Output);
            var version = json.RootElement.TryGetProperty("version", out var value) ? value.GetString() : null;
            return new ThemeSafetyResult(true, true,
                string.IsNullOrWhiteSpace(version)
                    ? "已认出官方 Codex，皮肤安全检查通过。"
                    : $"已认出官方 Codex {version}，皮肤安全检查通过。");
        }
        catch
        {
            return new ThemeSafetyResult(true, false, "安全检查返回了看不懂的结果，已禁止应用皮肤。");
        }
    }

    public async Task<ServerHealthResult> RunServerHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!ServerCheckExists)
            return Failed("服务器监控尚未启用。请先选择 SSH 配置并明确填写五台服务器名称。");
        if (!HashMatches(ServerHealthScript, ServerScriptHash))
            return Failed("服务器体检组件被改过，安全锁已经阻止运行。服务器没有被修改。");

        var currentConfigHash = TryComputeHash(ServerSshConfig!);
        if (currentConfigHash is null)
            return Failed("无法读取服务器 SSH 配置，体检没有启动。");
        if (ServerSshConfigPinnedHash is not null
            && !currentConfigHash.Equals(ServerSshConfigPinnedHash, StringComparison.OrdinalIgnoreCase))
            return Failed("服务器 SSH 配置与固定指纹不一致，安全锁已经阻止运行。");

        var result = await RunPowerShellAsync(
            new[]
            {
                "-File", ServerHealthScript,
                "-SshConfigPath", ServerSshConfig!,
                "-SshConfigSha256", currentConfigHash,
                "-ServerAliasesJson", JsonSerializer.Serialize(DiscoveredServerAliases)
            },
            TimeSpan.FromSeconds(95),
            cancellationToken);
        if (result.TimedOut) return Failed("整轮体检超过 95 秒，已经自动停止；服务器没有被修改。");
        if (result.ExitCode != 0)
            return Failed("服务器只读体检没有完成。请检查 SSH 配置、网络和密钥是否可用；服务器没有被修改。");

        var blocks = ParseServerBlocks(result.Output);
        var aliases = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("discovery:alias=", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["discovery:alias=".Length..].Trim())
            .Where(IsSafeAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (aliases.Length != ExpectedServerCount
            || !aliases.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(DiscoveredServerAliases))
            return Failed("服务器脚本返回的名称与明确配置的五台白名单不一致，结果已丢弃。");

        var servers = DiscoveredServerAliases
            .Select(alias => blocks.TryGetValue(alias, out var block)
                ? ParseServer(block, alias)
                : ServerTelemetry.Offline(alias, "本轮没有读到这台服务器的返回值"))
            .ToArray();

        var publicLines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("public:", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var publicEndpoints = publicLines
            .Select(ParsePublicEndpoint)
            .Where(item => item is not null)
            .Cast<KeyValuePair<string, string>>()
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        var publicOk = publicLines.Length == 0
                       || publicLines.All(line => line.Contains("state=ok", StringComparison.OrdinalIgnoreCase));
        var countMatches = servers.Length == ExpectedServerCount;
        var onlineCount = servers.Count(server => server.Online);
        var healthyCount = servers.Count(server => server.Online && server.Alerts.Count == 0);
        var success = servers.Length > 0
                      && countMatches
                      && healthyCount == servers.Length
                      && publicOk;
        var publicStatus = publicLines.Length == 0
            ? "未配置公开入口探测"
            : publicOk ? "入口保护正常" : "入口状态需要检查";
        var countMessage = countMatches
            ? $"按白名单读取 {servers.Length} 台服务器"
            : $"本轮读取 {servers.Length} 台，明确配置是 {ExpectedServerCount} 台";
        var message = success
            ? $"{countMessage}，全部在线且没有阈值告警。"
            : $"{countMessage}，{onlineCount} 台在线、{servers.Length - onlineCount} 台离线，{servers.Sum(server => server.Alerts.Count)} 项告警。";

        return new ServerHealthResult(
            success,
            publicStatus,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            message,
            servers,
            ExpectedServerCount,
            publicEndpoints);
    }

    private ServerHealthResult Failed(string message) => new(
        false,
        "未完成体检",
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        message,
        DiscoveredServerAliases.Select(alias => ServerTelemetry.Offline(alias, message)).ToArray(),
        ExpectedServerCount);

    private static ServerTelemetry ParseServer(string block, string role)
    {
        var checkedAt = DateTimeOffset.Now;
        if (!block.Contains($"role={role}", StringComparison.OrdinalIgnoreCase))
            return ServerTelemetry.Offline(role, "没有读到服务器返回值");
        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var error = lines.FirstOrDefault(line => line.StartsWith("error=", StringComparison.OrdinalIgnoreCase));
        if (error is not null) return ServerTelemetry.Offline(role, error["error=".Length..]);

        var metrics = ParsePairs(lines, "metric:");
        var services = ParsePairs(lines, "service:");
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cpu = ReadMetricDouble(metrics, "cpu_percent");
        var memory = ReadMetricDouble(metrics, "memory_percent");
        var disk = ReadMetricDouble(metrics, "disk_percent");
        var latency = ReadMetricLong(metrics, "latency_ms");
        var alerts = new List<string>();
        if (cpu is >= 90) alerts.Add($"CPU 过高：{cpu:0.#}%");
        if (memory is >= 90) alerts.Add($"内存过高：{memory:0.#}%");
        if (disk is >= 85) alerts.Add($"磁盘占用过高：{disk:0.#}%");
        if (latency is >= 8000) alerts.Add($"只读 SSH 连接耗时过高：{latency} ms");
        foreach (var service in services.Where(service =>
                     !service.Value.Equals("active", StringComparison.OrdinalIgnoreCase)))
            alerts.Add($"服务 {service.Key}：{service.Value}");

        return new ServerTelemetry(
            role,
            true,
            latency,
            cpu,
            memory,
            ReadMetricLong(metrics, "memory_used_bytes"),
            ReadMetricLong(metrics, "memory_total_bytes"),
            disk,
            ReadMetricLong(metrics, "disk_used_bytes"),
            ReadMetricLong(metrics, "disk_total_bytes"),
            ReadMetricLong(metrics, "download_bps"),
            ReadMetricLong(metrics, "upload_bps"),
            ReadMetricDouble(metrics, "load1"),
            ReadMetricDouble(metrics, "load5"),
            ReadMetricDouble(metrics, "load15"),
            ReadMetricLong(metrics, "uptime_seconds"),
            services,
            accounts,
            alerts,
            checkedAt,
            string.Empty,
            string.Empty,
            alerts.Count == 0 ? "本轮只读采样正常" : string.Join("；", alerts.Take(2)));
    }

    private static IReadOnlyDictionary<string, string> ParsePairs(IEnumerable<string> lines, string prefix) =>
        lines.Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && line.Contains('='))
            .Select(line => line[prefix.Length..].Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> ParseServerBlocks(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentAlias = null;
        var block = new StringBuilder();
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("--- SERVER ", StringComparison.OrdinalIgnoreCase) && line.EndsWith(" ---", StringComparison.Ordinal))
            {
                if (currentAlias is not null) result[currentAlias] = block.ToString();
                currentAlias = line["--- SERVER ".Length..^" ---".Length].Trim();
                block.Clear();
                continue;
            }
            if (line.Equals("--- PUBLIC ENDPOINTS ---", StringComparison.OrdinalIgnoreCase))
            {
                if (currentAlias is not null) result[currentAlias] = block.ToString();
                currentAlias = null;
                block.Clear();
                continue;
            }
            if (currentAlias is not null) block.AppendLine(rawLine);
        }
        if (currentAlias is not null) result[currentAlias] = block.ToString();
        return result;
    }

    public static IReadOnlyList<string> DiscoverSshHostAliases(string sshConfigPath)
    {
        try
        {
            var aliases = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(sshConfigPath))
            {
                var match = Regex.Match(line, @"^\s*Host\s+([^#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                foreach (var alias in match.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!IsSafeAlias(alias) || !seen.Add(alias))
                        continue;
                    aliases.Add(alias);
                }
            }
            return aliases;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsSafeAlias(string alias) =>
        Regex.IsMatch(alias, "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    private static KeyValuePair<string, string>? ParsePublicEndpoint(string line)
    {
        var payload = line["public:".Length..].Trim();
        var equals = payload.IndexOf('=');
        if (equals <= 0) return null;
        var name = payload[..equals];
        var fields = payload[(equals + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var code = fields.FirstOrDefault() ?? "--";
        var ok = fields.Any(field => field.Equals("state=ok", StringComparison.OrdinalIgnoreCase));
        return new KeyValuePair<string, string>(name, $"HTTP {code} · {(ok ? "符合保护预期" : "状态异常")}");
    }

    private static long? ReadMetricLong(IReadOnlyDictionary<string, string> metrics, string key) =>
        metrics.TryGetValue(key, out var value)
        && long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static double? ReadMetricDouble(IReadOnlyDictionary<string, string> metrics, string key) =>
        metrics.TryGetValue(key, out var value)
        && double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static bool HashMatches(string path, string expected)
    {
        var actual = TryComputeHash(path);
        return actual is not null && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryComputeHash(string path)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
        catch { return null; }
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var gitShell = @"C:\Program Files\Git\usr\bin\sh.exe";
        if (File.Exists(gitShell)) start.Environment["SHELL"] = gitShell;

        using var process = Process.Start(start);
        if (process is null) return new ProcessResult(-1, string.Empty, "无法启动 PowerShell。", false);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(true); } catch { }
            return new ProcessResult(-1, await stdout, await stderr, true);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);
}
