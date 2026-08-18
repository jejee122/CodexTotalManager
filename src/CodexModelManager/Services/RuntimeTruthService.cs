using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public interface IRuntimeTruthSource
{
    RuntimeTruthPreferenceSource? ReadPreference();
    string? ReadCodexDefaultModel();
    Task<CodexDesktopState?> ReadTaskAsync(CancellationToken cancellationToken = default);
    Task<ActiveRoute?> ReadConfiguredRouteAsync(CancellationToken cancellationToken = default);
    Task<RuntimeRouteExecution?> ReadLatestExecutionAsync(CancellationToken cancellationToken = default);
}

public sealed class DefaultRuntimeTruthSource : IRuntimeTruthSource
{
    private readonly PoolCatalogService _poolCatalog;
    private readonly CodexConfigService _codexConfig;
    private readonly CodexDesktopBridgeService _desktop;
    private readonly OpenCodexClient _openCodex;

    public DefaultRuntimeTruthSource(
        PoolCatalogService poolCatalog,
        CodexConfigService codexConfig,
        CodexDesktopBridgeService desktop,
        OpenCodexClient openCodex)
    {
        _poolCatalog = poolCatalog;
        _codexConfig = codexConfig;
        _desktop = desktop;
        _openCodex = openCodex;
    }

    public RuntimeTruthPreferenceSource? ReadPreference()
    {
        var active = _poolCatalog.GetActive();
        var pool = _poolCatalog.Find(active.PoolId);
        var expectedTaskModel = pool?.Transport switch
        {
            PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount => active.Model,
            PoolTransport.CliProxyApi when !string.IsNullOrWhiteSpace(pool.ProviderId) =>
                $"{pool.ProviderId}/{active.Model}",
            _ => pool?.RouteAlias
        };
        var accountId = !string.IsNullOrWhiteSpace(pool?.NativeAccountId)
            ? pool.NativeAccountId
            : pool?.ProviderId;
        var accountIdentitySource = !string.IsNullOrWhiteSpace(pool?.NativeAccountId)
            ? RuntimeAccountIdentitySource.ExplicitAccountId
            : !string.IsNullOrWhiteSpace(pool?.ProviderId)
                ? RuntimeAccountIdentitySource.ProviderRoute
                : RuntimeAccountIdentitySource.Unknown;
        var displayName = string.IsNullOrWhiteSpace(pool?.DisplayName) ? active.PoolId : pool.DisplayName;
        return new RuntimeTruthPreferenceSource(
            active.PoolId,
            displayName,
            accountId,
            accountIdentitySource,
            displayName,
            active.Model,
            expectedTaskModel,
            active.SwitchedAt,
            active.Verification);
    }

    public string? ReadCodexDefaultModel() => _codexConfig.ReadDefaultModel();

    public async Task<CodexDesktopState?> ReadTaskAsync(CancellationToken cancellationToken = default) =>
        await _desktop.ReadStateAsync(cancellationToken);

    public async Task<ActiveRoute?> ReadConfiguredRouteAsync(CancellationToken cancellationToken = default) =>
        await _openCodex.GetActiveRouteAsync(cancellationToken);

    public async Task<RuntimeRouteExecution?> ReadLatestExecutionAsync(CancellationToken cancellationToken = default) =>
        await _openCodex.GetLatestRouteExecutionAsync(cancellationToken);
}

public sealed class RuntimeTruthService
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(15);

    private readonly IRuntimeTruthSource _source;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _staleAfter;
    private readonly AccountUsageLedgerService? _accountUsageLedger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private RuntimeTruthSnapshot? _lastSnapshot;
    private long _revision;

    public RuntimeTruthService(
        IRuntimeTruthSource source,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? staleAfter = null,
        AccountUsageLedgerService? accountUsageLedger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? (() => DateTimeOffset.Now);
        _staleAfter = staleAfter ?? DefaultStaleAfter;
        _accountUsageLedger = accountUsageLedger;
        if (_staleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
    }

    public RuntimeTruthSnapshot? LastSnapshot
    {
        get
        {
            lock (_snapshotLock) return _lastSnapshot;
        }
    }

    public event EventHandler<RuntimeTruthSnapshot>? SnapshotChanged;

    public async Task<RuntimeTruthSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        RuntimeTruthSnapshot snapshot;
        try
        {
            var observedAt = _clock();
            var preferenceRead = ReadSafely(_source.ReadPreference);
            var configRead = ReadSafely(_source.ReadCodexDefaultModel);
            var taskReadTask = ReadSafelyAsync(_source.ReadTaskAsync, cancellationToken);
            var routeReadTask = ReadSafelyAsync(_source.ReadConfiguredRouteAsync, cancellationToken);
            var executionReadTask = ReadSafelyAsync(_source.ReadLatestExecutionAsync, cancellationToken);
            await Task.WhenAll(taskReadTask, routeReadTask, executionReadTask);

            var taskRead = taskReadTask.Result;
            var routeRead = routeReadTask.Result;
            var executionRead = executionReadTask.Result;
            var preference = CreatePreference(preferenceRead.Value, configRead.Value);
            var task = CreateTask(taskRead.Value, preferenceRead.Value?.ExpectedTaskModel);
            var stale = IsStale(executionRead.Value, observedAt);
            var predatesPreference = PredatesPreference(executionRead.Value, preferenceRead.Value);
            var consistency = EvaluateConsistency(
                preferenceRead.Value,
                configRead.Value,
                taskRead.Value,
                routeRead.Value,
                executionRead.Value,
                stale,
                predatesPreference);
            var evidence = CreateEvidence(
                observedAt,
                preferenceRead,
                configRead,
                taskRead,
                routeRead,
                executionRead,
                _accountUsageLedger);
            var candidate = new RuntimeTruthSnapshot(
                Interlocked.Increment(ref _revision),
                observedAt,
                preference,
                task,
                routeRead.Value,
                executionRead.Value,
                stale || predatesPreference,
                predatesPreference,
                consistency,
                evidence);
            snapshot = SanitizeSnapshot(candidate);
            lock (_snapshotLock) _lastSnapshot = snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
        PublishSnapshotChanged(snapshot);
        return snapshot;
    }

    private RuntimeTruthPreference CreatePreference(
        RuntimeTruthPreferenceSource? source,
        string? codexDefaultModel)
    {
        if (source is null)
            return RuntimeTruthPreference.Unknown with
            {
                CodexDefaultModel = RuntimeTruthSanitizer.Redact(codexDefaultModel)
            };
        var expected = source.ExpectedTaskModel;
        return new RuntimeTruthPreference(
            SafeValue(source.PoolId, string.Empty),
            SafeValue(source.PoolDisplayName, "首选账号未知"),
            RuntimeTruthSanitizer.Redact(source.PreferredAccountId),
            source.PreferredAccountIdentitySource,
            SafeValue(source.PreferredAccountDisplayName, "首选账号未知"),
            SafeValue(source.PreferredModel, string.Empty),
            RuntimeTruthSanitizer.Redact(expected),
            RuntimeTruthSanitizer.Redact(codexDefaultModel),
            !string.IsNullOrWhiteSpace(expected)
            && string.Equals(codexDefaultModel, expected, StringComparison.OrdinalIgnoreCase),
            source.SwitchedAt,
            source.Verification);
    }

    private static RuntimeTruthTask CreateTask(CodexDesktopState? source, string? expectedModel)
    {
        var displayed = RuntimeTruthSanitizer.Redact(source?.CurrentModel);
        var displayedLabel = OpenCodexClient.ToUserVisibleModelName(displayed, null, "任务模型未识别");
        var expectedLabel = OpenCodexClient.ToUserVisibleModelName(expectedModel, null, "期望入口未配置");
        var matches = source?.Connected == true
                      && !string.IsNullOrWhiteSpace(displayed)
                      && !string.IsNullOrWhiteSpace(expectedModel)
                      && string.Equals(displayed, expectedModel, StringComparison.OrdinalIgnoreCase);
        return new RuntimeTruthTask(
            source is not null,
            source?.Connected == true,
            source?.IsTurnRunning == true,
            displayed,
            displayedLabel,
            expectedModel,
            expectedLabel,
            matches,
            RuntimeTruthSanitizer.Redact(source?.Message) ?? "没有可读取的 Codex 当前任务状态");
    }

    private bool IsStale(RuntimeRouteExecution? execution, DateTimeOffset observedAt) =>
        execution?.Timestamp is not null && observedAt - execution.Timestamp.Value > _staleAfter;

    private static bool PredatesPreference(
        RuntimeRouteExecution? execution,
        RuntimeTruthPreferenceSource? preference) =>
        execution?.Timestamp is not null
        && preference is not null
        && execution.Timestamp.Value < preference.SwitchedAt;

    private static RuntimeTruthConsistency EvaluateConsistency(
        RuntimeTruthPreferenceSource? preference,
        string? codexDefaultModel,
        CodexDesktopState? task,
        ActiveRoute? configuredRoute,
        RuntimeRouteExecution? execution,
        bool stale,
        bool predatesPreference)
    {
        var mismatches = new List<string>();
        var missing = new List<string>();
        if (preference is null) missing.Add("首选号池");
        if (task?.Connected != true) missing.Add("Codex 当前任务");
        if (execution is null) missing.Add("最近实际执行");

        var expectedModel = preference?.ExpectedTaskModel;
        if (preference is not null && string.IsNullOrWhiteSpace(expectedModel)) missing.Add("期望任务入口");
        if (!string.IsNullOrWhiteSpace(expectedModel))
        {
            if (string.IsNullOrWhiteSpace(codexDefaultModel))
                missing.Add("Codex 默认模型");
            else if (!string.Equals(codexDefaultModel, expectedModel, StringComparison.OrdinalIgnoreCase))
                AddMismatch(mismatches, $"Codex 默认模型为 {codexDefaultModel}，首选入口为 {expectedModel}");

            if (task?.Connected == true
                && !string.Equals(task.CurrentModel, expectedModel, StringComparison.OrdinalIgnoreCase))
                AddMismatch(mismatches, $"当前任务显示 {task.CurrentModel ?? "未知"}，首选入口为 {expectedModel}");

            if (execution is not null
                && !predatesPreference
                && !string.IsNullOrWhiteSpace(execution.RequestedModel)
                && !string.Equals(execution.RequestedModel, expectedModel, StringComparison.OrdinalIgnoreCase))
                AddMismatch(mismatches, $"最近请求入口为 {execution.RequestedModel}，首选入口为 {expectedModel}");
        }

        var actual = execution?.ActualAttempt;
        if (execution is not null && !predatesPreference)
        {
            if (actual is null)
            {
                missing.Add("最近实际 attempt");
            }
            else if (preference is not null)
            {
                var identitiesComparable = preference.PreferredAccountIdentitySource != RuntimeAccountIdentitySource.Unknown
                                           && actual.AccountIdentitySource == preference.PreferredAccountIdentitySource
                                           && !string.IsNullOrWhiteSpace(preference.PreferredAccountId)
                                           && !string.IsNullOrWhiteSpace(actual.AccountId);
                if (!identitiesComparable)
                {
                    missing.Add("首选与实际账号身份不可比较");
                }
                else if (!string.Equals(
                             preference.PreferredAccountId,
                             actual.AccountId,
                             StringComparison.OrdinalIgnoreCase))
                {
                    AddMismatch(
                        mismatches,
                        $"首选账号为 {preference.PreferredAccountId}，最近实际账号为 {actual.AccountId}");
                }
            }
        }

        var requiresConfiguredRoute = OpenCodexClient.IsInternalRouteAlias(expectedModel)
                                      || execution is not null
                                      && !predatesPreference
                                      && OpenCodexClient.IsInternalRouteAlias(execution.RequestedModel);
        if (requiresConfiguredRoute && configuredRoute is null)
        {
            missing.Add("internal route 配置证据");
        }
        else if (configuredRoute is not null
                 && execution is not null
                 && !predatesPreference
                 && actual is not null
                 && OpenCodexClient.IsInternalRouteAlias(execution.RequestedModel)
                 && !configuredRoute.Targets.Any(target =>
                     string.Equals(target.Provider, actual.ProviderId, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(target.Model, actual.Model, StringComparison.OrdinalIgnoreCase)))
        {
            AddMismatch(mismatches, $"最近实际执行 {actual.ProviderId}/{actual.Model} 不在当前号池目标中");
        }

        if (task?.IsTurnRunning == true)
            return new RuntimeTruthConsistency(RuntimeTruthState.Pending, "当前任务正在回答，事实快照只读且不会触发切换", mismatches);
        if (mismatches.Count > 0)
            return new RuntimeTruthConsistency(RuntimeTruthState.Diverged, "首选、当前任务或最近实际执行存在差异", mismatches);
        if (predatesPreference)
            return new RuntimeTruthConsistency(
                RuntimeTruthState.Stale,
                "最近实际执行发生在首选切换之前，只能作为旧证据，不能证明当前首选已经生效",
                Array.Empty<string>());
        if (execution?.Outcome == RuntimeExecutionOutcome.Failed)
            return new RuntimeTruthConsistency(RuntimeTruthState.Failed, "最近实际执行失败，已保留完整尝试链", mismatches);
        if (missing.Count > 0)
            return new RuntimeTruthConsistency(RuntimeTruthState.Unknown, $"事实来源不完整：{string.Join("、", missing.Distinct())}", missing);
        if (execution?.Timestamp is null)
            return new RuntimeTruthConsistency(RuntimeTruthState.Unknown, "最近实际执行缺少时间戳，无法判断陈旧性", Array.Empty<string>());
        if (stale)
            return new RuntimeTruthConsistency(RuntimeTruthState.Stale, "首选与可见状态一致，但最近实际执行记录已经陈旧", Array.Empty<string>());
        return new RuntimeTruthConsistency(RuntimeTruthState.Consistent, "首选、当前任务显示和最近实际执行证据一致", Array.Empty<string>());
    }

    private static IReadOnlyList<RuntimeTruthEvidence> CreateEvidence(
        DateTimeOffset observedAt,
        SourceRead<RuntimeTruthPreferenceSource> preference,
        SourceRead<string> config,
        SourceRead<CodexDesktopState> task,
        SourceRead<ActiveRoute> route,
        SourceRead<RuntimeRouteExecution> execution,
        AccountUsageLedgerService? ledger)
    {
        var evidence = new List<RuntimeTruthEvidence>
        {
            Evidence(RuntimeTruthEvidenceSource.PoolCatalog, preference.Value is not null, observedAt,
                preference.Error ?? (preference.Value is null ? "没有首选号池数据" : $"首选号池 {preference.Value.PoolId}")),
            Evidence(RuntimeTruthEvidenceSource.CodexConfiguration, !string.IsNullOrWhiteSpace(config.Value), observedAt,
                config.Error ?? (string.IsNullOrWhiteSpace(config.Value) ? "没有读取到 Codex 默认模型" : $"默认模型 {config.Value}")),
            Evidence(RuntimeTruthEvidenceSource.CodexDesktop, task.Value?.Connected == true, observedAt,
                task.Error ?? task.Value?.Message ?? "没有读取到 Codex 当前任务"),
            Evidence(RuntimeTruthEvidenceSource.OpenCodexRoute, route.Value is not null, observedAt,
                route.Error ?? (route.Value is null ? "没有读取到总管家本机引擎的当前号池目标" : $"号池目标 {route.Value.Targets.Count} 个")),
            Evidence(RuntimeTruthEvidenceSource.OpenCodexLog, execution.Value is not null, observedAt,
                execution.Error ?? (execution.Value is null ? "没有读取到最近实际执行" : $"实际尝试 {execution.Value.Attempts.Count} 次"))
        };
        if (ledger is not null)
        {
            var ledgerSnapshot = ledger.LastSnapshot;
            var importer = ledgerSnapshot.ImporterStatus;
            var tokenHealth = importer.TokenHealth == AccountUsageImporterHealth.NotStarted
                ? importer.Health : importer.TokenHealth;
            var healthy = tokenHealth == AccountUsageImporterHealth.Healthy
                          && !ledgerSnapshot.TokenSourceStale
                          && !ledgerSnapshot.CoverageGapDetected
                          && ledgerSnapshot.TokenIntegrityFailureCount == 0;
            var snapshotAt = ledgerSnapshot.ObservedAt == DateTimeOffset.MinValue
                ? observedAt : ledgerSnapshot.ObservedAt;
            var reason = importer.Health == AccountUsageImporterHealth.Stopped
                ? $"归账已停止：{importer.StoppedReason ?? importer.LastErrorClass ?? "Unknown"}"
                : ledgerSnapshot.CoverageGapDetected ? "Token 来源存在持久覆盖缺口"
                : ledgerSnapshot.TokenIntegrityFailureCount > 0 ? $"Token 台账完整性异常 {ledgerSnapshot.TokenIntegrityFailureCount} 项"
                : ledgerSnapshot.TokenSourceStale ? "Token 来源陈旧；保留旧快照"
                : tokenHealth == AccountUsageImporterHealth.Degraded ? $"Token 归账降级：{importer.TokenErrorClass ?? importer.LastErrorClass ?? "Unknown"}"
                : healthy ? $"逐账号台账快照 {ledgerSnapshot.StoredAttemptCount} 条 attempt"
                : "Token 台账尚未形成健康快照";
            evidence.Add(Evidence(
                RuntimeTruthEvidenceSource.AccountUsageLedger,
                healthy,
                snapshotAt,
                $"只读证据：{reason}"));
        }
        return evidence;
    }

    private static RuntimeTruthEvidence Evidence(
        RuntimeTruthEvidenceSource source,
        bool available,
        DateTimeOffset observedAt,
        string? message) =>
        new(source, available, observedAt, RuntimeTruthSanitizer.Redact(message) ?? "没有详细信息");

    private static ActiveRoute? SanitizeRoute(ActiveRoute? route)
    {
        if (route is null) return null;
        var targets = route.Targets.Select(target => (
            Provider: SafeValue(target.Provider, "unknown"),
            Model: SafeValue(target.Model, "未知模型"))).ToArray();
        return new ActiveRoute(
            SafeValue(route.Provider, "unknown"),
            SafeValue(route.Model, "未知模型"),
            targets);
    }

    private static RuntimeTruthSnapshot SanitizeSnapshot(RuntimeTruthSnapshot snapshot)
    {
        var preference = snapshot.Preferred with
        {
            PoolId = SafeValue(snapshot.Preferred.PoolId, string.Empty),
            PoolDisplayName = SafeValue(snapshot.Preferred.PoolDisplayName, "首选账号未知"),
            PreferredAccountId = RuntimeTruthSanitizer.Redact(snapshot.Preferred.PreferredAccountId),
            PreferredAccountDisplayName = SafeValue(snapshot.Preferred.PreferredAccountDisplayName, "首选账号未知"),
            PreferredModel = SafeValue(snapshot.Preferred.PreferredModel, string.Empty),
            ExpectedTaskModel = RuntimeTruthSanitizer.Redact(snapshot.Preferred.ExpectedTaskModel),
            CodexDefaultModel = RuntimeTruthSanitizer.Redact(snapshot.Preferred.CodexDefaultModel),
            Verification = SafeValue(snapshot.Preferred.Verification, "unknown")
        };
        var task = snapshot.Task with
        {
            DisplayedModel = RuntimeTruthSanitizer.Redact(snapshot.Task.DisplayedModel),
            DisplayedModelLabel = SafeValue(snapshot.Task.DisplayedModelLabel, "任务模型未识别"),
            ExpectedModel = RuntimeTruthSanitizer.Redact(snapshot.Task.ExpectedModel),
            ExpectedModelLabel = SafeValue(snapshot.Task.ExpectedModelLabel, "期望入口未配置"),
            Message = SafeValue(snapshot.Task.Message, "没有可读取的 Codex 当前任务状态")
        };
        var execution = SanitizeExecution(snapshot.LastExecution);
        var consistency = snapshot.Consistency with
        {
            Message = SafeValue(snapshot.Consistency.Message, "事实状态未知"),
            Mismatches = snapshot.Consistency.Mismatches
                .Select(item => SafeValue(item, "事实不一致"))
                .ToArray()
        };
        var evidence = snapshot.Evidence
            .Select(item => item with { Message = SafeValue(item.Message, "没有详细信息") })
            .ToArray();
        return snapshot with
        {
            Preferred = preference,
            Task = task,
            ConfiguredRoute = SanitizeRoute(snapshot.ConfiguredRoute),
            LastExecution = execution,
            Consistency = consistency,
            Evidence = evidence
        };
    }

    private static RuntimeRouteExecution? SanitizeExecution(RuntimeRouteExecution? execution)
    {
        if (execution is null) return null;
        var attempts = execution.Attempts.Select(attempt => attempt with
        {
            ProviderId = SafeValue(attempt.ProviderId, "unknown"),
            ProviderDisplayName = SafeValue(attempt.ProviderDisplayName, "来源未知"),
            AccountId = RuntimeTruthSanitizer.Redact(attempt.AccountId),
            AccountDisplayName = SafeValue(attempt.AccountDisplayName, "账号未知"),
            Model = SafeValue(attempt.Model, "未知模型"),
            ErrorCode = RuntimeTruthSanitizer.Redact(attempt.ErrorCode),
            ErrorMessage = RuntimeTruthSanitizer.Redact(attempt.ErrorMessage),
            TokenUsage = SanitizeUsage(attempt.TokenUsage),
            AccountIdentityMaterial = null
        }).ToArray();
        return execution with
        {
            RequestId = RuntimeTruthSanitizer.Redact(execution.RequestId),
            RequestedModel = SafeValue(execution.RequestedModel, "未知"),
            ErrorCode = RuntimeTruthSanitizer.Redact(execution.ErrorCode),
            ErrorMessage = RuntimeTruthSanitizer.Redact(execution.ErrorMessage),
            Attempts = attempts,
            RequestLevelTokenUsage = SanitizeUsage(execution.RequestLevelTokenUsage),
            RequestIdentityMaterial = null
        };
    }

    private static AttemptTokenUsageFact? SanitizeUsage(AttemptTokenUsageFact? usage) => usage is null
        ? null
        : usage with
        {
            ValidationMessage = SafeValue(usage.ValidationMessage, "校验状态未知"),
            SourcePath = SafeValue(usage.SourcePath, "usage")
        };

    private void PublishSnapshotChanged(RuntimeTruthSnapshot snapshot)
    {
        var handlers = SnapshotChanged;
        if (handlers is null) return;
        foreach (EventHandler<RuntimeTruthSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch { /* A UI observer must never turn a successful read into a failed read. */ }
        }
    }

    private static void AddMismatch(ICollection<string> mismatches, string value) =>
        mismatches.Add(RuntimeTruthSanitizer.Redact(value) ?? "事实不一致");

    private static string SafeValue(string? value, string fallback) =>
        RuntimeTruthSanitizer.Redact(value) ?? fallback;

    private static SourceRead<T> ReadSafely<T>(Func<T?> read) where T : class
    {
        try { return new SourceRead<T>(read(), null); }
        catch (Exception ex) { return new SourceRead<T>(null, RuntimeTruthSanitizer.Redact(ex.Message)); }
    }

    private static async Task<SourceRead<T>> ReadSafelyAsync<T>(
        Func<CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken) where T : class
    {
        try { return new SourceRead<T>(await read(cancellationToken), null); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new SourceRead<T>(null, RuntimeTruthSanitizer.Redact(ex.Message)); }
    }

    private sealed record SourceRead<T>(T? Value, string? Error) where T : class;
}

internal static partial class RuntimeTruthSanitizer
{
    [GeneratedRegex(@"(?i)([?&](?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|authorization|secret|password)=)[^&#\s]+")]
    private static partial Regex QuerySecretPattern();

    [GeneratedRegex(@"(?i)([""']?(?:api[_ -]?key|access[_ -]?token|refresh[_ -]?token|token|authorization|secret|password)[""']?\s*:\s*)(?:""[^""]*""|'[^']*'|[^,\s}\]]+)")]
    private static partial Regex JsonSecretPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)\bBasic\s+[A-Za-z0-9+/=]+")]
    private static partial Regex BasicPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)\b(api[_ -]?key|access[_ -]?token|refresh[_ -]?token|token|authorization|secret|password)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex NamedSecretPattern();

    [GeneratedRegex(@"(?i)\bsk-[A-Za-z0-9_-]{8,}")]
    private static partial Regex OpenAiKeyPattern();

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?i)(?:\b[A-Z]:\\|/Users/|/home/)[^\r\n\t,;]+")]
    private static partial Regex LocalPathPattern();

    public static string? Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        // Authentication payloads must be removed before a surrounding named field
        // (for example "Authorization: Basic ...") consumes only the scheme word.
        var redacted = BearerPattern().Replace(value, "Bearer [已隐藏]");
        redacted = BasicPattern().Replace(redacted, "Basic [已隐藏]");
        redacted = JwtPattern().Replace(redacted, "[已隐藏 JWT]");
        redacted = OpenAiKeyPattern().Replace(redacted, "[已隐藏]");
        redacted = JsonSecretPattern().Replace(redacted, "$1\"[已隐藏]\"");
        redacted = QuerySecretPattern().Replace(redacted, "$1[已隐藏]");
        redacted = NamedSecretPattern().Replace(redacted, "$1=[已隐藏]");
        redacted = EmailPattern().Replace(redacted, "[已隐藏邮箱]");
        redacted = LocalPathPattern().Replace(redacted, "[已隐藏路径]");
        return redacted.Length <= 512 ? redacted : redacted[..512] + "…";
    }
}
