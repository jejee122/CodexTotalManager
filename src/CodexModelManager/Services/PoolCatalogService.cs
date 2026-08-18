using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Models;
using CodexOpenCodexNative.Providers;

namespace CodexModelManager.Services;

public sealed class PoolCatalogService
{
    public const int CurrentSchemaVersion = 4;

    private readonly string _directory;
    private readonly string _path;
    private readonly HashSet<int> _reservedPorts;
    private readonly object _gate = new();
    private PoolCatalogDocument _catalog;
    private string _loadedFingerprint;

    public string? LoadWarning { get; private set; }
    public string FilePath => _path;

    public static bool IsSafeCliPoolId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && char.IsAsciiLetterOrDigit(value[0])
        && !value.EndsWith(".", StringComparison.Ordinal)
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string ExpectedCliProviderId(string poolId) => $"cmm-{poolId}";

    public static bool IsManagerOwnedProviderId(string? providerId) =>
        !string.IsNullOrWhiteSpace(providerId)
        && providerId.StartsWith("cmm-", StringComparison.OrdinalIgnoreCase);

    public static bool IsSafeCliProviderBinding(PoolDefinition pool) =>
        IsSafeCliPoolId(pool.Id)
        && !pool.Id.Equals(PoolCatalogDefaults.OfficialPoolId, StringComparison.OrdinalIgnoreCase)
        && !pool.Id.Equals(PoolCatalogDefaults.PlusPoolId, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(pool.ProviderId)
        && string.Equals(pool.ProviderId, ExpectedCliProviderId(pool.Id), StringComparison.Ordinal)
        && !pool.ProviderId.StartsWith("internal:", StringComparison.OrdinalIgnoreCase);

    public static bool IsSafeCliPortBinding(
        PoolDefinition pool,
        bool allowLegacyReservedPortMigration = false,
        IEnumerable<int>? reservedPorts = null)
    {
        var port = pool.LocalPort;
        if (port is < 1024 or > 65535) return false;
        return allowLegacyReservedPortMigration
               || LocalPortPolicy.IsCliProxyPortAllowed(port.GetValueOrDefault(), reservedPorts);
    }

    public static bool IsExactCliEndpoint(PoolDefinition pool) =>
        pool.LocalPort is >= 1024 and <= 65535
        && string.Equals(
            pool.BaseUrl,
            $"http://127.0.0.1:{pool.LocalPort}/v1",
            StringComparison.Ordinal);

    public static string BuildCliBaseUrl(PoolDefinition pool)
    {
        if (!IsSafeCliPortBinding(pool)
            || !IsSafeCliProviderBinding(pool))
            throw new InvalidOperationException("CLIProxyAPI 端点、内建端口或凭据槽身份不安全。");
        return $"http://127.0.0.1:{pool.LocalPort}/v1";
    }

    public PoolCatalogService(string directory, IEnumerable<int>? reservedPorts = null)
    {
        _directory = directory;
        _path = Path.Combine(directory, "pools.json");
        _reservedPorts = reservedPorts is null
            ? new HashSet<int>
            {
                LocalPortPolicy.DefaultNativeEnginePort,
                LocalPortPolicy.DefaultUnifiedGatewayPort
            }
            : new HashSet<int>(reservedPorts);
        Directory.CreateDirectory(directory);
        _catalog = Load();
        _loadedFingerprint = LocalFileTransaction.Fingerprint(_path);
        if (LoadWarning is not null)
        {
            _catalog = CreateSafeFallbackCatalog();
            return;
        }
        EnsureDefaults();
    }

    public IReadOnlyList<PoolDefinition> GetPools()
    {
        lock (_gate)
            return _catalog.Pools.Select(Clone).ToArray();
    }

    public void UpdateReservedPorts(IEnumerable<int> reservedPorts)
    {
        ArgumentNullException.ThrowIfNull(reservedPorts);
        var replacement = reservedPorts.ToHashSet();
        if (replacement.Any(port => !LocalPortPolicy.IsUserPort(port)))
            throw new InvalidOperationException("核心服务保留端口超出允许范围。");
        lock (_gate)
        {
            _reservedPorts.Clear();
            _reservedPorts.UnionWith(replacement);
        }
    }

    public IReadOnlyList<PoolDefinition> GetPoolsFresh()
    {
        lock (_gate)
            return ReadFreshSnapshot().Pools.Select(Clone).ToArray();
    }

    public IReadOnlyList<PoolDefinition> GetPoolsFreshForDiscovery()
    {
        lock (_gate)
            return ReadFreshSnapshotFile(_path, validatePools: false).Pools.Select(Clone).ToArray();
    }

    public PoolDefinition? Find(string id)
    {
        lock (_gate)
        {
            var pool = _catalog.Pools.FirstOrDefault(item =>
                item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return pool is null ? null : Clone(pool);
        }
    }

    public PoolDefinition? FindFresh(string id)
    {
        lock (_gate)
        {
            var pool = ReadFreshSnapshot().Pools.FirstOrDefault(item =>
                item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return pool is null ? null : Clone(pool);
        }
    }

    public ActivePoolState GetActive()
    {
        lock (_gate)
            return Clone(_catalog.Active);
    }

    public void SetActive(string poolId, string model, string verification)
    {
        lock (_gate)
        {
            EnsureWritable();
            var pool = RequirePool(poolId);
            if (!pool.Enabled) throw new InvalidOperationException("号池已停用，不能切换。");
            pool.DefaultModel = model;
            _catalog.Active = new ActivePoolState
            {
                PoolId = pool.Id,
                Model = model,
                SwitchedAt = DateTimeOffset.Now,
                Verification = verification
            };
            Save();
        }
    }

    public void RestoreActive(ActivePoolState state)
    {
        lock (_gate)
        {
            EnsureWritable();
            var pool = RequirePool(state.PoolId);
            pool.DefaultModel = state.Model;
            _catalog.Active = Clone(state);
            Save();
        }
    }

    public PoolDefinition AddCliProxyPool(AccountProduct product)
    {
        if (product is not AccountProduct.CodexPlus and not AccountProduct.CodexPro)
            throw new ArgumentOutOfRangeException(nameof(product));
        lock (_gate)
        {
            EnsureWritable();
            var number = 1;
            var stem = product == AccountProduct.CodexPlus ? "plus-api" : "pro-api";
            while (_catalog.Pools.Any(item => item.Id.Equals($"{stem}-{number}", StringComparison.OrdinalIgnoreCase)))
                number++;
            var id = $"{stem}-{number}";
            var port = AllocateCliPort(id);
            var label = product == AccountProduct.CodexPlus ? "Plus" : "Pro";
            var pool = new PoolDefinition
            {
                Id = id,
                DisplayName = $"Codex {label} 独立出口 {number}",
                Description = $"一个独立 CLIProxyAPI 出口只放一个 {label} 账号，不与其他账号串用。",
                Transport = PoolTransport.CliProxyApi,
                Product = product,
                IsProtected = false,
                Enabled = true,
                RouteAlias = $"{InternalRouteNames.Prefix}{(product == AccountProduct.CodexPlus ? "plus" : "pro")}-{number}",
                ProviderId = ExpectedCliProviderId(id),
                DefaultModel = "gpt-5.6-sol",
                BaseUrl = $"http://127.0.0.1:{port}/v1",
                LocalPort = port
            };
            Validate(pool, ignoreId: null);
            _catalog.Pools.Add(pool);
            Save();
            return Clone(pool);
        }
    }

    public void SyncNativeCodexAccounts(
        IReadOnlyList<CodexAccountView> accounts,
        bool addMissing = true)
    {
        if (accounts.Count == 0) return;
        lock (_gate)
        {
            EnsureWritable();
            var changed = false;
            var main = accounts.FirstOrDefault(account => account.IsMain) ?? accounts[0];
            var official = RequirePool(PoolCatalogDefaults.OfficialPoolId);
            changed |= BindNativeAccount(official, main, isOfficial: true);

            var secondary = accounts.Where(account => !account.Id.Equals(main.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            var legacyPlus = RequirePool(PoolCatalogDefaults.PlusPoolId);
            var legacyBoundAccount = secondary.FirstOrDefault(account =>
                string.Equals(account.Id, legacyPlus.NativeAccountId, StringComparison.OrdinalIgnoreCase));
            var preferredPlus = secondary.FirstOrDefault(account =>
                                    account.Plan?.Contains("plus", StringComparison.OrdinalIgnoreCase) == true)
                                ?? secondary.FirstOrDefault();
            if (legacyBoundAccount is not null)
                changed |= BindNativeAccount(legacyPlus, legacyBoundAccount, isOfficial: false);
            else if (addMissing && preferredPlus is not null)
                changed |= BindNativeAccount(legacyPlus, preferredPlus, isOfficial: false);

            foreach (var account in addMissing ? secondary : Array.Empty<CodexAccountView>())
            {
                if (_catalog.Pools.Any(pool =>
                        string.Equals(pool.NativeAccountId, account.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var product = ProductFor(account);
                var id = $"codex-account-{StableSuffix(account.Id)}";
                var label = product == AccountProduct.CodexPro ? "Pro" : "Plus";
                var number = _catalog.Pools.Count(pool => pool.Transport == PoolTransport.NativeCodexAccount
                                                         && pool.Product == product) + 1;
                _catalog.Pools.Add(new PoolDefinition
                {
                    Id = id,
                    DisplayName = $"Codex {label} 账号 {number}",
                    Description = "Codex 原生账号。切换后全局线路固定到这个扣费账号，各任务从下一条消息生效，模型名称和聊天上下文保持不变。",
                    Transport = PoolTransport.NativeCodexAccount,
                    Product = product,
                    Enabled = true,
                    NativeAccountId = account.Id,
                    DefaultModel = "gpt-5.6-sol",
                    BaseUrl = "OpenAI 原生账号"
                });
                changed = true;
            }

            if (changed) Save();
        }
    }

    public void RemoveNativeCodexAccountPool(string poolId, string accountId)
    {
        lock (_gate)
        {
            EnsureWritable();
            var pool = RequirePool(poolId);
            if (pool.IsProtected || pool.Transport == PoolTransport.OfficialCodex)
                throw new InvalidOperationException("Pro 主账号受保护，禁止删除。");
            if (pool.Transport != PoolTransport.NativeCodexAccount)
                throw new InvalidOperationException("这个卡片不是 Codex 原生账号。");
            if (!string.Equals(pool.NativeAccountId, accountId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("账号卡片与总管家本机引擎账号不匹配，已停止删除。");
            if (string.Equals(_catalog.Active.PoolId, pool.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("正在使用的账号不能删除，请先切到其他账号。");

            if (pool.Id.Equals(PoolCatalogDefaults.PlusPoolId, StringComparison.OrdinalIgnoreCase))
            {
                pool.NativeAccountId = null;
                pool.Enabled = false;
                pool.DisplayName = "Codex Plus 账号";
                pool.Description = "尚未绑定真实 Codex 账号；绑定成功前保持停用，不会参与切换或自动路由。";
            }
            else
            {
                _catalog.Pools.Remove(pool);
            }
            Save();
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            EnsureWritable();
            var pool = RequirePool(id);
            if (pool.IsProtected && !enabled)
                throw new InvalidOperationException("官方 Pro 保底号池不能停用。");
            if (enabled
                && pool.Transport == PoolTransport.NativeCodexAccount
                && string.IsNullOrWhiteSpace(pool.NativeAccountId))
                throw new InvalidOperationException("原生 Codex 账号尚未绑定，不能启用。");
            if (!enabled && string.Equals(_catalog.Active.PoolId, pool.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先切到其他号池，再停用当前号池。");
            pool.Enabled = enabled;
            Save();
        }
    }

    public void RemoveCliProxyPool(string id)
    {
        lock (_gate)
        {
            EnsureWritable();
            var pool = RequirePool(id);
            if (pool.Transport != PoolTransport.CliProxyApi || pool.IsProtected)
                throw new InvalidOperationException("只能删除用户创建的独立 CLIProxy 号池。");
            if (string.Equals(_catalog.Active.PoolId, pool.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先切到其他号池，再删除当前号池。");
            _catalog.Pools.Remove(pool);
            Save();
        }
    }

    private bool MigrateLegacyRouteNames()
    {
        if (_catalog.SchemaVersion > CurrentSchemaVersion) return false;
        var changed = false;
        if (_catalog.Pools is not null)
        {
            foreach (var pool in _catalog.Pools)
            {
                if (pool is null
                    || !InternalRouteNames.TryMigrateLegacyAlias(pool.RouteAlias, out var migratedAlias))
                    continue;
                pool.RouteAlias = migratedAlias;
                changed = true;
            }
        }
        if (_catalog.Active is not null
            && InternalRouteNames.TryMigrateLegacyAlias(_catalog.Active.Model, out var migratedModel))
        {
            _catalog.Active.Model = migratedModel;
            changed = true;
        }
        return changed;
    }

    private void EnsureDefaults()
    {
        lock (_gate)
        {
            var changed = MigrateLegacyRouteNames();
            try
            {
                ValidateLoadedCatalog(allowLegacyReservedPortMigration: true);
            }
            catch (InvalidOperationException ex)
            {
                EnterReadOnlyFallback(ex);
                return;
            }
            foreach (var cliPool in _catalog.Pools.Where(pool =>
                         pool.Transport == PoolTransport.CliProxyApi
                         && pool.LocalPort is >= 1024 and <= 65535))
            {
                var canonical = $"http://127.0.0.1:{cliPool.LocalPort}/v1";
                if (string.Equals(cliPool.BaseUrl, canonical, StringComparison.Ordinal)) continue;
                cliPool.BaseUrl = canonical;
                changed = true;
            }
            if (_catalog.Pools.All(item =>
                    !string.Equals(item.Id, PoolCatalogDefaults.OfficialPoolId, StringComparison.OrdinalIgnoreCase)))
            {
                _catalog.Pools.Insert(0, new PoolDefinition
                {
                    Id = PoolCatalogDefaults.OfficialPoolId,
                    DisplayName = "当前 Codex Pro（官方保底）",
                    Description = "当前正在使用的官方 Codex 主账号。永远单独保留，不加入任何自动号池。",
                    Transport = PoolTransport.OfficialCodex,
                    Product = AccountProduct.CodexPro,
                    IsProtected = true,
                    Enabled = true,
                    DefaultModel = "gpt-5.6-sol",
                    BaseUrl = "OpenAI 官方直连"
                });
                changed = true;
            }
            if (_catalog.Pools.All(item =>
                    !string.Equals(item.Id, PoolCatalogDefaults.PlusPoolId, StringComparison.OrdinalIgnoreCase)))
            {
                _catalog.Pools.Add(new PoolDefinition
                {
                    Id = PoolCatalogDefaults.PlusPoolId,
                    DisplayName = "Codex Plus 账号",
                    Description = "Codex 原生 Plus 账号。切换后全局线路固定由它扣费，各任务从下一条请求生效，仍可选择 Sol、Terra、Luna 等原生模型。",
                    Transport = PoolTransport.NativeCodexAccount,
                    Product = AccountProduct.CodexPlus,
                    Enabled = false,
                    DefaultModel = "gpt-5.6-sol",
                    BaseUrl = "OpenAI 原生账号"
                });
                changed = true;
            }
            var official = _catalog.Pools.First(item =>
                string.Equals(item.Id, PoolCatalogDefaults.OfficialPoolId, StringComparison.OrdinalIgnoreCase));
            official.IsProtected = true;
            official.Enabled = true;
            official.DisplayName = "Codex Pro 主账号（官方保底）";
            official.Description = "官方 Codex Pro 主账号。可把全局线路切回 Pro 扣费，各任务从下一条请求生效，不与 Plus 自动串池，且禁止删除。";
            official.BaseUrl = "OpenAI 原生账号";
            var plus = _catalog.Pools.First(item =>
                string.Equals(item.Id, PoolCatalogDefaults.PlusPoolId, StringComparison.OrdinalIgnoreCase));
            if (plus.Transport == PoolTransport.CliProxyApi)
            {
                plus.DisplayName = "Codex Plus 账号";
                plus.Description = "Codex 原生 Plus 账号。切换后由新建任务使用它扣费，继续使用原生模型名称。";
                plus.Transport = PoolTransport.NativeCodexAccount;
                plus.RouteAlias = null;
                plus.ProviderId = null;
                plus.LocalPort = null;
                plus.BaseUrl = "OpenAI 原生账号";
                changed = true;
            }
            if (plus.Transport == PoolTransport.NativeCodexAccount
                && string.IsNullOrWhiteSpace(plus.NativeAccountId))
            {
                if (plus.Enabled) { plus.Enabled = false; changed = true; }
                const string unboundDescription = "尚未绑定真实 Codex 账号；绑定成功前保持停用，不会参与切换或自动路由。";
                if (plus.Description != unboundDescription) { plus.Description = unboundDescription; changed = true; }
                if (string.Equals(_catalog.Active.PoolId, plus.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _catalog.Active = new ActivePoolState();
                    changed = true;
                }
            }
            changed |= EnsureAgentPool(
                PoolCatalogDefaults.PlusAgentPoolId,
                "Codex Plus Agent API",
                AccountProduct.CodexPlus,
                InternalRouteNames.Prefix + "agent-plus-1");
            changed |= EnsureAgentPool(
                PoolCatalogDefaults.ProAgentPoolId,
                "Codex Pro Agent API",
                AccountProduct.CodexPro,
                InternalRouteNames.Prefix + "agent-pro-1");
            if (_catalog.SchemaVersion < CurrentSchemaVersion)
            {
                _catalog.SchemaVersion = CurrentSchemaVersion;
                changed = true;
            }
            if (_catalog.Pools.All(item =>
                    !string.Equals(item.Id, _catalog.Active.PoolId, StringComparison.OrdinalIgnoreCase)))
            {
                _catalog.Active = new ActivePoolState();
                changed = true;
            }
            foreach (var cliPool in _catalog.Pools.Where(pool =>
                         pool.Transport == PoolTransport.CliProxyApi
                         && pool.LocalPort is not null
                         && !IsSafeCliPortBinding(pool, reservedPorts: _reservedPorts)))
            {
                var replacement = AllocateCliPort(cliPool.Id, cliPool.Id);
                cliPool.LocalPort = replacement;
                cliPool.BaseUrl = $"http://127.0.0.1:{replacement}/v1";
                changed = true;
            }
            try
            {
                ValidateLoadedCatalog(allowLegacyReservedPortMigration: false);
            }
            catch (InvalidOperationException ex)
            {
                EnterReadOnlyFallback(ex);
                return;
            }
            if (changed && LoadWarning is null) Save();
        }
    }

    private void ValidateLoadedCatalog(bool allowLegacyReservedPortMigration)
    {
        if (_catalog.SchemaVersion is < 1 or > CurrentSchemaVersion)
            throw new InvalidOperationException($"不支持的号池清单版本：{_catalog.SchemaVersion}。");
        if (_catalog.Pools is null)
            throw new InvalidOperationException("号池列表为空对象。");
        if (_catalog.Active is null)
            throw new InvalidOperationException("当前号池状态为空对象。");
        if (_catalog.Pools.Any(pool => pool is null))
            throw new InvalidOperationException("号池列表包含空项目。");
        if (string.IsNullOrWhiteSpace(_catalog.Active.PoolId)
            || string.IsNullOrWhiteSpace(_catalog.Active.Model))
            throw new InvalidOperationException("当前号池 ID 或模型为空。");
        if (_catalog.Pools.Any(pool => string.IsNullOrWhiteSpace(pool.Id)
                                       || string.IsNullOrWhiteSpace(pool.DisplayName)))
            throw new InvalidOperationException("号池列表包含缺少 ID 或名称的项目。");

        var duplicateIds = _catalog.Pools
            .GroupBy(pool => pool.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException($"号池清单存在重复 ID：{string.Join("、", duplicateIds)}");
        var duplicateProviders = _catalog.Pools
            .Where(pool => !string.IsNullOrWhiteSpace(pool.ProviderId))
            .GroupBy(pool => pool.ProviderId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateProviders.Length > 0)
            throw new InvalidOperationException($"号池清单存在重复凭据槽：{string.Join("、", duplicateProviders)}");
        var duplicatePorts = _catalog.Pools
            .Where(pool => pool.Transport == PoolTransport.CliProxyApi && pool.LocalPort is not null)
            .GroupBy(pool => pool.LocalPort!.Value)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePorts.Length > 0)
            throw new InvalidOperationException($"CLIProxy 号池存在重复本机端口：{string.Join("、", duplicatePorts)}");

        foreach (var pool in _catalog.Pools)
            Validate(pool, pool.Id, allowLegacyReservedPortMigration);
    }

    private void EnterReadOnlyFallback(Exception exception)
    {
        LoadWarning = $"号池清单包含不安全或不兼容的来源，已保留原文件并停止写入：{exception.Message}";
        _catalog = CreateSafeFallbackCatalog();
    }

    private static PoolCatalogDocument CreateSafeFallbackCatalog() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Pools = new List<PoolDefinition>
        {
            new()
            {
                Id = PoolCatalogDefaults.OfficialPoolId,
                DisplayName = "Codex Pro 主账号（官方保底）",
                Description = "只读安全回退视图；原号池文件未被覆盖。",
                Transport = PoolTransport.OfficialCodex,
                Product = AccountProduct.CodexPro,
                IsProtected = true,
                Enabled = true,
                DefaultModel = "gpt-5.6-sol",
                BaseUrl = "OpenAI 原生账号"
            },
            new()
            {
                Id = PoolCatalogDefaults.PlusPoolId,
                DisplayName = "Codex Plus 账号",
                Description = "只读安全回退视图；修复号池清单后才可切换。",
                Transport = PoolTransport.NativeCodexAccount,
                Product = AccountProduct.CodexPlus,
                Enabled = false,
                DefaultModel = "gpt-5.6-sol",
                BaseUrl = "OpenAI 原生账号"
            }
        },
        Active = new ActivePoolState()
    };

    private PoolCatalogDocument Load()
    {
        if (!File.Exists(_path)) return new PoolCatalogDocument();
        try
        {
            return JsonSerializer.Deserialize<PoolCatalogDocument>(File.ReadAllText(_path), JsonOptions)
                   ?? new PoolCatalogDocument();
        }
        catch
        {
            try
            {
                var backup = Path.Combine(_directory, $"pools.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
                File.Copy(_path, backup, false);
            }
            catch { }
            LoadWarning = "号池清单已损坏，已保留原文件并停止写入。";
            return new PoolCatalogDocument();
        }
    }

    private PoolCatalogDocument ReadFreshSnapshot() => ReadFreshSnapshotFile(_path, validatePools: true);

    public static PoolDefinition? FindFreshInDirectory(string directory, string id)
    {
        var path = Path.Combine(directory, "pools.json");
        var snapshot = ReadFreshSnapshotFile(path, validatePools: true);
        var pool = snapshot.Pools.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return pool is null ? null : Clone(pool);
    }

    private static PoolCatalogDocument ReadFreshSnapshotFile(string path, bool validatePools)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("号池清单文件不存在；已按失败关闭，未访问任何来源。");
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var snapshot = JsonSerializer.Deserialize<PoolCatalogDocument>(stream, JsonOptions)
                           ?? throw new InvalidDataException("号池清单内容为空。");
            if (snapshot.SchemaVersion is < 1 or > CurrentSchemaVersion)
                throw new InvalidDataException($"不支持的号池清单版本：{snapshot.SchemaVersion}。");
            if (snapshot.Pools is null || snapshot.Pools.Count == 0)
                throw new InvalidDataException("号池清单没有任何来源。");
            if (snapshot.Active is null
                || string.IsNullOrWhiteSpace(snapshot.Active.PoolId)
                || string.IsNullOrWhiteSpace(snapshot.Active.Model))
                throw new InvalidDataException("当前号池 ID 或模型为空。");
            if (snapshot.Pools.Any(pool => pool is null
                                           || string.IsNullOrWhiteSpace(pool.Id)
                                           || string.IsNullOrWhiteSpace(pool.DisplayName)))
                throw new InvalidDataException("号池清单包含空项目或缺少 ID/名称的项目。");
            var duplicateIds = snapshot.Pools
                .GroupBy(pool => pool.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() != 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateIds.Length > 0)
                throw new InvalidDataException($"号池清单存在重复 ID：{string.Join("、", duplicateIds)}");
            var duplicateProviders = snapshot.Pools
                .Where(pool => !string.IsNullOrWhiteSpace(pool.ProviderId))
                .GroupBy(pool => pool.ProviderId!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() != 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateProviders.Length > 0)
                throw new InvalidDataException($"号池清单存在重复凭据槽：{string.Join("、", duplicateProviders)}");
            var duplicatePorts = snapshot.Pools
                .Where(pool => pool.Transport == PoolTransport.CliProxyApi && pool.LocalPort is not null)
                .GroupBy(pool => pool.LocalPort!.Value)
                .Where(group => group.Count() != 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicatePorts.Length > 0)
                throw new InvalidDataException($"CLIProxy 号池存在重复本机端口：{string.Join("、", duplicatePorts)}");
            if (snapshot.Pools.Any(pool => pool.Transport == PoolTransport.CliProxyApi
                                           && !IsSafeCliPortBinding(pool)))
                throw new InvalidDataException("动态 CLIProxy 号池占用了内建 Agent API 端口或内建 ID/端口不匹配。");
            if (validatePools)
                foreach (var pool in snapshot.Pools) ValidateFreshPool(pool);
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or InvalidDataException)
        {
            throw new InvalidOperationException(
                $"无法重新验证最新号池清单；已按失败关闭且不会访问来源：{ex.Message}", ex);
        }
    }

    private static void ValidateFreshPool(PoolDefinition pool)
    {
        if (string.IsNullOrWhiteSpace(pool.Id) || string.IsNullOrWhiteSpace(pool.DisplayName))
            throw new InvalidDataException("号池 ID 或名称为空。");
        if (!HasValidReservedIdentity(pool))
            throw new InvalidDataException($"保留号池 {pool.Id} 的类型或凭据槽不正确。");
        if (!string.IsNullOrWhiteSpace(pool.RouteAlias)
            && !InternalRouteNames.IsAlias(pool.RouteAlias))
            throw new InvalidDataException($"号池 {pool.Id} 使用了不属于总管家的路由前缀。");
        if (pool.Transport == PoolTransport.CliProxyApi)
        {
            if (!IsSafeCliPoolId(pool.Id)
                || !IsExactCliEndpoint(pool)
                || !IsSafeCliPortBinding(pool)
                || !IsSafeCliProviderBinding(pool))
                throw new InvalidDataException($"CLIProxy 号池 {pool.Id} 的端点、端口或 provider 不安全。");
        }
    }

    private static bool HasValidReservedIdentity(PoolDefinition pool)
    {
        if (pool.Id.Equals(PoolCatalogDefaults.OfficialPoolId, StringComparison.OrdinalIgnoreCase))
            return pool.Transport == PoolTransport.OfficialCodex;
        return true;
    }

    private void Save()
    {
        EnsureWritable();
        using var fileLock = LocalFileTransaction.Acquire(_path);
        var currentFingerprint = LocalFileTransaction.Fingerprint(_path);
        if (!string.Equals(currentFingerprint, _loadedFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "号池清单已被另一个总管家进程修改；已拒绝覆盖，请刷新号池后重试。");
        LocalFileTransaction.WriteAtomic(_path, JsonSerializer.Serialize(_catalog, JsonOptions));
        _loadedFingerprint = LocalFileTransaction.Fingerprint(_path);
    }

    private void EnsureWritable()
    {
        if (LoadWarning is not null) throw new InvalidOperationException(LoadWarning);
    }

    private PoolDefinition RequirePool(string id) =>
        _catalog.Pools.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("号池不存在。");

    private void Validate(
        PoolDefinition pool,
        string? ignoreId,
        bool allowLegacyReservedPortMigration = false)
    {
        if (string.IsNullOrWhiteSpace(pool.Id) || string.IsNullOrWhiteSpace(pool.DisplayName))
            throw new InvalidOperationException("号池 ID 和名称不能为空。");
        if (!HasValidReservedIdentity(pool))
            throw new InvalidOperationException($"保留号池 {pool.Id} 的类型或凭据槽不正确。");
        if (!string.IsNullOrWhiteSpace(pool.RouteAlias)
            && !InternalRouteNames.IsAlias(pool.RouteAlias))
            throw new InvalidOperationException($"号池 {pool.Id} 的路由别名必须使用 {InternalRouteNames.Prefix} 前缀。");
        if (pool.Transport == PoolTransport.CliProxyApi
            && (!IsSafeCliPoolId(pool.Id)
                || !IsExactCliEndpoint(pool)
                || !IsSafeCliPortBinding(pool, allowLegacyReservedPortMigration, _reservedPorts)
                || !IsSafeCliProviderBinding(pool)))
            throw new InvalidOperationException("本机 CLIProxyAPI 号池必须绑定 127.0.0.1 的独立端口。");
        if (!string.IsNullOrWhiteSpace(pool.RouteAlias)
            && _catalog.Pools.Any(other => !string.Equals(other.Id, ignoreId, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(other.RouteAlias, pool.RouteAlias, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"路由别名 {pool.RouteAlias} 已被其他号池使用。");
        if (!string.IsNullOrWhiteSpace(pool.ProviderId)
            && _catalog.Pools.Any(other => !string.Equals(other.Id, ignoreId, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(other.ProviderId, pool.ProviderId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"凭据槽 {pool.ProviderId} 已被其他号池使用。");
        if (pool.Transport == PoolTransport.CliProxyApi
            && pool.LocalPort is not null
            && _catalog.Pools.Any(other => !string.Equals(other.Id, ignoreId, StringComparison.OrdinalIgnoreCase)
                                           && other.Transport == PoolTransport.CliProxyApi
                                           && other.LocalPort == pool.LocalPort))
            throw new InvalidOperationException($"本机端口 {pool.LocalPort} 已被其他 CLIProxy 号池使用。");
    }

    private static bool BindNativeAccount(PoolDefinition pool, CodexAccountView account, bool isOfficial)
    {
        var changed = false;
        var expectedTransport = isOfficial ? PoolTransport.OfficialCodex : PoolTransport.NativeCodexAccount;
        if (pool.Transport != expectedTransport) { pool.Transport = expectedTransport; changed = true; }
        var product = ProductFor(account);
        if (pool.Product != product) { pool.Product = product; changed = true; }
        if (!string.Equals(pool.NativeAccountId, account.Id, StringComparison.Ordinal))
        {
            pool.NativeAccountId = account.Id;
            changed = true;
        }
        if (!pool.Enabled) { pool.Enabled = true; changed = true; }
        if (pool.RouteAlias is not null) { pool.RouteAlias = null; changed = true; }
        if (pool.ProviderId is not null) { pool.ProviderId = null; changed = true; }
        if (pool.BaseUrl != "OpenAI 原生账号") { pool.BaseUrl = "OpenAI 原生账号"; changed = true; }
        if (pool.LocalPort is not null) { pool.LocalPort = null; changed = true; }
        var displayName = isOfficial
            ? "Codex Pro 主账号（官方保底）"
            : $"Codex {(product == AccountProduct.CodexPro ? "Pro" : "Plus")} 账号";
        var description = isOfficial
            ? "官方 Codex Pro 主账号。可把全局线路切回 Pro 扣费，各任务从下一条请求生效，不与 Plus 自动串池，且禁止删除。"
            : "Codex 原生账号。切换后全局线路固定到这个扣费账号，各任务从下一条消息生效，模型名称和聊天上下文保持不变。";
        if (pool.DisplayName != displayName) { pool.DisplayName = displayName; changed = true; }
        if (pool.Description != description) { pool.Description = description; changed = true; }
        return changed;
    }

    private static AccountProduct ProductFor(CodexAccountView account) =>
        account.Plan?.Contains("pro", StringComparison.OrdinalIgnoreCase) == true
            ? AccountProduct.CodexPro
            : AccountProduct.CodexPlus;

    private static string StableSuffix(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10].ToLowerInvariant();

    public PoolDefinition ReassignCliPort(PoolDefinition suppliedPool)
    {
        ArgumentNullException.ThrowIfNull(suppliedPool);
        lock (_gate)
        {
            EnsureWritable();
            var stored = RequirePool(suppliedPool.Id);
            if (stored.Transport != PoolTransport.CliProxyApi
                || suppliedPool.Transport != PoolTransport.CliProxyApi
                || stored.LocalPort != suppliedPool.LocalPort
                || !string.Equals(stored.ProviderId, suppliedPool.ProviderId, StringComparison.Ordinal))
            throw new InvalidOperationException("修复端口时 CLIProxy 号池已被修改，为避免覆盖新配置，本次操作已停止。");

            var replacement = AllocateCliPort(stored.Id, stored.Id);
            stored.LocalPort = replacement;
            stored.BaseUrl = $"http://127.0.0.1:{replacement}/v1";
            suppliedPool.LocalPort = replacement;
            suppliedPool.BaseUrl = stored.BaseUrl;
            Save();
            return Clone(stored);
        }
    }

    private int AllocateCliPort(string identity, string? ignorePoolId = null)
    {
        var used = _catalog.Pools
            .Where(pool => !string.Equals(pool.Id, ignorePoolId, StringComparison.OrdinalIgnoreCase))
            .Where(pool => pool.Transport == PoolTransport.CliProxyApi && pool.LocalPort is not null)
            .Select(pool => pool.LocalPort!.Value);
        return LocalPortPolicy.FindAvailableCliProxyPort(identity, used, _reservedPorts);
    }

    private bool EnsureAgentPool(
        string id,
        string displayName,
        AccountProduct product,
        string routeAlias)
    {
        var pool = _catalog.Pools.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
        {
            var port = AllocateCliPort(id);
            _catalog.Pools.Add(new PoolDefinition
            {
                Id = id,
                DisplayName = displayName,
                Description = "一个本机 CLIProxy 进程只绑定一个 OAuth 账号，并使用一个经过校验的独立端口。",
                Transport = PoolTransport.CliProxyApi,
                Product = product,
                Enabled = true,
                RouteAlias = routeAlias,
                ProviderId = ExpectedCliProviderId(id),
                DefaultModel = "gpt-5.6-sol",
                LocalPort = port,
                BaseUrl = $"http://127.0.0.1:{port}/v1"
            });
            return true;
        }

        if (pool.Transport != PoolTransport.CliProxyApi)
            throw new InvalidOperationException($"保留的 Agent API 号池 {id} 类型不正确。");
        var changed = false;
        if (pool.Product != product) { pool.Product = product; changed = true; }
        if (!string.Equals(pool.DisplayName, displayName, StringComparison.Ordinal))
        {
            pool.DisplayName = displayName;
            changed = true;
        }
        if (!string.Equals(pool.RouteAlias, routeAlias, StringComparison.Ordinal))
        {
            pool.RouteAlias = routeAlias;
            changed = true;
        }
        const string description = "一个本机 CLIProxy 进程只绑定一个 OAuth 账号，并使用一个经过校验的独立端口。";
        if (!string.Equals(pool.Description, description, StringComparison.Ordinal))
        {
            pool.Description = description;
            changed = true;
        }
        return changed;
    }

    private static PoolDefinition Clone(PoolDefinition source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        Description = source.Description,
        Transport = source.Transport,
        Product = source.Product,
        IsProtected = source.IsProtected,
        Enabled = source.Enabled,
        RouteAlias = source.RouteAlias,
        ProviderId = source.ProviderId,
        NativeAccountId = source.NativeAccountId,
        DefaultModel = source.DefaultModel,
        BaseUrl = source.BaseUrl,
        LocalPort = source.LocalPort,
        AdminUser = source.AdminUser,
        CreatedAt = source.CreatedAt
    };

    private static ActivePoolState Clone(ActivePoolState source) => new()
    {
        PoolId = source.PoolId,
        Model = source.Model,
        SwitchedAt = source.SwitchedAt,
        Verification = source.Verification
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
