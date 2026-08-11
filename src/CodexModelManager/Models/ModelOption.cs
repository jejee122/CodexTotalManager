namespace CodexModelManager.Models;

public sealed class ModelOption
{
    public string Provider { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Namespaced { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public bool IsOfficial { get; init; }
    public bool Disabled { get; init; }
    public bool IsSelectable => !Disabled;
    public bool IsActive { get; set; }
    public bool IsCurrentTaskRouted { get; set; }
    public long? ContextWindow { get; init; }

    public string Key => $"{Provider}\u001f{Id}";
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName!;
    public string ProviderLabel { get; set; } = string.Empty;
    public string UsageText { get; set; } = "最近 200 条日志中暂无记录";
    public string Detail => ContextWindow is > 0
        ? $"{ProviderLabel} · 约 {FormatContext(ContextWindow.Value)} 上下文 · {AvailabilityText}"
        : $"{ProviderLabel} · {AvailabilityText}";
    public string AvailabilityText => Disabled ? "已停用" : "可用";
    public string RouteBadgeText => IsCurrentTaskRouted ? "当前任务路由" : "仅后台配置";

    private static string FormatContext(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.#}M";
        if (value >= 1_000) return $"{value / 1_000d:0.#}K";
        return value.ToString();
    }
}
