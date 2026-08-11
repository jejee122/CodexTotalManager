namespace CodexModelManager.Models;

public enum SubagentWorkerKind
{
    CodexNative,
    External
}

public sealed record SubagentRoleDefinition(
    string Id,
    string DisplayName,
    string Purpose,
    string DefaultModel,
    string ReasoningEffort,
    string SandboxMode,
    bool AllowsExternalWorker,
    string DeveloperInstructions,
    decimal? PricePerMillionTokens = null,
    string? Currency = null,
    decimal? BudgetLimit = null,
    int? MaxTimeoutSeconds = null);

public sealed record class SubagentRoleSelection
{
    public string RoleId { get; set; } = string.Empty;
    public SubagentWorkerKind WorkerKind { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string? SourceId { get; set; }
}

public sealed record class SubagentSourceAuthorization
{
    public string SourceId { get; set; } = string.Empty;
    public string ExpectedFingerprint { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public string AuthorizedDisplayName { get; set; } = string.Empty;
    public string AuthorizedEndpoint { get; set; } = string.Empty;
    public string AuthorizedAdapter { get; set; } = string.Empty;
    public string AuthorizedRoutePrefix { get; set; } = string.Empty;
    public string AuthorizedCredentialScope { get; set; } = string.Empty;
}

public sealed class SubagentConfigurationDocument
{
    public int SchemaVersion { get; set; } = 3;
    public DateTimeOffset? SavedAt { get; set; }
    public DateTimeOffset? LastAppliedAt { get; set; }
    public List<SubagentRoleSelection> Roles { get; set; } = new();
    public List<SubagentSourceAuthorization> SourceAuthorizations { get; set; } = new();
    public Dictionary<string, string> ManagedAgentHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ManagedMcpBlockHash { get; set; }
}

public sealed record SubagentAppliedRoleState(
    string RoleId,
    string? AppliedModel,
    bool ExactMatch,
    string StatusText);

public sealed record SubagentConfigurationSnapshot(
    bool ConfigReadable,
    bool ConfigSafe,
    bool AgentsEnabled,
    string ConfigPath,
    string AgentsDirectory,
    SubagentConfigurationDocument Draft,
    IReadOnlyDictionary<string, SubagentAppliedRoleState> AppliedRoles,
    SubagentBridgeStatus Bridge,
    string BaselineRevision,
    string Summary,
    string? Warning);

public sealed record SubagentBridgeStatus(
    bool ConfiguredOnDisk,
    bool ConfigurationExact,
    bool HasConflict,
    string StatusText,
    DateTimeOffset? LastHandshakeAt,
    string? LastHandshakeClient,
    DateTimeOffset? LastCallAt,
    bool? LastCallSucceeded,
    string? LastRoleId,
    string? LastRequestedModel,
    string? LastResolvedModel,
    int? LastHttpStatus,
    long? InputTokens,
    long? OutputTokens,
    string? LastError,
    string? LastAccountSource = null);

public sealed record SubagentApplyPlan(
    bool CanApply,
    int NativeRoleCount,
    int ExternalPendingCount,
    IReadOnlyList<string> Issues,
    string Summary);

public sealed record SubagentApplyResult(
    string BackupDirectory,
    int NativeRoleCount,
    int ExternalPendingCount,
    DateTimeOffset AppliedAt,
    string Summary);
