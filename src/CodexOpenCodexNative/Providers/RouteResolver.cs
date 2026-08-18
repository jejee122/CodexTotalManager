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
            // 组合路由当前固定使用第一个目标；多目标组合已在配置写入时拒绝（TryReadCombo）。
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
            var explicitModel = model[(slash + 1)..];
            var explicitProvider = registry.Find(prefix);
            if (explicitProvider is not null
                && !explicitProvider.Disabled
                && !string.IsNullOrWhiteSpace(explicitModel)
                && (explicitProvider.Models.Contains(explicitModel, StringComparer.OrdinalIgnoreCase)
                    || string.Equals(explicitProvider.DefaultModel, explicitModel, StringComparison.OrdinalIgnoreCase)))
            {
                return new RouteResult
                {
                    Provider = explicitProvider,
                    ProviderId = explicitProvider.Id,
                    ModelId = explicitModel
                };
            }
        }

        // Bare model names are reserved for the built-in official provider.
        // Every third-party source must stay namespaced so two providers with
        // the same model ID can never route according to dictionary order.
        var official = registry.Find("openai");
        if (official is not null && !official.Disabled
            && (official.Models.Contains(model, StringComparer.OrdinalIgnoreCase)
                || string.Equals(official.DefaultModel, model, StringComparison.OrdinalIgnoreCase)))
        {
            return new RouteResult
            {
                Provider = official,
                ProviderId = official.Id,
                ModelId = model
            };
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
