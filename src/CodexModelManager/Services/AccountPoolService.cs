using System.Diagnostics;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class AccountPoolService
{
    public const string StableDesktopAlias = OpenCodexClient.SwitchAlias;
    public const bool AllowsAutomaticCodexRestart = false;

    private sealed record NativeCodexSnapshot(
        IReadOnlyList<CodexAccountView> Accounts,
        CodexPoolSettings Settings,
        IReadOnlyList<string> Models,
        string? Error,
        AccountRosterCompleteness RosterCompleteness);

    private readonly PoolCatalogService _catalog;
    private readonly CliProxyPoolService _cliProxy;
    private readonly OpenCodexClient _openCodex;
    private readonly OpenCodexProcessService _process;
    private readonly CodexConfigService _codexConfig;
    private readonly CodexDesktopBridgeService _desktop;
    private readonly ConfigBackupService _backups;
    private readonly AppSettingsService _settings;
    private readonly SecretStore _secrets;
    private readonly SemaphoreSlim _nativeReadGate = new(1, 1);
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _nativeCacheGate = new();
    private NativeCodexSnapshot? _nativeSnapshotCache;
    private DateTimeOffset _nativeSnapshotCachedAt;
    private static readonly TimeSpan NativeSnapshotCacheLifetime = TimeSpan.FromSeconds(12);

    public AccountPoolService(
        PoolCatalogService catalog,
        CliProxyPoolService cliProxy,
        OpenCodexClient openCodex,
        OpenCodexProcessService process,
        CodexConfigService codexConfig,
        CodexDesktopBridgeService desktop,
        ConfigBackupService backups,
        AppSettingsService settings,
        SecretStore secrets)
    {
        _catalog = catalog;
        _cliProxy = cliProxy;
        _openCodex = openCodex;
        _process = process;
        _codexConfig = codexConfig;
        _desktop = desktop;
        _backups = backups;
        _settings = settings;
        _secrets = secrets;
    }

    public PoolCatalogService Catalog => _catalog;
    public AccountRosterCompleteness CatalogRosterCompleteness => _catalog.LoadWarning is null
        ? AccountRosterCompleteness.Complete : AccountRosterCompleteness.ReadFailed;

    public void InvalidateReadCache()
    {
        lock (_nativeCacheGate)
        {
            _nativeSnapshotCache = null;
            _nativeSnapshotCachedAt = default;
        }
    }

    public async Task<LiveTokenUsageSnapshot> ReadLiveTokenUsageAsync(
        CancellationToken cancellationToken = default)
    {
        if (_catalog.LoadWarning is not null)
        {
            var source = "号池清单处于只读安全隔离；未读取本机或远端用量。";
            return new LiveTokenUsageSnapshot(
                LiveTokenUsageView.Empty("pro", "Codex Pro", source),
                LiveTokenUsageView.Empty("plus", "Codex Plus", source),
                Array.Empty<LiveTokenUsageView>(),
                DateTimeOffset.Now);
        }

        var native = await ReadNativeCodexSnapshotCachedAsync(cancellationToken);
        var nativeUsage = await _openCodex.GetNativeAccountUsageAsync(native.Accounts, cancellationToken);
        var pro = nativeUsage.GetValueOrDefault("pro")
                  ?? LiveTokenUsageView.Empty("pro", "Codex Pro", "总管家本机完整日志");
        var plus = nativeUsage.GetValueOrDefault("plus")
                   ?? LiveTokenUsageView.Empty("plus", "Codex Plus", "总管家本机完整日志");

        var others = nativeUsage.Values
            .Where(usage => usage.Key.StartsWith("provider:", StringComparison.OrdinalIgnoreCase))
            .Select(usage =>
            {
                var provider = usage.Key["provider:".Length..];
                return usage with
                {
                    DisplayName = _settings.GetProviderName(provider),
                    Source = $"OpenCodex 成功请求 · {provider}"
                };
            })
            .OrderByDescending(usage => usage.TotalTokens)
            .ToArray();
        return new LiveTokenUsageSnapshot(pro, plus, others, DateTimeOffset.Now);
    }

    public async Task<NativeRoutingAudit> ReadNativeRoutingAuditAsync(
        CancellationToken cancellationToken = default)
    {
        var active = _catalog.GetActive();
        if (_catalog.LoadWarning is not null)
            return NativeRoutingAudit.Unavailable(
                active.SwitchedAt,
                "号池清单处于只读安全隔离；未读取当前路由。");
        try
        {
            var native = await ReadNativeCodexSnapshotCachedAsync(cancellationToken);
            return await _openCodex.GetNativeRoutingAuditAsync(native.Accounts, active.SwitchedAt, cancellationToken);
        }
        catch (Exception ex)
        {
            return NativeRoutingAudit.Unavailable(active.SwitchedAt, $"本机路由审计暂不可用：{ex.Message}");
        }
    }

    public async Task<IReadOnlyList<AccountPoolView>> ReadViewsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_catalog.LoadWarning is not null)
        {
            var fallbackActive = _catalog.GetActive();
            return _catalog.GetPools()
                .Select(pool => CreateCatalogRecoveryView(pool, fallbackActive, _catalog.LoadWarning))
                .OrderBy(view => view.SectionOrder)
                .ThenBy(view => view.IsProtected ? 0 : 1)
                .ThenBy(view => view.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        var native = await ReadNativeCodexSnapshotCachedAsync(cancellationToken);
        if (native.Accounts.Count > 0) _catalog.SyncNativeCodexAccounts(native.Accounts, addMissing: false);
        var pools = _catalog.GetPools();
        var storedActive = _catalog.GetActive();
        var active = ResolveEffectiveActive(storedActive, pools, native);
        if (!active.PoolId.Equals(storedActive.PoolId, StringComparison.OrdinalIgnoreCase)
            || !active.Model.Equals(storedActive.Model, StringComparison.OrdinalIgnoreCase)
            || !active.Verification.Equals(storedActive.Verification, StringComparison.OrdinalIgnoreCase))
        {
            _catalog.SetActive(active.PoolId, active.Model, active.Verification);
        }
        var tasks = pools.Select(pool => ReadViewAsync(pool, active, native, cancellationToken)).ToArray();
        var views = await Task.WhenAll(tasks);
        return views.OrderBy(view => view.SectionOrder)
            .ThenBy(view => view.IsProtected ? 0 : 1)
            .ThenBy(view => view.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AccountPoolView>> ReadDisconnectedViewsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_catalog.LoadWarning is not null)
        {
            var active = _catalog.GetActive();
            return _catalog.GetPools()
                .Select(pool => CreateCatalogRecoveryView(pool, active, _catalog.LoadWarning))
                .OrderBy(view => view.SectionOrder)
                .ThenBy(view => view.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        var storedActive = _catalog.GetActive();
        var views = new List<AccountPoolView>();
        foreach (var pool in _catalog.GetPools())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount)
            {
                views.Add(CreateDisconnectedNativeView(pool, storedActive));
                continue;
            }

            views.Add(await ReadViewAsync(
                pool,
                storedActive,
                new NativeCodexSnapshot(
                    Array.Empty<CodexAccountView>(),
                    new CodexPoolSettings(null, 80, 3, "disconnected"),
                    Array.Empty<string>(),
                    "Codex 未连接；未读取原生账号。",
                    AccountRosterCompleteness.Unknown),
                cancellationToken));
        }

        return views.OrderBy(view => view.SectionOrder)
            .ThenBy(view => view.IsProtected ? 0 : 1)
            .ThenBy(view => view.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static AccountPoolView CreateDisconnectedNativeView(
        PoolDefinition pool,
        ActivePoolState active) => new()
    {
        Id = pool.Id,
        RuntimeProviderId = ResolveRuntimeProviderId(pool),
        RuntimeProviderIdentitySource = ResolveRuntimeProviderIdentitySource(pool),
        QuotaRosterCompleteness = AccountRosterCompleteness.Unknown,
        DisplayName = pool.DisplayName,
        Description = pool.Description,
        TypeText = TypeText(pool),
        SectionTitle = "Codex 原生账号 · 连接后读取",
        SectionOrder = 0,
        StatusTitle = "Codex 未连接",
        StatusDetail = "没有读取 Codex 账号、套餐、额度或聊天状态。",
        EndpointText = "OpenAI 原生账号",
        AccountCountText = "未读取账号",
        ModelCountText = "连接后读取模型",
        LastCheckedText = "Codex 隔离中",
        IsActive = active.PoolId.Equals(pool.Id, StringComparison.OrdinalIgnoreCase),
        IsProtected = pool.IsProtected,
        Enabled = pool.Enabled,
        CanSwitch = false,
        CanAddAccount = false,
        CanConfigure = false,
        CanTogglePool = false,
        CanSelectModel = false,
        NewTasksOnly = false,
        ModelSelectionHint = "先点总管家的一键连接 Codex，再读取或切换原生账号。",
        SelectedModel = pool.DefaultModel,
        AddAccountText = "连接后管理",
        ConfigureText = "连接后管理"
    };

    private static AccountPoolView CreateCatalogRecoveryView(
        PoolDefinition pool,
        ActivePoolState active,
        string warning) => new()
    {
        Id = pool.Id,
        DisplayName = pool.DisplayName,
        Description = pool.Description,
        TypeText = TypeText(pool),
        SectionTitle = pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
            ? "Codex 账号 · 只读安全视图"
            : "外部 API · 只读安全视图",
        SectionOrder = pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount ? 0 : 1,
        StatusTitle = "号池清单需要修复",
        StatusDetail = warning,
        EndpointText = pool.Transport == PoolTransport.CliProxyApi
            ? PoolCatalogService.BuildCliBaseUrl(pool)
            : pool.BaseUrl,
        AccountCountText = "未读取账号",
        ModelCountText = "未读取模型",
        LastCheckedText = "安全隔离：未连接任何来源",
        IsActive = active.PoolId.Equals(pool.Id, StringComparison.OrdinalIgnoreCase),
        IsProtected = pool.IsProtected,
        Enabled = false,
        CanSwitch = false,
        CanAddAccount = false,
        CanConfigure = false,
        CanTogglePool = false,
        CanSelectModel = false,
        NewTasksOnly = false,
        ModelSelectionHint = "修复 pools.json 后重新打开总管家；当前不会启动、探测或调用任何来源。",
        SelectedModel = pool.DefaultModel,
        AddAccountText = "安全隔离中",
        ConfigureText = "安全隔离中"
    };

    public PoolDefinition AddCliProxyPool(AccountProduct product) => _catalog.AddCliProxyPool(product);

    public void SetPoolEnabled(string poolId, bool enabled) => _catalog.SetEnabled(poolId, enabled);

    public async Task<PoolOAuthStartResult> StartOAuthAsync(
        string poolId,
        CancellationToken cancellationToken = default)
    {
        var pool = RequirePool(poolId, PoolTransport.CliProxyApi);
        return await _cliProxy.StartCodexOAuthAsync(pool, cancellationToken);
    }

    public async Task CompleteOAuthAsync(
        string poolId,
        string state,
        CancellationToken cancellationToken = default)
    {
        var pool = RequirePool(poolId, PoolTransport.CliProxyApi);
        await _cliProxy.WaitForOAuthAsync(pool, state, TimeSpan.FromMinutes(5), cancellationToken);
        var snapshot = await _cliProxy.ReadAsync(pool, ensureRunning: true, cancellationToken);
        if (!snapshot.Ready || snapshot.Accounts.Count != 1 || snapshot.Models.Count == 0)
            throw new InvalidOperationException($"授权已返回，但 API 还没有通过账号和模型复查：{snapshot.StatusDetail}");
    }

    public async Task SetCliProxyAccountEnabledAsync(
        string poolId,
        string accountId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var pool = RequirePool(poolId, PoolTransport.CliProxyApi);
        await _cliProxy.SetAccountEnabledAsync(pool, accountId, enabled, cancellationToken);
    }

    public async Task<CodexAccountLoginStartResult> StartNativeCodexLoginAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _process.EnsureNativeEngineOnlyAsync(cancellationToken))
            throw new OpenCodexAccountApiUnavailableException("OpenCodex 没有启动成功，无法调用账号登录接口。");
        return await _openCodex.StartCodexAccountLoginAsync(cancellationToken);
    }

    public async Task<CodexAccountLoginStatusResult> CompleteNativeCodexLoginAsync(
        string flowId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _openCodex.GetCodexAccountLoginStatusAsync(flowId, cancellationToken);
            if (status.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(status.AccountId))
                    throw new InvalidOperationException("官方登录已完成，但 OpenCodex 没有返回账号编号。");
                var refreshed = await _openCodex.GetCodexAccountsAsync(forceRefresh: true, cancellationToken);
                var account = refreshed.Accounts.FirstOrDefault(item =>
                    item.Id.Equals(status.AccountId, StringComparison.OrdinalIgnoreCase));
                if (account is null)
                    throw new InvalidOperationException("官方登录已完成，但账号没有出现在 OpenCodex 账号列表中。");
                _catalog.SyncNativeCodexAccounts(refreshed.Accounts, addMissing: true);
                InvalidateReadCache();
                return status;
            }
            if (status.Status.Equals("error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(status.Error ?? "Codex 账号登录失败。");
            if (status.Status.Equals("expired", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Codex 账号登录流程已过期，请重新添加。");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("等待 Codex 官方登录超时，请重新添加账号。");
    }

    public async Task OpenOpenCodexManagementAsync(CancellationToken cancellationToken = default)
    {
        if (!await _process.EnsureNativeEngineOnlyAsync(cancellationToken))
            throw new InvalidOperationException("本机号池引擎没有启动成功。");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _openCodex.ManagementUrl,
            UseShellExecute = true
        });
    }

    public async Task<string> DeleteNativeCodexAccountAsync(
        string poolId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var pool = _catalog.Find(poolId) ?? throw new InvalidOperationException("账号卡片不存在。");
        if (pool.IsProtected || pool.Transport == PoolTransport.OfficialCodex
            || accountId.Equals("__main__", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pro 主账号受保护，禁止删除。");
        if (pool.Transport != PoolTransport.NativeCodexAccount)
            throw new InvalidOperationException("这个卡片不是 Codex 原生账号。");
        if (!string.Equals(pool.NativeAccountId, accountId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("账号卡片与 OpenCodex 账号不匹配，已停止删除。");

        var native = await _openCodex.GetCodexAccountsAsync(forceRefresh: true, cancellationToken);
        var account = native.Accounts.FirstOrDefault(item =>
            item.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("这个账号已经不在 OpenCodex 中，请刷新状态。");
        if (account.IsMain)
            throw new InvalidOperationException("Pro 主账号受保护，禁止删除。");
        if (_catalog.GetActive().PoolId.Equals(poolId, StringComparison.OrdinalIgnoreCase)
            || native.Settings.Mode.Equals("pool", StringComparison.OrdinalIgnoreCase)
            && string.Equals(native.Settings.ActiveAccountId, accountId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("正在使用的账号不能删除，请先切到其他账号，并发送一条消息确认新线路可用。");

        var backup = _backups.CreateAccountDeletionBackup(_catalog.FilePath);
        await _openCodex.DeleteCodexAccountAsync(accountId, cancellationToken);
        var verified = await _openCodex.GetCodexAccountsAsync(forceRefresh: true, cancellationToken);
        if (verified.Accounts.Any(item => item.Id.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("OpenCodex 没有确认删除账号，大管家没有移除卡片。");
        _catalog.RemoveNativeCodexAccountPool(poolId, accountId);
        InvalidateReadCache();
        return backup;
    }

    public Task<OperationResult> SwitchAsync(
        string poolId,
        CancellationToken cancellationToken = default) =>
        SwitchAsync(poolId, requestedModel: null, cancellationToken);

    public async Task<OperationResult> SwitchAsync(
        string poolId,
        string? requestedModel,
        CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            return await SwitchCoreAsync(poolId, requestedModel, cancellationToken);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<OperationResult> SwitchCoreAsync(
        string poolId,
        string? requestedModel,
        CancellationToken cancellationToken)
    {
        var target = _catalog.Find(poolId) ?? throw new InvalidOperationException("号池不存在。");
        if (!target.Enabled) return OperationResult.Fail("这个号池已停用。");
        if (!_codexConfig.IsManagedNativeProviderSelected())
            return OperationResult.Fail("Codex 目前没有连接总管家。你仍可添加、登录和管理号池；要把某个号池切给 Codex 使用，请先点“一键连接 Codex”。");

        var desktopState = await _desktop.ReadStateAsync(cancellationToken);
        if (!desktopState.Connected && IsCodexProcessRunning())
            return OperationResult.Fail("Codex is running, but the Manager cannot verify its current task. Close Codex or restore the desktop bridge before switching pools.");
        if (desktopState.IsTurnRunning)
            return OperationResult.Fail("Codex 正在回答，等回答结束后再切换。");

        var oldActive = _catalog.GetActive();
        var codexSnapshot = _codexConfig.CreateSnapshot();
        CodexPoolSettings? nativeSettingsBefore = null;
        var nativeRoutingTouched = false;
        var openCodexConfigTouched = false;
        var openCodexBackup = _backups.Create();
        try
        {
            if (!await _process.EnsureOpenCodexAsync(cancellationToken)
                || !_codexConfig.IsManagedNativeProviderSelected())
                throw new InvalidOperationException("Native Engine 或 Codex 托管 Provider 没有安全准备好。");

            if (target.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount)
            {
                var native = await ReadNativeCodexSnapshotAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(native.Error))
                    throw new InvalidOperationException($"原生 Codex 账号读取失败：{native.Error}");
                var account = ResolveNativeAccount(target, native.Accounts)
                              ?? throw new InvalidOperationException("这个 Codex 账号已经不在 OpenCodex 账号列表中，请先同步账号。");
                var nativeModel = ResolveModel(requestedModel, target.DefaultModel, native.Models);
                VerifyFreshTarget(target);
                nativeSettingsBefore = native.Settings;
                nativeRoutingTouched = true;
                return await SwitchNativeCodexAccountAsync(target, account, nativeModel, cancellationToken);
            }

            var backend = target.Transport switch
            {
                PoolTransport.CliProxyApi => await _cliProxy.ReadAsync(target, ensureRunning: true, cancellationToken),
                _ => throw new InvalidOperationException("不支持的号池类型。")
            };
            if (!backend.Ready)
                throw new InvalidOperationException($"目标号池还没有准备好：{backend.StatusDetail}");

            var models = backend.Models;
            var model = ResolveModel(requestedModel, target.DefaultModel, models);
            VerifyFreshTarget(target);
            openCodexConfigTouched = true;
            await EnsureProviderAsync(target, models, cancellationToken);
            var providerTest = await _openCodex.TestProviderAsync(target.ProviderId!, cancellationToken);
            if (!providerTest.Success)
                throw new InvalidOperationException($"OpenCodex 没有通过号池 API 复查：{providerTest.Message}");

            var comboId = $"cmm-pool-{target.Id}";
            await _openCodex.UpsertPoolRouteAsync(
                comboId,
                target.RouteAlias!,
                new[] { (Provider: target.ProviderId!, Model: model) },
                cancellationToken);
            await VerifyPoolRouteAsync(comboId, target, model, cancellationToken);
            // The desktop always uses one stable alias. Pool-specific aliases remain
            // available for diagnostics, but creating a new desktop model name for
            // every pool made hot switching brittle and forced Codex restarts.
            await _openCodex.SetActiveTargetAsync(target.ProviderId!, model, cancellationToken);
            var activeTarget = await _openCodex.GetActiveTargetAsync(cancellationToken);
            if (activeTarget is null
                || !activeTarget.Value.Provider.Equals(target.ProviderId, StringComparison.OrdinalIgnoreCase)
                || !activeTarget.Value.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OpenCodex 没有确认固定入口已经指向目标号池。");
            var codexModel = $"{target.ProviderId}/{model}";
            _codexConfig.SetDefaultModel(codexModel);

            if (target.Id == PoolCatalogDefaults.PlusPoolId)
                await _openCodex.SetCodexAccountModeAsync("direct", cancellationToken);

            _catalog.SetActive(target.Id, model, "cli-provider-route-catalog-and-default-verified");
            return OperationResult.Ok(
                $"已准备 {target.DisplayName}，默认模型为 {codexModel}。总管家没有点击或重启 Codex；当前任务要切换时，请在 Codex 自己的模型菜单里选择这个名字。下一条真实请求后再按 provider 日志确认实际扣费。");
        }
        catch (Exception ex)
        {
            var rollback = new List<string>();
            using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var rollbackToken = rollbackTimeout.Token;
            if (openCodexConfigTouched)
            {
                try
                {
                    _backups.Restore(openCodexBackup);
                    if (!await _process.RestartOpenCodexAsync(rollbackToken))
                        rollback.Add("OpenCodex 配置已恢复，但服务健康检查未通过");
                }
                catch (Exception restore) { rollback.Add($"OpenCodex 配置恢复失败：{restore.Message}"); }
            }
            if (nativeRoutingTouched && nativeSettingsBefore is not null)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(nativeSettingsBefore.ActiveAccountId))
                        await _openCodex.SetPreferredCodexAccountAsync(nativeSettingsBefore.ActiveAccountId!, rollbackToken);
                    await _openCodex.SetCodexAutoSwitchThresholdAsync(nativeSettingsBefore.AutoSwitchThreshold, rollbackToken);
                    await _openCodex.SetCodexFailoverThresholdAsync(nativeSettingsBefore.FailoverThreshold, rollbackToken);
                    await _openCodex.SetCodexAccountModeAsync(nativeSettingsBefore.Mode, rollbackToken);
                }
                catch (Exception restore) { rollback.Add($"原生扣费账号恢复失败：{restore.Message}"); }
            }
            try { _codexConfig.RestoreSnapshot(codexSnapshot); }
            catch (Exception restore) { rollback.Add($"Codex 默认模型恢复失败：{restore.Message}"); }
            try { _catalog.RestoreActive(oldActive); }
            catch (Exception restore) { rollback.Add($"号池状态恢复失败：{restore.Message}"); }
            var detail = rollback.Count == 0 ? "原来的账号和默认模型已恢复；总管家从未操作当前任务菜单。" : string.Join("；", rollback);
            return OperationResult.Fail($"切换失败：{ex.Message} {detail}");
        }
    }

    private async Task<OperationResult> SwitchNativeCodexAccountAsync(
        PoolDefinition target,
        CodexAccountView account,
        string model,
        CancellationToken cancellationToken)
    {
        if (!account.HasCredential || account.NeedsReauth)
            throw new InvalidOperationException("这个 Codex 账号需要重新登录。");
        await _openCodex.SetPreferredCodexAccountAsync(account.Id, cancellationToken);
        await _openCodex.SetCodexAutoSwitchThresholdAsync(0, cancellationToken);
        await _openCodex.SetCodexFailoverThresholdAsync(0, cancellationToken);
        var targetMode = account.IsMain ? "direct" : "pool";
        // OpenCodex 在账号模式 PATCH 成功后会清除所有旧线程的账号亲和。
        // 即使目标仍是 pool，也必须重复 PATCH，确保所有已打开任务的下一条请求重新绑定所选账号。
        await _openCodex.SetCodexAccountModeAsync(targetMode, cancellationToken);
        var verified = await _openCodex.GetCodexAccountsAsync(forceRefresh: true, cancellationToken);
        if (!verified.Settings.Mode.Equals(targetMode, StringComparison.OrdinalIgnoreCase)
            || verified.Settings.AutoSwitchThreshold != 0
            || verified.Settings.FailoverThreshold != 0
            || !string.Equals(verified.Settings.ActiveAccountId, account.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenCodex 没有确认全局账号线路已固定到所选账号。");
        _codexConfig.SetDefaultModel(model);
        var finalAccounts = await _openCodex.GetCodexAccountsAsync(forceRefresh: true, cancellationToken);
        var runtime = await _openCodex.GetRuntimeStatusAsync(cancellationToken);
        if (!runtime.Healthy
            || runtime.Port != _settings.NativeEnginePort
            || finalAccounts.Settings.AutoSwitchThreshold != 0
            || finalAccounts.Settings.FailoverThreshold != 0
            || !finalAccounts.Settings.Mode.Equals(targetMode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(finalAccounts.Settings.ActiveAccountId, account.Id, StringComparison.OrdinalIgnoreCase)
            || !_codexConfig.IsManagedNativeProviderSelected()
            || !string.Equals(_codexConfig.ReadDefaultModel(), model, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The final Native Engine, account, failover, or model identity did not match the requested pool.");
        _catalog.SetActive(target.Id, model, "native-account-control-plane-and-default-verified");
        var label = account.IsMain ? "Pro 主账号" : $"{account.PlanText}账号";
        return OperationResult.Ok(
            $"已把账号线路固定到 {label}，默认模型为 {model}，自动串池已关闭。总管家没有点击或重启 Codex；当前任务要切换模型时，请使用 Codex 自己的模型菜单。真实扣费账号要以下一条请求日志为准。");
    }

    private async Task EnsureProviderAsync(
        PoolDefinition pool,
        IReadOnlyList<string> models,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pool.ProviderId) || string.IsNullOrWhiteSpace(pool.RouteAlias))
            throw new InvalidOperationException("号池缺少 provider 或路由别名。");
        if (models.Count == 0) throw new InvalidOperationException("号池没有返回模型。");
        var key = _secrets.Read(pool.ProviderId)
                  ?? throw new InvalidOperationException("号池客户端密钥还没有配置。");
        key = string.Empty;
        var envName = _secrets.GetEnvironmentName(pool.ProviderId);
        var adapter = pool.Transport == PoolTransport.CliProxyApi
            ? "openai-responses"
            : "openai-chat";
        var contextWindow = pool.Transport == PoolTransport.CliProxyApi ? 400000 : 128000;
        await _openCodex.AddProviderAsync(
            pool.ProviderId,
            PoolCatalogService.BuildCliBaseUrl(pool),
            $"${{{envName}}}",
            models,
            adapter,
            contextWindow,
            pool.Transport == PoolTransport.CliProxyApi,
            cancellationToken);
        _settings.SetProviderName(pool.ProviderId, pool.DisplayName);
        if (!await _process.RestartOpenCodexAsync(cancellationToken))
            throw new InvalidOperationException("OpenCodex 重启后没有通过健康检查。");
        var verifiedProvider = (await _openCodex.GetProvidersAsync(_settings, cancellationToken))
            .FirstOrDefault(provider => provider.Id.Equals(pool.ProviderId, StringComparison.OrdinalIgnoreCase));
        var expectedBaseUrl = PoolCatalogService.BuildCliBaseUrl(pool);
        if (verifiedProvider is null
            || verifiedProvider.Disabled
            || !verifiedProvider.HasApiKey
            || !verifiedProvider.BaseUrl.Equals(expectedBaseUrl, StringComparison.Ordinal)
            || !verifiedProvider.Adapter.Equals(adapter, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenCodex did not persist the exact CLIProxy provider identity and endpoint.");
    }

    private void VerifyFreshTarget(PoolDefinition target)
    {
        var fresh = _catalog.FindFresh(target.Id)
                    ?? throw new InvalidOperationException("The target pool disappeared before the switch was committed.");
        if (!fresh.Enabled
            || fresh.Transport != target.Transport
            || !string.Equals(fresh.NativeAccountId, target.NativeAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("The target pool changed while the switch was running.");
        if (target.Transport == PoolTransport.CliProxyApi
            && (fresh.LocalPort != target.LocalPort
                || !string.Equals(fresh.ProviderId, target.ProviderId, StringComparison.Ordinal)
                || !string.Equals(fresh.RouteAlias, target.RouteAlias, StringComparison.Ordinal)
                || !string.Equals(
                    PoolCatalogService.BuildCliBaseUrl(fresh),
                    PoolCatalogService.BuildCliBaseUrl(target),
                    StringComparison.Ordinal)))
            throw new InvalidOperationException("The target CLIProxy identity changed while the switch was running.");
    }

    private async Task VerifyPoolRouteAsync(
        string comboId,
        PoolDefinition target,
        string model,
        CancellationToken cancellationToken)
    {
        var route = await _openCodex.GetPoolRouteAsync(comboId, cancellationToken);
        if (route is null
            || !route.Alias.Equals(target.RouteAlias, StringComparison.Ordinal)
            || route.Targets.Count != 1
            || !route.Targets[0].Provider.Equals(target.ProviderId, StringComparison.Ordinal)
            || !route.Targets[0].Model.Equals(model, StringComparison.Ordinal))
            throw new InvalidOperationException("OpenCodex did not persist the exact target pool route.");
    }

    private static bool IsCodexProcessRunning()
        => CodexDesktopProcessDetector.IsRunning();

    private async Task<AccountPoolView> ReadViewAsync(
        PoolDefinition pool,
        ActivePoolState active,
        NativeCodexSnapshot native,
        CancellationToken cancellationToken)
    {
        PoolBackendSnapshot snapshot;
        if (pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount)
            snapshot = ReadNativeCodexAccount(pool, native);
        else if (pool.Transport == PoolTransport.CliProxyApi)
            snapshot = await _cliProxy.ReadAsync(pool, ensureRunning: false, cancellationToken);
        else
            throw new InvalidOperationException("不支持的号池类型。");

        var view = new AccountPoolView
        {
            Id = pool.Id,
            RuntimeProviderId = ResolveRuntimeProviderId(pool),
            RuntimeProviderIdentitySource = ResolveRuntimeProviderIdentitySource(pool),
            QuotaRosterCompleteness = snapshot.AccountRosterCompleteness,
            DisplayName = pool.DisplayName,
            Description = pool.Description,
            TypeText = TypeText(pool),
            SectionTitle = pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
                ? "Codex 账号 · 全局线路可手动切换，保留原生模型名称"
                : "独立 API 出口",
            SectionOrder = pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount ? 0 : 1,
            StatusTitle = pool.Enabled ? snapshot.StatusTitle : "已停用",
            StatusDetail = pool.Enabled ? snapshot.StatusDetail : "号池配置与凭据都已保留，可随时恢复。",
            EndpointText = snapshot.Endpoint,
            AccountCountText = $"{snapshot.Accounts.Count} 个账号",
            ModelCountText = $"{snapshot.Models.Count} 个模型",
            LastCheckedText = $"最近检查 {snapshot.CheckedAt:HH:mm:ss}",
            IsActive = active.PoolId.Equals(pool.Id, StringComparison.OrdinalIgnoreCase),
            IsProtected = pool.IsProtected,
            Enabled = pool.Enabled,
            CanSwitch = pool.Enabled && snapshot.Ready,
            CanAddAccount = pool.Enabled && pool.Transport == PoolTransport.CliProxyApi && snapshot.Accounts.Count == 0,
            CanConfigure = false,
            CanTogglePool = !pool.IsProtected
                            && pool.Transport is not PoolTransport.OfficialCodex
                            and not PoolTransport.NativeCodexAccount,
            CanSelectModel = pool.Enabled && snapshot.Models.Count > 1,
            NewTasksOnly = false,
            ModelSelectionHint = pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
                ? "这是 Codex 官方原生模型列表；切换后各任务从下一条消息使用所选账号和模型"
                : "模型列表来自这个外部 API，不会跨号池选择",
            SelectedModel = SelectViewModel(pool, active, snapshot.Models),
            AddAccountText = pool.Transport == PoolTransport.CliProxyApi
                ? snapshot.Accounts.Count == 0 ? "添加唯一账号" : "一个出口限一个账号"
                : "原生账号",
            ConfigureText = "无需配置"
        };
        foreach (var account in snapshot.Accounts) view.Accounts.Add(account);
        foreach (var model in snapshot.Models.Distinct(StringComparer.OrdinalIgnoreCase)) view.Models.Add(model);
        return view;
    }

    private PoolBackendSnapshot ReadNativeCodexAccount(PoolDefinition pool, NativeCodexSnapshot native)
    {
        var account = ResolveNativeAccount(pool, native.Accounts);
        if (account is null)
            return new PoolBackendSnapshot(false, "账号尚未同步", native.Error ?? "没有找到对应的 Codex 原生账号。",
                "OpenAI 原生账号", Array.Empty<PoolAccountView>(), native.Models, DateTimeOffset.Now)
            { AccountRosterCompleteness = native.RosterCompleteness };
        var healthy = account.HasCredential && !account.NeedsReauth
                      && account.HealthStatus.Equals("healthy", StringComparison.OrdinalIgnoreCase);
        var poolMode = native.Settings.Mode.Equals("pool", StringComparison.OrdinalIgnoreCase);
        var directMode = native.Settings.Mode.Equals("direct", StringComparison.OrdinalIgnoreCase);
        var activeHere = poolMode
            ? string.Equals(native.Settings.ActiveAccountId, account.Id, StringComparison.OrdinalIgnoreCase)
            : directMode && account.IsMain;
        var routing = poolMode
            ? activeHere
                ? native.Settings.AutoSwitchThreshold == 0 && native.Settings.FailoverThreshold == 0
                    ? "Codex 全局线路已固定到这个账号；自动切号和故障串池均已关闭。真实扣费账号以最近请求记录为准。"
                    : $"当前线路优先使用这个账号，但自动切号阈值为 {native.Settings.AutoSwitchThreshold}%，故障切号阈值为 {native.Settings.FailoverThreshold} 次。"
                : "账号已登录；切换后各任务会在下一条消息重新绑定到这个账号。"
            : account.IsMain
                ? "当前为官方 Pro 直通模式；下一条请求继续使用 Pro 主账号。"
                : "当前为官方 Pro 直通模式；这个账号尚未生效。点切换后各任务下一条会改用它。";
        var accountView = new PoolAccountView
        {
            PoolId = pool.Id,
            RuntimeProviderId = "openai",
            RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.NativeOpenCodex,
            Id = account.Id,
            Label = account.IsMain ? "Codex Pro 主账号" : $"Codex {account.PlanText}账号",
            Detail = $"{account.Email} · {account.UsageText}",
            Status = $"{account.HealthText}{(activeHere ? " · 当前请求线路" : string.Empty)}",
            Enabled = true,
            CanToggle = !account.IsMain,
            IsDestructiveAction = !account.IsMain,
            ActionText = account.IsMain ? string.Empty : "删除账号",
            ProtectionText = account.IsMain ? "Pro 主账号禁止删除" : string.Empty,
            QuotaWindows = account.QuotaWindows,
            QuotaProvenance = AccountQuotaProvenance.RelayReported,
            QuotaAvailability = account.QuotaWindows.Count > 0
                ? AccountQuotaAvailability.Provided
                : AccountQuotaAvailability.NotProvided,
            QuotaNote = account.QuotaWindows.Count == 0
                ? "OpenAI 当前没有为这个账号返回可显示的套餐额度；账号健康状态仍会独立检查。"
                : account.ResetCredits is > 0 ? $"可用额度重置次数：{account.ResetCredits}" : string.Empty,
            UsageSourceText = account.QuotaUpdatedTime is null
                ? "OpenCodex 按账号读取 ChatGPT 官方额度 · 官方汇总值，可能延迟更新"
                : $"OpenCodex 按账号读取 ChatGPT 官方额度 · 更新 {account.QuotaUpdatedTime:MM-dd HH:mm:ss} · 官方汇总值，可能延迟更新",
            UsageUpdatedAt = account.QuotaUpdatedTime
        };
        return new PoolBackendSnapshot(
            healthy,
            healthy
                ? activeHere ? "当前请求线路已设置" : directMode && !account.IsMain ? "尚未应用" : account.IsMain ? "官方保底可用" : "原生账号可用"
                : "账号需要处理",
            routing,
            "OpenAI 原生账号",
            new[] { accountView },
            native.Models,
            DateTimeOffset.Now)
        {
            AccountRosterCompleteness = native.RosterCompleteness
        };
    }

    private static string? ResolveRuntimeProviderId(PoolDefinition pool) =>
        pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
            ? "openai"
            : string.IsNullOrWhiteSpace(pool.ProviderId) ? null : pool.ProviderId;

    private static RuntimeProviderIdentitySource ResolveRuntimeProviderIdentitySource(PoolDefinition pool) =>
        pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
            ? RuntimeProviderIdentitySource.NativeOpenCodex
            : string.IsNullOrWhiteSpace(pool.ProviderId)
                ? RuntimeProviderIdentitySource.Unknown
                : RuntimeProviderIdentitySource.PoolDefinitionProviderId;

    private async Task<NativeCodexSnapshot> ReadNativeCodexSnapshotAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<CodexAccountView> Accounts, CodexPoolSettings Settings, AccountRosterCompleteness RosterCompleteness) accounts;
        try
        {
            accounts = await _openCodex.GetCodexAccountsAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            return new NativeCodexSnapshot(Array.Empty<CodexAccountView>(), new CodexPoolSettings(null, 80, 3, "unknown"),
                Array.Empty<string>(), ex.Message, AccountRosterCompleteness.ReadFailed);
        }

        try
        {
            var models = (await _openCodex.GetModelsAsync(_settings, cancellationToken))
                .Where(model => model.IsOfficial && !model.Disabled)
                .Select(model => model.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(NativeModelRank)
                .ThenBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new NativeCodexSnapshot(accounts.Accounts, accounts.Settings, models, null, accounts.RosterCompleteness);
        }
        catch (Exception ex)
        {
            return new NativeCodexSnapshot(accounts.Accounts, accounts.Settings, Array.Empty<string>(),
                "模型目录读取失败：" + ex.GetType().Name, accounts.RosterCompleteness);
        }
    }

    private async Task<NativeCodexSnapshot> ReadNativeCodexSnapshotCachedAsync(CancellationToken cancellationToken)
    {
        lock (_nativeCacheGate)
        {
            if (_nativeSnapshotCache is not null
                && DateTimeOffset.UtcNow - _nativeSnapshotCachedAt <= NativeSnapshotCacheLifetime)
                return _nativeSnapshotCache;
        }

        await _nativeReadGate.WaitAsync(cancellationToken);
        try
        {
            lock (_nativeCacheGate)
            {
                if (_nativeSnapshotCache is not null
                    && DateTimeOffset.UtcNow - _nativeSnapshotCachedAt <= NativeSnapshotCacheLifetime)
                    return _nativeSnapshotCache;
            }

            var snapshot = await ReadNativeCodexSnapshotAsync(cancellationToken);
            lock (_nativeCacheGate)
            {
                _nativeSnapshotCache = snapshot;
                _nativeSnapshotCachedAt = DateTimeOffset.UtcNow;
            }
            return snapshot;
        }
        finally
        {
            _nativeReadGate.Release();
        }
    }

    private static ActivePoolState ResolveEffectiveActive(
        ActivePoolState catalogActive,
        IReadOnlyList<PoolDefinition> pools,
        NativeCodexSnapshot native)
    {
        var configured = pools.FirstOrDefault(pool =>
            pool.Id.Equals(catalogActive.PoolId, StringComparison.OrdinalIgnoreCase));
        if (configured?.Transport is not PoolTransport.OfficialCodex
            and not PoolTransport.NativeCodexAccount)
            return catalogActive;

        string? effectiveAccountId = null;
        if (native.Settings.Mode.Equals("direct", StringComparison.OrdinalIgnoreCase))
            effectiveAccountId = native.Accounts.FirstOrDefault(account => account.IsMain)?.Id;
        else if (native.Settings.Mode.Equals("pool", StringComparison.OrdinalIgnoreCase))
            effectiveAccountId = native.Settings.ActiveAccountId;

        var effectivePool = string.IsNullOrWhiteSpace(effectiveAccountId)
            ? null
            : pools.FirstOrDefault(pool =>
                string.Equals(pool.NativeAccountId, effectiveAccountId, StringComparison.OrdinalIgnoreCase));
        return effectivePool is null
            ? catalogActive
            : new ActivePoolState
            {
                PoolId = effectivePool.Id,
                Model = effectivePool.DefaultModel,
                SwitchedAt = catalogActive.SwitchedAt,
                Verification = effectivePool.Id.Equals(catalogActive.PoolId, StringComparison.OrdinalIgnoreCase)
                               && catalogActive.Verification.StartsWith("native-account-", StringComparison.OrdinalIgnoreCase)
                    ? catalogActive.Verification
                    : native.Settings.Mode.Equals("pool", StringComparison.OrdinalIgnoreCase)
                        ? "native-account-current-line"
                        : "official-direct"
            };
    }

    private static CodexAccountView? ResolveNativeAccount(
        PoolDefinition pool,
        IReadOnlyList<CodexAccountView> accounts)
    {
        if (!string.IsNullOrWhiteSpace(pool.NativeAccountId))
            return accounts.FirstOrDefault(account => account.Id.Equals(pool.NativeAccountId, StringComparison.OrdinalIgnoreCase));
        if (pool.Transport == PoolTransport.OfficialCodex)
            return accounts.FirstOrDefault(account => account.IsMain) ?? accounts.FirstOrDefault();
        return accounts.FirstOrDefault(account => !account.IsMain
                                                  && (pool.Product == AccountProduct.CodexPro
                                                      ? account.Plan?.Contains("pro", StringComparison.OrdinalIgnoreCase) == true
                                                      : account.Plan?.Contains("plus", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static int NativeModelRank(string model) => model.ToLowerInvariant() switch
    {
        "gpt-5.6-sol" => 0,
        "gpt-5.6-terra" => 1,
        "gpt-5.6-luna" => 2,
        "gpt-5.5" => 3,
        "gpt-5.4" => 4,
        "gpt-5.4-mini" => 5,
        _ => 100
    };

    private PoolDefinition RequirePool(string id, PoolTransport expected)
    {
        var pool = _catalog.Find(id) ?? throw new InvalidOperationException("号池不存在。");
        if (pool.Transport != expected) throw new InvalidOperationException("号池类型不匹配。");
        return pool;
    }

    private static string ChooseModel(string preferred, IReadOnlyList<string> models) =>
        models.FirstOrDefault(model => model.Equals(preferred, StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(model => model.Contains("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault(model => model.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase))
        ?? models.First();

    private static string ResolveModel(
        string? requested,
        string preferred,
        IReadOnlyList<string> models)
    {
        if (models.Count == 0) throw new InvalidOperationException("号池没有返回可用模型。");
        if (string.IsNullOrWhiteSpace(requested)) return ChooseModel(preferred, models);
        return models.FirstOrDefault(model => model.Equals(requested, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"所选模型 {requested} 已不在这个号池的实时模型目录中，请刷新后重选。");
    }

    private static string SelectViewModel(
        PoolDefinition pool,
        ActivePoolState active,
        IReadOnlyList<string> models)
    {
        if (models.Count == 0) return string.Empty;
        var preferred = active.PoolId.Equals(pool.Id, StringComparison.OrdinalIgnoreCase)
            ? active.Model
            : pool.DefaultModel;
        return models.FirstOrDefault(model => model.Equals(preferred, StringComparison.OrdinalIgnoreCase))
               ?? ChooseModel(pool.DefaultModel, models);
    }

    private static string TypeText(PoolDefinition pool) => (pool.Transport, pool.Product) switch
    {
        (PoolTransport.OfficialCodex, AccountProduct.CodexPro) => "原生 Codex · Pro · 主账号受保护",
        (PoolTransport.NativeCodexAccount, AccountProduct.CodexPlus) => "原生 Codex · Plus · 全局线路可切换",
        (PoolTransport.NativeCodexAccount, AccountProduct.CodexPro) => "原生 Codex · Pro · 全局线路可切换",
        (PoolTransport.CliProxyApi, AccountProduct.CodexPlus) => "本机独立账号出口 · Plus · 一处一账号",
        (PoolTransport.CliProxyApi, AccountProduct.CodexPro) => "本机独立账号出口 · Pro · 一处一账号",
        _ => "OpenAI 兼容 API"
    };
}
