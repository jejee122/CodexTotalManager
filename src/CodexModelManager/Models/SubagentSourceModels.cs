namespace CodexModelManager.Models;

public enum SubagentSourceKind
{
    CliProxyPool,
    NativeCodexAccount,
    OpenAiCompatible
}

public sealed record SubagentSourceDescriptor(
    string SourceId,
    string DisplayName,
    SubagentSourceKind Kind,
    string RoutePrefix,
    string EndpointDisplay,
    string CredentialScopeText,
    string QuotaScopeText,
    string Adapter,
    string Fingerprint,
    bool Enabled,
    bool Ready,
    bool SupportsTextWorker,
    IReadOnlyList<string> Models,
    string StatusText,
    string? UnsupportedReason,
    DateTimeOffset DiscoveredAt);
