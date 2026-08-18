namespace CodexModelManager.Models;

public sealed class UsageWindowView
{
    public string PeriodKey { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public double UsedPercent { get; init; }
    public QuotaValueValidationState ValueValidation =>
        // Some providers legitimately report overage above 100%. Treat only
        // negative or non-finite values as malformed; the progress bar remains
        // visually capped while the exact upstream overage stays visible.
        double.IsFinite(UsedPercent) && UsedPercent >= 0d
            ? QuotaValueValidationState.Valid
            : QuotaValueValidationState.InvalidRange;
    public double VisualUsedPercent => Math.Clamp(double.IsFinite(UsedPercent) ? UsedPercent : 0d, 0d, 100d);
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
    public string ResetText { get; init; } = string.Empty;
    public DateTimeOffset? ResetAtUtc { get; init; }
    public QuotaResetState ResetState { get; init; } = QuotaResetState.NotProvided;
    public string SummaryText => ValueValidation == QuotaValueValidationState.Valid
        ? UsedPercent > 100d
            ? $"已用 {UsedPercent:0.#}% · 已超用 {UsedPercent - 100d:0.#}%"
            : $"已用 {UsedPercent:0.#}% · 剩余 {RemainingPercent:0.#}%"
        : $"额度值无效（上游原值 {UsedPercent:0.###}）";
}

public sealed record ProviderQuotaReportView(
    string Provider,
    string Source,
    IReadOnlyList<UsageWindowView> Windows,
    DateTimeOffset? UpdatedAt,
    bool ReverseEngineered);

public sealed record LocalUsageSummary(
    string Provider,
    string? Model,
    int RequestCount,
    int SuccessCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    double EstimatedCost,
    DateTimeOffset? LastSeen)
{
    public string CompactText =>
        $"最近日志 {RequestCount} 次（成功 {SuccessCount}）· {UsageFormatting.Number(TotalTokens)} Token";

    public string DetailedText =>
        $"最近日志：{RequestCount} 次请求 / {SuccessCount} 次成功 · 输入 {UsageFormatting.Number(InputTokens)} · 输出 {UsageFormatting.Number(OutputTokens)} · 合计 {UsageFormatting.Number(TotalTokens)} Token";

    public string CostText => EstimatedCost > 0
        ? $" · 本机估算费用 ${EstimatedCost:0.####}"
        : string.Empty;
}

public sealed record LocalUsageSnapshot(
    IReadOnlyDictionary<string, LocalUsageSummary> Providers,
    IReadOnlyDictionary<string, LocalUsageSummary> Models,
    int LogCount)
{
    public static LocalUsageSnapshot Empty { get; } = new(
        new Dictionary<string, LocalUsageSummary>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, LocalUsageSummary>(StringComparer.OrdinalIgnoreCase),
        0);

    public LocalUsageSummary? FindProvider(string provider) =>
        Providers.TryGetValue(provider, out var value) ? value : null;

    public LocalUsageSummary? FindModel(string provider, string model) =>
        Models.TryGetValue(Key(provider, model), out var value) ? value : null;

    public static string Key(string provider, string model) => $"{provider}\u001f{model}";
}

public sealed record LiveTokenUsageView(
    string Key,
    string DisplayName,
    long TodayInputTokens,
    long TodayOutputTokens,
    long TodayTotalTokens,
    long WeekInputTokens,
    long WeekOutputTokens,
    long WeekTotalTokens,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalTokens,
    int RequestCount,
    int SuccessCount,
    DateTimeOffset? LastSeen,
    string Source,
    bool Available)
{
    public static LiveTokenUsageView Empty(string key, string displayName, string source) => new(
        key, displayName, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, source, false);
}

public sealed record LiveTokenUsageSnapshot(
    LiveTokenUsageView Pro,
    LiveTokenUsageView Plus,
    IReadOnlyList<LiveTokenUsageView> Others,
    DateTimeOffset UpdatedAt)
{
    public static LiveTokenUsageSnapshot Empty { get; } = new(
        LiveTokenUsageView.Empty("pro", "Codex Pro", "总管家本机引擎日志"),
        LiveTokenUsageView.Empty("plus", "Codex Plus", "总管家本机引擎日志"),
        Array.Empty<LiveTokenUsageView>(),
        DateTimeOffset.Now);
}

public sealed record TokenSourceLedgerRow(
    string DisplayName,
    string ScopeText,
    string TodayText,
    string WeekText,
    string TotalText,
    string RequestText,
    string LastSeenText,
    string Accent);

public sealed record NativeRoutingAudit(
    string? LastBillingAccount,
    string? LastBillingProvider,
    DateTimeOffset? LastBillingAt,
    DateTimeOffset? ProLastRequestAt,
    int ProSuccessfulRequestsSinceSwitch,
    DateTimeOffset SwitchedAt,
    int NativeSuccessCount,
    bool SourceAvailable,
    string Message)
{
    public static NativeRoutingAudit Unavailable(DateTimeOffset switchedAt, string message) =>
        new(null, null, null, null, 0, switchedAt, 0, false, message);
}

public sealed record DailyUsagePoint(
    DateOnly Date,
    int RequestCount,
    int SuccessCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    double EstimatedCost);

public sealed record UsageTimelineSnapshot(
    IReadOnlyList<DailyUsagePoint> Days,
    int LogCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    double EstimatedCost,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    string SourcePath,
    bool SourceAvailable,
    string Message)
{
    public static UsageTimelineSnapshot Empty(string sourcePath, string message) => new(
        Array.Empty<DailyUsagePoint>(),
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        sourcePath,
        false,
        message);

    public DailyUsagePoint? Find(DateOnly date) => Days.FirstOrDefault(day => day.Date == date);

    public long TokensSince(DateOnly startDate) =>
        Days.Where(day => day.Date >= startDate).Sum(day => day.TotalTokens);
}

public static class UsageFormatting
{
    public static string Number(long value)
    {
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000d:0.##}B";
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.##}M";
        if (value >= 1_000) return $"{value / 1_000d:0.##}K";
        return value.ToString("N0");
    }

    public static DateTimeOffset? FromUnix(long? value)
    {
        if (value is null or <= 0) return null;
        try
        {
            return value >= 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
                : DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch
        {
            return null;
        }
    }

    public static string Reset(DateTimeOffset? value)
    {
        if (value is null) return "重置时间未返回";
        var remaining = value.Value - DateTimeOffset.Now;
        var relative = remaining <= TimeSpan.Zero
            ? "等待服务端刷新"
            : remaining.TotalDays >= 1
                ? $"约 {Math.Floor(remaining.TotalDays):0} 天 {remaining.Hours} 小时后"
                : remaining.TotalHours >= 1
                    ? $"约 {Math.Floor(remaining.TotalHours):0} 小时 {remaining.Minutes} 分后"
                    : $"约 {Math.Max(1, remaining.Minutes)} 分钟后";
        return $"{relative}重置 · 本地 {value.Value.ToLocalTime():MM-dd HH:mm}";
    }

    public static string CleanResetText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "重置时间未返回";
        var text = value.Trim();
        foreach (var marker in new[] { "Notify me", "Extra usage", "Keep using" })
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) text = text[..index].Trim();
        }
        var cleanText = text.TrimEnd('.', ' ');
        var parts = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && double.TryParse(parts[0], out var amount))
        {
            var unit = parts[1].ToLowerInvariant();
            if (unit.StartsWith("hour")) return $"约 {amount:0.#} 小时后重置";
            if (unit.StartsWith("day")) return $"约 {amount:0.#} 天后重置";
            if (unit.StartsWith("minute")) return $"约 {amount:0.#} 分钟后重置";
        }
        return cleanText switch
        {
            var clean when clean.EndsWith("hour", StringComparison.OrdinalIgnoreCase) => $"约 {clean} 后重置",
            var clean when clean.EndsWith("hours", StringComparison.OrdinalIgnoreCase) => $"约 {clean} 后重置",
            var clean when clean.EndsWith("day", StringComparison.OrdinalIgnoreCase) => $"约 {clean} 后重置",
            var clean when clean.EndsWith("days", StringComparison.OrdinalIgnoreCase) => $"约 {clean} 后重置",
            var clean => clean
        };
    }
}
