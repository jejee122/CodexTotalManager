namespace CodexModelManager.Models;

public sealed record ServerHealthResult(
    bool Success,
    string PublicEntryStatus,
    string CheckedAt,
    string Message,
    IReadOnlyList<ServerTelemetry> Servers,
    int ExpectedServerCount,
    IReadOnlyDictionary<string, string>? PublicEndpoints = null);

public sealed record ServerTelemetry(
    string Role,
    bool Online,
    long? LatencyMs,
    double? CpuPercent,
    double? MemoryPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? DiskPercent,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    long? DownloadBytesPerSecond,
    long? UploadBytesPerSecond,
    double? Load1,
    double? Load5,
    double? Load15,
    long? UptimeSeconds,
    IReadOnlyDictionary<string, string> Services,
    IReadOnlyDictionary<string, string> Accounts,
    IReadOnlyList<string> Alerts,
    DateTimeOffset CheckedAt,
    string Error,
    string HostName = "",
    string HealthEvent = "")
{
    public static ServerTelemetry Offline(string role, string error) => new(
        role, false, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null,
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new[] { error },
        DateTimeOffset.Now,
        error);
}

public sealed class ServerCardView
{
    public string Alias { get; init; } = string.Empty;
    public string StatusTitle { get; init; } = string.Empty;
    public string StatusDetail { get; init; } = string.Empty;
    public string DotColor { get; init; } = "#7B8D94";
    public string LatencyText { get; init; } = "延迟 --";
    public string CpuText { get; init; } = "CPU --";
    public double CpuValue { get; init; }
    public string CpuColor { get; init; } = "#7B8D94";
    public string MemoryText { get; init; } = "内存 --";
    public double MemoryValue { get; init; }
    public string MemoryColor { get; init; } = "#7B8D94";
    public string DiskText { get; init; } = "磁盘 --";
    public double DiskValue { get; init; }
    public string DiskColor { get; init; } = "#7B8D94";
    public string NetworkText { get; init; } = "网络 --";
    public string LoadText { get; init; } = "负载 --";
    public string UptimeText { get; init; } = "运行时间 --";
    public string ServicesText { get; init; } = "通用服务 --";
    public string ServiceDetailText { get; init; } = "服务明细：待读取";
    public string HealthEventText { get; init; } = "本轮结果：待读取";
}

public sealed class ServerFeedItem
{
    public string TimeText { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Accent { get; init; } = "#79DDBA";
}

public sealed record ThemeSafetyResult(bool ProjectFound, bool CodexFound, string Message);
