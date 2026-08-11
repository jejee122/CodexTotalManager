namespace CodexModelManager.Models;

public sealed class CodexAccountView
{
    public string Id { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Plan { get; init; }
    public bool IsMain { get; init; }
    public bool HasCredential { get; init; }
    public bool NeedsReauth { get; init; }
    public bool IsActive { get; set; }
    public double? WeeklyPercent { get; init; }
    public long? WeeklyResetAt { get; init; }
    public double? MonthlyPercent { get; init; }
    public long? MonthlyResetAt { get; init; }
    public long? QuotaUpdatedAt { get; init; }
    public int? ResetCredits { get; init; }
    public string HealthStatus { get; init; } = string.Empty;
    public bool IsCurrentTaskRouted { get; set; }

    public string AccountName => IsMain ? "主账号" : "第二个账号";
    public string PlanText => string.IsNullOrWhiteSpace(Plan) ? "套餐未知" : Plan.ToUpperInvariant() + " 套餐";
    public string UsageText => WeeklyPercent is null
        ? MonthlyPercent is null ? "官方未返回具体套餐额度" : $"本月已用 {MonthlyPercent:0.#}%"
        : $"本周已用 {WeeklyPercent:0.#}% · 还剩 {Math.Max(0, 100 - WeeklyPercent.Value):0.#}%";
    public IReadOnlyList<UsageWindowView> QuotaWindows
    {
        get
        {
            var result = new List<UsageWindowView>();
            if (WeeklyPercent is not null)
                result.Add(new UsageWindowView
                {
                    PeriodKey = "weekly",
                    Label = "每周额度",
                    UsedPercent = WeeklyPercent.Value,
                    ResetAtUtc = UsageFormatting.FromUnix(WeeklyResetAt),
                    ResetState = UsageFormatting.FromUnix(WeeklyResetAt) is null ? QuotaResetState.NotProvided : QuotaResetState.Parsed,
                    ResetText = UsageFormatting.Reset(UsageFormatting.FromUnix(WeeklyResetAt))
                });
            if (MonthlyPercent is not null)
                result.Add(new UsageWindowView
                {
                    PeriodKey = "monthly",
                    Label = "每月额度",
                    UsedPercent = MonthlyPercent.Value,
                    ResetAtUtc = UsageFormatting.FromUnix(MonthlyResetAt),
                    ResetState = UsageFormatting.FromUnix(MonthlyResetAt) is null ? QuotaResetState.NotProvided : QuotaResetState.Parsed,
                    ResetText = UsageFormatting.Reset(UsageFormatting.FromUnix(MonthlyResetAt))
                });
            return result;
        }
    }
    public DateTimeOffset? QuotaUpdatedTime => UsageFormatting.FromUnix(QuotaUpdatedAt);
    public bool IsNotActive => !IsActive;
    public bool CanApply => !IsActive || !IsCurrentTaskRouted;
    public string ActiveStatusText => !IsActive
        ? string.Empty
        : IsCurrentTaskRouted ? "当前任务已接入" : "仅设为优先，当前任务未接入";
    public string ActionText => IsActive
        ? IsCurrentTaskRouted ? "当前任务已接入" : "让当前任务使用它"
        : "切到这个账号";
    public string HealthText
    {
        get
        {
            if (NeedsReauth || !HasCredential || !HealthStatus.Equals("healthy", StringComparison.OrdinalIgnoreCase))
                return "需要重新登录";
            return "账号正常";
        }
    }
}

public sealed record CodexPoolSettings(string? ActiveAccountId, int AutoSwitchThreshold, int FailoverThreshold, string Mode);
