using System.Collections.ObjectModel;

namespace CodexModelManager.Models;

public enum PoolTransport
{
    OfficialCodex,
    NativeCodexAccount,
    CliProxyApi
}

public enum AccountProduct
{
    CodexPlus,
    CodexPro,
    Other
}

public enum RuntimeProviderIdentitySource
{
    Unknown,
    NativeOpenCodex,
    PoolDefinitionProviderId
}

public enum AccountRosterCompleteness
{
    Unknown,
    Complete,
    Partial,
    ReadFailed
}

public sealed class PoolDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PoolTransport Transport { get; set; }
    public AccountProduct Product { get; set; }
    public bool IsProtected { get; set; }
    public bool Enabled { get; set; } = true;
    public string? RouteAlias { get; set; }
    public string? ProviderId { get; set; }
    public string? NativeAccountId { get; set; }
    public string DefaultModel { get; set; } = "gpt-5.6-sol";
    public string BaseUrl { get; set; } = string.Empty;
    public int? LocalPort { get; set; }
    public string? AdminUser { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class ActivePoolState
{
    public string PoolId { get; set; } = PoolCatalogDefaults.OfficialPoolId;
    public string Model { get; set; } = "gpt-5.6-sol";
    public DateTimeOffset SwitchedAt { get; set; } = DateTimeOffset.Now;
    public string Verification { get; set; } = "official-direct";
}

public sealed class PoolCatalogDocument
{
    public int SchemaVersion { get; set; } = 4;
    public List<PoolDefinition> Pools { get; set; } = new();
    public ActivePoolState Active { get; set; } = new();
}

public static class PoolCatalogDefaults
{
    public const string OfficialPoolId = "official-pro";
    public const string PlusPoolId = "plus-api-1";
    public const string PlusAgentPoolId = "plus-agent-api-1";
    public const string ProAgentPoolId = "pro-agent-api-1";
}

public sealed class PoolAccountView
{
    public string PoolId { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public bool CanToggle { get; init; }
    public bool IsDestructiveAction { get; init; }
    public string ActionText { get; init; } = string.Empty;
    public string ProtectionText { get; init; } = string.Empty;
    public IReadOnlyList<UsageWindowView> QuotaWindows { get; init; } = Array.Empty<UsageWindowView>();
    public AccountQuotaProvenance QuotaProvenance { get; init; } = AccountQuotaProvenance.Unknown;
    public AccountQuotaAvailability QuotaAvailability { get; init; } = AccountQuotaAvailability.NotProvided;
    public string? QuotaErrorClass { get; init; }
    public bool QuotaSourceStale { get; init; }
    public string? RuntimeProviderId { get; init; }
    public RuntimeProviderIdentitySource RuntimeProviderIdentitySource { get; init; }
        = RuntimeProviderIdentitySource.Unknown;
    public bool RuntimeProviderLinked => RuntimeProviderIdentitySource != RuntimeProviderIdentitySource.Unknown
                                         && !string.IsNullOrWhiteSpace(RuntimeProviderId);
    public AccountRosterCompleteness QuotaRosterCompleteness { get; init; } = AccountRosterCompleteness.Unknown;
    public string UsageStatisticsText { get; init; } = string.Empty;
    public string ModelUsageText { get; init; } = string.Empty;
    public string UsageSourceText { get; init; } = string.Empty;
    public long UsageFiveHourTokens { get; init; }
    public long UsageWeekTokens { get; init; }
    public long UsageTotalTokens { get; init; }
    public int UsageWeekRequests { get; init; }
    public int UsageWeekSuccesses { get; init; }
    public DateTimeOffset? UsageUpdatedAt { get; init; }
    public string QuotaNote { get; init; } = string.Empty;
    public bool HasQuotaWindows => QuotaWindows.Count > 0;
    public bool HasUsageStatistics => !string.IsNullOrWhiteSpace(UsageStatisticsText);
    public bool HasModelUsage => !string.IsNullOrWhiteSpace(ModelUsageText);
    public bool HasUsageSource => !string.IsNullOrWhiteSpace(UsageSourceText);
    public bool HasQuotaNote => !string.IsNullOrWhiteSpace(QuotaNote);
    public bool HasProtection => !string.IsNullOrWhiteSpace(ProtectionText);
    public bool ShowRegularAction => CanToggle && !IsDestructiveAction;
    public bool ShowDangerAction => CanToggle && IsDestructiveAction;
    public string ToggleActionText => !string.IsNullOrWhiteSpace(ActionText)
        ? ActionText
        : !CanToggle ? "受保护" : Enabled ? "停用账号" : "恢复账号";
    public string ActionKey => $"{PoolId}|{Id}";
}

public sealed class AccountPoolView
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string TypeText { get; init; } = string.Empty;
    public string SectionTitle { get; init; } = string.Empty;
    public int SectionOrder { get; init; }
    public string StatusTitle { get; init; } = string.Empty;
    public string StatusDetail { get; init; } = string.Empty;
    public string EndpointText { get; init; } = string.Empty;
    public string AccountCountText { get; init; } = string.Empty;
    public string ModelCountText { get; init; } = string.Empty;
    public string LastCheckedText { get; init; } = string.Empty;
    public string? RuntimeProviderId { get; init; }
    public RuntimeProviderIdentitySource RuntimeProviderIdentitySource { get; init; }
        = RuntimeProviderIdentitySource.Unknown;
    public bool RuntimeProviderLinked => RuntimeProviderIdentitySource != RuntimeProviderIdentitySource.Unknown
                                         && !string.IsNullOrWhiteSpace(RuntimeProviderId);
    public AccountRosterCompleteness QuotaRosterCompleteness { get; init; } = AccountRosterCompleteness.Unknown;
    public bool IsActive { get; init; }
    public bool IsProtected { get; init; }
    public bool Enabled { get; init; }
    public bool CanSwitch { get; init; }
    public bool CanAddAccount { get; init; }
    public bool CanConfigure { get; init; }
    public bool CanTogglePool { get; init; }
    public bool CanSelectModel { get; init; }
    public bool NewTasksOnly { get; init; }
    public string ModelSelectionHint { get; init; } = string.Empty;
    public string SelectedModel { get; set; } = string.Empty;
    public string ModelHeaderText => "本次使用模型";
    public string ModelActionHint => "先选模型，再切换或应用；原生账号会让各任务下一条使用新线路";
    public string SwitchActionText => IsActive ? "应用所选模型" : "切到这个号池";
    public string AddAccountText { get; init; } = "添加账号";
    public string ConfigureText { get; init; } = "配置";
    public string TogglePoolText => Enabled ? "停用号池" : "恢复号池";
    public ObservableCollection<PoolAccountView> Accounts { get; } = new();
    public ObservableCollection<string> Models { get; } = new();
}

public sealed record PoolBackendSnapshot(
    bool Ready,
    string StatusTitle,
    string StatusDetail,
    string Endpoint,
    IReadOnlyList<PoolAccountView> Accounts,
    IReadOnlyList<string> Models,
    DateTimeOffset CheckedAt)
{
    public AccountRosterCompleteness AccountRosterCompleteness { get; init; } = AccountRosterCompleteness.Unknown;
}

public sealed record PoolOAuthStartResult(string Url, string State);

public sealed record CodexAccountLoginStartResult(string FlowId, string? Url);

public sealed record CodexAccountLoginStatusResult(
    string Status,
    string? AccountId,
    string? Email,
    string? Error);
