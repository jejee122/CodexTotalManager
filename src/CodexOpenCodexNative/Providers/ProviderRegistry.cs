using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Providers;

public sealed class ProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboDefinition> _combosByAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeProxyConfig _config;

    public ProviderRegistry(NativeProxyConfig config)
    {
        _config = config;
        foreach (var provider in config.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id)) continue;
            _byId[provider.Id] = ResolveApiKey(provider);
        }
        foreach (var builtIn in BuiltInProviders())
        {
            _byId[builtIn.Id] = builtIn;
        }
        foreach (var combo in config.Combos)
        {
            if (!string.IsNullOrWhiteSpace(combo.Alias)) _combosByAlias[combo.Alias] = combo;
        }
    }

    public IReadOnlyCollection<ProviderDefinition> All
    {
        get { lock (_gate) return _byId.Values.ToArray(); }
    }

    public ProviderDefinition? Find(string id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var provider) ? provider : null;
    }

    public void UpsertProvider(ProviderDefinition provider)
    {
        lock (_gate) _byId[provider.Id] = ResolveApiKey(provider);
    }

    public bool RemoveProvider(string id)
    {
        lock (_gate) return _byId.Remove(id);
    }

    public ComboDefinition? FindComboByAlias(string alias)
    {
        lock (_gate) return _combosByAlias.TryGetValue(alias, out var combo) ? combo : null;
    }

    public void UpsertCombo(ComboDefinition combo)
    {
        lock (_gate) _combosByAlias[combo.Alias] = combo;
    }

    public ProviderDefinition Default
    {
        get
        {
            lock (_gate)
                return _config.DefaultProvider is not null
                       && _byId.TryGetValue(_config.DefaultProvider, out var configured)
                       && !configured.Disabled
                    ? configured
                    : _byId.Values.FirstOrDefault(p => !p.Disabled && p.Id.Equals("openai", StringComparison.OrdinalIgnoreCase))
                      ?? _byId.Values.First(p => !p.Disabled);
        }
    }

    public List<OcxModelEntry> ListModels()
    {
        ProviderDefinition[] providers;
        lock (_gate) providers = _byId.Values.ToArray();
        var entries = new List<OcxModelEntry>();
        foreach (var provider in providers)
        {
            if (provider.Disabled) continue;
            var models = provider.Models
                .Concat(string.IsNullOrWhiteSpace(provider.DefaultModel)
                    ? Array.Empty<string>()
                    : new[] { provider.DefaultModel! })
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var model in models)
            {
                var namespaced = provider.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
                    ? model
                    : $"{provider.Id}/{model}";
                entries.Add(new OcxModelEntry
                {
                    Id = namespaced,
                    OwnedBy = provider.Name,
                    Namespaced = namespaced
                });
            }
        }
        return entries;
    }

    private static ProviderDefinition ResolveApiKey(ProviderDefinition source)
    {
        var apiKey = source.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey)
            && apiKey.StartsWith("${", StringComparison.Ordinal)
            && apiKey.EndsWith('}'))
        {
            var environmentName = apiKey[2..^1];
            apiKey = Environment.GetEnvironmentVariable(environmentName);
        }
        return new ProviderDefinition
        {
            Id = source.Id,
            Name = source.Name,
            Adapter = source.Adapter,
            BaseUrl = source.BaseUrl,
            ApiKey = apiKey,
            DefaultModel = source.DefaultModel,
            Models = source.Models.ToList(),
            Disabled = source.Disabled,
            ContextWindow = source.ContextWindow,
            AllowPrivateNetwork = source.AllowPrivateNetwork,
            Extra = source.Extra
        };
    }

    private static IEnumerable<ProviderDefinition> BuiltInProviders()
    {
        yield return new ProviderDefinition
        {
            Id = "openai",
            Name = "OpenAI 官方",
            Adapter = "openai-responses",
            BaseUrl = "https://chatgpt.com/backend-api/codex",
            DefaultModel = "gpt-5.6-sol",
            Models = new List<string> { "gpt-5.6-sol" },
            ContextWindow = 400000
        };
    }
}
