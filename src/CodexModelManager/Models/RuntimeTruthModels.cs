using System.Text.Json.Serialization;

namespace CodexModelManager.Models;

public enum RuntimeTruthState
{
    Unknown,
    Pending,
    Consistent,
    Diverged,
    Stale,
    Failed
}

public enum RuntimeExecutionOutcome
{
    Unknown,
    Succeeded,
    Failed,
    Cancelled
}

public enum RuntimeAccountIdentitySource
{
    Unknown,
    ExplicitAccountId,
    ProviderRoute
}

public enum RuntimeFailoverReason
{
    None,
    HttpFailure,
    RateLimit,
    Capacity,
    Authentication,
    Permission,
    ContextWindow,
    Connectivity,
    Cancelled,
    Unknown
}

public enum RuntimeLogSelectionBasis
{
    Timestamp,
    ArrayLastFallback
}

public enum RuntimeAttemptSelectionEvidence
{
    None,
    ExplicitFlag,
    Http2xxFallback,
    ArrayLastFallback,
    SingleAttempt
}

public enum RuntimeTruthEvidenceSource
{
    PoolCatalog,
    CodexConfiguration,
    CodexDesktop,
    OpenCodexRoute,
    OpenCodexLog,
    AccountUsageLedger
}

public sealed record RuntimeTruthPreferenceSource(
    string PoolId,
    string PoolDisplayName,
    string? PreferredAccountId,
    RuntimeAccountIdentitySource PreferredAccountIdentitySource,
    string PreferredAccountDisplayName,
    string PreferredModel,
    string? ExpectedTaskModel,
    DateTimeOffset SwitchedAt,
    string Verification);

public sealed record RuntimeTruthPreference(
    string PoolId,
    string PoolDisplayName,
    string? PreferredAccountId,
    RuntimeAccountIdentitySource PreferredAccountIdentitySource,
    string PreferredAccountDisplayName,
    string PreferredModel,
    string? ExpectedTaskModel,
    string? CodexDefaultModel,
    bool CodexDefaultMatchesExpected,
    DateTimeOffset SwitchedAt,
    string Verification)
{
    public static RuntimeTruthPreference Unknown { get; } = new(
        string.Empty,
        "首选账号未知",
        null,
        RuntimeAccountIdentitySource.Unknown,
        "首选账号未知",
        string.Empty,
        null,
        null,
        false,
        DateTimeOffset.MinValue,
        "unknown");
}

public sealed record RuntimeTruthTask(
    bool SourceAvailable,
    bool Connected,
    bool IsAnswering,
    string? DisplayedModel,
    string DisplayedModelLabel,
    string? ExpectedModel,
    string ExpectedModelLabel,
    bool MatchesPreference,
    string Message);

public sealed record RuntimeRouteAttempt(
    int Ordinal,
    string ProviderId,
    string ProviderDisplayName,
    string? AccountId,
    string AccountDisplayName,
    RuntimeAccountIdentitySource AccountIdentitySource,
    string Model,
    int? HttpStatus,
    long? DurationMs,
    string? ErrorCode,
    string? ErrorMessage,
    RuntimeFailoverReason FailoverReason,
    bool Selected,
    RuntimeAttemptSelectionEvidence SelectionEvidence = RuntimeAttemptSelectionEvidence.None,
    AttemptTokenUsageFact? TokenUsage = null,
    [property: JsonIgnore] string? AccountIdentityMaterial = null)
{
    public bool Succeeded => HttpStatus is >= 200 and < 300;

    public RuntimeExecutionOutcome Outcome => Succeeded
        ? RuntimeExecutionOutcome.Succeeded
        : FailoverReason == RuntimeFailoverReason.Cancelled
            ? RuntimeExecutionOutcome.Cancelled
            : HttpStatus is not null || !string.IsNullOrWhiteSpace(ErrorCode) || !string.IsNullOrWhiteSpace(ErrorMessage)
                ? RuntimeExecutionOutcome.Failed
                : RuntimeExecutionOutcome.Unknown;
}

public sealed record RuntimeRouteExecution(
    string? RequestId,
    string RequestedModel,
    int? HttpStatus,
    long? DurationMs,
    DateTimeOffset? Timestamp,
    RuntimeExecutionOutcome Outcome,
    string? ErrorCode,
    string? ErrorMessage,
    RuntimeLogSelectionBasis SelectionBasis,
    int SourceArrayIndex,
    IReadOnlyList<RuntimeRouteAttempt> Attempts,
    AttemptTokenUsageFact? RequestLevelTokenUsage = null,
    [property: JsonIgnore] string? RequestIdentityMaterial = null)
{
    public RuntimeRouteAttempt? ActualAttempt => Attempts.FirstOrDefault(attempt => attempt.Selected);
}

public sealed record RuntimeTruthConsistency(
    RuntimeTruthState State,
    string Message,
    IReadOnlyList<string> Mismatches);

public sealed record RuntimeTruthEvidence(
    RuntimeTruthEvidenceSource Source,
    bool Available,
    DateTimeOffset ObservedAt,
    string Message);

public sealed record RuntimeTruthSnapshot(
    long Revision,
    DateTimeOffset ObservedAt,
    RuntimeTruthPreference Preferred,
    RuntimeTruthTask Task,
    ActiveRoute? ConfiguredRoute,
    RuntimeRouteExecution? LastExecution,
    bool LastExecutionIsStale,
    bool LastExecutionPredatesPreference,
    RuntimeTruthConsistency Consistency,
    IReadOnlyList<RuntimeTruthEvidence> Evidence);

public static class RuntimeTruthDisplay
{
    public static string FormatAttempts(RuntimeRouteExecution? execution)
    {
        if (execution is null || execution.Attempts.Count == 0) return "尝试链：暂无可确认记录";
        return string.Join(
            Environment.NewLine,
            execution.Attempts.OrderBy(attempt => attempt.Ordinal).Select(FormatAttempt));
    }

    private static string FormatAttempt(RuntimeRouteAttempt attempt)
    {
        var account = attempt.AccountIdentitySource == RuntimeAccountIdentitySource.Unknown
                      || string.IsNullOrWhiteSpace(attempt.AccountId)
            ? "账号未归属"
            : $"账号 {attempt.AccountDisplayName}";
        var status = attempt.HttpStatus?.ToString() ?? "未知";
        var selected = attempt.Selected ? " · 最终采用" : string.Empty;
        return $"#{attempt.Ordinal} {attempt.ProviderDisplayName}/{attempt.Model} · {account} · HTTP {status} · {ReasonText(attempt)}{selected}";
    }

    private static string ReasonText(RuntimeRouteAttempt attempt)
    {
        if (attempt.Succeeded) return "成功";
        var reason = attempt.FailoverReason switch
        {
            RuntimeFailoverReason.RateLimit => "限流",
            RuntimeFailoverReason.Capacity => "容量/过载",
            RuntimeFailoverReason.Authentication => "认证失败",
            RuntimeFailoverReason.Permission => "权限不足",
            RuntimeFailoverReason.ContextWindow => "上下文超限",
            RuntimeFailoverReason.Connectivity => "连接失败",
            RuntimeFailoverReason.Cancelled => "已取消",
            RuntimeFailoverReason.HttpFailure => "HTTP 失败",
            _ => "失败原因未知"
        };
        var details = new[] { attempt.ErrorCode, attempt.ErrorMessage }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return details.Length == 0 ? reason : $"{reason}：{string.Join(" / ", details)}";
    }
}
