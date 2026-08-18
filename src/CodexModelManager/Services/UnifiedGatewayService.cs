using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class UnifiedGatewayService
{
    public const int DefaultPort = LocalPortPolicy.DefaultUnifiedGatewayPort;
    public const string PlusPoolId = PoolCatalogDefaults.PlusAgentPoolId;
    public const string ProPoolId = PoolCatalogDefaults.ProAgentPoolId;
    /// <summary>轮换组稳定模型名前缀：codex-auto/&lt;模型&gt; 聚合多个 Codex 账号池的同一模型。</summary>
    public const string CodexAutoRoutePrefix = "codex-auto/";
    private const string AdmissionSecretName = UnifiedGatewayKeys.MasterSecretName;
    private readonly AppSettingsService _settings;
    private readonly SecretStore _secrets;
    private readonly CliProxyPoolService _cliProxy;
    private readonly OpenCodexClient _openCodex;
    private readonly PoolCatalogService _poolCatalog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UnifiedGatewayService(
        AppSettingsService settings,
        SecretStore secrets,
        CliProxyPoolService cliProxy,
        OpenCodexClient openCodex,
        PoolCatalogService poolCatalog)
    {
        _settings = settings;
        _secrets = secrets;
        _cliProxy = cliProxy;
        _openCodex = openCodex;
        _poolCatalog = poolCatalog;
    }

    public int Port => _settings.UnifiedGatewayPort;
    public string Url => $"http://127.0.0.1:{Port}/v1";
    public string ConfigurationPath => Path.Combine(_settings.DataDirectory, "unified-gateway.json");

    /// <summary>
    /// Reads only the already-persisted gateway catalog. It does not start the
    /// gateway, discover accounts, create secrets, or send a model request.
    /// </summary>
    public IReadOnlyList<string> ReadConfiguredModelCatalog(string? prefix = null)
    {
        var models = ReadConfiguredRoutes()
            .Select(route => route.GatewayModel)
            .Where(model => string.IsNullOrWhiteSpace(prefix)
                            || model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return models;
    }

    public IReadOnlyList<UnifiedGatewayRoute> ReadConfiguredRoutes() =>
        ReadConfigurationRoutes()
            .Where(SubagentSourceIdentity.IsRouteIdentityValid)
            .Select(CloneRoute)
            .ToArray();

    public IReadOnlyList<PoolDefinition> GetCliWorkerPools(bool forDiscovery = false)
    {
        var catalogPools = (forDiscovery
                ? _poolCatalog.GetPoolsFreshForDiscovery()
                : _poolCatalog.GetPoolsFresh())
            .ToArray();
        return catalogPools
            .Where(pool => pool.Transport == PoolTransport.CliProxyApi)
            .GroupBy(pool => pool.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(pool => pool.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int GetCliAuthFileCount(string poolId) => CountAuthFiles(poolId);

    public string? GetCliCredentialIdentity(string poolId) =>
        CliCredentialIdentity.Read(_settings.DataDirectory, poolId);

    public static string GetCliRoutePrefix(PoolDefinition pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (pool.Id.Equals(PlusPoolId, StringComparison.OrdinalIgnoreCase)) return "codex-plus/";
        if (pool.Id.Equals(ProPoolId, StringComparison.OrdinalIgnoreCase)) return "codex-pro/";
        if (!PoolCatalogService.IsSafeCliPoolId(pool.Id))
            throw new InvalidOperationException("CLIProxy 号池 ID 不能安全地转换为模型路由前缀。");
        return $"cli/{pool.Id.ToLowerInvariant()}/";
    }

    public string GetClientKey()
    {
        var existing = _secrets.ReadInternal(AdmissionSecretName);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;
        var created = "cmm-gw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _secrets.SaveInternal(AdmissionSecretName, created);
        return created;

    }
    public sealed record GatewayClientKeyView(string Label, string KeyHint);

    /// <summary>为一个 harness 生成独立网关钥匙；返回完整钥匙（仅此一次可见），label 小写唯一。</summary>
    public string CreateGatewayClientKey(string label)
    {
        if (!UnifiedGatewayKeys.IsValidLabel(label))
            throw new InvalidOperationException("钥匙名称只能用小写字母、数字和连字符，1-32 位，且以字母或数字开头。");
        var secretName = UnifiedGatewayKeys.SecretNameForLabel(label);
        if (!string.IsNullOrWhiteSpace(_secrets.ReadInternal(secretName)))
            throw new InvalidOperationException($"钥匙 {label} 已存在；如需换新请先吊销旧钥匙。");
        var value = UnifiedGatewayKeys.GenerateKeyValue(label);
        _secrets.SaveInternal(secretName, value);
        return value;
    }

    public void RevokeGatewayClientKey(string label)
    {
        if (!UnifiedGatewayKeys.IsValidLabel(label))
            throw new InvalidOperationException("钥匙名称格式不正确。");
        if (label.Equals(UnifiedGatewayKeys.MasterLabel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("主钥匙不可吊销；如需更换主钥匙请重新安装或手动清理密钥库。");
        var secretName = UnifiedGatewayKeys.SecretNameForLabel(label);
        if (string.IsNullOrWhiteSpace(_secrets.ReadInternal(secretName)))
            throw new InvalidOperationException($"钥匙 {label} 不存在。");
        _secrets.RemoveInternal(secretName);
    }

    /// <summary>列出所有客户端钥匙（含主钥匙），只显示尾号提示，不返回完整钥匙。</summary>
    public IReadOnlyList<GatewayClientKeyView> ReadGatewayClientKeys()
    {
        var views = new List<GatewayClientKeyView>
        {
            new(
                UnifiedGatewayKeys.MasterLabel,
                HintFor(_secrets.ReadInternal(AdmissionSecretName)))
        };
        foreach (var internalName in _secrets.ListInternalNames(UnifiedGatewayKeys.ClientPrefix))
        {
            var label = UnifiedGatewayKeys.LabelForSecretName(internalName);
            if (label is null) continue;
            views.Add(new GatewayClientKeyView(label, HintFor(_secrets.ReadInternal(internalName))));
        }
        return views;
    }

    private static string HintFor(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "未生成" : $"••••{key[^Math.Min(6, key.Length)..]}";

    public async Task<UnifiedGatewayStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireConfigurationLockAsync(cancellationToken);
            await EnsureCompatibleHostStoppedBeforeConfigurationWriteAsync(cancellationToken);
            _ = GetClientKey();
            var discovered = await DiscoverAsync(ensureUpstreams: true, cancellationToken);
            WriteConfiguration(discovered.Routes, discovered.RotationGroups);
            var running = await EnsureHostRunningAsync(cancellationToken);
            var modelNames = discovered.Routes.Select(route => route.GatewayModel)
                .Concat(discovered.RotationGroups.Select(group => group.GatewayModel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return BuildStatus(running, modelNames, discovered.Pools);
        }
        finally { _gate.Release(); }
    }

    public async Task StopOwnedHostAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireConfigurationLockAsync(cancellationToken);
            await StopManagedHostIfRunningAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<UnifiedGatewayStatus> EnsureExternalWorkerReadyAsync(
        string sourceId,
        string expectedSourceFingerprint,
        string requiredModel,
        CancellationToken cancellationToken = default)
    {
        const string cliPrefix = "gateway-cli:";
        if (!sourceId.StartsWith(cliPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("该来源当前不支持安全的外部纯文本工人。");
        var poolId = sourceId[cliPrefix.Length..];
        var pool = ResolveCliWorkerPool(poolId);
        if (!pool.Enabled)
            throw new InvalidOperationException("所选 CLIProxy 号池已停用。");
        if (CountAuthFiles(pool.Id) != 1)
            throw new InvalidOperationException("所选 CLIProxy 号池必须恰好有一份独立账号授权，已阻止串号。");
        var credentialIdentity = GetCliCredentialIdentity(pool.Id)
                                 ?? throw new InvalidOperationException("唯一 CLIProxy 账号文件缺少可验证的 account_id/email，已阻止调用。");
        var routePrefix = GetCliRoutePrefix(pool);
        var fingerprint = SubagentSourceIdentity.ComputeForPool(
            pool,
            sourceId,
            SubagentSourceKind.CliProxyPool,
            routePrefix,
            SubagentSourceIdentity.OpenAiChatAdapter,
            pool.ProviderId,
            credentialIdentity);
        if (!SubagentSourceIdentity.FixedTimeEquals(fingerprint, expectedSourceFingerprint))
            throw new InvalidOperationException("CLIProxy 来源端点、provider 或凭据槽已变化，必须重新授权。");
        if (string.IsNullOrWhiteSpace(requiredModel)
            || !requiredModel.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("外部工人模型与获准来源的路由前缀不匹配。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireConfigurationLockAsync(cancellationToken);
            await EnsureCompatibleHostStoppedBeforeConfigurationWriteAsync(cancellationToken);
            pool = ResolveCliWorkerPool(poolId);
            if (!pool.Enabled || CountAuthFiles(pool.Id) != 1)
                throw new InvalidOperationException("CLIProxy 来源在调用前已停用或不再恰好包含一份账号授权。");
            credentialIdentity = GetCliCredentialIdentity(pool.Id)
                                 ?? throw new InvalidOperationException("CLIProxy 账号身份在调用前不可验证，已停止请求。");
            routePrefix = GetCliRoutePrefix(pool);
            fingerprint = SubagentSourceIdentity.ComputeForPool(
                pool,
                sourceId,
                SubagentSourceKind.CliProxyPool,
                routePrefix,
                SubagentSourceIdentity.OpenAiChatAdapter,
                pool.ProviderId,
                credentialIdentity);
            if (!SubagentSourceIdentity.FixedTimeEquals(fingerprint, expectedSourceFingerprint)
                || !requiredModel.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CLIProxy 来源身份在调用前发生变化，已停止请求并要求重新授权。");
            _ = GetClientKey();
            var snapshot = await _cliProxy.ReadAsync(pool, ensureRunning: true, cancellationToken);
            var selectedRoutes = snapshot.Ready
                ? snapshot.Models.Select(model => Route(
                    routePrefix + model,
                    model,
                    PoolCatalogService.BuildCliBaseUrl(pool),
                    pool.ProviderId,
                    pool.Id,
                    pool.DisplayName,
                    sourceId,
                    SubagentSourceKind.CliProxyPool,
                    routePrefix,
                    fingerprint,
                    credentialIdentity: credentialIdentity)).ToArray()
                : Array.Empty<UnifiedGatewayRoute>();
            if (!selectedRoutes.Any(route =>
                    route.GatewayModel.Equals(requiredModel, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("所选模型不在该 CLIProxy 号池当前目录中，未调用其他账号池。");

            var routes = MergeRoutesFailClosed(
                ReadConfiguredRoutes().Where(route =>
                    !route.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)),
                selectedRoutes);
            WriteConfiguration(routes, BuildCodexAutoRotationGroups(routes));
            var running = await EnsureHostRunningAsync(cancellationToken);
            return BuildStatus(running, routes.Select(route => route.GatewayModel).ToArray(), new[]
            {
                new UnifiedGatewayPoolStatus(pool.Id, pool.DisplayName, snapshot.Ready,
                    snapshot.StatusDetail, snapshot.Models.Count, false)
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UnifiedGatewayStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var running = await IsHostHealthyAsync(cancellationToken);
            var routes = ReadConfigurationRoutes();
            var pools = BuildPoolStatusFromConfiguration(routes);
            var models = routes.Select(route => route.GatewayModel)
                .Concat(ReadConfigurationRotationGroups().Select(group => group.GatewayModel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return BuildStatus(running, models, pools, createKeyIfMissing: false);
        }
        finally { _gate.Release(); }
    }

    public async Task<PoolOAuthStartResult> StartAuthorizationAsync(string poolId, CancellationToken cancellationToken = default)
    {
        var pool = ResolveCliWorkerPool(poolId);
        var count = CountAuthFiles(pool.Id);
        if (count > 0)
            throw new InvalidOperationException($"{pool.DisplayName} 已有独立登录。为防止串号，不能继续向同一 API 池添加账号。");
        return await _cliProxy.StartCodexOAuthAsync(pool, cancellationToken);
    }

    public async Task CompleteAuthorizationAsync(
        string poolId,
        string state,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var pool = ResolveCliWorkerPool(poolId);
        await _cliProxy.WaitForOAuthAsync(pool, state, timeout, cancellationToken);
        if (CountAuthFiles(pool.Id) != 1)
            throw new InvalidOperationException("授权完成后的账号文件数量不是 1，已阻止加入统一网关，避免串号。");
        await EnsureReadyAsync(cancellationToken);
    }

    private async Task<(List<UnifiedGatewayRoute> Routes, List<UnifiedGatewayPoolStatus> Pools, List<UnifiedGatewayRotationGroup> RotationGroups)> DiscoverAsync(
        bool ensureUpstreams,
        CancellationToken cancellationToken)
    {
        var routes = new List<UnifiedGatewayRoute>();
        var pools = new List<UnifiedGatewayPoolStatus>();
        foreach (var cliPool in GetCliWorkerPools())
            await AddCliPoolAsync(
                cliPool,
                GetCliRoutePrefix(cliPool),
                routes,
                pools,
                ensureUpstreams,
                cancellationToken);

        try
        {
            var providersTask = _openCodex.GetProvidersAsync(_settings, cancellationToken);
            var modelsTask = _openCodex.GetModelsAsync(_settings, cancellationToken);
            await Task.WhenAll(providersTask, modelsTask);
            var providers = providersTask.Result.Where(provider => !provider.Disabled
                && !provider.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
                && !PoolCatalogService.IsManagerOwnedProviderId(provider.Id)).ToArray();
            var customCount = 0;
            foreach (var provider in providers)
            {
                var providerRoutes = BuildCustomProviderRoutes(
                    provider,
                    modelsTask.Result,
                    _settings.NativeEnginePort);
                routes.AddRange(providerRoutes);
                customCount += providerRoutes.Count;
            }
            pools.Add(new UnifiedGatewayPoolStatus(
                "custom-api",
                "以后添加的兼容 API",
                true,
                customCount == 0 ? "暂无自建 API 模型；在“其他 API 模型”里添加后会自动同步。" : $"已从其他 API 模型同步 {customCount} 个模型。",
                customCount,
                false));
        }
        catch (Exception ex)
        {
            pools.Add(new UnifiedGatewayPoolStatus("custom-api", "以后添加的兼容 API", false,
                $"暂时无法同步：{ex.Message}", 0, false));
        }

        routes = MergeRoutesFailClosed(Array.Empty<UnifiedGatewayRoute>(), routes).ToList();
        return (routes, pools, BuildCodexAutoRotationGroups(routes));
    }

    internal static IReadOnlyList<UnifiedGatewayRoute> BuildCustomProviderRoutes(
        ProviderView provider,
        IEnumerable<ModelOption> models,
        int nativeEnginePort)
    {
        var result = new List<UnifiedGatewayRoute>();
        // CLIProxy account pools are already added by AddCliPoolAsync with their
        // own account identity and readiness checks. Treating the same cmm-*
        // provider as a custom API would expose it twice and bypass those labels.
        if (PoolCatalogService.IsManagerOwnedProviderId(provider.Id)) return result;
        var sourceId = $"custom:{provider.Id}";
        var routePrefix = provider.Id + "/";
        var endpoint = $"http://127.0.0.1:{nativeEnginePort}/v1";
        var fingerprint = SubagentSourceIdentity.Compute(
            sourceId,
            SubagentSourceKind.OpenAiCompatible.ToString(),
            endpoint,
            provider.Adapter,
            UnifiedGatewayKeys.NativeEngineAdmissionRouteSecretName,
            routePrefix);
        foreach (var model in models.Where(model => !model.Disabled
                     && model.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            // Third-party models are always namespaced. Adding a bare model ID
            // here makes two otherwise independent providers collide and can
            // fail-close the whole unified gateway.
            result.Add(Route(
                model.Namespaced,
                model.Namespaced,
                endpoint,
                UnifiedGatewayKeys.NativeEngineAdmissionRouteSecretName,
                sourceId,
                provider.DisplayName,
                sourceId,
                SubagentSourceKind.OpenAiCompatible,
                routePrefix,
                fingerprint,
                provider.Adapter));
        }
        return result;
    }

    /// <summary>
    /// 从 CLIProxy 号池路由推导 codex-auto/&lt;模型&gt; 轮换组：同一上游模型在每个账号池各有一条精确路由，
    /// 聚合后外部 harness 只用一个稳定模型名即可获得"哪个号有额度就用哪个号"的自动轮换。
    /// 撞名的组直接跳过（保持失败关闭，不用改写别人已占用的名字）。
    /// </summary>
    internal static List<UnifiedGatewayRotationGroup> BuildCodexAutoRotationGroups(
        IReadOnlyList<UnifiedGatewayRoute> routes)
    {
        var groups = new List<UnifiedGatewayRotationGroup>();
        var occupiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes) occupiedNames.Add(route.GatewayModel);
        foreach (var cluster in routes
                     .Where(route => route.SourceKind.Equals(
                                SubagentSourceKind.CliProxyPool.ToString(),
                                StringComparison.OrdinalIgnoreCase))
                     .GroupBy(route => route.UpstreamModel, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(cluster => cluster.Key, StringComparer.OrdinalIgnoreCase))
        {
            var gatewayModel = CodexAutoRoutePrefix + cluster.Key;
            if (!occupiedNames.Add(gatewayModel)) continue;
            groups.Add(new UnifiedGatewayRotationGroup
            {
                GatewayModel = gatewayModel,
                UpstreamModel = cluster.Key,
                Candidates = cluster
                    .OrderBy(route => route.PoolId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(route => route.GatewayModel, StringComparer.OrdinalIgnoreCase)
                    .Select(route => route.GatewayModel)
                    .ToList()
            });
        }
        return groups;
    }

    private async Task AddCliPoolAsync(
        PoolDefinition pool,
        string prefix,
        ICollection<UnifiedGatewayRoute> routes,
        ICollection<UnifiedGatewayPoolStatus> pools,
        bool ensure,
        CancellationToken cancellationToken)
    {
        if (!pool.Enabled)
        {
            pools.Add(new UnifiedGatewayPoolStatus(
                pool.Id,
                pool.DisplayName,
                false,
                "号池已停用，未启动、未读取账号，也未写入路由。",
                0,
                false));
            return;
        }
        var authCount = CountAuthFiles(pool.Id);
        if (authCount != 1)
        {
            var detail = authCount == 0
                ? "尚未为 Agent API 单独授权。原生 Codex 账号不受影响。"
                : $"检测到 {authCount} 个账号文件，已停止该出口，防止同池串号。";
            pools.Add(new UnifiedGatewayPoolStatus(pool.Id, pool.DisplayName, false, detail, 0, authCount == 0));
            return;
        }
        var credentialIdentity = GetCliCredentialIdentity(pool.Id);
        if (credentialIdentity is null)
        {
            pools.Add(new UnifiedGatewayPoolStatus(
                pool.Id,
                pool.DisplayName,
                false,
                "唯一账号文件缺少可验证的 account_id/email，已阻止启动和路由。",
                0,
                false));
            return;
        }
        var snapshot = await _cliProxy.ReadAsync(pool, ensure, cancellationToken);
        if (snapshot.Ready)
        {
            var sourceId = SubagentSourceIdentity.CliSourceId(pool.Id);
            var fingerprint = SubagentSourceIdentity.ComputeForPool(
                pool,
                sourceId,
                SubagentSourceKind.CliProxyPool,
                prefix,
                SubagentSourceIdentity.OpenAiChatAdapter,
                pool.ProviderId,
                credentialIdentity);
            foreach (var model in snapshot.Models)
                routes.Add(Route(
                    prefix + model,
                    model,
                    PoolCatalogService.BuildCliBaseUrl(pool),
                    pool.ProviderId,
                    pool.Id,
                    pool.DisplayName,
                    sourceId,
                    SubagentSourceKind.CliProxyPool,
                    prefix,
                    fingerprint,
                    credentialIdentity: credentialIdentity));
        }
        pools.Add(new UnifiedGatewayPoolStatus(pool.Id, pool.DisplayName, snapshot.Ready,
            snapshot.StatusDetail, snapshot.Models.Count, false));
    }

    private PoolDefinition ResolveCliWorkerPool(string poolId)
    {
        var configured = _poolCatalog.FindFresh(poolId);
        if (configured?.Transport == PoolTransport.CliProxyApi) return configured;
        throw new InvalidOperationException("经过验证的号池清单里找不到请求的 CLIProxy 号池。");
    }

    private int CountAuthFiles(string poolId)
    {
        if (!PoolCatalogService.IsSafeCliPoolId(poolId))
            throw new InvalidOperationException("CLIProxy 号池 ID 格式不安全。");
        var root = Path.GetFullPath(Path.Combine(_settings.DataDirectory, "cli-proxy", "pools"));
        var poolDirectory = Path.GetFullPath(Path.Combine(root, poolId));
        if (!poolDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CLIProxy 凭据目录越出受控号池根目录。");
        var directory = Path.Combine(poolDirectory, "auth");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Count() : 0;
    }


    private void WriteConfiguration(
        IReadOnlyList<UnifiedGatewayRoute> routes,
        IReadOnlyList<UnifiedGatewayRotationGroup> rotationGroups)
    {
        if (routes.Any(route => !SubagentSourceIdentity.IsRouteIdentityValid(route)))
            throw new InvalidOperationException("统一网关包含来源身份不完整的路由，已拒绝写入。");
        var normalizedRoutes = MergeRoutesFailClosed(Array.Empty<UnifiedGatewayRoute>(), routes);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
        var configuration = new UnifiedGatewayConfiguration
        {
            SchemaVersion = 4,
            Port = Port,
            DataDirectory = _settings.DataDirectory,
            Routes = normalizedRoutes.ToList(),
            RotationGroups = rotationGroups.OrderBy(group => group.GatewayModel, StringComparer.OrdinalIgnoreCase).ToList()
        };
        if (UnifiedGatewayHost.ValidateRotationGroups(configuration) is { Length: > 0 } groupError)
            throw new InvalidOperationException($"统一网关轮换组拒绝写入：{groupError}");
        configuration.ConfigurationFingerprint = UnifiedGatewayConfigurationIdentity.Compute(configuration);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var temp = ConfigurationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(configuration, options));
            File.Move(temp, ConfigurationPath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private IReadOnlyList<UnifiedGatewayRoute> ReadConfigurationRoutes()
    {
        try
        {
            if (!File.Exists(ConfigurationPath)) return Array.Empty<UnifiedGatewayRoute>();
            using var stream = new FileStream(
                ConfigurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var configuration = JsonSerializer.Deserialize<UnifiedGatewayConfiguration>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return configuration is not null
                   && configuration.Port == Port
                   && UnifiedGatewayConfigurationIdentity.Matches(configuration)
                ? configuration.Routes
                : Array.Empty<UnifiedGatewayRoute>();
        }
        catch { return Array.Empty<UnifiedGatewayRoute>(); }
    }

    private IReadOnlyList<UnifiedGatewayRotationGroup> ReadConfigurationRotationGroups()
    {
        try
        {
            if (!File.Exists(ConfigurationPath)) return Array.Empty<UnifiedGatewayRotationGroup>();
            using var stream = new FileStream(
                ConfigurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var configuration = JsonSerializer.Deserialize<UnifiedGatewayConfiguration>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return configuration is not null
                   && configuration.Port == Port
                   && UnifiedGatewayConfigurationIdentity.Matches(configuration)
                ? configuration.RotationGroups ?? new List<UnifiedGatewayRotationGroup>()
                : new List<UnifiedGatewayRotationGroup>();
        }
        catch { return Array.Empty<UnifiedGatewayRotationGroup>(); }
    }

    private async Task<FileStream> AcquireConfigurationLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_settings.DataDirectory);
        var path = Path.Combine(_settings.DataDirectory, "unified-gateway.commit.lock");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException ex) when (((ex.HResult & 0xFFFF) is 32 or 33)
                                         && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                throw new TimeoutException("等待统一网关跨进程配置锁超时。", ex);
            }
        }
    }

    private async Task<bool> EnsureHostRunningAsync(CancellationToken cancellationToken)
    {
        if (ReadConfigurationRoutes().Count == 0)
        {
            await StopManagedHostIfRunningAsync(cancellationToken);
            return false;
        }
        var existing = await ReadHostHealthAsync(cancellationToken);
        if (MatchesCurrentConfiguration(existing))
            return true;
        if (existing.IsManagerGateway && existing.Pid is > 0)
        {
            if (!await TryStopOutdatedManagedHostAsync(existing.Pid.Value, cancellationToken))
                throw new InvalidOperationException("检测到旧版统一网关，但无法验证其程序路径并安全重启。请关闭旧版总管家后重试。");
        }
        if (await IsPortOpenAsync(Port, cancellationToken))
            throw new InvalidOperationException($"端口 {Port} 已被其他程序占用，统一网关没有启动。");
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("无法定位总管家程序文件。");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--unified-gateway");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(ConfigurationPath);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动统一网关进程。");
        var ready = false;
        try
        {
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) return false;
                var health = await ReadHostHealthAsync(cancellationToken);
                if (health.Pid == process.Id && MatchesCurrentConfiguration(health))
                {
                    ready = true;
                    return true;
                }
                await Task.Delay(250, cancellationToken);
            }
            return false;
        }
        finally
        {
            if (!ready)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch { }
            }
            try { process.CancelOutputRead(); } catch { }
            try { process.CancelErrorRead(); } catch { }
            process.Dispose();
        }
    }

    private async Task StopManagedHostIfRunningAsync(CancellationToken cancellationToken)
    {
        var existing = await ReadHostHealthAsync(cancellationToken);
        if (!existing.IsManagerGateway) return;
        if (existing.Pid is not > 0
            || !await TryStopOutdatedManagedHostAsync(existing.Pid.Value, cancellationToken))
            throw new InvalidOperationException("统一网关已无可用路由，但无法安全停止旧的本机网关进程。");
    }

    private async Task EnsureCompatibleHostStoppedBeforeConfigurationWriteAsync(
        CancellationToken cancellationToken)
    {
        var existing = await ReadHostHealthAsync(cancellationToken);
        if (MatchesCurrentConfiguration(existing))
            return;
        if (existing.IsManagerGateway && existing.Pid is > 0)
        {
            if (await TryStopOutdatedManagedHostAsync(existing.Pid.Value, cancellationToken)) return;
            throw new InvalidOperationException(
                "检测到旧版统一网关，但无法验证其程序路径并在写入新格式前安全停止。请关闭旧版总管家后重试。");
        }
        if (await IsPortOpenAsync(Port, cancellationToken))
            throw new InvalidOperationException(
                $"端口 {Port} 已被无法验证的进程占用；为避免旧网关读取新格式配置，本次没有写入。");
    }

    private async Task<bool> IsHostHealthyAsync(CancellationToken cancellationToken)
    {
        var health = await ReadHostHealthAsync(cancellationToken);
        return MatchesCurrentConfiguration(health);
    }

    private async Task<GatewayHostHealth> ReadHostHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"http://127.0.0.1:{Port}/health", cancellationToken);
            if (!response.IsSuccessStatusCode) return GatewayHostHealth.Unhealthy;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var isManager = json.RootElement.TryGetProperty("service", out var service)
                            && service.GetString() == "codex-unified-gateway";
            var guardVersion = json.RootElement.TryGetProperty("routeGuardVersion", out var guard)
                               && guard.TryGetInt32(out var parsedGuard)
                ? parsedGuard
                : 0;
            var pid = json.RootElement.TryGetProperty("pid", out var pidNode)
                      && pidNode.TryGetInt32(out var parsedPid)
                ? parsedPid
                : (int?)null;
            var port = json.RootElement.TryGetProperty("port", out var portNode)
                       && portNode.TryGetInt32(out var parsedPort)
                ? parsedPort
                : 0;
            var routeCount = json.RootElement.TryGetProperty("routeCount", out var countNode)
                             && countNode.TryGetInt32(out var parsedCount)
                ? parsedCount
                : -1;
            var fingerprint = json.RootElement.TryGetProperty("configurationFingerprint", out var fingerprintNode)
                ? fingerprintNode.GetString()
                : null;
            var productVersion = json.RootElement.TryGetProperty("productVersion", out var versionNode)
                ? versionNode.GetString()
                : null;
            return new GatewayHostHealth(
                isManager,
                guardVersion,
                pid,
                port,
                routeCount,
                fingerprint,
                productVersion);
        }
        catch { return GatewayHostHealth.Unhealthy; }
    }

    private bool MatchesCurrentConfiguration(GatewayHostHealth health)
    {
        if (!health.IsManagerGateway
            || health.RouteGuardVersion < UnifiedGatewayHost.RouteGuardVersion
            || health.Pid is not > 0
            || health.Port != Port
            || !string.Equals(health.ProductVersion, CurrentProductVersion(), StringComparison.Ordinal))
            return false;
        try
        {
            using var stream = new FileStream(ConfigurationPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var configuration = JsonSerializer.Deserialize<UnifiedGatewayConfiguration>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return configuration is not null
                   && UnifiedGatewayConfigurationIdentity.Matches(configuration)
                   && configuration.Port == Port
                   && configuration.Routes.Count == health.RouteCount
                   && string.Equals(
                       configuration.ConfigurationFingerprint,
                       health.ConfigurationFingerprint,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string CurrentProductVersion() =>
        typeof(UnifiedGatewayService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UnifiedGatewayService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private async Task<bool> TryStopOutdatedManagedHostAsync(
        int pid,
        CancellationToken cancellationToken)
    {
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable)) return false;
            var currentDirectory = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var processDirectory = Path.GetFullPath(Path.GetDirectoryName(executable) ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileName = Path.GetFileName(executable);
            if (!processDirectory.Equals(currentDirectory, StringComparison.OrdinalIgnoreCase)
                || !fileName.StartsWith("CodexModelManager", StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!await IsPortOpenAsync(Port, cancellationToken)) return true;
                await Task.Delay(100, cancellationToken);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private UnifiedGatewayStatus BuildStatus(
        bool running,
        IReadOnlyList<string> models,
        IReadOnlyList<UnifiedGatewayPoolStatus> pools,
        bool createKeyIfMissing = true)
    {
        var key = createKeyIfMissing
            ? GetClientKey()
            : _secrets.ReadInternal(AdmissionSecretName);
        var keyHint = string.IsNullOrWhiteSpace(key)
            ? "未生成"
            : $"••••{key[^Math.Min(6, key.Length)..]}";
        var readyPools = pools.Count(pool => pool.Ready && pool.ModelCount > 0);
        var summary = running
            ? $"统一 API 已运行：{readyPools} 个来源、{models.Count} 个精确模型路由；失败时不会跨号池。"
            : "统一 API 尚未运行。点击“启动/同步网关”后会检查上游并启动。";
        return new UnifiedGatewayStatus(running, Url, keyHint, summary,
            models.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray(), pools, DateTimeOffset.Now);
    }

    private static IReadOnlyList<UnifiedGatewayPoolStatus> BuildPoolStatusFromConfiguration(IReadOnlyList<UnifiedGatewayRoute> routes) =>
        routes.GroupBy(route => new { route.PoolId, route.PoolLabel })
            .Select(group => new UnifiedGatewayPoolStatus(group.Key.PoolId, group.Key.PoolLabel, true,
                "已写入统一网关配置。", group.Count(), false))
            .ToArray();

    private static UnifiedGatewayRoute Route(
        string gatewayModel,
        string upstreamModel,
        string baseUrl,
        string? secretName,
        string poolId,
        string poolLabel,
        string sourceId,
        SubagentSourceKind sourceKind,
        string routePrefix,
        string sourceFingerprint,
        string adapter = SubagentSourceIdentity.OpenAiChatAdapter,
        string credentialIdentity = "") => new()
    {
        GatewayModel = gatewayModel,
        UpstreamModel = upstreamModel,
        BaseUrl = baseUrl,
        SecretName = secretName,
        PoolId = poolId,
        PoolLabel = poolLabel,
        SourceId = sourceId,
        SourceKind = sourceKind.ToString(),
        RoutePrefix = routePrefix,
        Adapter = adapter,
        CredentialIdentity = credentialIdentity,
        SourceFingerprint = sourceFingerprint
    };

    private static IReadOnlyList<UnifiedGatewayRoute> MergeRoutesFailClosed(
        IEnumerable<UnifiedGatewayRoute> existing,
        IEnumerable<UnifiedGatewayRoute> additions)
    {
        var all = existing.Concat(additions)
            .Where(SubagentSourceIdentity.IsRouteIdentityValid)
            .ToArray();
        var collisions = all
            .GroupBy(route => route.GatewayModel, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(route => route.SourceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(group => group.Key)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (collisions.Length > 0)
            throw new InvalidOperationException(
                $"模型命名在多个来源间冲突，已停止同步：{string.Join("、", collisions)}");
        return all
            .GroupBy(route => route.GatewayModel, StringComparer.OrdinalIgnoreCase)
            .Select(group => CloneRoute(group.Last()))
            .OrderBy(route => route.GatewayModel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static UnifiedGatewayRoute CloneRoute(UnifiedGatewayRoute route) => new()
    {
        GatewayModel = route.GatewayModel,
        UpstreamModel = route.UpstreamModel,
        BaseUrl = route.BaseUrl,
        SecretName = route.SecretName,
        PoolId = route.PoolId,
        PoolLabel = route.PoolLabel,
        SourceId = route.SourceId,
        SourceKind = route.SourceKind,
        RoutePrefix = route.RoutePrefix,
        Adapter = route.Adapter,
        CredentialIdentity = route.CredentialIdentity,
        SourceFingerprint = route.SourceFingerprint
    };

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(500);
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    private sealed record GatewayHostHealth(
        bool IsManagerGateway,
        int RouteGuardVersion,
        int? Pid,
        int Port,
        int RouteCount,
        string? ConfigurationFingerprint,
        string? ProductVersion)
    {
        public static GatewayHostHealth Unhealthy { get; } = new(false, 0, null, 0, -1, null, null);
    }
}
