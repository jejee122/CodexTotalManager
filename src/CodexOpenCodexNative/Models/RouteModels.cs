using System.Text.Json;

namespace CodexOpenCodexNative.Models;

public sealed class ProviderDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Adapter { get; set; } = "openai-chat";
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
    public List<string> Models { get; set; } = new();
    public bool Disabled { get; set; }
    public int ContextWindow { get; set; } = 128000;
    public bool AllowPrivateNetwork { get; set; }
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ComboTargetDefinition
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Weight { get; set; } = 1;
}

public sealed class ComboDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string Strategy { get; set; } = "failover";
    public int StickyLimit { get; set; } = 1;
    public List<ComboTargetDefinition> Targets { get; set; } = new();
}

public sealed class NativeProxyConfig
{
    public int ListenPort { get; set; } = 10100;
    public string? AdmissionToken { get; set; }
    public string? DefaultProvider { get; set; }
    public List<ProviderDefinition> Providers { get; set; } = new();
    public List<ComboDefinition> Combos { get; set; } = new();
}

public sealed class RouteResult
{
    public required ProviderDefinition Provider { get; init; }
    public string ProviderId { get; init; } = string.Empty;
    public required string ModelId { get; init; }
    public bool IsInternal { get; init; }
}
