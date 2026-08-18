using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class SubagentSourceRegistryService
{
    private readonly AppSettingsService _settings;
    private readonly PoolCatalogService _pools;
    private readonly CliProxyPoolService _cliProxy;
    private readonly OpenCodexClient _openCodex;
    private readonly UnifiedGatewayService _gateway;

    public SubagentSourceRegistryService(
        AppSettingsService settings,
        PoolCatalogService pools,
        CliProxyPoolService cliProxy,
        OpenCodexClient openCodex,
        UnifiedGatewayService gateway)
    {
        _settings = settings;
        _pools = pools;
        _cliProxy = cliProxy;
        _openCodex = openCodex;
        _gateway = gateway;
    }

    /// <summary>
    /// Discovers source metadata and model catalogs only. This method never starts
    /// an upstream, creates a secret, writes gateway/Codex configuration, or invokes a model.
    /// </summary>
    public async Task<IReadOnlyList<SubagentSourceDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default) =>
        await DiscoverAsync(includeCodexSources: true, cancellationToken);

    public async Task<IReadOnlyList<SubagentSourceDescriptor>> DiscoverAsync(
        bool includeCodexSources,
        CancellationToken cancellationToken = default)
    {
        var discoveredAt = DateTimeOffset.Now;
        var result = new List<SubagentSourceDescriptor>();
        var configuredRoutes = _gateway.ReadConfiguredRoutes();
        var catalogInvalid = false;
        IReadOnlyList<PoolDefinition> cliPools;
        IReadOnlyList<PoolDefinition> catalogPools;
        try
        {
            cliPools = _gateway.GetCliWorkerPools(forDiscovery: true);
            catalogPools = _pools.GetPoolsFreshForDiscovery();
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or InvalidDataException
                                   or UnauthorizedAccessException
                                   or IOException)
        {
            catalogInvalid = true;
            result.Add(UnsupportedCatalog(ex, discoveredAt));
            cliPools = Array.Empty<PoolDefinition>();
            catalogPools = _pools.GetPools();
        }

        foreach (var pool in cliPools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result.Add(await DiscoverCliAsync(pool, configuredRoutes, discoveredAt, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Add(Unsupported(
                    pool,
                    InvalidCliSourceId(pool.Id),
                    "invalid/",
                    $"该号池的 ID、端点或凭据目录无法安全读取：{ex.GetType().Name}",
                    discoveredAt));
            }
        }

        foreach (var pool in catalogPools.Where(pool =>
                     includeCodexSources
                     && pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount))
        {
            var accountIdentity = pool.NativeAccountId ?? pool.Id;
            var fingerprint = ComputeNonRoutableFingerprint(
                SubagentSourceIdentity.NativeSourceId(pool.Id),
                pool.Transport.ToString(),
                accountIdentity);
            result.Add(new SubagentSourceDescriptor(
                SubagentSourceIdentity.NativeSourceId(pool.Id),
                pool.DisplayName,
                SubagentSourceKind.NativeCodexAccount,
                "codex-native/",
                "OpenAI 原生账号",
                "由 Codex 当前全局账号管理",
                "跟随总管家当前全局扣费线路",
                "codex-native",
                fingerprint,
                pool.Enabled,
                pool.Enabled && !string.IsNullOrWhiteSpace(pool.NativeAccountId),
                false,
                Array.Empty<string>(),
                pool.Enabled ? "已发现 · 仅支持全局线路切换" : "号池已停用",
                "Codex 原生 Agent 配置只能固定模型，不能按角色固定账号；这里不会提供虚假的账号绑定。",
                discoveredAt));
        }

        if (includeCodexSources && !catalogInvalid)
            await AddCustomProvidersAsync(result, discoveredAt, cancellationToken);
        return result
            .GroupBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => SourceOrder(item.Kind))
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<SubagentSourceDescriptor> DiscoverCliAsync(
        PoolDefinition pool,
        IReadOnlyList<UnifiedGatewayRoute> routes,
        DateTimeOffset discoveredAt,
        CancellationToken cancellationToken)
    {
        var sourceId = SubagentSourceIdentity.CliSourceId(pool.Id);
        var prefix = UnifiedGatewayService.GetCliRoutePrefix(pool);
        if (!PoolCatalogService.IsExactCliEndpoint(pool)
            || !PoolCatalogService.IsSafeCliPortBinding(pool)
            || !PoolCatalogService.IsSafeCliProviderBinding(pool))
            return Unsupported(
                pool,
                sourceId,
                prefix,
                "CLIProxy 端点或凭据槽未精确绑定到该号池；已禁止授权、启动和调用。",
                discoveredAt);
        var authCount = _gateway.GetCliAuthFileCount(pool.Id);
        var credentialIdentity = authCount == 1
            ? _gateway.GetCliCredentialIdentity(pool.Id)
            : null;
        string fingerprint;
        try
        {
            fingerprint = SubagentSourceIdentity.ComputeForPool(
                pool, sourceId, SubagentSourceKind.CliProxyPool, prefix,
                SubagentSourceIdentity.OpenAiChatAdapter, pool.ProviderId, credentialIdentity);
        }
        catch (Exception ex)
        {
            return Unsupported(pool, sourceId, prefix, ex.Message, discoveredAt);
        }
        var snapshot = pool.Enabled && authCount == 1 && credentialIdentity is not null
            ? await _cliProxy.ReadAsync(pool, ensureRunning: false, cancellationToken)
            : null;
        var cached = routes.Where(route => route.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)
                                           && SubagentSourceIdentity.FixedTimeEquals(route.SourceFingerprint, fingerprint))
            .Select(route => route.GatewayModel);
        var models = (snapshot?.Models.Count > 0
                ? snapshot.Models.Select(model => prefix + model)
                : cached)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ready = pool.Enabled
                    && authCount == 1
                    && credentialIdentity is not null
                    && snapshot?.Ready == true
                    && models.Length > 0;
        var status = !pool.Enabled
            ? "号池已停用"
            : authCount == 0
                ? "已发现 · 尚无独立 Agent API 授权"
                : authCount > 1
                    ? $"发现 {authCount} 份账号文件 · 已阻止串号"
                    : credentialIdentity is null
                        ? "唯一账号文件缺少可验证的 account_id/email · 已阻止调用"
                    : snapshot?.Ready == true
                        ? $"可用 · 本机目录 {models.Length} 个模型"
                        : models.Length > 0
                            ? $"进程未运行 · 保留已验证缓存 {models.Length} 个模型"
                            : snapshot?.StatusDetail ?? "模型目录尚未同步";
        var unsupported = !pool.Enabled
            ? "请先在中转站恢复该号池。"
            : authCount != 1
                ? "外部工人要求每个 CLIProxy 号池恰好一份独立账号授权。"
                : credentialIdentity is null
                    ? "无法从唯一账号文件提取稳定的非敏感账号身份，已按失败关闭。"
                : null;
        return new SubagentSourceDescriptor(
            sourceId,
            pool.DisplayName,
            SubagentSourceKind.CliProxyPool,
            prefix,
            PoolCatalogService.BuildCliBaseUrl(pool),
            credentialIdentity is null
                ? $"独立凭据槽 {pool.ProviderId ?? "未配置"} · 账号身份不可验证"
                : $"独立凭据槽 {pool.ProviderId ?? "未配置"} · 账号 {credentialIdentity[^8..]}（不显示密钥/邮箱）",
            $"只消耗 {pool.DisplayName}；不跨号池兜底",
            SubagentSourceIdentity.OpenAiChatAdapter,
            fingerprint,
            pool.Enabled,
            ready,
            pool.Enabled && authCount == 1 && credentialIdentity is not null,
            models,
            status,
            unsupported,
            discoveredAt);
    }

    private async Task AddCustomProvidersAsync(
        ICollection<SubagentSourceDescriptor> result,
        DateTimeOffset discoveredAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var providersTask = _openCodex.GetProvidersAsync(_settings, cancellationToken);
            var modelsTask = _openCodex.GetModelsAsync(_settings, cancellationToken);
            await Task.WhenAll(providersTask, modelsTask);
            foreach (var provider in providersTask.Result.Where(provider =>
                         !provider.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
                         && !provider.Id.StartsWith("cmm-", StringComparison.OrdinalIgnoreCase)))
            {
                var sourceId = $"custom:{provider.Id}";
                var models = modelsTask.Result.Where(model =>
                        !model.Disabled && model.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(model => model.Namespaced)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var bareModels = modelsTask.Result.Where(model =>
                        !model.Disabled && model.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(model => model.Id)
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var allModels = bareModels
                    .Concat(models.Where(model => !bareModels.Contains(model, StringComparer.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var fingerprint = ComputeNonRoutableFingerprint(
                    sourceId,
                    provider.Adapter,
                    $"{provider.BaseUrl}\nkey:{provider.HasApiKey}");
                result.Add(new SubagentSourceDescriptor(
                    sourceId,
                    provider.DisplayName,
                    SubagentSourceKind.OpenAiCompatible,
                    provider.Id + "/",
                    SafeEndpointDisplay(provider.BaseUrl),
                provider.HasApiKey ? "总管家本机引擎已保存独立 API Key" : "未配置 API Key",
                    $"消耗兼容 API 来源 {provider.DisplayName}",
                    provider.Adapter,
                    fingerprint,
                    !provider.Disabled,
                    !provider.Disabled && provider.HasApiKey && allModels.Length > 0,
                    true,
                    allModels,
                    provider.Disabled ? "来源已停用" : $"已发现 {allModels.Length} 个模型 · 可作子代理工人",
                    provider.Disabled ? "来源已停用" : null,
                    discoveredAt));
            }
        }
        catch
        {
            // OpenCodex is optional for read-only discovery. Pool-backed sources remain visible.
        }
    }

    private static SubagentSourceDescriptor Unsupported(
        PoolDefinition pool,
        string sourceId,
        string prefix,
        string reason,
        DateTimeOffset discoveredAt) => new(
        sourceId,
        pool.DisplayName,
        SubagentSourceKind.CliProxyPool,
        prefix,
        SafeEndpointDisplay(pool.Transport == PoolTransport.CliProxyApi
            ? PoolCatalogService.BuildCliBaseUrl(pool)
            : pool.BaseUrl),
        "凭据身份不可验证",
        $"计划使用 {pool.DisplayName}",
        SubagentSourceIdentity.OpenAiChatAdapter,
        ComputeNonRoutableFingerprint(sourceId, pool.Transport.ToString(), pool.Id),
        pool.Enabled,
        false,
        false,
        Array.Empty<string>(),
        "来源配置不安全",
        reason,
        discoveredAt);

    private static SubagentSourceDescriptor UnsupportedCatalog(Exception exception, DateTimeOffset discoveredAt)
    {
        const string sourceId = "invalid-cli:catalog";
        return new SubagentSourceDescriptor(
            sourceId,
            "号池清单不可用",
            SubagentSourceKind.CliProxyPool,
            "invalid/",
            "端点未读取",
            "凭据未读取",
            "没有访问任何来源",
            SubagentSourceIdentity.OpenAiChatAdapter,
            ComputeNonRoutableFingerprint(sourceId, "invalid-catalog", exception.GetType().Name),
            false,
            false,
            false,
            Array.Empty<string>(),
            "来源配置不安全",
            $"号池清单没有通过重新验证：{exception.GetType().Name}",
            discoveredAt);
    }

    private static string ComputeNonRoutableFingerprint(string sourceId, string kind, string identity)
    {
        var safeSource = sourceId.Replace('/', ':');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"cmm-nonroute-v1\n{safeSource}\n{kind}\n{identity}")));
    }

    private static string SafeEndpointDisplay(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "端点格式不可识别";
        return $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static int SourceOrder(SubagentSourceKind kind) => kind switch
    {
        SubagentSourceKind.CliProxyPool => 0,
        SubagentSourceKind.OpenAiCompatible => 1,
        _ => 2
    };

    private static string InvalidCliSourceId(string? poolId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(poolId ?? string.Empty));
        return "invalid-cli:" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
