namespace CodexModelManager.Models;

public sealed class UnifiedGatewayConfiguration
{
    public string Service { get; set; } = "codex-unified-gateway";
    public int SchemaVersion { get; set; } = 4;
    public int Port { get; set; } = 10110;
    public string DataDirectory { get; set; } = string.Empty;
    public string ConfigurationFingerprint { get; set; } = string.Empty;
    public List<UnifiedGatewayRoute> Routes { get; set; } = new();
    public List<UnifiedGatewayRotationGroup> RotationGroups { get; set; } = new();
}

/// <summary>
/// 轮换组：把多个 Codex 账号池的同一模型聚合成一个稳定模型名（codex-auto/&lt;model&gt;）。
/// 客户端只需网关密钥即可调用；服务端在每次请求前对候选路由做与精确路由相同的来源校验，
/// 并在 429/401/403/5xx 时按冷却策略自动切换下一个账号池。
/// </summary>
public sealed class UnifiedGatewayRotationGroup
{
    public string GatewayModel { get; set; } = string.Empty;
    public string UpstreamModel { get; set; } = string.Empty;
    public List<string> Candidates { get; set; } = new();
}

public sealed class UnifiedGatewayRoute
{
    public string GatewayModel { get; set; } = string.Empty;
    public string UpstreamModel { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? SecretName { get; set; }
    public string PoolId { get; set; } = string.Empty;
    public string PoolLabel { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string RoutePrefix { get; set; } = string.Empty;
    public string Adapter { get; set; } = "openai-chat";
    public string CredentialIdentity { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
}

public sealed record UnifiedGatewayPoolStatus(
    string PoolId,
    string Label,
    bool Ready,
    string Detail,
    int ModelCount,
    bool CanAuthorize);

public sealed record UnifiedGatewayStatus(
    bool Running,
    string Url,
    string KeyHint,
    string Summary,
    IReadOnlyList<string> Models,
    IReadOnlyList<UnifiedGatewayPoolStatus> Pools,
    DateTimeOffset CheckedAt);
