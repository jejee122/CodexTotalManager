using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Providers;

public static class RouteResolver
{
    public const string SwitchAlias = InternalRouteNames.MainAlias;

    public static bool IsInternalRouteAlias(string? value) =>
        InternalRouteNames.IsAlias(value);

    public static RouteResult Resolve(ProviderRegistry registry, string? requestedModel)
    {
        var model = requestedModel?.Trim();

        if (IsInternalRouteAlias(model))
        {
            var combo = registry.FindComboByAlias(model!);
            var selected = combo?.Targets.FirstOrDefault();
            var target = selected is null ? null : registry.Find(selected.Provider);
            if (selected is null || target is null || target.Disabled)
                throw new ModelNotFoundException(model!);
            return new RouteResult
            {
                Provider = target,
                ProviderId = target.Id,
                ModelId = selected.Model,
                IsInternal = true
            };
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            var fallback = registry.Default;
            return new RouteResult
            {
                Provider = fallback,
                ProviderId = fallback.Id,
                ModelId = fallback.DefaultModel ?? string.Empty
            };
        }

        var slash = model.IndexOf('/');
        if (slash > 0)
        {
            var prefix = model[..slash];
            var explicitProvider = registry.Find(prefix);
            if (explicitProvider is not null && !explicitProvider.Disabled)
            {
                return new RouteResult
                {
                    Provider = explicitProvider,
                    ProviderId = explicitProvider.Id,
                    ModelId = model[(slash + 1)..]
                };
            }
        }

        foreach (var provider in registry.All)
        {
            if (provider.Disabled) continue;
            if (provider.Models.Contains(model, StringComparer.OrdinalIgnoreCase)
                || string.Equals(provider.DefaultModel, model, StringComparison.OrdinalIgnoreCase))
            {
                return new RouteResult
                {
                    Provider = provider,
                    ProviderId = provider.Id,
                    ModelId = model
                };
            }
        }

        throw new ModelNotFoundException(model);
    }
}

public sealed class ModelNotFoundException : Exception
{
    public string Model { get; }

    public ModelNotFoundException(string model)
        : base($"未找到模型：{model}")
    {
        Model = model;
    }
}
