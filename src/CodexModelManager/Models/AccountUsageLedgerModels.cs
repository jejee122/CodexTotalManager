using System.Text.Json.Serialization;

namespace CodexModelManager.Models;

public enum TokenTotalValidationState
{
    Unknown,
    Valid,
    Mismatch,
    InvalidValue
}

public enum TokenTotalSource
{
    Unknown,
    Upstream,
    DerivedInputOutput
}

public enum AccountUsageEvidenceStrength
{
    Weak,
    Moderate,
    Strong
}

public enum AccountQuotaAvailability
{
    NotProvided,
    Provided,
    ReadFailed
}

public enum AccountQuotaProvenance
{
    Unknown,
    Official,
    RelayReported,
    Estimated
}

public enum QuotaValueValidationState
{
    Unknown,
    Valid,
    InvalidRange
}

public enum QuotaResetState
{
    NotProvided,
    Parsed,
    LabelOnly,
    ParseFailed
}

public enum AccountUsageImporterHealth
{
    NotStarted,
    Healthy,
    Degraded,
    Stopped
}

public enum AccountUsageSourceAvailability
{
    Available,
    Missing,
    ReadFailed,
    Disabled
}

public sealed record AccountUsageImporterStatus(
    AccountUsageImporterHealth Health,
    DateTimeOffset? LastSuccessAt,
    string? LastErrorClass,
    string IdentityKeyState,
    string? StoppedReason)
{
    public DateTimeOffset? TokenLastSuccessAt { get; init; }
    public DateTimeOffset? QuotaLastSuccessAt { get; init; }
    public string? TokenErrorClass { get; init; }
    public string? QuotaErrorClass { get; init; }
    public AccountUsageImporterHealth TokenHealth { get; init; } = AccountUsageImporterHealth.NotStarted;
    public AccountUsageImporterHealth QuotaHealth { get; init; } = AccountUsageImporterHealth.NotStarted;
    public string? LifecycleErrorClass { get; init; }

    public static AccountUsageImporterStatus NotStarted { get; } = new(
        AccountUsageImporterHealth.NotStarted, null, null, "Uninitialized", null);
}

public sealed record AttemptTokenUsageFact(
    long? InputTokens,
    long? CachedInputTokens,
    long? CacheReadInputTokens,
    long? CacheCreationInputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    long? TotalTokens,
    TokenTotalSource TotalSource,
    TokenTotalValidationState TotalValidation,
    string ValidationMessage,
    string SourcePath);

public sealed record AccountUsageAttemptFact(
    int SchemaVersion,
    string IdempotencyKey,
    string PayloadHash,
    string? RequestId,
    string RequestIdentity,
    int AttemptOrdinal,
    bool RequestLevelUsage,
    string ProviderId,
    string AccountId,
    bool AccountAttributed,
    int AccountKeyVersion,
    string AccountKeyId,
    string StableAccountIdentity,
    RuntimeAccountIdentitySource AccountIdentitySource,
    string Model,
    string RequestedRoute,
    DateTimeOffset? OccurredAt,
    RuntimeExecutionOutcome Result,
    int? HttpStatus,
    RuntimeFailoverReason ErrorClassification,
    bool Selected,
    RuntimeAttemptSelectionEvidence SelectionEvidence,
    RuntimeLogSelectionBasis LogSelectionBasis,
    string? ErrorCode,
    string? ErrorMessage,
    AttemptTokenUsageFact? Usage,
    string SourceNamespace,
    string SourceEventIdentity,
    string Source,
    AccountUsageEvidenceStrength EvidenceStrength,
    DateTimeOffset RecordedAt)
{
    public bool IdentityVerified { get; init; } = true;
    public int RequestKeyVersion { get; init; }
    public string RequestKeyId { get; init; } = string.Empty;

    [JsonIgnore]
    public string UsageValidationBadge => Usage?.TotalValidation switch
    {
        TokenTotalValidationState.Valid => "校验一致",
        TokenTotalValidationState.Mismatch => "total 不一致",
        TokenTotalValidationState.InvalidValue => "usage 无效",
        _ => "未校验"
    };

    [JsonIgnore]
    public DateTimeOffset DisplayTime => OccurredAt ?? RecordedAt;

    [JsonIgnore]
    public string DisplayTimeText => $"本地 {DisplayTime.ToLocalTime():MM-dd HH:mm:ss}";

    [JsonIgnore]
    public string IdentityEvidenceText => IdentityVerified ? "稳定事件身份" : "身份不可验证，不计入精确账号总账";
}

public sealed record AccountUsageAnomaly(
    int SchemaVersion,
    string Kind,
    string? IdempotencyKey,
    string? ExistingPayloadHash,
    string? IncomingPayloadHash,
    long? SourceOffset,
    string Source,
    string Message,
    DateTimeOffset RecordedAt);

public sealed record TokenMetricAggregate(
    long Sum,
    int ProvidedAttemptCount,
    int AttemptCount,
    bool IsOverflow = false)
{
    [JsonIgnore]
    public long? Value => ProvidedAttemptCount == 0 || IsOverflow ? null : Sum;

    [JsonIgnore]
    public string DisplayText => IsOverflow
        ? $"超出 Int64 范围（{ProvidedAttemptCount}/{AttemptCount} 条提供）"
        : Value is null
        ? $"未提供（0/{AttemptCount}）"
        : $"{UsageFormatting.Number(Value.Value)}（{ProvidedAttemptCount}/{AttemptCount} 条提供）";
}

public sealed record AccountTokenAggregate(
    string ProviderId,
    string AccountId,
    bool AccountAttributed,
    int AttemptCount,
    int RequestCount,
    int SuccessCount,
    int FailedCount,
    int CancelledCount,
    int UsageAttemptCount,
    int InvalidUsageCount,
    int MismatchUsageCount,
    int OverflowMetricCount,
    TokenMetricAggregate Input,
    TokenMetricAggregate CachedInput,
    TokenMetricAggregate CacheReadInput,
    TokenMetricAggregate CacheCreationInput,
    TokenMetricAggregate Output,
    TokenMetricAggregate Reasoning,
    TokenMetricAggregate Total,
    DateTimeOffset? LastSeen);

public sealed record AccountQuotaSnapshotFact(
    int SchemaVersion,
    string IdempotencyKey,
    string PayloadHash,
    string ProviderId,
    bool ProviderLinked,
    string AccountId,
    bool AccountAttributed,
    int AccountKeyVersion,
    string AccountKeyId,
    string StableAccountIdentity,
    string PeriodKey,
    string DisplayLabel,
    decimal? Value,
    string Unit,
    AccountQuotaAvailability Availability,
    DateTimeOffset ObservedAt,
    bool SourceObservedAt,
    DateTimeOffset LocalObservedAt,
    bool SourceStale,
    string ObservationBatch,
    bool AccountObservationComplete,
    string? ErrorClass,
    string Source,
    AccountQuotaProvenance Provenance,
    DateTimeOffset RecordedAt)
{
    public string ObservationScope { get; init; } = "unknown";
    public QuotaValueValidationState ValueValidation { get; init; } = QuotaValueValidationState.Unknown;
    public DateTimeOffset? ResetAtUtc { get; init; }
    public string? ResetLabel { get; init; }
    public QuotaResetState ResetState { get; init; } = QuotaResetState.NotProvided;

    [JsonIgnore]
    public bool IsOfficial => Provenance == AccountQuotaProvenance.Official;

    [JsonIgnore]
    public bool IsEstimated => Provenance == AccountQuotaProvenance.Estimated;

    [JsonIgnore]
    public string ValueDisplayText => ValueValidation == QuotaValueValidationState.InvalidRange
        ? $"无效上游值 {Value:0.###} {Unit}"
        : Value is null ? "未提供" : $"{Value:0.###} {Unit}";

    [JsonIgnore]
    public string ResetDisplayText => ResetState switch
    {
        QuotaResetState.Parsed when ResetAtUtc is not null => $"重置 本地 {ResetAtUtc.Value.ToLocalTime():MM-dd HH:mm:ss}",
        QuotaResetState.LabelOnly => string.IsNullOrWhiteSpace(ResetLabel) ? "重置时间未提供" : $"重置 {ResetLabel}",
        QuotaResetState.ParseFailed => "重置时间无法解析",
        _ => "重置时间未提供"
    };
}

public sealed record AccountQuotaSnapshotView(
    AccountQuotaSnapshotFact Fact,
    bool IsStale,
    string StateText)
{
    [JsonIgnore]
    public string ObservedAtText => Fact.SourceObservedAt
        ? $"源观测 本地 {Fact.ObservedAt.ToLocalTime():MM-dd HH:mm:ss}"
        : $"本地记录 {Fact.LocalObservedAt.ToLocalTime():MM-dd HH:mm:ss}";
}

public sealed record AccountQuotaBatchCommit(
    int SchemaVersion,
    string IdempotencyKey,
    string PayloadHash,
    string ObservationBatch,
    string ProviderId,
    string StableAccountIdentity,
    bool AccountAttributed,
    int ExpectedFactCount,
    string FactsDigest,
    DateTimeOffset RecordedAt)
{
    public string ObservationScope { get; init; } = "unknown";
}

public sealed record AccountQuotaBatchPrepare(
    int SchemaVersion,
    string IdempotencyKey,
    string PayloadHash,
    string ObservationBatch,
    string ProviderId,
    string StableAccountIdentity,
    bool AccountAttributed,
    int ExpectedFactCount,
    string FactsDigest,
    DateTimeOffset RecordedAt)
{
    public string ObservationScope { get; init; } = "unknown";
}

public sealed record RequestScopeUsageAggregate(
    int FactCount,
    int RequestCount,
    int InvalidUsageCount,
    int MismatchUsageCount,
    TokenMetricAggregate Input,
    TokenMetricAggregate CachedInput,
    TokenMetricAggregate CacheReadInput,
    TokenMetricAggregate CacheCreationInput,
    TokenMetricAggregate Output,
    TokenMetricAggregate Reasoning,
    TokenMetricAggregate Total)
{
    public static RequestScopeUsageAggregate Empty { get; } = new(
        0, 0, 0, 0,
        new TokenMetricAggregate(0, 0, 0), new TokenMetricAggregate(0, 0, 0),
        new TokenMetricAggregate(0, 0, 0), new TokenMetricAggregate(0, 0, 0),
        new TokenMetricAggregate(0, 0, 0), new TokenMetricAggregate(0, 0, 0),
        new TokenMetricAggregate(0, 0, 0));
}

public sealed record AccountUsageLedgerSnapshot(
    IReadOnlyList<AccountTokenAggregate> Accounts,
    IReadOnlyList<AccountUsageAttemptFact> RecentAttempts,
    RequestScopeUsageAggregate RequestScopeUsage,
    IReadOnlyList<AccountQuotaSnapshotView> LatestQuotaSnapshots,
    int StoredAttemptCount,
    int StoredQuotaSnapshotCount,
    int BadAttemptLineCount,
    int BadQuotaLineCount,
    int IntegrityFailureCount,
    int AnomalyCount,
    DateTimeOffset ObservedAt,
    string TokenStatus,
    string QuotaStatus)
{
    public long Revision { get; init; }
    public AccountUsageImporterStatus ImporterStatus { get; init; } = AccountUsageImporterStatus.NotStarted;
    public RequestScopeUsageAggregate UnverifiedIdentityUsage { get; init; } = RequestScopeUsageAggregate.Empty;
    public bool TokenSourceStale { get; init; }
    public bool CoverageGapDetected { get; init; }
    public string? CoverageGapMessage { get; init; }
    public DateTimeOffset? CoverageGapFirstSeen { get; init; }
    public int TokenIntegrityFailureCount { get; init; }
    public int QuotaIntegrityFailureCount { get; init; }

    public static AccountUsageLedgerSnapshot Empty { get; } = new(
        Array.Empty<AccountTokenAggregate>(),
        Array.Empty<AccountUsageAttemptFact>(),
        RequestScopeUsageAggregate.Empty,
        Array.Empty<AccountQuotaSnapshotView>(),
        0,
        0,
        0,
        0,
        0,
        0,
        DateTimeOffset.MinValue,
        "Token 台账尚无记录",
        "配额/余额未提供；不会从 Token 用量推算");
}

public sealed record AccountUsageIngestResult(
    int CandidateCount,
    int AppendedCount,
    int DuplicateCount,
    int ConflictingReplayCount,
    int BadExistingLineCount,
    int BadSourceLineCount,
    bool SourceResetDetected,
    string Message)
{
    public AccountUsageSourceAvailability SourceAvailability { get; init; } = AccountUsageSourceAvailability.Available;
    public bool CoverageGapDetected { get; init; }
    public bool SourceContractMigrated { get; init; }
}

public sealed record AccountQuotaIngestResult(
    int CandidateCount,
    int AppendedCount,
    int DuplicateCount,
    int BadExistingLineCount,
    string Message);
