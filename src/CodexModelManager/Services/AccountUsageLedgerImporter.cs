using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class AccountUsageLedgerImporter : IAsyncDisposable
{
    private readonly AccountUsageLedgerService _ledger;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AccountPoolView>>> _readPools;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _quotaSampleInterval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<AccountRosterCompleteness> _readCatalogCompleteness;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private bool _runRequested;
    private bool _disposed;
    private long _refreshCount;
    private DateTimeOffset? _lastQuotaFetchAt;
    private DateTimeOffset? _lastTokenSuccessAt;
    private DateTimeOffset? _lastQuotaSuccessAt;
    private bool _quotaHealthy = true;
    private string? _quotaErrorClass;

    public AccountUsageLedgerImporter(
        AccountUsageLedgerService ledger,
        Func<CancellationToken, Task<IReadOnlyList<AccountPoolView>>> readPools,
        TimeSpan? interval = null,
        TimeSpan? quotaSampleInterval = null,
        Func<DateTimeOffset>? clock = null,
        Func<AccountRosterCompleteness>? readCatalogCompleteness = null)
    {
        _ledger = ledger;
        _readPools = readPools;
        _interval = interval ?? TimeSpan.FromSeconds(6);
        _quotaSampleInterval = quotaSampleInterval ?? TimeSpan.FromMinutes(5);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _readCatalogCompleteness = readCatalogCompleteness ?? (() => AccountRosterCompleteness.Complete);
        var priorStatus = ledger.ImporterStatus;
        _lastTokenSuccessAt = priorStatus.TokenLastSuccessAt;
        _lastQuotaSuccessAt = priorStatus.QuotaLastSuccessAt;
        _quotaHealthy = priorStatus.QuotaHealth is AccountUsageImporterHealth.Healthy
            or AccountUsageImporterHealth.NotStarted;
        _quotaErrorClass = priorStatus.QuotaErrorClass;
    }

    public long RefreshCount => Interlocked.Read(ref _refreshCount);
    public AccountUsageImporterStatus Status => _ledger.ImporterStatus;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runRequested = true;
            if (_loop is { IsCompleted: false }) return;
            StartLoopLocked();
        }
    }

    private void StartLoopLocked()
    {
        _lifetime?.Dispose();
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        var loop = Task.Run(() => RunAsync(lifetime.Token));
        _loop = loop;
        _ = loop.ContinueWith(
            _ => CompleteLoop(loop, lifetime),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteLoop(Task completedLoop, CancellationTokenSource completedLifetime)
    {
        lock (_lifecycleGate)
        {
            if (!ReferenceEquals(_loop, completedLoop)) return;
            _loop = null;
            if (ReferenceEquals(_lifetime, completedLifetime)) _lifetime = null;
            completedLifetime.Dispose();
            if (_runRequested && !_disposed) StartLoopLocked();
        }
    }

    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sampleNow = _clock().ToUniversalTime();
            var tokenSourceHealthy = true;
            string? tokenSourceError = null;
            try
            {
                var tokenImport = await _ledger.IngestSourceAsync(cancellationToken).ConfigureAwait(false);
                if (tokenImport.CoverageGapDetected || _ledger.CoverageGapDetected)
                {
                    tokenSourceHealthy = false;
                    tokenSourceError = "CoverageGap";
                }
                else if (_ledger.PersistentTokenIntegrityIssue)
                {
                    tokenSourceHealthy = false;
                    tokenSourceError = _ledger.PersistentTokenIntegrityClass ?? "SourceIntegrity";
                }
                else if (tokenImport.ConflictingReplayCount > 0)
                {
                    tokenSourceHealthy = false;
                    tokenSourceError = "ReplayCollision";
                }
                else if (tokenImport.BadSourceLineCount > 0)
                {
                    tokenSourceHealthy = false;
                    tokenSourceError = "SourceMalformed";
                }
                else if (tokenImport.SourceAvailability == AccountUsageSourceAvailability.Missing && _ledger.SourceMustBeAvailable)
                {
                    tokenSourceHealthy = false;
                    tokenSourceError = "TokenSourceMissing";
                }
                else
                {
                    _lastTokenSuccessAt = sampleNow;
                }
            }
            catch (AccountLedgerIdentityKeyUnavailableException)
            {
                await _ledger.SetImporterStatusAsync(
                    AccountUsageImporterHealth.Stopped, LatestSuccessAt, "IdentityKeyUnavailable",
                    "IdentityKeyUnavailable", tokenSourceStale: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await _ledger.SetImporterStatusAsync(
                    AccountUsageImporterHealth.Degraded, LatestSuccessAt, SafeErrorClass(ex),
                    null, tokenSourceStale: true,
                    tokenLastSuccessAt: _lastTokenSuccessAt, quotaLastSuccessAt: _lastQuotaSuccessAt,
                    tokenErrorClass: SafeErrorClass(ex), quotaErrorClass: _quotaErrorClass,
                    tokenHealth: AccountUsageImporterHealth.Degraded,
                    quotaHealth: _quotaHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                tokenSourceHealthy = false;
                tokenSourceError = SafeErrorClass(ex);
            }
            if (!tokenSourceHealthy)
            {
                await _ledger.SetImporterStatusAsync(
                    AccountUsageImporterHealth.Degraded, LatestSuccessAt, tokenSourceError,
                    null, tokenSourceStale: true,
                    tokenLastSuccessAt: _lastTokenSuccessAt, quotaLastSuccessAt: _lastQuotaSuccessAt,
                    tokenErrorClass: tokenSourceError, quotaErrorClass: _quotaErrorClass,
                    tokenHealth: AccountUsageImporterHealth.Degraded,
                    quotaHealth: _quotaHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            if (_lastQuotaFetchAt is not null && sampleNow - _lastQuotaFetchAt < _quotaSampleInterval)
            {
                Interlocked.Increment(ref _refreshCount);
                await _ledger.SetImporterStatusAsync(
                    tokenSourceHealthy && _quotaHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    LatestSuccessAt, tokenSourceError ?? _quotaErrorClass, null, tokenSourceStale: !tokenSourceHealthy,
                    tokenLastSuccessAt: _lastTokenSuccessAt, quotaLastSuccessAt: _lastQuotaSuccessAt,
                    tokenErrorClass: tokenSourceError, quotaErrorClass: _quotaErrorClass,
                    tokenHealth: tokenSourceHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    quotaHealth: _quotaHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }
            try
            {
                var priorLedgerSnapshot = await _ledger.ReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (_ledger.CoverageGapDetected || _ledger.PersistentTokenIntegrityIssue)
                {
                    tokenSourceHealthy = false;
                    tokenSourceError = _ledger.CoverageGapDetected
                        ? "CoverageGap" : _ledger.PersistentTokenIntegrityClass ?? "SourceIntegrity";
                }
                var pools = await _readPools(cancellationToken).ConfigureAwait(false);
                var catalogCompleteness = _readCatalogCompleteness();
                var historicalScopes = priorLedgerSnapshot.LatestQuotaSnapshots.Select(view => view.Fact)
                    .Select(fact => (fact.ProviderId, fact.ObservationScope)).ToHashSet();
                var relevantPools = pools.Where(pool => pool.Enabled && pool.RuntimeProviderLinked
                    && (pool.Accounts.Count > 0
                        || pool.QuotaRosterCompleteness is AccountRosterCompleteness.ReadFailed or AccountRosterCompleteness.Partial
                        || historicalScopes.Contains((pool.RuntimeProviderId!.Trim().ToLowerInvariant(),
                            $"pool:{pool.Id}".ToLowerInvariant())))).ToArray();
                var unhealthyRosters = relevantPools.Where(pool => pool.QuotaRosterCompleteness != AccountRosterCompleteness.Complete).ToArray();
                var quotaHealthy = unhealthyRosters.Length == 0;
                var quotaErrorClass = unhealthyRosters.Any(pool => pool.QuotaRosterCompleteness == AccountRosterCompleteness.ReadFailed)
                    ? "RosterReadFailed"
                    : unhealthyRosters.Any(pool => pool.QuotaRosterCompleteness == AccountRosterCompleteness.Partial)
                        ? "RosterPartial" : unhealthyRosters.Length > 0 ? "RosterIncomplete" : null;
                if (catalogCompleteness != AccountRosterCompleteness.Complete)
                {
                    quotaHealthy = false;
                    quotaErrorClass = catalogCompleteness == AccountRosterCompleteness.ReadFailed
                        ? "CatalogReadFailed" : "CatalogIncomplete";
                }
                if (relevantPools.SelectMany(pool => pool.Accounts).Any(account =>
                        account.QuotaAvailability == AccountQuotaAvailability.ReadFailed || account.QuotaSourceStale))
                {
                    quotaHealthy = false;
                    quotaErrorClass ??= "QuotaSourceStale";
                }
                if (relevantPools.SelectMany(pool => pool.Accounts).SelectMany(account => account.QuotaWindows)
                    .Any(window => window.ValueValidation == QuotaValueValidationState.InvalidRange))
                {
                    quotaHealthy = false;
                    quotaErrorClass = "QuotaInvalidValue";
                }
                if (relevantPools.SelectMany(pool => pool.Accounts).Any(account =>
                        account.QuotaAvailability == AccountQuotaAvailability.Provided
                        && (account.QuotaWindows.Count == 0
                            || account.QuotaWindows.Any(window => string.IsNullOrWhiteSpace(window.PeriodKey)))))
                {
                    quotaHealthy = false;
                    quotaErrorClass = "MissingPeriodKey";
                }
                var localObservedAt = sampleNow;
                var facts = new List<AccountQuotaSnapshotFact>();
                foreach (var pool in pools)
                {
                    var observationScope = $"pool:{pool.Id}";
                    foreach (var account in pool.Accounts)
                    {
                    var sourceObservedAt = account.UsageUpdatedAt is not null;
                    var observedAt = account.UsageUpdatedAt?.ToUniversalTime() ?? localObservedAt;
                    var fetchIdentity = sourceObservedAt
                        ? $"source:{observedAt:O}:local:{localObservedAt:O}"
                        : $"local-fetch:{localObservedAt:O}";
                    var batch = _ledger.CreateQuotaObservationBatch(
                        account.RuntimeProviderId, account.Id, observedAt, sourceObservedAt, fetchIdentity, observationScope);
                    var stableWindows = account.QuotaWindows.Where(window => !string.IsNullOrWhiteSpace(window.PeriodKey)).ToArray();
                    var quotaWindowStructureInvalid = account.QuotaAvailability == AccountQuotaAvailability.Provided
                                                      && (account.QuotaWindows.Count == 0
                                                          || stableWindows.Length != account.QuotaWindows.Count);
                    var effectiveAvailability = quotaWindowStructureInvalid
                        ? AccountQuotaAvailability.ReadFailed
                        : account.QuotaAvailability;
                    var effectiveErrorClass = quotaWindowStructureInvalid
                        ? "MissingPeriodKey"
                        : account.QuotaErrorClass;
                    if (effectiveAvailability != AccountQuotaAvailability.Provided || stableWindows.Length == 0)
                    {
                        facts.Add(_ledger.CreateQuotaSnapshot(
                            account.RuntimeProviderId, account.RuntimeProviderLinked, account.Id,
                            string.Empty,
                            effectiveAvailability == AccountQuotaAvailability.ReadFailed ? "读取失败" : "未提供",
                            null, "unknown", effectiveAvailability, observedAt, sourceObservedAt,
                            localObservedAt, account.QuotaSourceStale, batch, false, effectiveErrorClass,
                            account.UsageSourceText, provenance: account.QuotaProvenance,
                            observationScope: observationScope));
                        continue;
                    }
                    foreach (var window in stableWindows)
                    {
                        facts.Add(_ledger.CreateQuotaSnapshot(
                            account.RuntimeProviderId, account.RuntimeProviderLinked, account.Id,
                            window.PeriodKey, window.Label, (decimal)window.UsedPercent, "percent_used",
                            AccountQuotaAvailability.Provided, observedAt, sourceObservedAt, localObservedAt,
                            account.QuotaSourceStale, batch, false, account.QuotaErrorClass,
                            account.UsageSourceText, account.QuotaProvenance,
                            window.ResetAtUtc, window.ResetText, window.ResetState, observationScope));
                    }
                }
                }
                var returnedScopeAccounts = facts.Where(fact => fact.AccountAttributed)
                    .Select(fact => (fact.ProviderId, fact.ObservationScope, fact.StableAccountIdentity)).ToHashSet();
                foreach (var pool in pools.Where(pool => pool.RuntimeProviderLinked
                                                         && pool.QuotaRosterCompleteness != AccountRosterCompleteness.Complete))
                {
                    var providerId = pool.RuntimeProviderId!.Trim().ToLowerInvariant();
                    var scope = $"pool:{pool.Id}".ToLowerInvariant();
                    foreach (var prior in priorLedgerSnapshot.LatestQuotaSnapshots.Select(view => view.Fact)
                                 .Where(fact => fact.ProviderId == providerId
                                                && fact.ObservationScope == scope
                                                && fact.AccountAttributed
                                                && fact.Availability == AccountQuotaAvailability.Provided
                                                && !returnedScopeAccounts.Contains((fact.ProviderId, fact.ObservationScope, fact.StableAccountIdentity)))
                                 .GroupBy(fact => fact.StableAccountIdentity, StringComparer.Ordinal)
                                 .Select(group => group.OrderByDescending(fact => fact.LocalObservedAt).First()))
                    {
                        facts.Add(_ledger.CreateScopeReadFailureOverlay(
                            prior, localObservedAt,
                            pool.QuotaRosterCompleteness == AccountRosterCompleteness.ReadFailed
                                ? "RosterReadFailed" : "RosterIncomplete",
                            $"scope-health:{providerId}:{scope}:{localObservedAt:O}"));
                    }
                }
                var completeScopes = pools
                    .Where(pool => pool.RuntimeProviderLinked
                                   && pool.QuotaRosterCompleteness == AccountRosterCompleteness.Complete)
                    .Select(pool => (ProviderId: pool.RuntimeProviderId!.Trim().ToLowerInvariant(), Scope: $"pool:{pool.Id}".ToLowerInvariant()))
                    .ToHashSet();
                var currentCatalogScopes = pools.Where(pool => pool.RuntimeProviderLinked)
                    .Select(pool => (ProviderId: pool.RuntimeProviderId!.Trim().ToLowerInvariant(),
                        Scope: $"pool:{pool.Id}".ToLowerInvariant())).ToHashSet();
                foreach (var retired in (catalogCompleteness == AccountRosterCompleteness.Complete
                             ? priorLedgerSnapshot.LatestQuotaSnapshots.Select(view => view.Fact)
                             : Array.Empty<AccountQuotaSnapshotFact>())
                             .Where(fact => fact.AccountAttributed
                                            && !currentCatalogScopes.Contains((fact.ProviderId, fact.ObservationScope)))
                             .Select(fact => (fact.ProviderId, fact.ObservationScope)).Distinct())
                    completeScopes.Add(retired);
                var currentAccountKeys = facts.Where(fact => fact.AccountAttributed)
                    .Select(fact => (fact.ProviderId, fact.ObservationScope, fact.StableAccountIdentity))
                    .ToHashSet();
                foreach (var prior in priorLedgerSnapshot.LatestQuotaSnapshots.Select(view => view.Fact)
                             .Where(fact => fact.AccountAttributed
                                            && fact.Availability == AccountQuotaAvailability.Provided
                                            && completeScopes.Contains((fact.ProviderId, fact.ObservationScope)))
                             .GroupBy(fact => (fact.ProviderId, fact.ObservationScope, fact.StableAccountIdentity))
                             .Select(group => group.OrderByDescending(fact => fact.LocalObservedAt).First()))
                {
                    if (currentAccountKeys.Contains((prior.ProviderId, prior.ObservationScope, prior.StableAccountIdentity))) continue;
                    facts.Add(_ledger.CreateMissingAccountTombstone(
                        prior, localObservedAt, $"provider-fetch:{prior.ProviderId}:{localObservedAt:O}"));
                }
                await _ledger.IngestQuotaSnapshotsAsync(facts, cancellationToken).ConfigureAwait(false);
                _lastQuotaFetchAt = sampleNow;
                _quotaHealthy = quotaHealthy;
                _quotaErrorClass = quotaErrorClass;
                if (quotaHealthy) _lastQuotaSuccessAt = sampleNow;
                await _ledger.SetImporterStatusAsync(
                    tokenSourceHealthy && quotaHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    LatestSuccessAt, tokenSourceError ?? quotaErrorClass, null, tokenSourceStale: !tokenSourceHealthy,
                    tokenLastSuccessAt: _lastTokenSuccessAt, quotaLastSuccessAt: _lastQuotaSuccessAt,
                    tokenErrorClass: tokenSourceError, quotaErrorClass: quotaErrorClass,
                    tokenHealth: tokenSourceHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    quotaHealth: quotaHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (AccountLedgerIdentityKeyUnavailableException)
            {
                await _ledger.SetImporterStatusAsync(
                    AccountUsageImporterHealth.Stopped, LatestSuccessAt, "IdentityKeyUnavailable",
                    "IdentityKeyUnavailable", tokenSourceStale: !tokenSourceHealthy,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _quotaHealthy = false;
                _quotaErrorClass = SafeErrorClass(ex);
                await _ledger.ReadAsync(quotaSourceReadFailed: true, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                await _ledger.SetImporterStatusAsync(
                    AccountUsageImporterHealth.Degraded, LatestSuccessAt, SafeErrorClass(ex),
                    null, tokenSourceStale: !tokenSourceHealthy,
                    tokenLastSuccessAt: _lastTokenSuccessAt, quotaLastSuccessAt: _lastQuotaSuccessAt,
                    tokenErrorClass: tokenSourceError, quotaErrorClass: SafeErrorClass(ex),
                    tokenHealth: tokenSourceHealthy ? AccountUsageImporterHealth.Healthy : AccountUsageImporterHealth.Degraded,
                    quotaHealth: AccountUsageImporterHealth.Degraded,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            Interlocked.Increment(ref _refreshCount);
        }
        finally { _refreshGate.Release(); }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RefreshOnceAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (AccountLedgerIdentityKeyUnavailableException) { break; }
            catch { /* Health was published by RefreshOnceAsync; the last immutable snapshot remains readable. */ }
            try { await Task.Delay(_interval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        CancellationTokenSource? lifetime;
        Task? loop;
        lock (_lifecycleGate)
        {
            _runRequested = false;
            lifetime = _lifetime;
            loop = _loop;
            if (lifetime is null || loop is null) return true;
            lifetime.CancelAfter(TimeSpan.Zero);
        }
        var stopped = true;
        if (loop is not null)
        {
            try { await loop.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { stopped = false; }
            catch (TimeoutException) { stopped = false; }
        }
        var priorStatus = _ledger.ImporterStatus;
        _ledger.SetImporterStatusImmediate(
            stopped ? AccountUsageImporterHealth.Stopped : AccountUsageImporterHealth.Degraded,
            LatestSuccessAt,
            stopped ? null : "ShutdownTimeout",
            stopped ? "ApplicationClosing" : "ShutdownTimeout",
            tokenSourceStale: null,
            tokenLastSuccessAt: _lastTokenSuccessAt,
            quotaLastSuccessAt: _lastQuotaSuccessAt,
            tokenErrorClass: priorStatus.TokenErrorClass,
            quotaErrorClass: priorStatus.QuotaErrorClass,
            tokenHealth: stopped ? AccountUsageImporterHealth.Stopped : priorStatus.TokenHealth,
            quotaHealth: stopped ? AccountUsageImporterHealth.Stopped : priorStatus.QuotaHealth,
            lifecycleErrorClass: stopped ? null : "ShutdownTimeout");
        if (stopped)
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_loop, loop))
                {
                    _loop = null;
                    _lifetime = null;
                    lifetime.Dispose();
                }
            }
        }
        return stopped;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            _disposed = true;
            _runRequested = false;
        }
        if (!await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            throw new TimeoutException("The account usage importer did not reach a durable stop boundary within five seconds.");
        _refreshGate.Dispose();
    }

    private DateTimeOffset? LatestSuccessAt => _lastTokenSuccessAt is null ? _lastQuotaSuccessAt
        : _lastQuotaSuccessAt is null ? _lastTokenSuccessAt
        : _lastTokenSuccessAt > _lastQuotaSuccessAt ? _lastTokenSuccessAt : _lastQuotaSuccessAt;

    private static string SafeErrorClass(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "UnauthorizedAccess",
        IOException => "IoFailure",
        TimeoutException => "Timeout",
        InvalidDataException => "InvalidData",
        _ => ex.GetType().Name.Length <= 80 ? ex.GetType().Name : "ImporterFailure"
    };
}
