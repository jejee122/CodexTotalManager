namespace CodexModelManager.Models;

public sealed class UnifiedGatewayConfiguration
{
    public string Service { get; set; } = "codex-unified-gateway";
    public int SchemaVersion { get; set; } = 4;
    public int Port { get; set; } = 10110;
    public string DataDirectory { get; set; } = string.Empty;
    public string ConfigurationFingerprint { get; set; } = string.Empty;
    public List<UnifiedGatewayRoute> Routes { get; set; } = new();
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
