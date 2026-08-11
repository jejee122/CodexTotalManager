namespace CodexModelManager.Models;

public sealed class ProviderView
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string Adapter { get; init; } = "openai-chat";
    public bool HasApiKey { get; init; }
    public bool Disabled { get; init; }
    public int ModelCount { get; set; }
    public string ConnectionState { get; set; } = "未测试";
    public string RecentError { get; set; } = "暂无错误记录";
    public long? LatencyMs { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public IReadOnlyList<UsageWindowView> QuotaWindows { get; set; } = Array.Empty<UsageWindowView>();
    public string UsageText { get; set; } = "还没有本机请求记录";
    public string QuotaText { get; set; } = "服务端未提供可读取的套餐额度";
    public bool HasQuotaWindows => QuotaWindows.Count > 0;

    public string StatusText => Disabled
        ? $"已停用 · {ModelCount} 个模型"
        : $"{ConnectionState} · {ModelCount} 个模型";
    public string HealthDetail => CheckedAt is null
        ? RecentError
        : $"{(LatencyMs is null ? "未读到延迟" : $"{LatencyMs} ms")} · {CheckedAt:HH:mm:ss} · {RecentError}";
    public string EnableActionText => Disabled ? "启用" : "停用";
}
