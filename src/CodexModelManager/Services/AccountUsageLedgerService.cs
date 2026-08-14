using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class AccountUsageLedgerService : IDisposable
{
    public const string UnattributedAccountId = "__unattributed__";
    public const string UnlinkedProviderId = "__unlinked_provider__";
    private const int LedgerSchemaVersion = 4;
    private const int CursorSchemaVersion = 5;
    private const string OpenCodexSourceNamespace = "codex-total-manager:request-log.jsonl:v1";
    private const string DirectSourceNamespace = "direct:runtime-execution:v4";
    private const int MaximumPersistedStringLength = 240;
    private const int MaximumSourceLineBytes = 1024 * 1024;
    private const long MaximumSourceBatchBytes = 8L * 1024 * 1024;
    private const int MaximumSourceBatchLines = 2000;
    private const int AnchorWindowBytes = 256;
    private const int PrefixWindowBytes = 4096;
    private const int SourceDigestBlockBytes = 64 * 1024;
    private const int LedgerCommitmentBlockBytes = 1024 * 1024;
    private const int SourceMarkerSchemaVersion = 2;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] IdentityEntropy = Encoding.UTF8.GetBytes("CodexModelManager:account-ledger-identity:v1");
    private static readonly ConcurrentDictionary<string, object> IdentityKeyGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _dataDirectory;
    private readonly bool _sourceRequired;
    private readonly bool _sourceDisabled;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly JsonIndex<AccountUsageAttemptFact> _attemptIndex = new();
    private readonly JsonIndex<AccountQuotaSnapshotFact> _quotaIndex = new();
    private readonly JsonIndex<AccountQuotaBatchPrepare> _quotaPrepareIndex = new();
    private readonly JsonIndex<AccountQuotaBatchCommit> _quotaCommitIndex = new();
    private byte[]? _identityKey;
    private string? _identityKeyId;
    private AccountLedgerIdentityKeyState _identityKeyState;
    private bool _identityKeyUnavailableLatched;
    private readonly Dictionary<string, SchemaArtifactStamp> _schemaPreflightStamps = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AccountTokenAggregate> _cachedAggregates = Array.Empty<AccountTokenAggregate>();
    private IReadOnlyList<AccountUsageAttemptFact> _cachedRecent = Array.Empty<AccountUsageAttemptFact>();
    private RequestScopeUsageAggregate _cachedRequestScope = RequestScopeUsageAggregate.Empty;
    private RequestScopeUsageAggregate _cachedUnverifiedIdentityUsage = RequestScopeUsageAggregate.Empty;
    private IReadOnlyList<AccountQuotaSnapshotFact> _cachedLatestQuotaFacts = Array.Empty<AccountQuotaSnapshotFact>();
    private IReadOnlyList<AccountQuotaSnapshotFact> _cachedCommittedQuotaFacts = Array.Empty<AccountQuotaSnapshotFact>();
    private IReadOnlyList<AccountQuotaSnapshotView> _cachedHealthyQuotaViews = Array.Empty<AccountQuotaSnapshotView>();
    private readonly MembershipIndex _accountRequestMembership = new();
    private readonly MembershipIndex _scopeRequestMembership = new();
    private readonly AttemptProjectionCache _attemptProjection;
    private readonly QuotaProjectionCache _quotaProjection = new();
    private int _cachedIncompleteQuotaFactCount;
    private int _cachedCommittedQuotaFactCount;
    private int _cachedOrphanCommitCount;
    private int _cachedOrphanPrepareCount;
    private int _cachedInvalidQuotaValueCount;
    private long _attemptProjectionVersion = -1;
    private long _attemptProjectionRebuildGeneration = -1;
    private long _quotaProjectionVersion = -1;
    private long _quotaPrepareProjectionVersion = -1;
    private long _quotaCommitProjectionVersion = -1;
    private long _quotaProjectionRebuildGeneration = -1;
    private long _quotaPrepareProjectionRebuildGeneration = -1;
    private long _quotaCommitProjectionRebuildGeneration = -1;
    private int _quotaProjectionItemCount;
    private int _quotaPrepareProjectionItemCount;
    private int _quotaCommitProjectionItemCount;
    private int _cachedOverflowCount;
    private int _cachedStoredAttemptCount;
    private int _anomalyCount;
    private int _persistentQuotaIntegrityCount;
    private SegmentState? _anomalyState;
    private AccountUsageLedgerSnapshot _lastSnapshot = AccountUsageLedgerSnapshot.Empty;
    private long _fullIndexRebuildCount;
    private long _incrementalSegmentReadCount;
    private long _parsedLedgerLineCount;
    private long _noChangeRefreshCount;
    private long _sourceImportCount;
    private long _ledgerVerificationBytes;
    private long _attemptProjectionRowsProcessed;
    private long _quotaProjectionRowsProcessed;
    private bool _quotaSourceReadFailed;
    private bool _tokenSourceStale;
    private bool _coverageGapDetected;
    private string? _coverageGapMessage;
    private DateTimeOffset? _coverageGapFirstSeen;
    private bool _persistentTokenIntegrityIssue;
    private string? _persistentTokenIntegrityClass;
    private FileStream? _sourceContinuationStream;
    private string? _sourceContinuationIdentity;
    private string? _sourceContinuationGeneration;
    private long _sourceContinuationCreationUtcTicks;
    private long _sourceContinuationLastWriteUtcTicks;
    private AccountUsageImporterStatus _importerStatus = AccountUsageImporterStatus.NotStarted;
    private readonly object _snapshotStateGate = new();
    private readonly object _snapshotSubscriberGate = new();
    private readonly List<SnapshotSubscriber> _snapshotSubscribers = new();
    private long _snapshotRevision;
    private Action? SnapshotImmediateBarrierForTests { get; set; }
    private Action? ProjectionCheckpointRestoredBarrierForTests { get; set; }
    private Action? ProjectionRowAcceptedBarrierForTests { get; set; }
    private readonly object _checkpointGate = new();
    private bool _checkpointLoadAttempted;
    private long _checkpointLoadCount;
    private long _checkpointRebuildCount;
    private long _checkpointPublishCount;
    private long _checkpointValidationFailureCount;
    private (long Attempt, long Quota, long Prepare, long Commit) _publishedCheckpointVersions = (-1, -1, -1, -1);

    public AccountUsageLedgerService(
        string dataDirectory,
        string? sourcePath = null,
        Func<DateTimeOffset>? clock = null,
        bool sourceRequired = true,
        bool sourceDisabled = false)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("台账目录不能为空。", nameof(dataDirectory));
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _attemptIndex.Configure(Path.Combine(_dataDirectory, "account-token-attempts-v1.idx"));
        _quotaIndex.Configure(Path.Combine(_dataDirectory, "account-quota-facts-v1.idx"));
        _quotaPrepareIndex.Configure(Path.Combine(_dataDirectory, "account-quota-prepares-v1.idx"));
        _quotaCommitIndex.Configure(Path.Combine(_dataDirectory, "account-quota-commits-v1.idx"));
        _accountRequestMembership.Configure(Path.Combine(_dataDirectory, "account-request-membership-v1.idx"));
        _scopeRequestMembership.Configure(Path.Combine(_dataDirectory, "request-scope-membership-v1.idx"));
        _attemptProjection = new AttemptProjectionCache(_accountRequestMembership, _scopeRequestMembership);
        AttemptLockPath = Path.Combine(_dataDirectory, "account-token-attempts.lock");
        QuotaLockPath = Path.Combine(_dataDirectory, "account-quota-snapshots.lock");
        AnomalyLockPath = Path.Combine(_dataDirectory, "account-token-anomalies.lock");
        DerivedCacheLockPath = Path.Combine(_dataDirectory, "account-usage-derived-cache.lock");
        SourceIntegrityLockPath = Path.Combine(_dataDirectory, "account-token-source-integrity.lock");
        IdentityKeyPath = Path.Combine(_dataDirectory, "account-ledger-identity.key");
        IdentityDomainPath = Path.Combine(_dataDirectory, "account-ledger-key-domain.json");
        AnomalyPath = Path.Combine(_dataDirectory, "account-token-anomalies.jsonl");
        CursorPath = Path.Combine(_dataDirectory, "account-token-source-cursor.json");
        CursorRecoveryPath = Path.Combine(_dataDirectory, "account-token-source-generation.json");
        SourceInitializedPath = Path.Combine(_dataDirectory, "account-token-source-initialized.json");
        SourceIntegrityStatePath = Path.Combine(_dataDirectory, "account-token-source-integrity.json");
        SchemaRebuildRequiredPath = Path.Combine(_dataDirectory, "account-ledger-schema-rebuild-required.json");
        ProjectionCheckpointPath = Path.Combine(_dataDirectory, "account-usage-projection-v1.json");
        SourcePath = sourcePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTotalManager", "runtime-v3", "native-proxy", "request-log.jsonl");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _sourceRequired = sourceRequired;
        _sourceDisabled = sourceDisabled;
    }

    // Monthly partitioning is defined in UTC and is captured once per operation.
    public string AttemptLedgerPath => AttemptSegmentPath(UtcNow());
    public string QuotaLedgerPath => QuotaSegmentPath(UtcNow());
    public string QuotaPrepareLedgerPath => QuotaPrepareSegmentPath(UtcNow());
    public string QuotaCommitLedgerPath => QuotaCommitSegmentPath(UtcNow());
    public string AttemptLockPath { get; }
    public string QuotaLockPath { get; }
    public string AnomalyLockPath { get; }
    public string DerivedCacheLockPath { get; }
    public string SourceIntegrityLockPath { get; }
    public string IdentityKeyPath { get; }
    public string IdentityDomainPath { get; }
    public string AnomalyPath { get; }
    public string CursorPath { get; }
    public string CursorRecoveryPath { get; }
    public string SourceInitializedPath { get; }
    public string SourceIntegrityStatePath { get; }
    public string SchemaRebuildRequiredPath { get; }
    public string ProjectionCheckpointPath { get; }
    public string SourcePath { get; }
    public AccountUsageLedgerSnapshot LastSnapshot => Volatile.Read(ref _lastSnapshot);
    public AccountLedgerIdentityKeyState IdentityKeyState => _identityKeyState;
    public bool CoverageGapDetected => _coverageGapDetected;
    public bool PersistentTokenIntegrityIssue => _persistentTokenIntegrityIssue;
    public string? PersistentTokenIntegrityClass => _persistentTokenIntegrityClass;
    public bool SourceMustBeAvailable => !_sourceDisabled && (_sourceRequired
        || _attemptIndex.KnownCount > 0
        || GetSegments("account-token-attempts-*.jsonl", "account-token-attempts.jsonl").Count > 0);
    public AccountUsageImporterStatus ImporterStatus => Volatile.Read(ref _importerStatus);
    public AccountUsageLedgerDiagnostics Diagnostics
    {
        get
        {
            SnapshotSubscriber[] subscribers;
            lock (_snapshotSubscriberGate) subscribers = _snapshotSubscribers.ToArray();
            return new(
        Interlocked.Read(ref _fullIndexRebuildCount),
        Interlocked.Read(ref _incrementalSegmentReadCount),
        Interlocked.Read(ref _parsedLedgerLineCount),
        Interlocked.Read(ref _noChangeRefreshCount),
        Interlocked.Read(ref _sourceImportCount),
        Interlocked.Read(ref _ledgerVerificationBytes),
        Interlocked.Read(ref _attemptProjectionRowsProcessed),
        Interlocked.Read(ref _quotaProjectionRowsProcessed),
        0)
    {
        CheckpointLoadCount = Interlocked.Read(ref _checkpointLoadCount),
        CheckpointRebuildCount = Interlocked.Read(ref _checkpointRebuildCount),
        CheckpointPublishCount = Interlocked.Read(ref _checkpointPublishCount),
        CheckpointValidationFailureCount = Interlocked.Read(ref _checkpointValidationFailureCount),
        InMemoryFactObjectCount = _attemptIndex.Items.Count + _quotaIndex.Items.Count
                                  + _quotaPrepareIndex.Items.Count + _quotaCommitIndex.Items.Count
                                  + _quotaProjection.RetainedFactCount + _cachedRecent.Count,
        CompactIdempotencyEntryCount = _attemptIndex.KnownCount + _quotaIndex.KnownCount
                                       + _quotaPrepareIndex.KnownCount + _quotaCommitIndex.KnownCount
                                       + _accountRequestMembership.Count + _scopeRequestMembership.Count,
        QuotaFallbackSelectionCount = _quotaProjection.FallbackSelectionCount,
        QuotaFallbackCandidateRowsExamined = 0,
        DerivedIndexBytesWritten = _attemptIndex.BytesWritten + _quotaIndex.BytesWritten
                                   + _quotaPrepareIndex.BytesWritten + _quotaCommitIndex.BytesWritten
                                   + _accountRequestMembership.BytesWritten + _scopeRequestMembership.BytesWritten,
        DerivedIndexReplacementCount = _attemptIndex.ReplacementCount + _quotaIndex.ReplacementCount
                                       + _quotaPrepareIndex.ReplacementCount + _quotaCommitIndex.ReplacementCount
                                       + _accountRequestMembership.ReplacementCount + _scopeRequestMembership.ReplacementCount
        ,SnapshotSubscriberCount = subscribers.Length
        ,ActiveSnapshotSubscriberWorkers = subscribers.Count(item => item.IsRunning)
        ,PendingSnapshotSubscriberMailboxes = subscribers.Count(item => item.HasPending)
    };
        }
    }

    public event EventHandler<AccountUsageLedgerSnapshot>? SnapshotChanged
    {
        add
        {
            if (value is null) return;
            lock (_snapshotSubscriberGate) _snapshotSubscribers.Add(new SnapshotSubscriber(this, value));
        }
        remove
        {
            if (value is null) return;
            SnapshotSubscriber? removed = null;
            lock (_snapshotSubscriberGate)
            {
                for (var index = _snapshotSubscribers.Count - 1; index >= 0; index--)
                {
                    if (_snapshotSubscribers[index].Handler != value) continue;
                    removed = _snapshotSubscribers[index];
                    _snapshotSubscribers.RemoveAt(index);
                    break;
                }
            }
            removed?.Detach();
        }
    }
    public event EventHandler? SourceReadStarted;
    public event EventHandler<long>? SourceLengthCaptured;
    public event EventHandler? DurableCommitStarted;

    public async Task<AccountUsageIngestResult> IngestSourceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationNow = UtcNow();
            var segmentPath = AttemptSegmentPath(operationNow);
            await using var ledgerLock = await AcquireFileLockAsync(AttemptLockPath, cancellationToken).ConfigureAwait(false);
            await using var cacheLock = await AcquireFileLockAsync(DerivedCacheLockPath, cancellationToken).ConfigureAwait(false);
            var legacyTrackingMigration = LegacySourceTrackingCanMigrate();
            if (legacyTrackingMigration) MigrateLegacySourceTrackingArtifacts();
            EnsureIdentityKeyForWrite();
            TryLoadProjectionCheckpoint();
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await RefreshAttemptsAsync(cancellationToken).ConfigureAwait(false);
            var cursorRead = legacyTrackingMigration
                ? new CursorRead(null, null, true)
                : ReadCursorCore();
            PublishSignal(SourceReadStarted);
            cancellationToken.ThrowIfCancellationRequested();
            var scan = await Task.Run(
                () => ReadSourceBatch(cursorRead.Cursor, cursorRead.LegacyContractMigration, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            // Establish durable source history before any fact/anomaly append. A crash after the
            // first committed row but before cursor publication must fail closed after rotation.
            if (scan.Availability == AccountUsageSourceAvailability.Available && scan.NextCursor is not null)
                WriteSourceInitializedMarker(scan.NextCursor, scan.PrefixHash, scan.ContentBlockHashes, operationNow);
            var anomalies = new List<AccountUsageAnomaly>();
            if (cursorRead.Anomaly is not null) anomalies.Add(cursorRead.Anomaly);
            if (cursorRead.Anomaly?.Kind == "source_coverage_gap")
            {
                _coverageGapDetected = true;
                _coverageGapMessage = "source cursor/generation 双重证据缺失；无法证明离线轮转期间完整覆盖";
                _coverageGapFirstSeen ??= operationNow;
                PersistSourceIntegrityState(true, "CoverageGap", _coverageGapMessage, operationNow);
            }
            anomalies.AddRange(scan.BadLines.Select(line => SourceAnomaly(line.Kind, line.Offset, line.Message)));
            if (scan.CoverageGapDetected)
            {
                _coverageGapDetected = true;
                _coverageGapMessage = "usage 源离线轮转且旧尾不可定位；覆盖存在缺口";
                _coverageGapFirstSeen ??= operationNow;
                PersistSourceIntegrityState(true, "CoverageGap", _coverageGapMessage, operationNow);
                anomalies.Add(new AccountUsageAnomaly(
                    LedgerSchemaVersion, "source_coverage_gap", null, null, null, scan.PreviousOffset,
                    $"source-generation:{scan.Generation[..Math.Min(12, scan.Generation.Length)]}",
                    "旧 usage source identity 的已知未读尾部不可定位；后续从新文件 0 开始",
                    operationNow));
            }
            var envelopes = new List<ExecutionEnvelope>();
            foreach (var line in scan.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var execution = OpenCodexClient.ParseLatestRouteExecution($"[{line.Text}]");
                    if (execution is null)
                    {
                        anomalies.Add(SourceAnomaly("unrecognized_source_line", line.Offset, "完整 JSON 行没有可识别的请求对象"));
                        continue;
                    }
                    // The complete raw line hash is the semantic fallback identity when requestId is absent.
                    // Generation/offset are provenance only and must never make a replay bill twice.
                    envelopes.Add(new ExecutionEnvelope(execution, OpenCodexSourceNamespace,
                        HmacDigest("source-event:v1", new { line = line.Text }),
                        Hash(Canonical(new { scan.Generation, line.Offset })), false));
                }
                catch (Exception ex) when (ex is JsonException or DecoderFallbackException or FormatException)
                {
                    anomalies.Add(SourceAnomaly("bad_source_line", line.Offset, "中间坏行已隔离：" + Safe(ex.Message, "无法解析")));
                }
            }
            if (anomalies.Any(item => item.Kind is "bad_source_line" or "oversized_source_line" or "unrecognized_source_line"
                                      or "truncated_archived_tail"))
                PersistSourceIntegrityState(false, "SourceMalformed",
                    "usage source contained malformed, oversized, unrecognized, or truncated evidence", operationNow);
            var append = await AppendFactsCoreAsync(existing, BuildFacts(envelopes, operationNow), anomalies,
                segmentPath, cancellationToken).ConfigureAwait(false);
            if (append.ConflictingReplayCount > 0)
                PersistSourceIntegrityState(false, "ReplayCollision",
                    "same semantic source event produced conflicting payload facts", operationNow);
            if (anomalies.Any(item => item.Kind == "ambiguous_duplicate_without_request_id"))
                PersistSourceIntegrityState(false, "AmbiguousIdentity",
                    "duplicate source events without a stable producer request/event id cannot be audited exactly", operationNow);
            if (anomalies.Count > 0)
                await AppendAnomaliesAsync(anomalies, cancellationToken).ConfigureAwait(false);
            UpdateIdentityDomainManifestAfterCommit();
            if (scan.NextCursor is not null) WriteCursorCore(scan.NextCursor);
            Interlocked.Increment(ref _sourceImportCount);
            await RefreshAttemptsAsync(CancellationToken.None).ConfigureAwait(false);
            PublishIfFullyInitialized(80, false);
            return new AccountUsageIngestResult(
                append.CandidateCount, append.AppendedCount, append.DuplicateCount, append.ConflictingReplayCount,
                existing.BadLineCount, anomalies.Count(item => item.Kind is "bad_source_line" or "oversized_source_line" or "unrecognized_source_line"),
                scan.SourceResetDetected,
                scan.SourceContractMigrated
                    ? "旧用量来源已安全切换到总管家请求日志；旧账保留，切换前已有的新日志只作基线，不重复归账"
                    : $"逐 attempt 台账新增 {append.AppendedCount} 条，重复 {append.DuplicateCount} 条，碰撞 anomaly {append.ConflictingReplayCount} 条")
            {
                SourceAvailability = scan.Availability
                ,CoverageGapDetected = scan.CoverageGapDetected
                ,SourceContractMigrated = scan.SourceContractMigrated
            };
        }
        finally { _gate.Release(); }
    }

    public Task<AccountUsageIngestResult> IngestExecutionsAsync(
        IEnumerable<RuntimeRouteExecution> executions,
        CancellationToken cancellationToken = default) =>
        IngestExecutionsAsync(executions, externalEventIdentity: null, cancellationToken);

    public async Task<AccountUsageIngestResult> IngestExecutionsAsync(
        IEnumerable<RuntimeRouteExecution> executions,
        string? externalEventIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationNow = UtcNow();
            var segmentPath = AttemptSegmentPath(operationNow);
            await using var ledgerLock = await AcquireFileLockAsync(AttemptLockPath, cancellationToken).ConfigureAwait(false);
            await using var cacheLock = await AcquireFileLockAsync(DerivedCacheLockPath, cancellationToken).ConfigureAwait(false);
            EnsureIdentityKeyForWrite();
            TryLoadProjectionCheckpoint();
            var existing = await RefreshAttemptsAsync(cancellationToken).ConfigureAwait(false);
            var executionArray = executions.ToArray();
            if (string.IsNullOrWhiteSpace(externalEventIdentity)
                && executionArray.Any(execution => string.IsNullOrWhiteSpace(execution.RequestIdentityMaterial)
                                                   && string.IsNullOrWhiteSpace(execution.RequestId)))
                throw new ArgumentException("无 requestId 的直接导入必须提供稳定 externalEventIdentity；拒绝生成可重复计费的随机弱键。",
                    nameof(externalEventIdentity));
            var sharedIdentity = string.IsNullOrWhiteSpace(externalEventIdentity)
                ? null
                : HmacDigest("external-event:v1", new { externalEventIdentity });
            var envelopes = executionArray.Select((execution, index) => new ExecutionEnvelope(
                execution,
                DirectSourceNamespace,
                HmacDigest("direct-event:v2", new
                {
                    external = sharedIdentity,
                    requestIdentity = execution.RequestIdentityMaterial ?? execution.RequestId,
                    semantic = DirectExecutionIdentity(execution)
                }),
                Hash(Canonical(new { direct = true, index })),
                sharedIdentity is not null)).ToArray();
            var anomalies = new List<AccountUsageAnomaly>();
            var append = await AppendFactsCoreAsync(existing, BuildFacts(envelopes, operationNow), anomalies,
                segmentPath, cancellationToken).ConfigureAwait(false);
            if (append.ConflictingReplayCount > 0)
                PersistSourceIntegrityState(false, "ReplayCollision",
                    "same semantic direct event produced conflicting payload facts", operationNow);
            if (anomalies.Count > 0)
                await AppendAnomaliesAsync(anomalies, cancellationToken).ConfigureAwait(false);
            UpdateIdentityDomainManifestAfterCommit();
            await RefreshAttemptsAsync(CancellationToken.None).ConfigureAwait(false);
            PublishIfFullyInitialized(80, false);
            return new AccountUsageIngestResult(append.CandidateCount, append.AppendedCount, append.DuplicateCount,
                append.ConflictingReplayCount, existing.BadLineCount, 0, false,
                $"逐 attempt 台账新增 {append.AppendedCount} 条，重复 {append.DuplicateCount} 条，碰撞 anomaly {append.ConflictingReplayCount} 条");
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountQuotaIngestResult> IngestQuotaSnapshotsAsync(
        IEnumerable<AccountQuotaSnapshotFact> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operationNow = UtcNow();
            var segmentPath = QuotaSegmentPath(operationNow);
            await using var quotaLock = await AcquireFileLockAsync(QuotaLockPath, cancellationToken).ConfigureAwait(false);
            await using var cacheLock = await AcquireFileLockAsync(DerivedCacheLockPath, cancellationToken).ConfigureAwait(false);
            EnsureIdentityKeyForWrite();
            TryLoadProjectionCheckpoint();
            var refreshedQuota = await RefreshQuotaSetAsync(cancellationToken).ConfigureAwait(false);
            var existing = refreshedQuota.Facts;
            var existingPrepares = refreshedQuota.Prepares;
            var existingCommits = refreshedQuota.Commits;
            _ = BuildSnapshot(80, false);
            var pendingKnown = new Dictionary<string, string>(StringComparer.Ordinal);
            var pendingPrepares = new Dictionary<string, string>(StringComparer.Ordinal);
            var pendingCommits = new Dictionary<string, string>(StringComparer.Ordinal);
            var candidates = snapshots.Select(NormalizeQuota).ToArray();
            using var quotaFactIndexSession = _quotaIndex.OpenSession();
            using var quotaPrepareIndexSession = _quotaPrepareIndex.OpenSession();
            using var quotaCommitIndexSession = _quotaCommitIndex.OpenSession();
            var quotaAnomalies = candidates
                .Where(candidate => candidate.ValueValidation == QuotaValueValidationState.InvalidRange)
                .Select(candidate => new AccountUsageAnomaly(
                    LedgerSchemaVersion, "quota_value_invalid", candidate.IdempotencyKey, null, candidate.PayloadHash,
                    null, SafeSourceKind(candidate.Source), "额度上游值超出声明单位的有效范围；保留事实但不作为正常额度", operationNow))
                .Concat(candidates
                    .Where(candidate => candidate.Availability == AccountQuotaAvailability.ReadFailed
                                        && string.Equals(candidate.ErrorClass, "MissingPeriodKey", StringComparison.Ordinal))
                    .Select(candidate => new AccountUsageAnomaly(
                        LedgerSchemaVersion, "quota_window_structure_invalid", candidate.IdempotencyKey, null,
                        candidate.PayloadHash, null, SafeSourceKind(candidate.Source),
                        "Provided quota snapshot contained no complete stable PeriodKey window set; prior committed value remains stale.",
                        operationNow)))
                .ToArray();
            if (candidates.Any(candidate => candidate.AccountAttributed
                                            && !string.Equals(candidate.AccountKeyId, _identityKeyId, StringComparison.Ordinal)))
                throw new AccountLedgerIdentityKeyUnavailableException(
                    "额度 candidate 的 AccountKeyId 与当前身份键不一致；整批拒绝。 ");
            var appendedCount = 0;
            var duplicates = 0;
            foreach (var batch in candidates.GroupBy(item => new QuotaBatchKey(
                         item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch)))
            {
                var normalizedFacts = new List<AccountQuotaSnapshotFact>();
                foreach (var period in batch.GroupBy(item => item.PeriodKey, StringComparer.Ordinal))
                {
                    var variants = period.GroupBy(item => item.PayloadHash, StringComparer.Ordinal).ToArray();
                    if (variants.Length != 1)
                        throw new InvalidDataException("同一额度批次的 PeriodKey 出现冲突 payload；整批拒绝。 ");
                    normalizedFacts.Add(variants[0].First());
                    duplicates += variants[0].Count() - 1;
                }
                var batchFacts = normalizedFacts.OrderBy(item => item.PeriodKey, StringComparer.Ordinal).ToArray();
                var diskBatchFacts = _quotaProjection.GetOpenFacts(batch.Key)
                    .OrderBy(item => item.PeriodKey, StringComparer.Ordinal).ToArray();
                var intendedCommit = CreateQuotaBatchCommit(batchFacts, operationNow);
                var intendedPrepare = CreateQuotaBatchPrepare(batchFacts, operationNow);
                if (_quotaPrepareIndex.TryGetPayload(intendedPrepare.IdempotencyKey, out var preparedPayload)
                    || pendingPrepares.TryGetValue(intendedPrepare.IdempotencyKey, out preparedPayload))
                {
                    if (!FixedHexEquals(preparedPayload, intendedPrepare.PayloadHash))
                        throw new InvalidDataException("额度 prepare 已固定完整批次；子集或 mutation 重试整批拒绝。 ");
                }
                else
                {
                    await AppendJsonLinesCoreAsync(QuotaPrepareSegmentPath(operationNow), new[] { intendedPrepare }, cancellationToken)
                        .ConfigureAwait(false);
                    pendingPrepares[intendedPrepare.IdempotencyKey] = intendedPrepare.PayloadHash;
                }
                if (_quotaCommitIndex.TryGetPayload(intendedCommit.IdempotencyKey, out var committedPayload)
                    || pendingCommits.TryGetValue(intendedCommit.IdempotencyKey, out committedPayload))
                {
                    if (!FixedHexEquals(committedPayload, intendedCommit.PayloadHash)
                        || batchFacts.Any(candidate => !_quotaIndex.TryGetPayload(
                            candidate.IdempotencyKey, out var storedPayload)
                            || !FixedHexEquals(storedPayload, candidate.PayloadHash)))
                        throw new InvalidDataException("已提交额度批次不可变；mutation 整批拒绝。 ");
                    duplicates += batchFacts.Length;
                    continue;
                }
                if (diskBatchFacts.Any(existingFact => batchFacts.All(candidate =>
                        !string.Equals(candidate.IdempotencyKey, existingFact.IdempotencyKey, StringComparison.Ordinal))))
                    throw new InvalidDataException("未提交额度批次含本次规范集合之外的 period；整批拒绝。 ");
                var append = new List<AccountQuotaSnapshotFact>();
                foreach (var candidate in batchFacts)
                {
                    if (_quotaIndex.TryGetPayload(candidate.IdempotencyKey, out var priorPayload)
                        || pendingKnown.TryGetValue(candidate.IdempotencyKey, out priorPayload))
                    {
                        if (FixedHexEquals(priorPayload, candidate.PayloadHash)) duplicates++;
                        else throw new InvalidDataException("额度批次事实与磁盘同 key 异 payload；整批拒绝且不写 commit。 ");
                        continue;
                    }
                    append.Add(candidate);
                }
                if (append.Count > 0)
                {
                    await AppendJsonLinesCoreAsync(segmentPath, append, cancellationToken).ConfigureAwait(false);
                    appendedCount += append.Count;
                    foreach (var item in append) pendingKnown[item.IdempotencyKey] = item.PayloadHash;
                }
                // Facts are durable before the sole completeness truth (commit) is appended.
                await AppendJsonLinesCoreAsync(QuotaCommitSegmentPath(operationNow), new[] { intendedCommit }, cancellationToken)
                    .ConfigureAwait(false);
                pendingCommits[intendedCommit.IdempotencyKey] = intendedCommit.PayloadHash;
            }
            await RefreshQuotaSetAsync(CancellationToken.None).ConfigureAwait(false);
            if (quotaAnomalies.Length > 0)
                await AppendAnomaliesAsync(quotaAnomalies, CancellationToken.None).ConfigureAwait(false);
            UpdateIdentityDomainManifestAfterCommit();
            _quotaSourceReadFailed = false;
            PublishIfFullyInitialized(80, false);
            return new AccountQuotaIngestResult(candidates.Length, appendedCount, duplicates,
                existing.BadLineCount, $"独立额度快照新增 {appendedCount} 条；Token 台账未参与额度计算");
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountUsageLedgerSnapshot> ReadAsync(
        int recentAttemptLimit = 80,
        bool quotaSourceReadFailed = false,
        CancellationToken cancellationToken = default)
    {
        if (recentAttemptLimit <= 0) throw new ArgumentOutOfRangeException(nameof(recentAttemptLimit));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureIdentityKeyForWrite();
            AccountUsageLedgerSnapshot snapshot;
            await using (await AcquireFileLockAsync(AttemptLockPath, cancellationToken).ConfigureAwait(false))
            await using (await AcquireFileLockAsync(QuotaLockPath, cancellationToken).ConfigureAwait(false))
            await using (await AcquireFileLockAsync(DerivedCacheLockPath, cancellationToken).ConfigureAwait(false))
            {
                TryLoadProjectionCheckpoint();
                await RefreshAttemptsAsync(cancellationToken).ConfigureAwait(false);
                await RefreshQuotaSetAsync(cancellationToken).ConfigureAwait(false);
                if (quotaSourceReadFailed) _quotaSourceReadFailed = true;
                snapshot = PublishSnapshot(BuildSnapshot(recentAttemptLimit, quotaSourceReadFailed));
                WriteProjectionCheckpoint(snapshot);
            }
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task SetImporterStatusAsync(
        AccountUsageImporterHealth health,
        DateTimeOffset? lastSuccessAt,
        string? lastErrorClass,
        string? stoppedReason,
        bool? tokenSourceStale = null,
        DateTimeOffset? tokenLastSuccessAt = null,
        DateTimeOffset? quotaLastSuccessAt = null,
        string? tokenErrorClass = null,
        string? quotaErrorClass = null,
        AccountUsageImporterHealth? tokenHealth = null,
        AccountUsageImporterHealth? quotaHealth = null,
        string? lifecycleErrorClass = null,
        CancellationToken cancellationToken = default)
    {
        AccountUsageLedgerSnapshot? snapshot = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = CreateImporterStatus(health, lastSuccessAt, lastErrorClass, stoppedReason,
                tokenLastSuccessAt, quotaLastSuccessAt, tokenErrorClass, quotaErrorClass, tokenHealth, quotaHealth,
                lifecycleErrorClass);
            Volatile.Write(ref _importerStatus, status);
            if (tokenSourceStale is not null) _tokenSourceStale = tokenSourceStale.Value;
            if (_attemptIndex.Initialized && _quotaIndex.Initialized
                && _quotaPrepareIndex.Initialized && _quotaCommitIndex.Initialized)
                snapshot = PublishSnapshot(BuildSnapshot(80, _quotaSourceReadFailed));
        }
        finally { _gate.Release(); }
    }

    internal void SetImporterStatusImmediate(
        AccountUsageImporterHealth health,
        DateTimeOffset? lastSuccessAt,
        string? lastErrorClass,
        string? stoppedReason,
        bool? tokenSourceStale = null,
        DateTimeOffset? tokenLastSuccessAt = null,
        DateTimeOffset? quotaLastSuccessAt = null,
        string? tokenErrorClass = null,
        string? quotaErrorClass = null,
        AccountUsageImporterHealth? tokenHealth = null,
        AccountUsageImporterHealth? quotaHealth = null,
        string? lifecycleErrorClass = null)
    {
        var status = CreateImporterStatus(health, lastSuccessAt, lastErrorClass, stoppedReason,
            tokenLastSuccessAt, quotaLastSuccessAt, tokenErrorClass, quotaErrorClass, tokenHealth, quotaHealth,
            lifecycleErrorClass);
        lock (_snapshotStateGate)
        {
            Volatile.Write(ref _importerStatus, status);
            if (tokenSourceStale is not null) Volatile.Write(ref _tokenSourceStale, tokenSourceStale.Value);
            var current = LastSnapshot;
            SnapshotImmediateBarrierForTests?.Invoke();
            PublishSnapshotUnderStateLock(current with
            {
                ImporterStatus = status,
                TokenSourceStale = _tokenSourceStale
            });
        }
    }

    private AccountUsageImporterStatus CreateImporterStatus(
        AccountUsageImporterHealth health,
        DateTimeOffset? lastSuccessAt,
        string? lastErrorClass,
        string? stoppedReason,
        DateTimeOffset? tokenLastSuccessAt,
        DateTimeOffset? quotaLastSuccessAt,
        string? tokenErrorClass,
        string? quotaErrorClass,
        AccountUsageImporterHealth? tokenHealth,
        AccountUsageImporterHealth? quotaHealth,
        string? lifecycleErrorClass = null) => new(
        health,
        lastSuccessAt is null ? null : Utc(lastSuccessAt.Value),
        SafeNullable(lastErrorClass),
        _identityKeyState.ToString(),
        SafeNullable(stoppedReason))
    {
        TokenLastSuccessAt = tokenLastSuccessAt is null ? null : Utc(tokenLastSuccessAt.Value),
        QuotaLastSuccessAt = quotaLastSuccessAt is null ? null : Utc(quotaLastSuccessAt.Value),
        TokenErrorClass = SafeNullable(tokenErrorClass),
        QuotaErrorClass = SafeNullable(quotaErrorClass),
        TokenHealth = tokenHealth ?? health,
        QuotaHealth = quotaHealth ?? health,
        LifecycleErrorClass = SafeNullable(lifecycleErrorClass)
    };

    public string CreateQuotaObservationBatch(
        string? runtimeProviderId,
        string? accountIdentity,
        DateTimeOffset observedAt,
        bool sourceObservedAt,
        string fetchIdentity,
        string? observationScope = null)
    {
        var provider = NormalizeProvider(runtimeProviderId, linked: !string.IsNullOrWhiteSpace(runtimeProviderId));
        var account = CreateAccountIdentity(provider, accountIdentity, attributed: !string.IsNullOrWhiteSpace(accountIdentity));
        return Hash(Canonical(new
        {
            provider,
            account.StableIdentity,
            observedAt = Utc(observedAt),
            sourceObservedAt,
            observationScope = NormalizeObservationScope(observationScope),
            fetchIdentity = SafeOpaque(fetchIdentity)
        }));
    }

    public AccountQuotaSnapshotFact CreateQuotaSnapshot(
        string? runtimeProviderId,
        bool providerLinked,
        string? accountIdentity,
        string periodKey,
        string displayLabel,
        decimal? value,
        string unit,
        AccountQuotaAvailability availability,
        DateTimeOffset observedAt,
        bool sourceObservedAt,
        DateTimeOffset localObservedAt,
        bool sourceStale,
        string observationBatch,
        bool accountObservationComplete,
        string? errorClass,
        string source,
        AccountQuotaProvenance provenance = AccountQuotaProvenance.RelayReported,
        DateTimeOffset? resetAtUtc = null,
        string? resetLabel = null,
        QuotaResetState resetState = QuotaResetState.NotProvided,
        string? observationScope = null)
    {
        var provider = NormalizeProvider(runtimeProviderId, providerLinked);
        var attributed = !string.IsNullOrWhiteSpace(accountIdentity);
        var account = CreateAccountIdentity(provider, accountIdentity, attributed);
        return NormalizeQuota(new AccountQuotaSnapshotFact(
            LedgerSchemaVersion, string.Empty, string.Empty, provider, providerLinked,
            account.DisplayLabel, attributed, account.KeyVersion, account.KeyId, account.StableIdentity,
            SafePeriodKey(periodKey, availability), Safe(displayLabel, availability == AccountQuotaAvailability.NotProvided ? "未提供" : "额度"),
            value, Safe(unit, "unknown"), availability, Utc(observedAt), sourceObservedAt, Utc(localObservedAt),
            sourceStale, SafeOpaque(observationBatch), accountObservationComplete, SafeNullable(errorClass),
            Safe(source, "unknown"), provenance, UtcNow())
        {
            ResetAtUtc = resetAtUtc is null ? null : Utc(resetAtUtc.Value),
            ResetLabel = SafeNullable(resetLabel),
            ResetState = resetState,
            ObservationScope = NormalizeObservationScope(observationScope)
        });
    }

    public AccountQuotaSnapshotFact CreateMissingAccountTombstone(
        AccountQuotaSnapshotFact prior,
        DateTimeOffset observedAt,
        string fetchIdentity)
    {
        if (!prior.AccountAttributed || string.IsNullOrWhiteSpace(prior.StableAccountIdentity))
            throw new ArgumentException("只有已归因历史账号可创建消失 tombstone。", nameof(prior));
        EnsureIdentityKeyForWrite();
        if (!string.Equals(prior.AccountKeyId, _identityKeyId, StringComparison.Ordinal))
            throw new AccountLedgerIdentityKeyUnavailableException("历史额度账号键域与当前 identity key 不一致。");
        var batch = Hash(Canonical(new
        {
            kind = "provider_account_manifest_tombstone",
            prior.ProviderId,
            prior.StableAccountIdentity,
            prior.ObservationScope,
            observedAt = Utc(observedAt).ToString("O"),
            fetchIdentity
        }));
        return NormalizeQuota(prior with
        {
            IdempotencyKey = string.Empty,
            PayloadHash = string.Empty,
            PeriodKey = string.Empty,
            DisplayLabel = "本轮完整账号集合未返回该账号",
            Value = null,
            Unit = "unknown",
            Availability = AccountQuotaAvailability.NotProvided,
            ObservedAt = Utc(observedAt),
            SourceObservedAt = false,
            LocalObservedAt = Utc(observedAt),
            SourceStale = false,
            ObservationBatch = batch,
            AccountObservationComplete = false,
            ErrorClass = null,
            Source = "provider account manifest",
            RecordedAt = UtcNow(),
            ResetAtUtc = null,
            ResetLabel = null,
            ResetState = QuotaResetState.NotProvided
        });
    }

    public AccountQuotaSnapshotFact CreateScopeReadFailureOverlay(
        AccountQuotaSnapshotFact prior,
        DateTimeOffset observedAt,
        string errorClass,
        string fetchIdentity)
    {
        if (!prior.AccountAttributed || prior.Availability != AccountQuotaAvailability.Provided)
            throw new ArgumentException("read-failure overlay requires a prior attributed provided fact", nameof(prior));
        EnsureIdentityKeyForWrite();
        if (!string.Equals(prior.AccountKeyId, _identityKeyId, StringComparison.Ordinal))
            throw new AccountLedgerIdentityKeyUnavailableException("prior quota account key domain does not match current identity key");
        var batch = Hash(Canonical(new
        {
            kind = "scope_read_failed",
            prior.ProviderId,
            prior.ObservationScope,
            prior.StableAccountIdentity,
            observedAt = Utc(observedAt).ToString("O"),
            fetchIdentity = SafeOpaque(fetchIdentity)
        }));
        return NormalizeQuota(prior with
        {
            IdempotencyKey = string.Empty,
            PayloadHash = string.Empty,
            PeriodKey = string.Empty,
            DisplayLabel = "读取失败，保留旧额度快照",
            Value = null,
            Unit = "unknown",
            Availability = AccountQuotaAvailability.ReadFailed,
            ObservedAt = Utc(observedAt),
            SourceObservedAt = false,
            LocalObservedAt = Utc(observedAt),
            SourceStale = true,
            ObservationBatch = batch,
            ErrorClass = Safe(errorClass, "ScopeReadFailed"),
            Source = "structured pool roster health",
            RecordedAt = UtcNow(),
            ResetAtUtc = null,
            ResetLabel = null,
            ResetState = QuotaResetState.NotProvided
        });
    }

    private IEnumerable<AccountUsageAttemptFact> BuildFacts(IEnumerable<ExecutionEnvelope> envelopes, DateTimeOffset recordedAt)
    {
        foreach (var envelope in envelopes)
        {
            var execution = envelope.Execution;
            var requestIdentityMaterial = !string.IsNullOrWhiteSpace(execution.RequestIdentityMaterial)
                ? execution.RequestIdentityMaterial
                : !string.IsNullOrWhiteSpace(execution.RequestId) ? execution.RequestId : null;
            DateTimeOffset? occurredAt = execution.Timestamp is null ? null : Utc(execution.Timestamp.Value);
            var requestIdentity = CreateRequestIdentity(envelope, requestIdentityMaterial);
            foreach (var attempt in execution.Attempts)
                yield return CreateAttemptFact(envelope, execution, attempt, requestIdentity,
                    occurredAt, recordedAt, requestLevelUsage: false);
            if (execution.RequestLevelTokenUsage is not null && execution.Attempts.Count > 1)
            {
                var requestAttempt = new RuntimeRouteAttempt(
                    0, "__request_scope__", "请求级未归属", null, UnattributedAccountId, RuntimeAccountIdentitySource.Unknown,
                    execution.RequestedModel, null, execution.DurationMs,
                    null, null, RuntimeFailoverReason.Unknown,
                    false, RuntimeAttemptSelectionEvidence.None, execution.RequestLevelTokenUsage);
                yield return CreateAttemptFact(envelope, execution, requestAttempt, requestIdentity,
                    occurredAt, recordedAt, requestLevelUsage: true);
            }
        }
    }

    private AccountUsageAttemptFact CreateAttemptFact(
        ExecutionEnvelope envelope,
        RuntimeRouteExecution execution,
        RuntimeRouteAttempt attempt,
        RequestIdentity requestIdentity,
        DateTimeOffset? occurredAt,
        DateTimeOffset recordedAt,
        bool requestLevelUsage)
    {
        var provider = NormalizeProvider(attempt.ProviderId, true);
        var identityMaterial = !string.IsNullOrWhiteSpace(attempt.AccountIdentityMaterial)
            ? attempt.AccountIdentityMaterial
            : !string.IsNullOrWhiteSpace(attempt.AccountId) ? attempt.AccountId : null;
        var attributed = !requestLevelUsage
                         && attempt.AccountIdentitySource == RuntimeAccountIdentitySource.ExplicitAccountId
                         && identityMaterial is not null;
        var account = CreateAccountIdentity(provider, identityMaterial, attributed);
        var sourceEventIdentity = SafeOpaque(envelope.EventIdentity);
        var fact = new AccountUsageAttemptFact(
            LedgerSchemaVersion, string.Empty, string.Empty, requestIdentity.DisplayLabel, requestIdentity.Value,
            attempt.Ordinal, requestLevelUsage, provider, account.DisplayLabel, attributed,
            account.KeyVersion, account.KeyId, account.StableIdentity,
            attributed ? RuntimeAccountIdentitySource.ExplicitAccountId : RuntimeAccountIdentitySource.Unknown,
            Safe(attempt.Model, "unknown"), Safe(execution.RequestedModel, "unknown"), occurredAt,
            attempt.Outcome, attempt.HttpStatus, attempt.FailoverReason, attempt.Selected,
            attempt.SelectionEvidence, execution.SelectionBasis,
            SafeNullable(attempt.ErrorCode), SafeNullable(attempt.ErrorMessage), SanitizeUsage(attempt.TokenUsage),
            envelope.SourceNamespace, sourceEventIdentity, Safe(envelope.SourceNamespace, "unknown"),
            requestIdentity.Verified && occurredAt is not null && attributed && attempt.TokenUsage is not null
                ? AccountUsageEvidenceStrength.Strong
                : requestIdentity.Verified && occurredAt is not null ? AccountUsageEvidenceStrength.Moderate : AccountUsageEvidenceStrength.Weak,
            recordedAt);
        var normalizedFact = fact with
        {
            IdentityVerified = requestIdentity.Verified,
            RequestKeyVersion = requestIdentity.KeyVersion,
            RequestKeyId = requestIdentity.KeyId
        };
        return normalizedFact with
        {
            PayloadHash = Hash(AttemptPayloadCanonical(normalizedFact)),
            IdempotencyKey = Hash(AttemptIdentityCanonical(normalizedFact))
        };
    }

    private async Task<AppendResult> AppendFactsCoreAsync(
        JsonLineRead<AccountUsageAttemptFact> existing,
        IEnumerable<AccountUsageAttemptFact> incoming,
        ICollection<AccountUsageAnomaly> anomalies,
        string segmentPath,
        CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = incoming.ToArray();
        var append = new List<AccountUsageAttemptFact>();
        var duplicate = 0;
        var collision = 0;
        using var indexSession = _attemptIndex.OpenSession();
        foreach (var candidate in candidates)
        {
            if (_attemptIndex.TryGetPayload(candidate.IdempotencyKey, out var priorPayload)
                || pending.TryGetValue(candidate.IdempotencyKey, out priorPayload))
            {
                if (FixedHexEquals(priorPayload, candidate.PayloadHash))
                {
                    duplicate++;
                    // A cursor recovery or an explicit stable-event retry is a normal idempotent replay.
                    // It must not append another sticky anomaly or degrade the token domain forever.
                }
                else
                {
                    collision++;
                    anomalies.Add(new AccountUsageAnomaly(
                        LedgerSchemaVersion, "idempotency_payload_collision", candidate.IdempotencyKey,
                        priorPayload, candidate.PayloadHash, null, SafeSourceKind(candidate.Source),
                        "same semantic attempt identity carried a conflicting payload; first durable fact retained",
                        UtcNow()));
                }
                continue;
            }
            pending[candidate.IdempotencyKey] = candidate.PayloadHash;
            append.Add(candidate);
        }
        if (append.Count > 0) await AppendJsonLinesCoreAsync(segmentPath, append, cancellationToken).ConfigureAwait(false);
        return new AppendResult(candidates.Length, append.Count, duplicate, collision);
    }

    private async Task<JsonLineRead<AccountUsageAttemptFact>> RefreshAttemptsAsync(CancellationToken cancellationToken)
    {
        var changed = await Task.Run(() =>
        {
            using var accountMembershipSession = _accountRequestMembership.OpenSession();
            using var scopeMembershipSession = _scopeRequestMembership.OpenSession();
            return RefreshIndexCore(
                _attemptIndex,
                GetSegments("account-token-attempts-*.jsonl", "account-token-attempts.jsonl"),
                ValidateAttemptIntegrity,
                item => item.IdempotencyKey,
                item => item.PayloadHash,
                cancellationToken,
                onRebuild: _attemptProjection.Reset,
                onAccepted: item =>
                {
                    _attemptProjection.AppendOne(item);
                    Interlocked.Increment(ref _attemptProjectionRowsProcessed);
                });
        }, cancellationToken).ConfigureAwait(false);
        _attemptProjection.FlushMembership();
        _attemptProjectionRebuildGeneration = _attemptIndex.RebuildGeneration;
        if (!changed) Interlocked.Increment(ref _noChangeRefreshCount);
        return _attemptIndex.Read();
    }

    private async Task<(JsonLineRead<AccountQuotaSnapshotFact> Facts,
        JsonLineRead<AccountQuotaBatchPrepare> Prepares,
        JsonLineRead<AccountQuotaBatchCommit> Commits)> RefreshQuotaSetAsync(CancellationToken cancellationToken)
    {
        var factPaths = GetSegments("account-quota-snapshots-*.jsonl", "account-quota-snapshots.jsonl");
        var preparePaths = GetSegments("account-quota-prepares-*.jsonl", "account-quota-prepares.jsonl");
        var commitPaths = GetSegments("account-quota-commits-*.jsonl", "account-quota-commits.jsonl");
        if (IndexNeedsRebuild(_quotaIndex, factPaths)
            || IndexNeedsRebuild(_quotaPrepareIndex, preparePaths)
            || IndexNeedsRebuild(_quotaCommitIndex, commitPaths))
        {
            _quotaIndex.Clear();
            _quotaPrepareIndex.Clear();
            _quotaCommitIndex.Clear();
            _quotaProjection.Reset();
        }
        try
        {
            var facts = await RefreshQuotaAsync(cancellationToken).ConfigureAwait(false);
            var prepares = await RefreshQuotaPreparesAsync(cancellationToken).ConfigureAwait(false);
            var commits = await RefreshQuotaCommitsAsync(cancellationToken).ConfigureAwait(false);
            var recoveredCommits = _quotaProjection.GetRecoverablePreparedBatches()
                .Select(batch => CreateQuotaBatchCommit(batch, UtcNow()))
                .Where(commit => !_quotaCommitIndex.TryGetPayload(commit.IdempotencyKey, out _))
                .ToArray();
            if (recoveredCommits.Length > 0)
            {
                await AppendJsonLinesCoreAsync(QuotaCommitSegmentPath(UtcNow()), recoveredCommits, cancellationToken)
                    .ConfigureAwait(false);
                commits = await RefreshQuotaCommitsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            _quotaProjectionRebuildGeneration = _quotaIndex.RebuildGeneration;
            _quotaPrepareProjectionRebuildGeneration = _quotaPrepareIndex.RebuildGeneration;
            _quotaCommitProjectionRebuildGeneration = _quotaCommitIndex.RebuildGeneration;
            return (facts, prepares, commits);
        }
        catch
        {
            // The facts, prepare and commit ledgers form one projection. If any stage fails,
            // keeping the other two stage watermarks would expose a mixed, partially projected
            // transaction set. Invalidate all derived quota state so the next refresh rebuilds
            // the complete transaction set from the durable JSONL ledgers.
            InvalidateQuotaDerivedState();
            throw;
        }
    }

    private void InvalidateQuotaDerivedState()
    {
        _quotaIndex.Clear();
        _quotaPrepareIndex.Clear();
        _quotaCommitIndex.Clear();
        _quotaProjection.Reset();
        _quotaProjectionVersion = -1;
        _quotaPrepareProjectionVersion = -1;
        _quotaCommitProjectionVersion = -1;
        _quotaProjectionRebuildGeneration = -1;
        _quotaPrepareProjectionRebuildGeneration = -1;
        _quotaCommitProjectionRebuildGeneration = -1;
        _quotaProjectionItemCount = 0;
        _quotaPrepareProjectionItemCount = 0;
        _quotaCommitProjectionItemCount = 0;
    }

    private async Task<JsonLineRead<AccountQuotaSnapshotFact>> RefreshQuotaAsync(CancellationToken cancellationToken)
    {
        var changed = await Task.Run(() => RefreshIndexCore(
            _quotaIndex,
            GetSegments("account-quota-snapshots-*.jsonl", "account-quota-snapshots.jsonl"),
            ValidateQuotaIntegrity,
            item => item.IdempotencyKey,
            item => item.PayloadHash,
            cancellationToken,
            onAccepted: item =>
            {
                _quotaProjection.AppendFact(item);
                Interlocked.Increment(ref _quotaProjectionRowsProcessed);
            }), cancellationToken).ConfigureAwait(false);
        if (!changed) Interlocked.Increment(ref _noChangeRefreshCount);
        return _quotaIndex.Read();
    }

    private async Task<JsonLineRead<AccountQuotaBatchCommit>> RefreshQuotaCommitsAsync(CancellationToken cancellationToken)
    {
        var changed = await Task.Run(() => RefreshIndexCore(
            _quotaCommitIndex,
            GetSegments("account-quota-commits-*.jsonl", "account-quota-commits.jsonl"),
            ValidateQuotaCommitIntegrity,
            item => item.IdempotencyKey,
            item => item.PayloadHash,
            cancellationToken,
            onAccepted: item =>
            {
                _quotaProjection.AppendCommit(item);
                Interlocked.Increment(ref _quotaProjectionRowsProcessed);
            }), cancellationToken).ConfigureAwait(false);
        if (!changed) Interlocked.Increment(ref _noChangeRefreshCount);
        return _quotaCommitIndex.Read();
    }

    private async Task<JsonLineRead<AccountQuotaBatchPrepare>> RefreshQuotaPreparesAsync(CancellationToken cancellationToken)
    {
        var changed = await Task.Run(() => RefreshIndexCore(
            _quotaPrepareIndex,
            GetSegments("account-quota-prepares-*.jsonl", "account-quota-prepares.jsonl"),
            ValidateQuotaPrepareIntegrity,
            item => item.IdempotencyKey,
            item => item.PayloadHash,
            cancellationToken,
            duplicateIsIntegrity: true,
            onAccepted: item =>
            {
                _quotaProjection.AppendPrepare(item);
                Interlocked.Increment(ref _quotaProjectionRowsProcessed);
            }), cancellationToken).ConfigureAwait(false);
        if (!changed) Interlocked.Increment(ref _noChangeRefreshCount);
        return _quotaPrepareIndex.Read();
    }

    private bool RefreshIndexCore<T>(
        JsonIndex<T> index,
        IReadOnlyList<string> paths,
        Func<T, bool> validate,
        Func<T, string> key,
        Func<T, string> payloadHash,
        CancellationToken cancellationToken,
        bool duplicateIsIntegrity = false,
        Action? onRebuild = null,
        Action<T>? onAccepted = null) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rebuild = !index.Initialized || index.Segments.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).Any();
        if (!rebuild)
        {
            foreach (var path in paths)
            {
                if (!index.Segments.TryGetValue(path, out var state)) continue;
                if (!File.Exists(path) || !SegmentCanIncrement(path, state)) { rebuild = true; break; }
            }
        }
        if (rebuild)
        {
            index.Clear();
            onRebuild?.Invoke();
            Interlocked.Increment(ref _fullIndexRebuildCount);
        }
        var changed = rebuild;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            if (RepairIncompleteJsonLineTail(path, validate)) changed = true;
            var start = index.Segments.TryGetValue(path, out var prior) ? prior.ParsedLength : 0;
            using var indexSession = index.OpenSession();
            var staged = new List<(T Item, long LineOffset)>();
            var read = ReadJsonLinesIncrementalStreaming<T>(path, start, cancellationToken, (item, lineOffset) =>
                staged.Add((item, lineOffset)));
            try
            {
                foreach (var (item, lineOffset) in staged)
                {
                    if (!validate(item)) { index.IntegrityFailureCount++; continue; }
                    var itemKey = key(item);
                    var occurrence = LedgerRowOccurrence(path, lineOffset);
                    if (index.TryGetPayload(itemKey, out var priorPayload, out var priorOccurrence))
                    {
                        if (!FixedHexEquals(priorPayload, payloadHash(item)))
                        {
                            index.IntegrityFailureCount++;
                            index.CollisionCount++;
                        }
                        else if (FixedHexEquals(priorOccurrence, occurrence))
                        {
                            // Another initialized process may already have indexed this exact durable row.
                            // If our durable segment watermark is behind the shared idempotency index,
                            // this instance still has to advance its projection exactly once.
                            if (onAccepted is null) index.Items.Add(item); else onAccepted(item);
                            ProjectionRowAcceptedBarrierForTests?.Invoke();
                        }
                        else if (duplicateIsIntegrity)
                        {
                            index.IntegrityFailureCount++;
                            index.DuplicateCount++;
                        }
                        continue;
                    }
                    index.Remember(itemKey, payloadHash(item), occurrence);
                    if (onAccepted is null) index.Items.Add(item); else onAccepted(item);
                    ProjectionRowAcceptedBarrierForTests?.Invoke();
                }
            }
            catch
            {
                // No segment watermark has been published yet. Discard the derived index so the
                // next refresh rebuilds from durable JSONL instead of replaying into a partial projection.
                index.Clear();
                onRebuild?.Invoke();
                throw;
            }
            if (read.ParsedLength > start || read.BadLineCount > 0) changed = true;
            index.BadLineCount += read.BadLineCount;
            index.Segments[path] = CreateSegmentState(path, read.ParsedLength, prior);
            if (read.ParsedLength > start) Interlocked.Increment(ref _incrementalSegmentReadCount);
        }
        index.FlushOverlay();
        if (changed) index.Version++;
        index.Initialized = true;
        return changed;
    }

    private bool IndexNeedsRebuild<T>(JsonIndex<T> index, IReadOnlyList<string> paths) where T : class
    {
        if (!index.Initialized || index.Segments.Keys.Except(paths, StringComparer.OrdinalIgnoreCase).Any()) return true;
        foreach (var path in paths)
            if (index.Segments.TryGetValue(path, out var state)
                && (!File.Exists(path) || !SegmentCanIncrement(path, state))) return true;
        return false;
    }

    private IncrementalRead<T> ReadJsonLinesIncremental<T>(string path, long start, CancellationToken cancellationToken) where T : class
    {
        var items = new List<T>();
        var bad = 0;
        var lines = ReadCompleteLines(path, start, MaximumSourceLineBytes, cancellationToken);
        foreach (var line in lines.Lines)
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(line.Text, _jsonOptions);
                if (item is null) bad++; else items.Add(item);
            }
            catch (JsonException) { bad++; }
        }
        bad += lines.OversizedLineCount;
        return new IncrementalRead<T>(items, bad, lines.NextOffset, lines.HasIncompleteTail);
    }

    private bool RepairIncompleteJsonLineTail<T>(string path, Func<T, bool> validate) where T : class
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.WriteThrough);
        if (stream.Length == 0) return false;
        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == (byte)'\n') return false;

        var end = stream.Length;
        var start = end;
        var scanned = 0;
        while (start > 0 && scanned <= MaximumSourceLineBytes)
        {
            start--;
            stream.Position = start;
            if (stream.ReadByte() == (byte)'\n') { start++; break; }
            scanned++;
        }
        if (scanned > MaximumSourceLineBytes) start = end;

        var keepTail = false;
        if (start < end && end - start <= MaximumSourceLineBytes)
        {
            var bytes = new byte[checked((int)(end - start))];
            stream.Position = start;
            stream.ReadExactly(bytes);
            try
            {
                var text = StrictUtf8.GetString(bytes);
                var item = JsonSerializer.Deserialize<T>(text, _jsonOptions);
                keepTail = item is not null && validate(item);
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                keepTail = false;
            }
        }

        if (keepTail)
        {
            stream.Position = end;
            stream.WriteByte((byte)'\n');
        }
        else
        {
            stream.SetLength(start);
        }
        stream.Flush(true);
        try { File.Delete(AppendSealPath(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return true;
    }

    private StreamingRead ReadJsonLinesIncrementalStreaming<T>(
        string path,
        long start,
        CancellationToken cancellationToken,
        Action<T, long> onItem) where T : class
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (start < 0 || start > stream.Length) throw new InvalidDataException("Ledger index offset is outside the segment.");
        stream.Position = start;
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream(Math.Min(MaximumSourceLineBytes, 64 * 1024));
        var parsedLength = start;
        var absolute = start;
        var lineStart = start;
        var bad = 0;
        var oversized = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value != (byte)'\n')
                {
                    if (!oversized)
                    {
                        if (line.Length >= MaximumSourceLineBytes) oversized = true;
                        else line.WriteByte(value);
                    }
                    continue;
                }
                parsedLength = absolute + index + 1;
                if (oversized) bad++;
                else
                {
                    try
                    {
                        var text = StrictUtf8.GetString(line.ToArray());
                        var item = JsonSerializer.Deserialize<T>(text, _jsonOptions);
                        if (item is null) bad++;
                        else
                        {
                            Interlocked.Increment(ref _parsedLedgerLineCount);
                            onItem(item, lineStart);
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { bad++; }
                }
                line.SetLength(0);
                oversized = false;
                lineStart = parsedLength;
            }
            absolute += read;
        }
        return new StreamingRead(bad, parsedLength, line.Length > 0 || oversized);
    }

    private string LedgerRowOccurrence(string path, long lineOffset) => Hash(Canonical(new
    {
        schema = 1,
        segment = Path.GetFileName(path).ToLowerInvariant(),
        lineOffset
    }));

    private void TryLoadProjectionCheckpoint()
    {
        if (_checkpointLoadAttempted) return;
        lock (_checkpointGate)
        {
            if (_checkpointLoadAttempted) return;
            _checkpointLoadAttempted = true;
            if (_attemptIndex.Initialized || _quotaIndex.Initialized
                || _quotaPrepareIndex.Initialized || _quotaCommitIndex.Initialized) return;
            if (!File.Exists(ProjectionCheckpointPath))
            {
                Interlocked.Increment(ref _checkpointRebuildCount);
                return;
            }
            try
            {
                var envelope = JsonSerializer.Deserialize<ProjectionCheckpointEnvelope>(
                    File.ReadAllBytes(ProjectionCheckpointPath), _jsonOptions)
                    ?? throw new InvalidDataException("Projection checkpoint envelope is empty.");
                if (envelope.SchemaVersion != 3 || !IsSha256Hex(envelope.PayloadSha256)
                    || !IsSha256Hex(envelope.PayloadHmac))
                    throw new InvalidDataException("Projection checkpoint envelope schema is invalid.");
                var payloadBytes = Convert.FromBase64String(envelope.PayloadBase64);
                if (!FixedHexEquals(Hash(payloadBytes), envelope.PayloadSha256))
                    throw new InvalidDataException("Projection checkpoint payload hash is invalid.");
                if (!FixedHexEquals(ProjectionCheckpointHmac(payloadBytes), envelope.PayloadHmac))
                    throw new InvalidDataException("Projection checkpoint authentication failed.");
                var checkpoint = JsonSerializer.Deserialize<ProjectionCheckpoint>(payloadBytes, _jsonOptions)
                    ?? throw new InvalidDataException("Projection checkpoint payload is empty.");
                if (checkpoint.SchemaVersion != 3 || checkpoint.LedgerSchemaVersion != LedgerSchemaVersion
                    || string.IsNullOrWhiteSpace(_identityKeyId)
                    || !string.Equals(checkpoint.IdentityKeyId, _identityKeyId, StringComparison.Ordinal))
                    throw new InvalidDataException("Projection checkpoint identity/schema domain does not match the ledger.");
                ValidateProjectionCheckpointLedgerBinding(checkpoint);
                _attemptIndex.Restore(checkpoint.AttemptIndex);
                _quotaIndex.Restore(checkpoint.QuotaIndex);
                _quotaPrepareIndex.Restore(checkpoint.QuotaPrepareIndex);
                _quotaCommitIndex.Restore(checkpoint.QuotaCommitIndex);
                _accountRequestMembership.Restore(checkpoint.AccountRequestMembership);
                _scopeRequestMembership.Restore(checkpoint.ScopeRequestMembership);
                _attemptProjection.Restore(checkpoint.AttemptProjection);
                _quotaProjection.Restore(checkpoint.QuotaProjection);
                _attemptProjectionVersion = _attemptIndex.Version;
                _attemptProjectionRebuildGeneration = _attemptIndex.RebuildGeneration;
                _quotaProjectionVersion = _quotaIndex.Version;
                _quotaPrepareProjectionVersion = _quotaPrepareIndex.Version;
                _quotaCommitProjectionVersion = _quotaCommitIndex.Version;
                _quotaProjectionRebuildGeneration = _quotaIndex.RebuildGeneration;
                _quotaPrepareProjectionRebuildGeneration = _quotaPrepareIndex.RebuildGeneration;
                _quotaCommitProjectionRebuildGeneration = _quotaCommitIndex.RebuildGeneration;
                _quotaProjectionItemCount = _quotaPrepareProjectionItemCount = _quotaCommitProjectionItemCount = 0;
                _cachedAggregates = checkpoint.Snapshot.Accounts;
                _cachedRecent = checkpoint.Snapshot.RecentAttempts;
                _cachedRequestScope = checkpoint.Snapshot.RequestScopeUsage;
                _cachedUnverifiedIdentityUsage = checkpoint.Snapshot.UnverifiedIdentityUsage;
                _cachedStoredAttemptCount = checkpoint.Snapshot.StoredAttemptCount;
                _cachedCommittedQuotaFactCount = _quotaProjection.CommittedFactCount;
                _cachedIncompleteQuotaFactCount = _quotaProjection.IncompleteFactCount;
                _cachedOrphanCommitCount = _quotaProjection.OrphanCommitCount;
                _cachedOrphanPrepareCount = _quotaProjection.OrphanPrepareCount;
                _cachedInvalidQuotaValueCount = _quotaProjection.InvalidQuotaValueCount;
                _persistentQuotaIntegrityCount = _quotaProjection.StructuralIntegrityCount;
                _cachedHealthyQuotaViews = _quotaProjection.ProjectViews(false);
                _quotaSourceReadFailed = checkpoint.QuotaSourceReadFailed;
                lock (_snapshotStateGate)
                {
                    _snapshotRevision = Math.Max(_snapshotRevision, checkpoint.Snapshot.Revision);
                }
                _publishedCheckpointVersions = (_attemptIndex.Version, _quotaIndex.Version,
                    _quotaPrepareIndex.Version, _quotaCommitIndex.Version);
                Interlocked.Increment(ref _checkpointLoadCount);
                ProjectionCheckpointRestoredBarrierForTests?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                             or FormatException or CryptographicException or InvalidDataException
                                             or OverflowException)
            {
                Interlocked.Increment(ref _checkpointValidationFailureCount);
                Interlocked.Increment(ref _checkpointRebuildCount);
                _attemptIndex.Clear();
                _quotaIndex.Clear();
                _quotaPrepareIndex.Clear();
                _quotaCommitIndex.Clear();
                _attemptProjection.Reset();
                _quotaProjection.Reset();
            }
        }
    }

    private void WriteProjectionCheckpoint(AccountUsageLedgerSnapshot snapshot)
    {
        var versions = (_attemptIndex.Version, _quotaIndex.Version,
            _quotaPrepareIndex.Version, _quotaCommitIndex.Version);
        if (versions == _publishedCheckpointVersions || string.IsNullOrWhiteSpace(_identityKeyId)) return;
        lock (_checkpointGate)
        {
            versions = (_attemptIndex.Version, _quotaIndex.Version,
                _quotaPrepareIndex.Version, _quotaCommitIndex.Version);
            if (versions == _publishedCheckpointVersions) return;
            var checkpoint = new ProjectionCheckpoint(
                3, LedgerSchemaVersion, _identityKeyId!, UtcNow(),
                _attemptIndex.Capture(), _quotaIndex.Capture(), _quotaPrepareIndex.Capture(), _quotaCommitIndex.Capture(),
                _accountRequestMembership.Capture(), _scopeRequestMembership.Capture(),
                _attemptProjection.Capture(), _quotaProjection.Capture(), snapshot, _quotaSourceReadFailed);
            var payload = JsonSerializer.SerializeToUtf8Bytes(checkpoint, _jsonOptions);
            WriteAtomicJson(ProjectionCheckpointPath,
                new ProjectionCheckpointEnvelope(3, Convert.ToBase64String(payload), Hash(payload),
                    ProjectionCheckpointHmac(payload)));
            _publishedCheckpointVersions = versions;
            Interlocked.Increment(ref _checkpointPublishCount);
        }
    }

    private string ProjectionCheckpointHmac(ReadOnlySpan<byte> payload)
    {
        var domain = Utf8NoBom.GetBytes("CodexModelManager:account-usage-projection:v3\0");
        var authenticated = new byte[domain.Length + payload.Length];
        domain.CopyTo(authenticated, 0);
        payload.CopyTo(authenticated.AsSpan(domain.Length));
        try { return Convert.ToHexString(HMACSHA256.HashData(GetIdentityKey(), authenticated)); }
        finally { CryptographicOperations.ZeroMemory(authenticated); }
    }

    private void ValidateProjectionCheckpointLedgerBinding(ProjectionCheckpoint checkpoint)
    {
        if (checkpoint.Snapshot.StoredAttemptCount != checkpoint.AttemptProjection.StoredAttemptCount
            || checkpoint.Snapshot.StoredQuotaSnapshotCount != checkpoint.QuotaProjection.CommittedFactCount)
            throw new InvalidDataException("Projection checkpoint snapshot counters do not match its projections.");

        // Set equality is a separate, first-phase gate. Do not inspect any checkpoint-supplied
        // record path until all four types have been proven to describe exactly the files that
        // currently belong to this data directory.
        var attemptSegments = ValidateSegmentSet("attempt", checkpoint.AttemptIndex.Segments,
            GetSegments("account-token-attempts-*.jsonl", "account-token-attempts.jsonl"));
        var quotaSegments = ValidateSegmentSet("quota", checkpoint.QuotaIndex.Segments,
            GetSegments("account-quota-snapshots-*.jsonl", "account-quota-snapshots.jsonl"));
        var quotaPrepareSegments = ValidateSegmentSet("quota-prepare", checkpoint.QuotaPrepareIndex.Segments,
            GetSegments("account-quota-prepares-*.jsonl", "account-quota-prepares.jsonl"));
        var quotaCommitSegments = ValidateSegmentSet("quota-commit", checkpoint.QuotaCommitIndex.Segments,
            GetSegments("account-quota-commits-*.jsonl", "account-quota-commits.jsonl"));

        ValidateSegmentContents(attemptSegments);
        ValidateSegmentContents(quotaSegments);
        ValidateSegmentContents(quotaPrepareSegments);
        ValidateSegmentContents(quotaCommitSegments);
        ReplaceSegments(checkpoint.AttemptIndex.Segments, attemptSegments);
        ReplaceSegments(checkpoint.QuotaIndex.Segments, quotaSegments);
        ReplaceSegments(checkpoint.QuotaPrepareIndex.Segments, quotaPrepareSegments);
        ReplaceSegments(checkpoint.QuotaCommitIndex.Segments, quotaCommitSegments);

        Dictionary<string, SegmentState> ValidateSegmentSet(string kind,
            Dictionary<string, SegmentState> checkpointSegments, IReadOnlyList<string> actualSegments)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataDirectory));
            var normalizedCheckpoint = new Dictionary<string, SegmentState>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in checkpointSegments)
            {
                string path;
                try { path = Path.GetFullPath(pair.Key); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new InvalidDataException($"Projection checkpoint {kind} segment path is invalid.", ex);
                }
                if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
                        root, StringComparison.OrdinalIgnoreCase)
                    || !normalizedCheckpoint.TryAdd(path, pair.Value))
                    throw new InvalidDataException($"Projection checkpoint {kind} segment set is outside or ambiguous for the current ledger directory.");
            }

            var normalizedActual = actualSegments.Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (normalizedCheckpoint.Count != normalizedActual.Count
                || !normalizedActual.SetEquals(normalizedCheckpoint.Keys))
                throw new InvalidDataException($"Projection checkpoint {kind} segment set does not exactly match the current ledger directory.");
            return normalizedActual.ToDictionary(path => path, path => normalizedCheckpoint[path],
                StringComparer.OrdinalIgnoreCase);
        }

        void ValidateSegmentContents(Dictionary<string, SegmentState> segments)
        {
            foreach (var pair in segments)
                if (!File.Exists(pair.Key) || !SegmentMatchesCheckpoint(pair.Key, pair.Value))
                    throw new InvalidDataException("Projection checkpoint ledger watermark/content chain does not match the append-only ledger.");
        }

        static void ReplaceSegments(Dictionary<string, SegmentState> destination,
            Dictionary<string, SegmentState> source)
        {
            destination.Clear();
            foreach (var pair in source) destination.Add(pair.Key, pair.Value);
        }
    }

    private AccountUsageLedgerSnapshot BuildSnapshot(int recentAttemptLimit, bool quotaSourceReadFailed)
    {
        LoadSourceIntegrityStateCore();
        RefreshAnomalyCountCore();
        if (_attemptProjectionVersion != _attemptIndex.Version)
        {
            var rebuild = _attemptProjectionRebuildGeneration != _attemptIndex.RebuildGeneration;
            if (rebuild)
            {
                _attemptProjection.Reset();
            }
            var rows = _attemptIndex.Items.ToArray();
            _attemptProjection.Append(rows);
            Interlocked.Add(ref _attemptProjectionRowsProcessed, rows.Length);
            _attemptIndex.Items.Clear();
            _attemptProjectionRebuildGeneration = _attemptIndex.RebuildGeneration;
            _cachedAggregates = _attemptProjection.ProjectAccounts(out _cachedOverflowCount);
            _cachedRequestScope = _attemptProjection.RequestScope;
            _cachedUnverifiedIdentityUsage = _attemptProjection.UnverifiedScope;
            _cachedRecent = _attemptProjection.Recent;
            _cachedStoredAttemptCount = _attemptProjection.StoredAttemptCount;
            _attemptProjectionVersion = _attemptIndex.Version;
        }
        if (_quotaProjectionVersion != _quotaIndex.Version
            || _quotaPrepareProjectionVersion != _quotaPrepareIndex.Version
            || _quotaCommitProjectionVersion != _quotaCommitIndex.Version)
        {
            var rebuild = _quotaProjectionRebuildGeneration != _quotaIndex.RebuildGeneration
                          || _quotaPrepareProjectionRebuildGeneration != _quotaPrepareIndex.RebuildGeneration
                          || _quotaCommitProjectionRebuildGeneration != _quotaCommitIndex.RebuildGeneration
                          || _quotaProjectionItemCount > _quotaIndex.Items.Count
                          || _quotaPrepareProjectionItemCount > _quotaPrepareIndex.Items.Count
                          || _quotaCommitProjectionItemCount > _quotaCommitIndex.Items.Count;
            if (rebuild)
            {
                _quotaProjection.Reset();
                _quotaProjectionItemCount = 0;
                _quotaPrepareProjectionItemCount = 0;
                _quotaCommitProjectionItemCount = 0;
            }
            var facts = _quotaIndex.Items.ToArray();
            var prepares = _quotaPrepareIndex.Items.ToArray();
            var commits = _quotaCommitIndex.Items.ToArray();
            _quotaProjection.Append(facts, prepares, commits);
            Interlocked.Add(ref _quotaProjectionRowsProcessed, facts.Length + prepares.Length + commits.Length);
            _quotaIndex.Items.Clear();
            _quotaPrepareIndex.Items.Clear();
            _quotaCommitIndex.Items.Clear();
            _quotaProjectionItemCount = _quotaPrepareProjectionItemCount = _quotaCommitProjectionItemCount = 0;
            _quotaProjectionRebuildGeneration = _quotaIndex.RebuildGeneration;
            _quotaPrepareProjectionRebuildGeneration = _quotaPrepareIndex.RebuildGeneration;
            _quotaCommitProjectionRebuildGeneration = _quotaCommitIndex.RebuildGeneration;
            _cachedCommittedQuotaFactCount = _quotaProjection.CommittedFactCount;
            _cachedIncompleteQuotaFactCount = _quotaProjection.IncompleteFactCount;
            _cachedOrphanCommitCount = _quotaProjection.OrphanCommitCount;
            _cachedOrphanPrepareCount = _quotaProjection.OrphanPrepareCount;
            _cachedInvalidQuotaValueCount = _quotaProjection.InvalidQuotaValueCount;
            _persistentQuotaIntegrityCount = _quotaProjection.StructuralIntegrityCount;
            _cachedHealthyQuotaViews = _quotaProjection.ProjectViews(false);
            _quotaProjectionVersion = _quotaIndex.Version;
            _quotaPrepareProjectionVersion = _quotaPrepareIndex.Version;
            _quotaCommitProjectionVersion = _quotaCommitIndex.Version;
        }
        var effectiveQuotaReadFailed = _quotaSourceReadFailed || quotaSourceReadFailed;
        var latestQuotaViews = effectiveQuotaReadFailed
            ? _quotaProjection.ProjectViews(true)
            : _cachedHealthyQuotaViews;
        var integrity = _attemptIndex.IntegrityFailureCount + _quotaIndex.IntegrityFailureCount
                        + _quotaPrepareIndex.IntegrityFailureCount + _quotaCommitIndex.IntegrityFailureCount
                        + _cachedIncompleteQuotaFactCount + _cachedOrphanCommitCount
                        + _cachedOverflowCount;
        var tokenIntegrity = _attemptIndex.IntegrityFailureCount + _attemptIndex.BadLineCount
                             + _cachedOverflowCount + (_persistentTokenIntegrityIssue ? 1 : 0);
        var quotaIntegrity = _quotaIndex.IntegrityFailureCount + _quotaPrepareIndex.IntegrityFailureCount
                             + _quotaCommitIndex.IntegrityFailureCount + _quotaIndex.BadLineCount
                             + _quotaPrepareIndex.BadLineCount + _quotaCommitIndex.BadLineCount
                             + _cachedIncompleteQuotaFactCount + _cachedOrphanCommitCount
                             + _cachedInvalidQuotaValueCount + _persistentQuotaIntegrityCount;
        var importerStatus = Volatile.Read(ref _importerStatus) with { IdentityKeyState = _identityKeyState.ToString() };
        var tokenHealth = importerStatus.TokenHealth == AccountUsageImporterHealth.NotStarted
            ? importerStatus.Health : importerStatus.TokenHealth;
        var quotaHealth = importerStatus.QuotaHealth == AccountUsageImporterHealth.NotStarted
            ? importerStatus.Health : importerStatus.QuotaHealth;
        var snapshot = new AccountUsageLedgerSnapshot(
            _cachedAggregates,
            _cachedRecent.Take(recentAttemptLimit).ToArray(),
            _cachedRequestScope,
            latestQuotaViews,
            _cachedStoredAttemptCount,
            _cachedCommittedQuotaFactCount,
            _attemptIndex.BadLineCount,
            _quotaIndex.BadLineCount + _quotaPrepareIndex.BadLineCount + _quotaCommitIndex.BadLineCount,
            integrity,
            _anomalyCount + _attemptIndex.CollisionCount + _quotaIndex.CollisionCount
            + _quotaPrepareIndex.CollisionCount + _quotaCommitIndex.CollisionCount,
            UtcNow(),
            _cachedStoredAttemptCount == 0
                ? "Token 台账尚无记录"
                : tokenHealth is AccountUsageImporterHealth.Degraded or AccountUsageImporterHealth.Stopped
                    || _tokenSourceStale || _coverageGapDetected || tokenIntegrity > 0
                    ? $"Token 归账降级：{_cachedStoredAttemptCount} 条 attempt；token-integrity {tokenIntegrity}；{importerStatus.TokenErrorClass ?? _coverageGapMessage ?? "SourceStale"}"
                    : $"Token 归账正常：已审计 {_cachedStoredAttemptCount} 条 attempt；token-integrity 0",
            effectiveQuotaReadFailed && latestQuotaViews.Count == 0
                ? "配额读取失败，无可保留快照；Token 用量未参与推算"
                : latestQuotaViews.Count == 0
                ? quotaHealth is AccountUsageImporterHealth.Degraded or AccountUsageImporterHealth.Stopped || quotaIntegrity > 0
                    ? $"配额域降级：{importerStatus.QuotaErrorClass ?? "QuotaIntegrity"}；无可显示快照；不会从 Token 推算"
                    : "配额/余额未配置或未提供；不会从 Token 用量推算"
                : effectiveQuotaReadFailed
                    ? "配额读取失败：保留旧快照并标记 stale；Token 用量未参与推算"
                    : quotaHealth is AccountUsageImporterHealth.Degraded or AccountUsageImporterHealth.Stopped || quotaIntegrity > 0
                        ? $"配额域降级：保留 {latestQuotaViews.Count} 项；quota-integrity {quotaIntegrity}；{importerStatus.QuotaErrorClass ?? "Unknown"}"
                        : $"独立额度观测 {latestQuotaViews.Count} 项；quota-integrity 0；未与 Token 混算")
        {
            ImporterStatus = importerStatus,
            UnverifiedIdentityUsage = _cachedUnverifiedIdentityUsage,
            TokenSourceStale = _tokenSourceStale,
            CoverageGapDetected = _coverageGapDetected,
            CoverageGapMessage = _coverageGapMessage,
            CoverageGapFirstSeen = _coverageGapFirstSeen,
            TokenIntegrityFailureCount = tokenIntegrity,
            QuotaIntegrityFailureCount = quotaIntegrity
        };
        return snapshot;
    }

    private static TokenMetricAggregate Metric(
        IReadOnlyList<AccountUsageAttemptFact> rows,
        Func<AttemptTokenUsageFact, long?> selector)
    {
        var values = rows
            .Where(row => row.Usage is not null && row.Usage.TotalValidation != TokenTotalValidationState.InvalidValue)
            .Select(row => selector(row.Usage!)).Where(value => value is >= 0).Select(value => value!.Value).ToArray();
        var sum = values.Aggregate(BigInteger.Zero, (current, value) => current + value);
        var overflow = sum > long.MaxValue;
        return new TokenMetricAggregate(overflow ? 0 : (long)sum, values.Length, rows.Count, overflow);
    }

    private static RequestScopeUsageAggregate BuildRequestScopeAggregate(IReadOnlyList<AccountUsageAttemptFact> facts)
    {
        var rows = facts.Where(fact => fact.RequestLevelUsage).ToArray();
        return new RequestScopeUsageAggregate(
            rows.Length,
            rows.Select(row => row.RequestIdentity).Distinct(StringComparer.Ordinal).Count(),
            rows.Count(row => row.Usage?.TotalValidation == TokenTotalValidationState.InvalidValue),
            rows.Count(row => row.Usage?.TotalValidation == TokenTotalValidationState.Mismatch),
            Metric(rows, usage => usage.InputTokens),
            Metric(rows, usage => usage.CachedInputTokens),
            Metric(rows, usage => usage.CacheReadInputTokens),
            Metric(rows, usage => usage.CacheCreationInputTokens),
            Metric(rows, usage => usage.OutputTokens),
            Metric(rows, usage => usage.ReasoningTokens),
            Metric(rows, usage => usage.TotalTokens));
    }

    private static RequestScopeUsageAggregate BuildUnverifiedIdentityAggregate(IReadOnlyList<AccountUsageAttemptFact> facts)
    {
        var rows = facts.Where(fact => !fact.RequestLevelUsage && !fact.IdentityVerified).ToArray();
        return new RequestScopeUsageAggregate(
            rows.Length,
            0,
            rows.Count(row => row.Usage?.TotalValidation == TokenTotalValidationState.InvalidValue),
            rows.Count(row => row.Usage?.TotalValidation == TokenTotalValidationState.Mismatch),
            Metric(rows, usage => usage.InputTokens),
            Metric(rows, usage => usage.CachedInputTokens),
            Metric(rows, usage => usage.CacheReadInputTokens),
            Metric(rows, usage => usage.CacheCreationInputTokens),
            Metric(rows, usage => usage.OutputTokens),
            Metric(rows, usage => usage.ReasoningTokens),
            Metric(rows, usage => usage.TotalTokens));
    }

    private static IReadOnlyList<AccountQuotaSnapshotFact> SelectLatestQuotaFacts(IReadOnlyList<AccountQuotaSnapshotFact> facts)
    {
        var selected = new List<AccountQuotaSnapshotFact>();
        foreach (var account in facts.GroupBy(item => new QuotaAccountGroupKey(
                     item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed)))
        {
            var batches = account.GroupBy(item => item.ObservationBatch, StringComparer.Ordinal)
                .OrderByDescending(batch => batch.Max(item => item.LocalObservedAt))
                .ThenByDescending(batch => batch.Max(item => item.RecordedAt)).ToArray();
            var latest = batches.FirstOrDefault();
            if (latest is null) continue;
            if (latest.Any(item => item.Availability == AccountQuotaAvailability.ReadFailed))
            {
                var retained = batches.Skip(1).FirstOrDefault(batch => batch.All(item => item.Availability != AccountQuotaAvailability.ReadFailed));
                selected.AddRange(retained ?? latest);
            }
            else selected.AddRange(latest);
        }
        return selected.OrderBy(item => item.ProviderId, StringComparer.Ordinal)
            .ThenBy(item => item.AccountId, StringComparer.Ordinal)
            .ThenBy(item => item.PeriodKey, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<AccountQuotaSnapshotView> BuildQuotaViews(
        IReadOnlyList<AccountQuotaSnapshotFact> all,
        IReadOnlyList<AccountQuotaSnapshotFact> selected,
        bool globalReadFailed)
    {
        var result = new List<AccountQuotaSnapshotView>();
        foreach (var fact in selected)
        {
            var accountKey = new QuotaAccountGroupKey(fact.ProviderId, fact.ObservationScope, fact.StableAccountIdentity, fact.AccountAttributed);
            var latestBatch = all.Where(item => new QuotaAccountGroupKey(item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed) == accountKey)
                .GroupBy(item => item.ObservationBatch, StringComparer.Ordinal)
                .OrderByDescending(batch => batch.Max(item => item.LocalObservedAt)).FirstOrDefault();
            var latestFailure = latestBatch?.FirstOrDefault(item => item.Availability == AccountQuotaAvailability.ReadFailed);
            var stale = globalReadFailed || fact.SourceStale || latestFailure is not null;
            var state = fact.ValueValidation == QuotaValueValidationState.InvalidRange
                ? "无效 · 上游额度值超出有效范围"
                : globalReadFailed
                ? "stale · 本次读取失败，保留旧值"
                : latestFailure is not null
                    ? $"stale · 来源读取失败，保留旧值 · {latestFailure.ErrorClass ?? "Unknown"}"
                    : fact.SourceStale
                        ? "stale · 上游明确标记陈旧"
                        : fact.Availability == AccountQuotaAvailability.Provided ? "已提供" : "源明确未提供";
            result.Add(new AccountQuotaSnapshotView(ProjectQuotaForDisplay(fact), stale, state));
        }
        return result;
    }

    private static AccountUsageAttemptFact ProjectAttemptForDisplay(AccountUsageAttemptFact fact) => fact with
    {
        AccountId = DeriveAccountDisplay(fact.StableAccountIdentity, fact.AccountAttributed),
        RequestId = RequestDisplay(fact.RequestIdentity),
        ErrorMessage = null,
        Source = SafeSourceKind(fact.SourceNamespace),
        SourceEventIdentity = string.Empty
    };

    private static AccountQuotaSnapshotFact ProjectQuotaForDisplay(AccountQuotaSnapshotFact fact) => fact with
    {
        AccountId = DeriveAccountDisplay(fact.StableAccountIdentity, fact.AccountAttributed),
        Source = SafeSourceKind(fact.Source),
        ResetLabel = SafeNullable(fact.ResetLabel)
    };

    private static string DeriveAccountDisplay(string stableIdentity, bool attributed)
    {
        if (!attributed) return UnattributedAccountId;
        var parts = stableIdentity.Split(':');
        return parts.Length == 3 && IsSha256Hex(parts[2]) ? $"账号 {parts[2][..8]}" : "账号 无效";
    }

    private static string RequestDisplay(string identity)
    {
        var parts = identity.Split(':');
        if (parts is { Length: 3 } && parts[0] == "RK1" && IsSha256Hex(parts[2])) return $"请求 {parts[2][..8]}";
        if (parts is { Length: 3 } && parts[0] == "UV1" && IsSha256Hex(parts[2])) return $"未验证 {parts[2][..8]}";
        return "请求 未知";
    }

    private static string SafeSourceKind(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "unknown";
        if (source.StartsWith("opencodex:", StringComparison.OrdinalIgnoreCase)) return "OpenCodex usage ledger";
        if (source.StartsWith("direct:", StringComparison.OrdinalIgnoreCase)) return "direct runtime event";
        if (source.Contains("relay", StringComparison.OrdinalIgnoreCase)) return "relay reported";
        if (source.Contains("official", StringComparison.OrdinalIgnoreCase)) return "official reported";
        return "structured source";
    }

    private AccountQuotaSnapshotFact NormalizeQuota(AccountQuotaSnapshotFact fact)
    {
        var normalizedStableIdentity = fact.AccountAttributed ? SafeOpaque(fact.StableAccountIdentity) : string.Empty;
        var valueValidation = fact.Availability != AccountQuotaAvailability.Provided || fact.Value is null
            ? QuotaValueValidationState.Unknown
            : fact.Value < 0m
                ? QuotaValueValidationState.InvalidRange
                : QuotaValueValidationState.Valid;
        var normalized = fact with
        {
            SchemaVersion = LedgerSchemaVersion,
            ProviderId = NormalizeProvider(fact.ProviderId, fact.ProviderLinked),
            AccountId = DeriveAccountDisplay(normalizedStableIdentity, fact.AccountAttributed),
            AccountKeyVersion = fact.AccountAttributed ? fact.AccountKeyVersion : 0,
            AccountKeyId = fact.AccountAttributed ? SafeOpaque(fact.AccountKeyId) : string.Empty,
            StableAccountIdentity = normalizedStableIdentity,
            PeriodKey = SafePeriodKey(fact.PeriodKey, fact.Availability),
            DisplayLabel = Safe(fact.DisplayLabel, fact.Availability == AccountQuotaAvailability.NotProvided ? "未提供" : "额度"),
            Unit = Safe(fact.Unit, "unknown"),
            ObservedAt = Utc(fact.ObservedAt),
            LocalObservedAt = Utc(fact.LocalObservedAt),
            ObservationBatch = SafeOpaque(fact.ObservationBatch),
            ObservationScope = NormalizeObservationScope(fact.ObservationScope),
            AccountObservationComplete = false,
            ErrorClass = SafeNullable(fact.ErrorClass),
            Source = Safe(fact.Source, "unknown"),
            RecordedAt = fact.RecordedAt == default ? UtcNow() : Utc(fact.RecordedAt),
            ValueValidation = valueValidation,
            ResetAtUtc = fact.ResetAtUtc is null ? null : Utc(fact.ResetAtUtc.Value),
            ResetLabel = SafeNullable(fact.ResetLabel),
            ResetState = fact.ResetState
        };
        return normalized with
        {
            PayloadHash = Hash(QuotaPayloadCanonical(normalized)),
            IdempotencyKey = Hash(QuotaIdentityCanonical(normalized))
        };
    }

    private AccountQuotaBatchCommit CreateQuotaBatchCommit(
        IReadOnlyList<AccountQuotaSnapshotFact> facts,
        DateTimeOffset recordedAt)
    {
        if (facts.Count == 0) throw new ArgumentException("额度批次不能为空。", nameof(facts));
        var first = facts[0];
        if (facts.Any(item => item.ObservationBatch != first.ObservationBatch
                              || item.ProviderId != first.ProviderId
                              || item.StableAccountIdentity != first.StableAccountIdentity
                              || item.AccountAttributed != first.AccountAttributed
                              || item.ObservationScope != first.ObservationScope))
            throw new InvalidDataException("额度批次混入了不同账号或 observationBatch。 ");
        var digest = Hash(Canonical(facts.Select(item => item.PayloadHash).OrderBy(value => value, StringComparer.Ordinal).ToArray()));
        var commit = new AccountQuotaBatchCommit(
            LedgerSchemaVersion, string.Empty, string.Empty, first.ObservationBatch, first.ProviderId,
            first.StableAccountIdentity, first.AccountAttributed, facts.Count, digest, Utc(recordedAt))
        { ObservationScope = first.ObservationScope };
        var identity = Canonical(new { v = LedgerSchemaVersion, commit.ObservationBatch,
            commit.ProviderId, commit.ObservationScope, commit.StableAccountIdentity, commit.AccountAttributed });
        var payload = Canonical(new { v = LedgerSchemaVersion, commit.ObservationBatch,
            commit.ProviderId, commit.ObservationScope, commit.StableAccountIdentity, commit.AccountAttributed,
            commit.ExpectedFactCount, commit.FactsDigest });
        return commit with { IdempotencyKey = Hash(identity), PayloadHash = Hash(payload) };
    }

    private AccountQuotaBatchPrepare CreateQuotaBatchPrepare(
        IReadOnlyList<AccountQuotaSnapshotFact> facts,
        DateTimeOffset recordedAt)
    {
        var commit = CreateQuotaBatchCommit(facts, recordedAt);
        var prepare = new AccountQuotaBatchPrepare(
            LedgerSchemaVersion, string.Empty, string.Empty, commit.ObservationBatch, commit.ProviderId,
            commit.StableAccountIdentity, commit.AccountAttributed, commit.ExpectedFactCount,
            commit.FactsDigest, Utc(recordedAt)) { ObservationScope = commit.ObservationScope };
        var identity = Canonical(new { v = LedgerSchemaVersion, kind = "quota_prepare", prepare.ObservationBatch,
            prepare.ProviderId, prepare.ObservationScope, prepare.StableAccountIdentity, prepare.AccountAttributed });
        var payload = Canonical(new { v = LedgerSchemaVersion, kind = "quota_prepare", prepare.ObservationBatch,
            prepare.ProviderId, prepare.ObservationScope, prepare.StableAccountIdentity, prepare.AccountAttributed,
            prepare.ExpectedFactCount, prepare.FactsDigest });
        return prepare with { IdempotencyKey = Hash(identity), PayloadHash = Hash(payload) };
    }

    private bool ValidateQuotaPrepareIntegrity(AccountQuotaBatchPrepare prepare)
    {
        if (prepare.SchemaVersion != LedgerSchemaVersion || !IsSha256Hex(prepare.IdempotencyKey)
            || !IsSha256Hex(prepare.PayloadHash) || !IsSha256Hex(prepare.FactsDigest)
            || prepare.ExpectedFactCount <= 0 || string.IsNullOrWhiteSpace(prepare.ObservationBatch)
            || !ObservationScopeIsValid(prepare.ObservationScope)) return false;
        var identity = Canonical(new { v = LedgerSchemaVersion, kind = "quota_prepare", prepare.ObservationBatch,
            prepare.ProviderId, prepare.ObservationScope, prepare.StableAccountIdentity, prepare.AccountAttributed });
        var payload = Canonical(new { v = LedgerSchemaVersion, kind = "quota_prepare", prepare.ObservationBatch,
            prepare.ProviderId, prepare.ObservationScope, prepare.StableAccountIdentity, prepare.AccountAttributed,
            prepare.ExpectedFactCount, prepare.FactsDigest });
        return FixedHexEquals(prepare.IdempotencyKey, Hash(identity))
               && FixedHexEquals(prepare.PayloadHash, Hash(payload));
    }

    private bool ValidateQuotaCommitIntegrity(AccountQuotaBatchCommit commit)
    {
        if (commit.SchemaVersion != LedgerSchemaVersion || !IsSha256Hex(commit.IdempotencyKey)
            || !IsSha256Hex(commit.PayloadHash) || !IsSha256Hex(commit.FactsDigest)
            || commit.ExpectedFactCount <= 0 || string.IsNullOrWhiteSpace(commit.ObservationBatch)
            || !ObservationScopeIsValid(commit.ObservationScope)) return false;
        var identity = Canonical(new { v = LedgerSchemaVersion, commit.ObservationBatch,
            commit.ProviderId, commit.ObservationScope, commit.StableAccountIdentity, commit.AccountAttributed });
        var payload = Canonical(new { v = LedgerSchemaVersion, commit.ObservationBatch,
            commit.ProviderId, commit.ObservationScope, commit.StableAccountIdentity, commit.AccountAttributed,
            commit.ExpectedFactCount, commit.FactsDigest });
        return FixedHexEquals(commit.IdempotencyKey, Hash(identity)) && FixedHexEquals(commit.PayloadHash, Hash(payload));
    }

    private IReadOnlyList<AccountQuotaSnapshotFact> GetCommittedQuotaFacts(
        out int incompleteFactCount,
        out int orphanCommitCount)
    {
        var result = new List<AccountQuotaSnapshotFact>();
        var committedBatches = _quotaCommitIndex.Items.ToDictionary(item => new QuotaBatchKey(
            item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch));
        var preparedBatches = _quotaPrepareIndex.Items.ToDictionary(item => new QuotaBatchKey(
            item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch));
        incompleteFactCount = 0;
        var matchedCommits = new HashSet<QuotaBatchKey>();
        foreach (var batch in _quotaIndex.Items.GroupBy(item => new QuotaBatchKey(
                     item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch)))
        {
            var facts = batch.OrderBy(item => item.PeriodKey, StringComparer.Ordinal).ToArray();
            var digest = Hash(Canonical(facts.Select(item => item.PayloadHash).OrderBy(value => value, StringComparer.Ordinal).ToArray()));
            if (!preparedBatches.TryGetValue(batch.Key, out var prepare)
                || !committedBatches.TryGetValue(batch.Key, out var commit)
                || prepare.ExpectedFactCount != facts.Length
                || commit.ExpectedFactCount != facts.Length
                || prepare.ExpectedFactCount != commit.ExpectedFactCount
                || !FixedHexEquals(prepare.FactsDigest, digest)
                || !FixedHexEquals(commit.FactsDigest, digest)
                || !FixedHexEquals(prepare.FactsDigest, commit.FactsDigest))
            {
                incompleteFactCount += facts.Length;
                continue;
            }
            matchedCommits.Add(batch.Key);
            result.AddRange(facts);
        }
        orphanCommitCount = committedBatches.Keys.Count(key => !matchedCommits.Contains(key));
        return result;
    }

    private int CountOrphanQuotaPrepares()
    {
        var committed = _quotaCommitIndex.Items.Select(item => new QuotaBatchKey(
            item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch)).ToHashSet();
        return _quotaPrepareIndex.Items.Count(item => !committed.Contains(new QuotaBatchKey(
            item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch)));
    }

    private bool ValidateAttemptIntegrity(AccountUsageAttemptFact fact)
    {
        if (fact.SchemaVersion != LedgerSchemaVersion || !IsSha256Hex(fact.IdempotencyKey) || !IsSha256Hex(fact.PayloadHash)
            || !RequestIdentityIsValid(fact) || !IsSha256Hex(fact.SourceEventIdentity)
            || string.IsNullOrWhiteSpace(fact.ProviderId) || string.IsNullOrWhiteSpace(fact.Model)
            || fact.AttemptOrdinal < 0 || fact.RequestLevelUsage != (fact.AttemptOrdinal == 0)
            || (fact.AccountAttributed && !string.Equals(fact.AccountKeyId, fact.RequestKeyId, StringComparison.Ordinal))
            || !AttributionIsValid(fact.AccountAttributed, fact.AccountId, fact.AccountKeyVersion, fact.AccountKeyId, fact.StableAccountIdentity)
            || !PersistedAttemptStringsAreValid(fact)
            || !UsageIsValid(fact.Usage)) return false;
        return FixedHexEquals(fact.PayloadHash, Hash(AttemptPayloadCanonical(fact)))
               && FixedHexEquals(fact.IdempotencyKey, Hash(AttemptIdentityCanonical(fact)));
    }

    private bool ValidateQuotaIntegrity(AccountQuotaSnapshotFact fact)
    {
        if (fact.SchemaVersion != LedgerSchemaVersion || !IsSha256Hex(fact.IdempotencyKey) || !IsSha256Hex(fact.PayloadHash)
            || string.IsNullOrWhiteSpace(fact.ProviderId) || string.IsNullOrWhiteSpace(fact.ObservationBatch)
            || !AttributionIsValid(fact.AccountAttributed, fact.AccountId, fact.AccountKeyVersion, fact.AccountKeyId, fact.StableAccountIdentity)
            || !PersistedQuotaStringsAreValid(fact)
            || (fact.Availability == AccountQuotaAvailability.Provided && string.IsNullOrWhiteSpace(fact.PeriodKey))
            || (fact.Availability != AccountQuotaAvailability.Provided && !string.IsNullOrEmpty(fact.PeriodKey))
            || (fact.Availability == AccountQuotaAvailability.Provided && fact.Value is null)
            || (fact.Availability != AccountQuotaAvailability.Provided && fact.Value is not null)
            || (fact.Availability == AccountQuotaAvailability.Provided && fact.ValueValidation == QuotaValueValidationState.Unknown)
            || (fact.Availability != AccountQuotaAvailability.Provided && fact.ValueValidation != QuotaValueValidationState.Unknown)) return false;
        return FixedHexEquals(fact.PayloadHash, Hash(QuotaPayloadCanonical(fact)))
               && FixedHexEquals(fact.IdempotencyKey, Hash(QuotaIdentityCanonical(fact)));
    }

    private static bool UsageIsValid(AttemptTokenUsageFact? usage)
    {
        if (usage is null) return true;
        var values = new[] { usage.InputTokens, usage.CachedInputTokens, usage.CacheReadInputTokens,
            usage.CacheCreationInputTokens, usage.OutputTokens, usage.ReasoningTokens, usage.TotalTokens };
        var hasNegative = values.Any(value => value is < 0);
        if (hasNegative != (usage.TotalValidation == TokenTotalValidationState.InvalidValue)) return false;
        if (usage.TotalSource == TokenTotalSource.DerivedInputOutput)
        {
            if (usage.InputTokens is null || usage.OutputTokens is null || usage.TotalTokens is null) return false;
            var expected = (BigInteger)usage.InputTokens.Value + usage.OutputTokens.Value;
            if (expected != usage.TotalTokens.Value) return false;
        }
        if (!hasNegative && usage.TotalSource == TokenTotalSource.Upstream
                         && usage.InputTokens is >= 0 && usage.OutputTokens is >= 0 && usage.TotalTokens is >= 0)
        {
            var expected = (BigInteger)usage.InputTokens.Value + usage.OutputTokens.Value;
            var state = expected == usage.TotalTokens.Value ? TokenTotalValidationState.Valid : TokenTotalValidationState.Mismatch;
            if (usage.TotalValidation != state) return false;
        }
        return true;
    }

    private static bool AttributionIsValid(bool attributed, string accountId, int keyVersion, string keyId, string stableIdentity) => attributed
        ? keyVersion == 1 && keyId.Length == 32 && keyId.All(Uri.IsHexDigit)
          && stableIdentity == $"AK1:{keyId}:{stableIdentity.Split(':').LastOrDefault()}"
          && stableIdentity.Split(':') is { Length: 3 } parts && IsSha256Hex(parts[2])
          && accountId == DeriveAccountDisplay(stableIdentity, true)
        : keyVersion == 0 && string.IsNullOrEmpty(keyId) && string.IsNullOrEmpty(stableIdentity)
          && accountId == UnattributedAccountId;

    private static bool RequestIdentityIsValid(AccountUsageAttemptFact fact)
    {
        if (fact.IdentityVerified)
        {
            var parts = fact.RequestIdentity.Split(':');
            return fact.RequestKeyVersion == 1 && fact.RequestKeyId.Length == 32
                   && fact.RequestKeyId.All(Uri.IsHexDigit) && parts is { Length: 3 }
                   && parts[0] == "RK1" && string.Equals(parts[1], fact.RequestKeyId, StringComparison.Ordinal)
                   && IsSha256Hex(parts[2]);
        }
        var ambiguousParts = fact.RequestIdentity.Split(':');
        return fact.RequestKeyVersion == 1 && fact.RequestKeyId.Length == 32
               && fact.RequestKeyId.All(Uri.IsHexDigit) && ambiguousParts is { Length: 3 }
               && ambiguousParts[0] == "UV1" && string.Equals(ambiguousParts[1], fact.RequestKeyId, StringComparison.Ordinal)
               && IsSha256Hex(ambiguousParts[2]);
    }

    private static bool PersistedAttemptStringsAreValid(AccountUsageAttemptFact fact) =>
        PersistedStringIsValid(fact.RequestId, allowNull: true)
        && PersistedStringIsValid(fact.RequestKeyId)
        && PersistedStringIsValid(fact.ProviderId)
        && PersistedStringIsValid(fact.AccountId)
        && PersistedStringIsValid(fact.Model)
        && PersistedStringIsValid(fact.RequestedRoute)
        && PersistedStringIsValid(fact.ErrorCode, allowNull: true)
        && PersistedStringIsValid(fact.ErrorMessage, allowNull: true)
        && PersistedStringIsValid(fact.SourceNamespace)
        && PersistedStringIsValid(fact.Source)
        && (fact.Usage is null || PersistedStringIsValid(fact.Usage.ValidationMessage)
            && PersistedStringIsValid(fact.Usage.SourcePath));

    private static bool PersistedQuotaStringsAreValid(AccountQuotaSnapshotFact fact) =>
        PersistedStringIsValid(fact.ProviderId)
        && PersistedStringIsValid(fact.AccountId)
        && PersistedStringIsValid(fact.PeriodKey, allowEmpty: fact.Availability != AccountQuotaAvailability.Provided)
        && PersistedStringIsValid(fact.DisplayLabel)
        && PersistedStringIsValid(fact.Unit)
        && PersistedStringIsValid(fact.ObservationBatch)
        && PersistedStringIsValid(fact.ObservationScope)
        && PersistedStringIsValid(fact.ErrorClass, allowNull: true)
        && PersistedStringIsValid(fact.Source)
        && PersistedStringIsValid(fact.ResetLabel, allowNull: true);

    private static bool PersistedStringIsValid(string? value, bool allowNull = false, bool allowEmpty = false)
    {
        if (value is null) return allowNull;
        if (!allowEmpty && value.Length == 0) return false;
        return value.Length <= MaximumPersistedStringLength && !value.Any(char.IsControl);
    }

    private string AttemptIdentityCanonical(AccountUsageAttemptFact fact) => Canonical(new
    {
        v = LedgerSchemaVersion,
        fact.SourceNamespace,
        fact.RequestIdentity,
        fact.AttemptOrdinal,
        fact.RequestLevelUsage
    });

    private static object DirectExecutionIdentity(RuntimeRouteExecution execution) => new
    {
        execution.RequestId,
        execution.RequestIdentityMaterial,
        execution.RequestedModel,
        execution.HttpStatus,
        execution.DurationMs,
        timestamp = execution.Timestamp is null ? null : Utc(execution.Timestamp.Value).ToString("O"),
        execution.Outcome,
        execution.ErrorCode,
        execution.ErrorMessage,
        execution.SelectionBasis,
        attempts = execution.Attempts.OrderBy(item => item.Ordinal).Select(item => new
        {
            item.Ordinal,
            item.ProviderId,
            item.ProviderDisplayName,
            item.AccountIdentityMaterial,
            item.AccountId,
            item.AccountIdentitySource,
            item.Model,
            item.HttpStatus,
            item.DurationMs,
            item.Outcome,
            item.ErrorCode,
            item.FailoverReason,
            item.Selected,
            item.SelectionEvidence,
            item.TokenUsage
        }).ToArray(),
        execution.RequestLevelTokenUsage
    };

    private string AttemptPayloadCanonical(AccountUsageAttemptFact fact) => Canonical(new
    {
        v = LedgerSchemaVersion,
        fact.RequestId, fact.RequestIdentity, fact.RequestKeyVersion, fact.RequestKeyId,
        fact.AttemptOrdinal, fact.RequestLevelUsage,
        fact.ProviderId, fact.AccountId, fact.AccountKeyVersion, fact.AccountKeyId, fact.StableAccountIdentity,
        fact.AccountAttributed, fact.AccountIdentitySource,
        fact.Model, fact.RequestedRoute, occurredAt = fact.OccurredAt is null ? null : Utc(fact.OccurredAt.Value).ToString("O"),
        fact.Result, fact.HttpStatus, fact.ErrorClassification, fact.Selected, fact.SelectionEvidence,
        fact.LogSelectionBasis, fact.ErrorCode, fact.ErrorMessage, fact.Usage,
        fact.IdentityVerified,
        fact.SourceNamespace,
        weakSourceEventIdentity = !fact.IdentityVerified ? fact.SourceEventIdentity : null,
        fact.Source, fact.EvidenceStrength
    });

    private string QuotaIdentityCanonical(AccountQuotaSnapshotFact fact) => Canonical(new
    {
        v = LedgerSchemaVersion, fact.ProviderId, fact.ProviderLinked, fact.AccountKeyVersion, fact.AccountKeyId, fact.StableAccountIdentity,
        fact.AccountAttributed, fact.ObservationScope, fact.ObservationBatch, fact.PeriodKey
    });

    private string QuotaPayloadCanonical(AccountQuotaSnapshotFact fact) => Canonical(new
    {
        v = LedgerSchemaVersion, fact.ProviderId, fact.ProviderLinked, fact.AccountKeyVersion, fact.AccountKeyId, fact.StableAccountIdentity,
        fact.AccountAttributed, fact.AccountId, fact.ObservationScope, fact.PeriodKey, fact.DisplayLabel,
        fact.Value, fact.ValueValidation, fact.Unit, fact.Availability,
        observedAt = Utc(fact.ObservedAt).ToString("O"), fact.SourceObservedAt,
        localObservedAt = Utc(fact.LocalObservedAt).ToString("O"), fact.SourceStale,
        fact.ObservationBatch, fact.ErrorClass, fact.Source, fact.Provenance,
        resetAtUtc = fact.ResetAtUtc is null ? null : Utc(fact.ResetAtUtc.Value).ToString("O"),
        fact.ResetLabel, fact.ResetState
    });

    private SourceScan ReadSourceBatch(
        SourceCursor? cursor,
        bool legacyContractMigration,
        CancellationToken cancellationToken)
    {
        if (_sourceDisabled) return SourceScan.Disabled;
        FileStream? stream = null;
        var continuation = _sourceContinuationStream;
        var continuing = continuation is not null && cursor is not null
                         && string.Equals(cursor.SourceIdentity, _sourceContinuationIdentity, StringComparison.Ordinal)
                         && string.Equals(cursor.Generation, _sourceContinuationGeneration, StringComparison.Ordinal)
                         && AnchorMatches(continuation, cursor);
        if (continuing)
        {
            stream = continuation;
            _sourceContinuationStream = null;
        }
        else
        {
            CloseSourceContinuation();
            if (!File.Exists(SourcePath)) return SourceScan.Missing;
            stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        var activeStream = stream ?? throw new IOException("usage source handle 不可用。");
        var fileIdentity = continuing ? _sourceContinuationIdentity! : GetFileIdentity(activeStream, SourcePath);
        var info = continuing ? null : new FileInfo(SourcePath);
        var creationUtcTicks = continuing ? _sourceContinuationCreationUtcTicks : info!.CreationTimeUtc.Ticks;
        var lastWriteUtcTicks = continuing ? _sourceContinuationLastWriteUtcTicks : info!.LastWriteTimeUtc.Ticks;
        if (legacyContractMigration)
        {
            var migrationLength = activeStream.Length;
            var baselineOffset = FindCompleteLineBoundary(activeStream, migrationLength);
            var migrationGeneration = Hash(Canonical(new
            {
                sourceContract = OpenCodexSourceNamespace,
                fileIdentity,
                creationUtcTicks,
                lastWriteUtcTicks,
                length = migrationLength,
                prefix = ReadWindowHash(activeStream, 0, (int)Math.Min(PrefixWindowBytes, migrationLength))
            }));
            var prefixHash = ReadWindowHash(activeStream, 0, (int)Math.Min(PrefixWindowBytes, migrationLength));
            var contentBlockHashes = BuildIncrementalSourceBlockHashes(
                activeStream, fileIdentity, migrationLength, forceFull: true);
            var marker = baselineOffset == 0
                ? new SourceCursor(CursorSchemaVersion, 0, 0, 0, string.Empty,
                    migrationLength, lastWriteUtcTicks, fileIdentity, migrationGeneration, UtcNow(),
                    OpenCodexSourceNamespace, GenerationMarkerOnly: true)
                : new SourceCursor(CursorSchemaVersion, baselineOffset,
                    baselineOffset - (int)Math.Min(AnchorWindowBytes, baselineOffset),
                    (int)Math.Min(AnchorWindowBytes, baselineOffset),
                    ReadWindowHash(activeStream,
                        baselineOffset - (int)Math.Min(AnchorWindowBytes, baselineOffset),
                        (int)Math.Min(AnchorWindowBytes, baselineOffset)),
                    migrationLength, lastWriteUtcTicks, fileIdentity, migrationGeneration, UtcNow(),
                    OpenCodexSourceNamespace);
            activeStream.Dispose();
            return new SourceScan(
                Array.Empty<SourceLine>(), Array.Empty<BadSourceLine>(), marker, true, migrationGeneration,
                AccountUsageSourceAvailability.Available, false, cursor?.Offset, prefixHash,
                contentBlockHashes, SourceContractMigrated: true);
        }
        var resumingPriorGeneration = continuing;
        var readingArchive = false;
        var archivedTailComplete = false;
        var archivedCoverageGap = false;
        if (!continuing && cursor is not null
            && !string.Equals(cursor.SourceIdentity, fileIdentity, StringComparison.Ordinal))
        {
            var archived = TryOpenArchivedSource(cursor);
            if (archived is not null)
            {
                archivedTailComplete = archived.TailComplete;
                archivedCoverageGap = archived.CoverageGap;
                if (!archived.TailComplete)
                {
                    activeStream.Dispose();
                    activeStream = archived.Stream;
                    fileIdentity = cursor.SourceIdentity;
                    creationUtcTicks = archived.CreationUtcTicks;
                    lastWriteUtcTicks = archived.LastWriteUtcTicks;
                    resumingPriorGeneration = true;
                    readingArchive = true;
                }
                else archived.Stream.Dispose();
            }
        }
        var length = activeStream.Length;
        try { SourceLengthCaptured?.Invoke(this, length); }
        catch { /* Diagnostic subscribers cannot interrupt source accounting. */ }
        var identityMismatch = cursor is not null
                               && !string.Equals(cursor.SourceIdentity, fileIdentity, StringComparison.Ordinal);
        var coverageGap = archivedCoverageGap
                          || !resumingPriorGeneration && identityMismatch && !archivedTailComplete
                          || cursor is not null && cursor.Offset < cursor.SourceLength && length < cursor.SourceLength;
        var reset = !resumingPriorGeneration && cursor is not null && (
            !string.Equals(cursor.SourceIdentity, fileIdentity, StringComparison.Ordinal)
            || length < cursor.Offset
            || !AnchorMatches(activeStream, cursor)
            || (length == cursor.SourceLength && lastWriteUtcTicks != cursor.SourceLastWriteUtcTicks));
        if (reset && cursor is not null && cursor.Offset < cursor.SourceLength
                  && !SourceMarkerProvesRange(activeStream, cursor.SourceIdentity, cursor.SourceLength))
            coverageGap = true;
        var generation = resumingPriorGeneration ? cursor!.Generation : reset || cursor is null
            ? Hash(Canonical(new { fileIdentity, creationUtcTicks,
                lastWriteUtcTicks, length,
                prefix = ReadWindowHash(activeStream, 0, (int)Math.Min(PrefixWindowBytes, length)) }))
            : cursor.Generation;
        var start = reset ? 0 : cursor?.Offset ?? 0;
        try
        {
            var read = ReadCompleteLines(activeStream, start, MaximumSourceLineBytes, cancellationToken,
                MaximumSourceBatchBytes, MaximumSourceBatchLines, length);
            var observedSourceLength = Math.Max(read.ObservedSourceLength, read.NextOffset);
            var bad = read.OversizedLines.Select(offset => new BadSourceLine(
                "oversized_source_line", offset, $"单行超过 {MaximumSourceLineBytes} 字节；已跳过至完整换行并继续")).ToList();
            var nextOffset = read.NextOffset;
            if (readingArchive && read.HasIncompleteTail && !read.StoppedAtLimit)
            {
                bad.Add(new BadSourceLine("truncated_archived_tail", read.NextOffset,
                    "已归档 source 以不完整末行结束；记录覆盖缺口并有限推进到归档 EOF"));
                nextOffset = observedSourceLength;
                coverageGap = true;
            }
            if (nextOffset == 0)
            {
                var prefixHash = ReadWindowHash(activeStream, 0, (int)Math.Min(PrefixWindowBytes, observedSourceLength));
                var contentBlockHashes = BuildIncrementalSourceBlockHashes(activeStream, fileIdentity, observedSourceLength, reset);
                activeStream.Dispose();
                var marker = new SourceCursor(CursorSchemaVersion, 0, 0, 0, string.Empty,
                    observedSourceLength, lastWriteUtcTicks, fileIdentity, generation, UtcNow(),
                    OpenCodexSourceNamespace, GenerationMarkerOnly: true);
                return new SourceScan(read.Lines, bad, marker, reset, generation,
                    AccountUsageSourceAvailability.Available, coverageGap, cursor?.Offset, prefixHash,
                    contentBlockHashes, legacyContractMigration);
            }
            var anchorLength = (int)Math.Min(AnchorWindowBytes, nextOffset);
            var anchorStart = nextOffset - anchorLength;
            var anchorHash = ReadWindowHash(activeStream, anchorStart, anchorLength);
            var sourcePrefixHash = ReadWindowHash(activeStream, 0, (int)Math.Min(PrefixWindowBytes, observedSourceLength));
            var sourceBlockHashes = BuildIncrementalSourceBlockHashes(activeStream, fileIdentity, observedSourceLength, reset);
            var next = new SourceCursor(CursorSchemaVersion, nextOffset, anchorStart, anchorLength, anchorHash,
                observedSourceLength, lastWriteUtcTicks, fileIdentity, generation, UtcNow(),
                OpenCodexSourceNamespace);
            if (read.StoppedAtLimit)
            {
                _sourceContinuationStream = activeStream;
                _sourceContinuationIdentity = fileIdentity;
                _sourceContinuationGeneration = generation;
                _sourceContinuationCreationUtcTicks = creationUtcTicks;
                _sourceContinuationLastWriteUtcTicks = lastWriteUtcTicks;
            }
            else activeStream.Dispose();
            return new SourceScan(read.Lines, bad, next, reset, generation, AccountUsageSourceAvailability.Available,
                coverageGap, cursor?.Offset, sourcePrefixHash, sourceBlockHashes, legacyContractMigration);
        }
        catch
        {
            activeStream.Dispose();
            throw;
        }
    }

    private void CloseSourceContinuation()
    {
        _sourceContinuationStream?.Dispose();
        _sourceContinuationStream = null;
        _sourceContinuationIdentity = null;
        _sourceContinuationGeneration = null;
    }

    private static long FindCompleteLineBoundary(FileStream stream, long length)
    {
        if (length <= 0) return 0;
        const int window = 64 * 1024;
        var buffer = new byte[window];
        var end = length;
        while (end > 0)
        {
            var start = Math.Max(0, end - window);
            var count = checked((int)(end - start));
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer.AsSpan(0, count));
            for (var index = count - 1; index >= 0; index--)
                if (buffer[index] == (byte)'\n') return start + index + 1;
            end = start;
        }
        return 0;
    }

    private ArchivedSourceResume? TryOpenArchivedSource(SourceCursor cursor)
    {
        var directory = Path.GetDirectoryName(SourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        var baseName = Path.GetFileName(SourcePath);
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.Equals(SourcePath, StringComparison.OrdinalIgnoreCase))
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
                           || name.StartsWith(Path.GetFileNameWithoutExtension(baseName) + "-", StringComparison.OrdinalIgnoreCase)
                           && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                .Take(128)
                .ToArray();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        foreach (var path in candidates)
        {
            FileStream? archive = null;
            try
            {
                archive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!string.Equals(GetFileIdentity(archive, path), cursor.SourceIdentity, StringComparison.Ordinal)
                    || !AnchorMatches(archive, cursor))
                {
                    archive.Dispose();
                    continue;
                }
                var info = new FileInfo(path);
                var tailComplete = cursor.Offset >= archive.Length;
                var coverageGap = archive.Length < cursor.SourceLength && cursor.Offset < cursor.SourceLength;
                return new ArchivedSourceResume(
                    archive, tailComplete, coverageGap, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks);
            }
            catch (IOException) { archive?.Dispose(); }
            catch (UnauthorizedAccessException) { archive?.Dispose(); }
        }
        return null;
    }

    private static bool AnchorMatches(FileStream stream, SourceCursor cursor)
    {
        if (cursor.GenerationMarkerOnly) return cursor.Offset == 0 && cursor.AnchorLength == 0;
        if (cursor.Offset == 0) return false;
        if (cursor.AnchorLength <= 0 || cursor.AnchorStart < 0
            || cursor.AnchorStart + cursor.AnchorLength != cursor.Offset
            || cursor.Offset > stream.Length) return false;
        return FixedHexEquals(ReadWindowHash(stream, cursor.AnchorStart, cursor.AnchorLength), cursor.AnchorHash);
    }

    private CursorRead ReadCursorCore()
    {
        if (!File.Exists(CursorPath))
        {
            var recovered = ReadCursorRecoveryCore();
            return recovered is null
                ? HasPriorSourceHistory() && !SourceMarkerAllowsFullRescan()
                    ? new CursorRead(null, SourceAnomaly("source_coverage_gap", null,
                        "primary/recovery cursor both missing while ledger history exists; rotation coverage is uncertain"))
                    : new CursorRead(null, null)
                : new CursorRead(recovered, SourceAnomaly("missing_source_cursor_recovered", recovered.Offset,
                    "primary cursor missing; recovered durable source generation marker"));
        }
        try
        {
            using var stream = new FileStream(CursorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var cursor = JsonSerializer.Deserialize<SourceCursor>(stream, _jsonOptions);
            if (cursor is not null && CursorIsValid(cursor)) return new CursorRead(cursor, null);
            var recovered = ReadCursorRecoveryCore();
            return new CursorRead(recovered, SourceAnomaly(recovered is null && HasPriorSourceHistory() && !SourceMarkerAllowsFullRescan()
                    ? "source_coverage_gap" : "invalid_source_cursor", null,
                recovered is null ? "cursor 语义非法且无 generation recovery；覆盖完整性不确定"
                    : "cursor 语义非法；已使用独立 durable generation marker 恢复"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var recovered = ReadCursorRecoveryCore();
            return new CursorRead(recovered, SourceAnomaly(recovered is null && HasPriorSourceHistory() && !SourceMarkerAllowsFullRescan()
                    ? "source_coverage_gap" : "invalid_source_cursor", null,
                (recovered is null ? "cursor 无法读取且无 generation recovery；覆盖完整性不确定："
                    : "cursor 无法读取；已使用 durable generation marker 恢复：") + Safe(ex.Message, "unknown")));
        }
    }

    private bool LegacySourceTrackingCanMigrate()
    {
        if (HasCurrentSourceContractTag(CursorPath)
            || HasCurrentSourceContractTag(CursorRecoveryPath)
            || HasCurrentSourceContractTag(SourceInitializedPath)) return false;
        var cursor = ReadLegacySourceCursor(CursorPath) ?? ReadLegacySourceCursor(CursorRecoveryPath);
        var marker = ReadLegacySourceMarker(SourceInitializedPath);
        if (cursor is null || marker is null)
            return cursor is not null || marker is not null || ExistingLedgerUsesLegacySource();
        return string.Equals(cursor.SourceIdentity, marker.SourceIdentity, StringComparison.Ordinal)
               && string.Equals(cursor.Generation, marker.Generation, StringComparison.Ordinal)
               && cursor.Offset <= marker.SourceLength
               && marker.ObservedOffset <= marker.SourceLength;
    }

    private static bool HasCurrentSourceContractTag(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("sourceContract", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private bool ExistingLedgerUsesLegacySource()
    {
        foreach (var path in GetSegments("account-token-attempts-*.jsonl", "account-token-attempts.jsonl"))
        {
            foreach (var line in File.ReadLines(path, StrictUtf8))
            {
                if (!line.Contains("opencodex:usage.jsonl:v4", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    foreach (var property in document.RootElement.EnumerateObject())
                        if (property.Name.Equals("sourceNamespace", StringComparison.OrdinalIgnoreCase)
                            && property.Value.ValueKind == JsonValueKind.String
                            && property.Value.GetString()?.Equals("opencodex:usage.jsonl:v4", StringComparison.OrdinalIgnoreCase) == true)
                            return true;
                }
                catch (JsonException)
                {
                    // Normal ledger validation handles malformed durable rows. Do not use an
                    // unverified text fragment as authority to baseline a different source.
                }
            }
        }
        return false;
    }

    private void MigrateLegacySourceTrackingArtifacts()
    {
        CloseSourceContinuation();
        var suffix = $".legacy-opencodex-v4-{UtcNow():yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.bak";
        foreach (var path in new[] { CursorPath, CursorRecoveryPath, SourceInitializedPath })
        {
            if (!File.Exists(path)) continue;
            var backup = path + suffix;
            File.Move(path, backup, overwrite: false);
        }
    }

    private static LegacySourceCursor? ReadLegacySourceCursor(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("sourceContract", out _)
                || document.RootElement.TryGetProperty("SourceContract", out _)) return null;
            var cursor = JsonSerializer.Deserialize<LegacySourceCursor>(bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cursor is null || cursor.SchemaVersion != 4 || cursor.SourceLength < 0
                || cursor.Offset < 0 || cursor.Offset > cursor.SourceLength
                || cursor.SourceLastWriteUtcTicks < 0 || string.IsNullOrWhiteSpace(cursor.SourceIdentity)
                || !IsSha256Hex(cursor.Generation)) return null;
            if (cursor.GenerationMarkerOnly)
                return cursor.Offset == 0 && cursor.AnchorStart == 0 && cursor.AnchorLength == 0
                       && string.IsNullOrEmpty(cursor.AnchorHash) ? cursor : null;
            return cursor.Offset > 0 && cursor.AnchorStart >= 0 && cursor.AnchorLength > 0
                   && cursor.AnchorStart + cursor.AnchorLength == cursor.Offset
                   && IsSha256Hex(cursor.AnchorHash) ? cursor : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static LegacySourceInitializedMarker? ReadLegacySourceMarker(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("sourceContract", out _)
                || document.RootElement.TryGetProperty("SourceContract", out _)) return null;
            var marker = JsonSerializer.Deserialize<LegacySourceInitializedMarker>(bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (marker is null || marker.SchemaVersion != 2 || marker.SourceLength < 0
                || marker.ObservedOffset < 0 || marker.ObservedOffset > marker.SourceLength
                || string.IsNullOrWhiteSpace(marker.SourceIdentity) || !IsSha256Hex(marker.Generation)
                || !IsSha256Hex(marker.PrefixHash) || marker.DigestBlockBytes != SourceDigestBlockBytes)
                return null;
            var expectedBlocks = marker.SourceLength == 0
                ? 0
                : (int)((marker.SourceLength + SourceDigestBlockBytes - 1) / SourceDigestBlockBytes);
            return marker.ContentBlockHashes.Count == expectedBlocks
                   && marker.ContentBlockHashes.All(IsSha256Hex) ? marker : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private SourceCursor? ReadCursorRecoveryCore()
    {
        if (!File.Exists(CursorRecoveryPath)) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<SourceCursor>(File.ReadAllBytes(CursorRecoveryPath), _jsonOptions);
            return cursor is not null && CursorIsValid(cursor) ? cursor : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private bool HasPriorSourceHistory() => File.Exists(SourceInitializedPath);

    private bool SourceMarkerAllowsFullRescan()
    {
        if (!File.Exists(SourceInitializedPath) || !File.Exists(SourcePath)) return false;
        try
        {
            using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var marker = JsonSerializer.Deserialize<SourceInitializedMarker>(File.ReadAllBytes(SourceInitializedPath), _jsonOptions);
            return SourceMarkerIsValid(marker)
                   && SourceMarkerProvesRange(stream, marker!.SourceIdentity, marker.SourceLength, marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private bool SourceMarkerProvesRange(FileStream stream, string sourceIdentity, long sourceLength)
    {
        if (!TryReadSourceMarker(out var marker)) return false;
        return SourceMarkerProvesRange(stream, sourceIdentity, sourceLength, marker);
    }

    private static bool SourceMarkerProvesRange(
        FileStream stream,
        string sourceIdentity,
        long sourceLength,
        SourceInitializedMarker? marker)
    {
        if (!SourceMarkerIsValid(marker)
            || marker!.SourceLength != sourceLength
            || !string.Equals(marker.SourceIdentity, sourceIdentity, StringComparison.Ordinal)
            || stream.Length < sourceLength) return false;
        var prefixLength = (int)Math.Min(PrefixWindowBytes, sourceLength);
        return FixedHexEquals(ReadWindowHash(stream, 0, prefixLength), marker.PrefixHash)
               && SourceBlockHashesMatch(stream, sourceLength, marker.ContentBlockHashes);
    }

    private static bool SourceMarkerIsValid(SourceInitializedMarker? marker)
    {
        if (marker is null || marker.SchemaVersion != SourceMarkerSchemaVersion || !IsSha256Hex(marker.Generation)
                || !string.Equals(marker.SourceContract, OpenCodexSourceNamespace, StringComparison.Ordinal)
                || !IsSha256Hex(marker.PrefixHash) || marker.SourceLength < 0
                || marker.ObservedOffset < 0 || marker.ObservedOffset > marker.SourceLength
                || marker.DigestBlockBytes != SourceDigestBlockBytes) return false;
        var expectedBlocks = marker.SourceLength == 0 ? 0 : (int)((marker.SourceLength + SourceDigestBlockBytes - 1) / SourceDigestBlockBytes);
        return marker.ContentBlockHashes.Count == expectedBlocks
               && marker.ContentBlockHashes.All(IsSha256Hex);
    }

    private void WriteSourceInitializedMarker(
        SourceCursor cursor,
        string prefixHash,
        IReadOnlyList<string> contentBlockHashes,
        DateTimeOffset observedAt)
    {
        if (!IsSha256Hex(prefixHash))
            throw new InvalidDataException("source pre-commit WAL prefix hash is invalid");
        var expectedBlocks = cursor.SourceLength == 0 ? 0 : (int)((cursor.SourceLength + SourceDigestBlockBytes - 1) / SourceDigestBlockBytes);
        if (contentBlockHashes.Count != expectedBlocks || contentBlockHashes.Any(hash => !IsSha256Hex(hash)))
            throw new InvalidDataException("source pre-commit WAL content block hashes are invalid");
        var temp = SourceInitializedPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(new SourceInitializedMarker(
                SourceMarkerSchemaVersion, cursor.SourceIdentity, cursor.SourceLength, cursor.Offset, cursor.Generation,
                OpenCodexSourceNamespace,
                prefixHash, SourceDigestBlockBytes, contentBlockHashes.ToArray(), Utc(observedAt)), _jsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, SourceInitializedPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private IReadOnlyList<string> BuildIncrementalSourceBlockHashes(
        FileStream stream,
        string sourceIdentity,
        long sourceLength,
        bool forceFull)
    {
        var hashes = new List<string>();
        long start = 0;
        if (!forceFull && TryReadSourceMarker(out var prior)
            && SourceMarkerIsValid(prior)
            && string.Equals(prior!.SourceIdentity, sourceIdentity, StringComparison.Ordinal)
            && prior.SourceLength <= sourceLength)
        {
            var reusableBlocks = (int)(prior.SourceLength / SourceDigestBlockBytes);
            hashes.AddRange(prior.ContentBlockHashes.Take(reusableBlocks));
            start = (long)reusableBlocks * SourceDigestBlockBytes;
        }
        var buffer = new byte[SourceDigestBlockBytes];
        stream.Seek(start, SeekOrigin.Begin);
        while (start < sourceLength)
        {
            var expected = (int)Math.Min(buffer.Length, sourceLength - start);
            var read = 0;
            while (read < expected)
            {
                var count = stream.Read(buffer, read, expected - read);
                if (count == 0) throw new EndOfStreamException("source changed while computing pre-commit WAL digest");
                read += count;
            }
            hashes.Add(Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))));
            start += read;
        }
        return hashes;
    }

    private bool TryReadSourceMarker(out SourceInitializedMarker? marker)
    {
        marker = null;
        if (!File.Exists(SourceInitializedPath)) return false;
        try
        {
            marker = JsonSerializer.Deserialize<SourceInitializedMarker>(File.ReadAllBytes(SourceInitializedPath), _jsonOptions);
            return marker is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static bool SourceBlockHashesMatch(
        FileStream stream,
        long sourceLength,
        IReadOnlyList<string> expectedHashes)
    {
        var buffer = new byte[SourceDigestBlockBytes];
        stream.Seek(0, SeekOrigin.Begin);
        long offset = 0;
        var index = 0;
        while (offset < sourceLength)
        {
            var expected = (int)Math.Min(buffer.Length, sourceLength - offset);
            var read = 0;
            while (read < expected)
            {
                var count = stream.Read(buffer, read, expected - read);
                if (count == 0) return false;
                read += count;
            }
            if (index >= expectedHashes.Count
                || !FixedHexEquals(Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))), expectedHashes[index]))
                return false;
            offset += read;
            index++;
        }
        return index == expectedHashes.Count;
    }

    private static bool CursorIsValid(SourceCursor cursor) =>
        cursor.SchemaVersion == CursorSchemaVersion
        && string.Equals(cursor.SourceContract, OpenCodexSourceNamespace, StringComparison.Ordinal)
        && (cursor.GenerationMarkerOnly
            ? cursor.Offset == 0 && cursor.AnchorStart == 0 && cursor.AnchorLength == 0
              && string.IsNullOrEmpty(cursor.AnchorHash)
            : cursor.Offset > 0 && cursor.AnchorStart >= 0 && cursor.AnchorLength > 0
              && cursor.AnchorStart + cursor.AnchorLength == cursor.Offset
              && IsSha256Hex(cursor.AnchorHash))
        && cursor.Offset <= cursor.SourceLength && cursor.SourceLength >= 0
        && cursor.SourceLastWriteUtcTicks >= 0
        && IsSha256Hex(cursor.Generation)
        && !string.IsNullOrWhiteSpace(cursor.SourceIdentity);

    private void WriteCursorCore(SourceCursor cursor)
    {
        WriteCursorDocument(CursorPath, cursor);
        WriteCursorDocument(CursorRecoveryPath, cursor);
    }

    private void WriteCursorDocument(string path, SourceCursor cursor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(cursor, _jsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private CompleteLineRead ReadCompleteLines(
        string path, long start, int maximumLineBytes, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
        var observedSourceLength = stream.Length;
        return ReadCompleteLines(stream, start, maximumLineBytes, cancellationToken,
            long.MaxValue, int.MaxValue, observedSourceLength);
    }

    private CompleteLineRead ReadCompleteLines(
        FileStream stream,
        long start,
        int maximumLineBytes,
        CancellationToken cancellationToken,
        long maximumBatchBytes,
        int maximumBatchLines,
        long observedSourceLength)
    {
        stream.Seek(start, SeekOrigin.Begin);
        var lines = new List<SourceLine>();
        var oversized = new List<long>();
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream(Math.Min(maximumLineBytes, 64 * 1024));
        var lineStart = start;
        var absolute = start;
        var overLimit = false;
        long nextOffset = start;
        var completedLineCount = 0;
        var stopAfterLine = false;
        while (!stopAfterLine && absolute < observedSourceLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, observedSourceLength - absolute));
            if (count == 0) break;
            for (var index = 0; index < count; index++, absolute++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (overLimit) oversized.Add(lineStart);
                    else
                    {
                        var bytes = line.ToArray();
                        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') Array.Resize(ref bytes, bytes.Length - 1);
                        try
                        {
                            var text = StrictUtf8.GetString(bytes);
                            if (!string.IsNullOrWhiteSpace(text)) lines.Add(new SourceLine(lineStart, text));
                        }
                        catch (DecoderFallbackException) { oversized.Add(lineStart); }
                    }
                    line.SetLength(0); overLimit = false; nextOffset = absolute + 1; lineStart = absolute + 1;
                    completedLineCount++;
                    stopAfterLine = nextOffset - start >= maximumBatchBytes || completedLineCount >= maximumBatchLines;
                    if (stopAfterLine) break;
                    continue;
                }
                if (overLimit) continue;
                if (line.Length >= maximumLineBytes) { overLimit = true; line.SetLength(0); continue; }
                line.WriteByte(value);
            }
        }
        return new CompleteLineRead(lines, oversized, nextOffset, stopAfterLine, absolute > nextOffset, observedSourceLength);
    }

    private async Task AppendJsonLinesCoreAsync<T>(string path, IEnumerable<T> items, CancellationToken cancellationToken)
        where T : class
    {
        var rows = items.ToArray();
        if (rows.Length == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) RepairIncompleteJsonLineTail<T>(path, _ => true);
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var startLength = stream.Length;
        var fileIdentity = GetFileIdentity(stream, path);
        var appendBytes = new List<byte>();
        foreach (var item in rows)
            appendBytes.AddRange(Utf8NoBom.GetBytes(JsonSerializer.Serialize(item, _jsonOptions) + "\n"));
        var priorSeal = TryReadAppendSeal(path, out var seal) && AppendSealIsValid(seal)
                        && seal!.Length == startLength
                        && string.Equals(seal.FileIdentity, fileIdentity, StringComparison.Ordinal)
            ? seal
            : null;
        var previousChain = priorSeal?.ChainDigest
                            ?? InitialAppendChain(startLength == 0 ? Hash(Array.Empty<byte>()) : ReadRangeHash(stream, 0, startLength), startLength);
        var payload = appendBytes.ToArray();
        var appendedHash = Hash(payload);
        stream.Seek(0, SeekOrigin.End);
        PublishSignal(DurableCommitStarted);
        await stream.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        stream.Flush(true);
        var finalLength = stream.Length;
        var chain = AppendChain(previousChain, startLength, finalLength, appendedHash);
        WriteAppendSeal(path, new AppendSeal(1, fileIdentity, startLength, finalLength,
            previousChain, appendedHash, chain, UtcNow()));
        RememberTrustedSchemaArtifact(path);
    }

    private async Task AppendAnomaliesAsync(
        IEnumerable<AccountUsageAnomaly> anomalies,
        CancellationToken cancellationToken)
    {
        var rows = anomalies.ToArray();
        if (rows.Length == 0) return;
        await using var anomalyLock = await AcquireFileLockAsync(AnomalyLockPath, cancellationToken).ConfigureAwait(false);
        await AppendJsonLinesCoreAsync(AnomalyPath, rows, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileStream> AcquireFileLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) { throw new IOException("等待逐账号事实台账跨进程锁超时。", ex); }
        }
    }

    private FileStream AcquireFileLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
            { Thread.Sleep(25); }
            catch (IOException ex)
            { throw new IOException("Waiting for the account usage ledger cross-process lock timed out.", ex); }
        }
    }

    private bool SegmentCanIncrement(string path, SegmentState state)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = new FileInfo(path);
        if (stream.Length < state.ParsedLength
            || info.CreationTimeUtc.Ticks != state.CreationUtcTicks
            || !string.Equals(GetFileIdentity(stream, path), state.FileIdentity, StringComparison.Ordinal)) return false;
        if (stream.Length == state.FileLength && info.LastWriteTimeUtc.Ticks == state.LastWriteUtcTicks) return true;
        if (stream.Length > state.ParsedLength
            && TryReadAppendSeal(path, out var seal)
            && AppendSealIsValid(seal)
            && seal!.StartLength == state.ParsedLength
            && seal.Length == stream.Length
            && string.Equals(seal.FileIdentity, state.FileIdentity, StringComparison.Ordinal)
            && FixedHexEquals(seal.PreviousChainDigest, state.AppendChainDigest)
            && FixedHexEquals(ReadRangeHash(stream, state.ParsedLength, stream.Length - state.ParsedLength), seal.AppendedHash)
            && FixedHexEquals(seal.ChainDigest,
                AppendChain(seal.PreviousChainDigest, seal.StartLength, seal.Length, seal.AppendedHash)))
        {
            Interlocked.Add(ref _ledgerVerificationBytes, stream.Length - state.ParsedLength);
            return true;
        }
        if (!FixedHexEquals(ReadWindowHash(stream, 0, state.PrefixLength), state.PrefixHash)
            || (state.ParsedLength > 0 && !FixedHexEquals(
                ReadWindowHash(stream, state.ParsedLength - state.TailLength, state.TailLength), state.TailHash))
            || !SegmentContentMatches(stream, state)) return false;
        return stream.Length > state.ParsedLength;
    }

    private bool SegmentMatchesCheckpoint(string path, SegmentState state)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = new FileInfo(path);
        if (stream.Length < state.ParsedLength
            || info.CreationTimeUtc.Ticks != state.CreationUtcTicks
            || !string.Equals(GetFileIdentity(stream, path), state.FileIdentity, StringComparison.Ordinal)
            || !SegmentContentMatches(stream, state)) return false;

        var sealPath = AppendSealPath(path);
        if (stream.Length == state.FileLength)
        {
            if (string.IsNullOrEmpty(state.AppendSealCommitment)) return !File.Exists(sealPath);
            return TryReadAppendSeal(path, out var currentSeal)
                   && AppendSealIsValid(currentSeal)
                   && FixedHexEquals(AppendSealCommitment(currentSeal!), state.AppendSealCommitment);
        }

        return stream.Length > state.ParsedLength
               && TryReadAppendSeal(path, out var appendedSeal)
               && AppendSealIsValid(appendedSeal)
               && appendedSeal!.StartLength == state.ParsedLength
               && appendedSeal.Length == stream.Length
               && string.Equals(appendedSeal.FileIdentity, state.FileIdentity, StringComparison.Ordinal)
               && FixedHexEquals(appendedSeal.PreviousChainDigest, state.AppendChainDigest)
               && FixedHexEquals(ReadRangeHash(stream, state.ParsedLength, stream.Length - state.ParsedLength),
                   appendedSeal.AppendedHash)
               && FixedHexEquals(appendedSeal.ChainDigest,
                   AppendChain(appendedSeal.PreviousChainDigest, appendedSeal.StartLength,
                       appendedSeal.Length, appendedSeal.AppendedHash));
    }

    private bool SegmentContentMatches(FileStream stream, SegmentState state)
    {
        if (state.CommitmentBlockBytes != LedgerCommitmentBlockBytes
            || state.ParsedLength < 0
            || state.ParsedBlockHashes is null
            || state.ParsedBlockHashes.Length != SegmentBlockCount(state.ParsedLength)
            || state.ParsedBlockHashes.Any(hash => !IsSha256Hex(hash))
            || !IsSha256Hex(state.ParsedContentCommitment)) return false;
        for (var index = 0; index < state.ParsedBlockHashes.Length; index++)
        {
            var offset = checked((long)index * LedgerCommitmentBlockBytes);
            var length = Math.Min(LedgerCommitmentBlockBytes, state.ParsedLength - offset);
            var actual = ReadRangeHash(stream, offset, length);
            Interlocked.Add(ref _ledgerVerificationBytes, length);
            if (!FixedHexEquals(actual, state.ParsedBlockHashes[index])) return false;
        }
        return FixedHexEquals(SegmentContentCommitment(state.ParsedLength, state.ParsedBlockHashes),
            state.ParsedContentCommitment);
    }

    private SegmentState CreateSegmentState(string path, long parsedLength, SegmentState? prior = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = new FileInfo(path);
        var prefixLength = (int)Math.Min(PrefixWindowBytes, parsedLength);
        var tailLength = (int)Math.Min(AnchorWindowBytes, parsedLength);
        var identity = GetFileIdentity(stream, path);
        var hasSeal = TryReadAppendSeal(path, out var seal) && AppendSealIsValid(seal)
                      && seal!.Length == parsedLength
                      && string.Equals(seal.FileIdentity, identity, StringComparison.Ordinal);
        if (!hasSeal && File.Exists(AppendSealPath(path)))
        {
            // The seal is a rebuildable cache. A mismatched/tampered seal must not be
            // carried into the next authenticated projection checkpoint.
            try { File.Delete(AppendSealPath(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var parsedHash = hasSeal ? string.Empty : ReadRangeHash(stream, 0, parsedLength);
        var appendChain = hasSeal ? seal!.ChainDigest : InitialAppendChain(parsedHash, parsedLength);
        var blockHashes = BuildSegmentBlockHashes(stream, parsedLength, prior);
        return new SegmentState(parsedLength, stream.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks,
            identity, prefixLength, ReadWindowHash(stream, 0, prefixLength), tailLength,
            ReadWindowHash(stream, parsedLength - tailLength, tailLength), parsedHash, appendChain,
            LedgerCommitmentBlockBytes, blockHashes, SegmentContentCommitment(parsedLength, blockHashes),
            hasSeal ? AppendSealCommitment(seal!) : string.Empty);
    }

    private string[] BuildSegmentBlockHashes(FileStream stream, long parsedLength, SegmentState? prior)
    {
        var count = SegmentBlockCount(parsedLength);
        var result = new string[count];
        var reusable = 0;
        if (prior is not null
            && prior.CommitmentBlockBytes == LedgerCommitmentBlockBytes
            && prior.ParsedLength >= 0 && prior.ParsedLength <= parsedLength
            && prior.ParsedBlockHashes is not null
            && prior.ParsedBlockHashes.Length == SegmentBlockCount(prior.ParsedLength)
            && prior.ParsedBlockHashes.All(IsSha256Hex))
        {
            reusable = checked((int)Math.Min(count, prior.ParsedLength / LedgerCommitmentBlockBytes));
            Array.Copy(prior.ParsedBlockHashes, result, reusable);
        }
        for (var index = reusable; index < count; index++)
        {
            var offset = checked((long)index * LedgerCommitmentBlockBytes);
            var length = Math.Min(LedgerCommitmentBlockBytes, parsedLength - offset);
            result[index] = ReadRangeHash(stream, offset, length);
        }
        return result;
    }

    private static int SegmentBlockCount(long length) =>
        length == 0 ? 0 : checked((int)(((length - 1) / LedgerCommitmentBlockBytes) + 1));

    private string SegmentContentCommitment(long parsedLength, IReadOnlyList<string> blockHashes) =>
        Hash(Canonical(new { schema = 1, blockBytes = LedgerCommitmentBlockBytes, parsedLength, blockHashes }));

    private string AppendSealCommitment(AppendSeal seal) => Hash(Canonical(new
    {
        seal.SchemaVersion,
        seal.FileIdentity,
        seal.StartLength,
        seal.Length,
        seal.PreviousChainDigest,
        seal.AppendedHash,
        seal.ChainDigest,
        updatedAt = Utc(seal.UpdatedAt).ToString("O")
    }));

    private string InitialAppendChain(string contentHash, long length) =>
        Hash(Canonical(new { schema = 1, kind = "ledger-initial", length, contentHash }));

    private string AppendChain(string previousChain, long startLength, long length, string appendedHash) =>
        Hash(Canonical(new { schema = 1, kind = "ledger-append", previousChain, startLength, length, appendedHash }));

    private static string AppendSealPath(string path) => path + ".append-seal.json";

    private bool TryReadAppendSeal(string path, out AppendSeal? seal)
    {
        seal = null;
        var sealPath = AppendSealPath(path);
        if (!File.Exists(sealPath)) return false;
        try
        {
            seal = JsonSerializer.Deserialize<AppendSeal>(File.ReadAllBytes(sealPath), _jsonOptions);
            return seal is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static bool AppendSealIsValid(AppendSeal? seal) => seal is
    {
        SchemaVersion: 1,
        StartLength: >= 0,
        Length: >= 0
    }
        && seal.Length >= seal.StartLength
        && !string.IsNullOrWhiteSpace(seal.FileIdentity)
        && IsSha256Hex(seal.PreviousChainDigest)
        && IsSha256Hex(seal.AppendedHash)
        && IsSha256Hex(seal.ChainDigest);

    private void WriteAppendSeal(string path, AppendSeal seal)
    {
        var sealPath = AppendSealPath(path);
        var temp = sealPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(seal, _jsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, sealPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void RefreshAnomalyCountCore()
    {
        using var anomalyLock = AcquireFileLock(AnomalyLockPath);
        if (!File.Exists(AnomalyPath))
        {
            _anomalyCount = 0;
            _anomalyState = null;
            return;
        }
        if (_anomalyState is not null && SegmentCanIncrement(AnomalyPath, _anomalyState))
        {
            if (new FileInfo(AnomalyPath).Length == _anomalyState.ParsedLength) return;
        }
        else
        {
            _anomalyCount = 0;
            _anomalyState = null;
        }
        var start = _anomalyState?.ParsedLength ?? 0;
        var read = ReadJsonLinesIncremental<AccountUsageAnomaly>(AnomalyPath, start, CancellationToken.None);
        _anomalyCount += read.Items.Count;
        if (read.BadLineCount > 0 || read.HasIncompleteTail)
        {
            _coverageGapDetected = true;
            _coverageGapMessage = "anomaly ledger is malformed or has an incomplete tail; prior coverage evidence is uncertain";
            _coverageGapFirstSeen ??= UtcNow();
            _persistentTokenIntegrityIssue = true;
            _persistentTokenIntegrityClass = "AnomalyIntegrity";
            _tokenSourceStale = true;
            PersistSourceIntegrityState(true, "AnomalyIntegrity", _coverageGapMessage, UtcNow());
        }
        var gaps = read.Items.Where(item => item.Kind == "source_coverage_gap").ToArray();
        if (gaps.Length > 0)
        {
            _coverageGapDetected = true;
            _coverageGapMessage = "usage 源曾发生不可恢复的离线轮转缺口；需显式重建/确认后才能清除";
            var first = gaps.Min(item => Utc(item.RecordedAt));
            _coverageGapFirstSeen = _coverageGapFirstSeen is null || first < _coverageGapFirstSeen ? first : _coverageGapFirstSeen;
        }
        var durableIntegrity = read.Items.Where(item => item.Kind is
            "bad_source_line" or "oversized_source_line" or "unrecognized_source_line"
            or "truncated_archived_tail" or "idempotency_payload_collision").ToArray();
        if (durableIntegrity.Length > 0)
        {
            _persistentTokenIntegrityIssue = true;
            _persistentTokenIntegrityClass = durableIntegrity.Any(item => item.Kind == "idempotency_payload_collision")
                ? "ReplayCollision"
                : durableIntegrity.Any(item => item.Kind == "truncated_archived_tail") ? "CoverageGap" : "SourceMalformed";
            _tokenSourceStale = true;
            PersistSourceIntegrityState(
                _persistentTokenIntegrityClass == "CoverageGap",
                _persistentTokenIntegrityClass,
                "durable source integrity anomaly remains unacknowledged",
                durableIntegrity.Min(item => Utc(item.RecordedAt)));
        }
        _anomalyState = CreateSegmentState(AnomalyPath, read.ParsedLength, _anomalyState);
    }

    private void LoadSourceIntegrityStateCore()
    {
        if (!File.Exists(SourceIntegrityStatePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<SourceIntegrityState>(
                File.ReadAllBytes(SourceIntegrityStatePath), _jsonOptions);
            if (state is null || state.SchemaVersion != 2 || state.UpdatedAt == default)
                throw new InvalidDataException("source integrity sticky state is invalid");
            if (state.CoverageGap is not null)
            {
                _coverageGapDetected = true;
                _coverageGapMessage = Safe(state.CoverageGap.Message, "source coverage gap remains unacknowledged");
                var firstSeen = Utc(state.CoverageGap.FirstSeen);
                _coverageGapFirstSeen = _coverageGapFirstSeen is null || firstSeen < _coverageGapFirstSeen
                    ? firstSeen : _coverageGapFirstSeen;
            }
            if (state.SourceMalformed is not null || state.CoverageGap is not null)
            {
                _persistentTokenIntegrityIssue = true;
                _persistentTokenIntegrityClass = Safe(
                    state.CoverageGap?.ErrorClass ?? state.SourceMalformed?.ErrorClass, "SourceIntegrity");
                _tokenSourceStale = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _coverageGapDetected = true;
            _coverageGapMessage = "source integrity sticky state cannot be verified; fail-closed coverage uncertainty";
            _coverageGapFirstSeen ??= UtcNow();
            _persistentTokenIntegrityIssue = true;
            _persistentTokenIntegrityClass = "IntegrityStateInvalid";
            _tokenSourceStale = true;
        }
    }

    private void PersistSourceIntegrityState(
        bool coverageGap,
        string errorClass,
        string? message,
        DateTimeOffset observedAt)
    {
        using var stateLock = AcquireFileLock(SourceIntegrityLockPath);
        SourceIntegrityState? prior = null;
        if (File.Exists(SourceIntegrityStatePath))
        {
            try
            {
                prior = JsonSerializer.Deserialize<SourceIntegrityState>(
                    File.ReadAllBytes(SourceIntegrityStatePath), _jsonOptions);
                if (prior?.SchemaVersion != 2)
                    prior = InvalidStickyState(Utc(observedAt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { prior = InvalidStickyState(Utc(observedAt)); }
        }
        var now = Utc(observedAt);
        var evidence = new StickyIntegrityEvidence(
            now,
            Safe(errorClass, "SourceIntegrity"),
            Safe(message, "source integrity evidence requires explicit rebuild or acknowledgement"),
            now);
        StickyIntegrityEvidence Merge(StickyIntegrityEvidence? existing) => existing is null
            ? evidence
            : existing with { UpdatedAt = now };
        var state = new SourceIntegrityState(
            2,
            coverageGap ? Merge(prior?.CoverageGap) : prior?.CoverageGap,
            coverageGap ? prior?.SourceMalformed : Merge(prior?.SourceMalformed),
            now);
        var temp = SourceIntegrityStatePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(state, _jsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, SourceIntegrityStatePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        LoadSourceIntegrityStateCore();
    }

    private static SourceIntegrityState InvalidStickyState(DateTimeOffset observedAt)
    {
        var evidence = new StickyIntegrityEvidence(observedAt, "IntegrityStateInvalid",
            "prior source integrity sticky state could not be verified; explicit rebuild or acknowledgement is required",
            observedAt);
        return new SourceIntegrityState(2, evidence, null, observedAt);
    }

    private AccountIdentity CreateAccountIdentity(string provider, string? rawAccount, bool attributed)
    {
        if (!attributed || rawAccount is null || rawAccount.All(char.IsWhiteSpace))
            return new AccountIdentity(UnattributedAccountId, 0, string.Empty, string.Empty);
        // Account identity is opaque. HMAC receives the complete in-memory identifier;
        // redaction/truncation is only for display and never participates in identity.
        var opaque = rawAccount.Normalize(NormalizationForm.FormC);
        var key = GetIdentityKey();
        var payload = Utf8NoBom.GetBytes(Canonical(new { provider, account = opaque }));
        var digest = Convert.ToHexString(HMACSHA256.HashData(key, payload));
        var keyId = _identityKeyId ?? throw new AccountLedgerIdentityKeyUnavailableException("身份 keyId 不可用。");
        var stable = $"AK1:{keyId}:{digest}";
        return new AccountIdentity($"账号 {digest[..8]}", 1, keyId, stable);
    }

    private RequestIdentity CreateRequestIdentity(ExecutionEnvelope envelope, string? rawRequestIdentity)
    {
        var stableMaterial = rawRequestIdentity;
        if (string.IsNullOrWhiteSpace(stableMaterial) && envelope.StableIdentity)
            stableMaterial = envelope.EventIdentity;
        if (!string.IsNullOrWhiteSpace(stableMaterial))
        {
            var digest = HmacDigest("request-identity:v1", new
            {
                sourceNamespace = envelope.SourceNamespace,
                requestIdentity = stableMaterial.Normalize(NormalizationForm.FormC)
            });
            var keyId = _identityKeyId ?? throw new AccountLedgerIdentityKeyUnavailableException("request identity keyId unavailable");
            return new RequestIdentity($"RK1:{keyId}:{digest}", $"请求 {digest[..8]}", 1, keyId, true);
        }

        // A missing producer identity cannot support an exact occurrence count. This keyed content
        // identity suppresses replays, but the fact remains in the ambiguous/unverified bucket.
        var ambiguous = HmacDigest("ambiguous-request:v1", new
        {
            sourceNamespace = envelope.SourceNamespace,
            envelope.EventIdentity
        });
        var ambiguousKeyId = _identityKeyId ?? throw new AccountLedgerIdentityKeyUnavailableException("ambiguous request identity keyId unavailable");
        return new RequestIdentity($"UV1:{ambiguousKeyId}:{ambiguous}", $"未验证 {ambiguous[..8]}", 1, ambiguousKeyId, false);
    }

    private string HmacDigest(string domain, object value)
    {
        var payload = Utf8NoBom.GetBytes(domain + "\0" + Canonical(value));
        return Convert.ToHexString(HMACSHA256.HashData(GetIdentityKey(), payload));
    }

    private byte[] GetIdentityKey()
    {
        if (_identityKeyUnavailableLatched)
            throw new AccountLedgerIdentityKeyUnavailableException("身份密钥状态已锁存为 Unavailable；需重启并恢复原 key。 ");
        if (_identityKey is not null) return _identityKey;
        var gate = IdentityKeyGates.GetOrAdd(IdentityKeyPath, _ => new object());
        lock (gate)
        {
            if (_identityKey is not null) return _identityKey;
            Directory.CreateDirectory(_dataDirectory);
            using var crossProcessLock = AcquireIdentityKeyLock();
            if (File.Exists(IdentityKeyPath)) return LoadIdentityKeyEnvelope();
            if (HasExistingLedgerArtifacts())
            {
                _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
                throw new AccountLedgerIdentityKeyUnavailableException(
                    "已有逐账号台账但身份密钥缺失；已 fail closed。请恢复原 account-ledger-identity.key 后重试。");
            }
            var clear = RandomNumberGenerator.GetBytes(32);
            var temp = IdentityKeyPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var envelope = new IdentityKeyEnvelope(
                    1, "HMAC-SHA-256/DPAPI-CurrentUser", DeriveIdentityKeyId(clear),
                    Convert.ToBase64String(ProtectedData.Protect(clear, IdentityEntropy, DataProtectionScope.CurrentUser)));
                var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(envelope, _jsonOptions));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                RestrictIdentityKeyAcl(temp);
                try { File.Move(temp, IdentityKeyPath, false); }
                catch (IOException ex)
                {
                    if (!File.Exists(IdentityKeyPath))
                        throw new AccountLedgerIdentityKeyUnavailableException("身份密钥原子创建失败；未继续归账。", ex);
                }
                return LoadIdentityKeyEnvelope();
            }
            catch (AccountLedgerIdentityKeyUnavailableException) { _identityKeyState = AccountLedgerIdentityKeyState.Unavailable; throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
                throw new AccountLedgerIdentityKeyUnavailableException("身份密钥不可创建或不可解密；已 fail closed。", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private void EnsureIdentityKeyForWrite()
    {
        EnsureLedgerSchemaReadyForWrite();
        if (_identityKeyUnavailableLatched)
            throw new AccountLedgerIdentityKeyUnavailableException("身份密钥状态已锁存为 Unavailable；拒绝再次写入。 ");
        var gate = IdentityKeyGates.GetOrAdd(IdentityKeyPath, _ => new object());
        lock (gate)
        {
            using var crossProcessLock = AcquireIdentityKeyLock();
            if (!File.Exists(IdentityKeyPath))
            {
                if (_identityKey is not null || HasExistingLedgerArtifacts())
                {
                    _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
                    throw new AccountLedgerIdentityKeyUnavailableException(
                        "身份密钥在运行中丢失；已 fail closed，未继续写入。 ");
                }
                _ = GetIdentityKeyAfterLockHeld();
                EnsureIdentityDomainManifest(_identityKeyId
                    ?? throw new AccountLedgerIdentityKeyUnavailableException("identity keyId unavailable after creation"));
                return;
            }
            var prior = _identityKey?.ToArray();
            var priorId = _identityKeyId;
            IdentityKeyMaterial candidate;
            try
            {
                candidate = ReadIdentityKeyEnvelopeMaterial();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or JsonException or FormatException)
            {
                _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
                _identityKeyUnavailableLatched = true;
                throw new AccountLedgerIdentityKeyUnavailableException(
                    "已有身份密钥的 ACL 或 envelope 无法验证；已 fail closed。",
                    ex);
            }
            if (prior is not null)
            {
                var matches = string.Equals(priorId, candidate.KeyId, StringComparison.Ordinal)
                              && CryptographicOperations.FixedTimeEquals(prior, candidate.ClearKey);
                CryptographicOperations.ZeroMemory(prior);
                if (!matches)
                {
                    CryptographicOperations.ZeroMemory(candidate.ClearKey);
                    _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
                    _identityKeyUnavailableLatched = true;
                    throw new AccountLedgerIdentityKeyUnavailableException(
                        "身份密钥在运行中被替换；已 fail closed，拒绝形成第二账号键域。 ");
                }
                CryptographicOperations.ZeroMemory(candidate.ClearKey);
                return;
            }
            var expectedIds = ReadExpectedIdentityKeyIds();
            if (expectedIds.Count > 0 && (expectedIds.Count != 1 || !expectedIds.Contains(candidate.KeyId)))
            {
                CryptographicOperations.ZeroMemory(candidate.ClearKey);
                _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
                _identityKeyUnavailableLatched = true;
                throw new AccountLedgerIdentityKeyUnavailableException(
                    "身份 keyId 与既有 v3 台账不一致；已 fail closed。 ");
            }
            _identityKey = candidate.ClearKey;
            _identityKeyId = candidate.KeyId;
            _identityKeyState = AccountLedgerIdentityKeyState.Available;
            EnsureIdentityDomainManifest(candidate.KeyId);
        }
    }

    private void EnsureLedgerSchemaReadyForWrite()
    {
        if (File.Exists(SchemaRebuildRequiredPath))
        {
            var marker = ReadSchemaRebuildMarker();
            throw new AccountLedgerSchemaMigrationRequiredException(marker.BackupDirectory, marker.DetectedSchema);
        }

        var artifacts = EnumerateSchemaPreflightArtifacts();
        foreach (var missing in _schemaPreflightStamps.Keys.Except(artifacts, StringComparer.OrdinalIgnoreCase).ToArray())
            _schemaPreflightStamps.Remove(missing);
        foreach (var artifact in artifacts)
        {
            var stamp = GetSchemaArtifactStamp(artifact);
            if (_schemaPreflightStamps.TryGetValue(artifact, out var validated) && validated == stamp) continue;
            var expectedSchema = ExpectedArtifactSchema(artifact);
            var stable = false;
            for (var attempt = 0; attempt < 2 && !stable; attempt++)
            {
                foreach (var detectedSchema in ReadArtifactSchemas(artifact))
                {
                    if (detectedSchema == expectedSchema) continue;
                    var backupDirectory = BackupLegacyLedgerArtifacts(artifacts, detectedSchema, artifact);
                    throw new AccountLedgerSchemaMigrationRequiredException(backupDirectory, detectedSchema);
                }
                var after = GetSchemaArtifactStamp(artifact);
                stable = stamp == after;
                stamp = after;
            }
            if (!stable) throw new IOException($"Ledger schema artifact '{Path.GetFileName(artifact)}' changed during preflight.");
            _schemaPreflightStamps[artifact] = stamp;
        }
    }

    private SchemaArtifactStamp GetSchemaArtifactStamp(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        var info = new FileInfo(path);
        var prefixLength = (int)Math.Min(PrefixWindowBytes, stream.Length);
        var tailLength = (int)Math.Min(AnchorWindowBytes, stream.Length);
        return new SchemaArtifactStamp(stream.Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks,
            GetFileIdentity(stream, path), ReadWindowHash(stream, 0, prefixLength),
            ReadWindowHash(stream, stream.Length - tailLength, tailLength));
    }

    private void RememberTrustedSchemaArtifact(string path)
    {
        if (File.Exists(path)) _schemaPreflightStamps[path] = GetSchemaArtifactStamp(path);
    }

    private IReadOnlyList<string> EnumerateSchemaPreflightArtifacts()
    {
        if (!Directory.Exists(_dataDirectory)) return Array.Empty<string>();
        var names = new[]
        {
            "account-token-attempts-*.jsonl", "account-token-attempts.jsonl",
            "account-quota-snapshots-*.jsonl", "account-quota-snapshots.jsonl",
            "account-quota-prepares-*.jsonl", "account-quota-prepares.jsonl",
            "account-quota-commits-*.jsonl", "account-quota-commits.jsonl",
            "account-token-anomalies.jsonl",
            Path.GetFileName(CursorPath), Path.GetFileName(CursorRecoveryPath),
            Path.GetFileName(SourceInitializedPath)
        };
        return names.SelectMany(name => name.Contains('*')
                ? Directory.EnumerateFiles(_dataDirectory, name, SearchOption.TopDirectoryOnly)
                : new[] { Path.Combine(_dataDirectory, name) }.Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int ExpectedArtifactSchema(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Equals(Path.GetFullPath(CursorPath), StringComparison.OrdinalIgnoreCase)
            || full.Equals(Path.GetFullPath(CursorRecoveryPath), StringComparison.OrdinalIgnoreCase))
            return CursorSchemaVersion;
        if (full.Equals(Path.GetFullPath(SourceInitializedPath), StringComparison.OrdinalIgnoreCase))
            return SourceMarkerSchemaVersion;
        return LedgerSchemaVersion;
    }

    private IEnumerable<int> ReadArtifactSchemas(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            if (stream.Length > MaximumSourceLineBytes)
                throw new InvalidDataException($"Ledger metadata '{Path.GetFileName(path)}' exceeds the bounded schema preflight limit.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            if (TryParseSchema(memory.ToArray(), out var schema)) yield return schema;
            yield break;
        }

        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream(4096);
        var oversized = false;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    if (!oversized && line.Length >= MaximumSourceLineBytes)
                    {
                        oversized = true;
                        line.SetLength(0);
                    }
                    else if (!oversized) line.WriteByte(buffer[index]);
                    continue;
                }
                if (!oversized && line.Length > 0 && TryParseSchema(line.ToArray(), out var schema))
                    yield return schema;
                line.SetLength(0);
                oversized = false;
            }
        }
        if (!oversized && line.Length > 0 && TryParseSchema(line.ToArray(), out var tailSchema))
            yield return tailSchema;
    }

    private static bool TryParseSchema(byte[] bytes, out int schema)
    {
        schema = 0;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return TryReadSchema(document.RootElement, out schema);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadSchema(JsonElement root, out int schema)
    {
        schema = 0;
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty("schemaVersion", out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out schema);
    }

    private string BackupLegacyLedgerArtifacts(IReadOnlyList<string> artifacts, int detectedSchema, string detectedPath)
    {
        Directory.CreateDirectory(_dataDirectory);
        var root = Path.Combine(_dataDirectory, "ledger-schema-upgrade-backups");
        Directory.CreateDirectory(root);
        var timestamp = UtcNow().ToString("yyyyMMdd-HHmmss-fff");
        var backupDirectory = Path.Combine(root, $"{timestamp}-schema{detectedSchema}-to-v{LedgerSchemaVersion}");
        for (var suffix = 1; Directory.Exists(backupDirectory); suffix++)
            backupDirectory = Path.Combine(root, $"{timestamp}-schema{detectedSchema}-to-v{LedgerSchemaVersion}-{suffix}");
        Directory.CreateDirectory(backupDirectory);
        var manifest = new List<SchemaBackupEntry>();
        foreach (var source in artifacts)
        {
            var destination = Path.Combine(backupDirectory, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: false);
            using var input = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest.Add(new SchemaBackupEntry(Path.GetFileName(source), input.Length,
                Convert.ToHexString(SHA256.HashData(input))));
        }
        WriteAtomicJson(Path.Combine(backupDirectory, "manifest.json"), new SchemaBackupManifest(
            1, detectedSchema, LedgerSchemaVersion, Path.GetFileName(detectedPath), UtcNow(), manifest));
        var marker = new SchemaRebuildRequiredMarker(1, detectedSchema, LedgerSchemaVersion,
            backupDirectory, "Explicitly migrate the backup or rebuild the v4 ledger from the authoritative source; mixed-schema append is blocked.", UtcNow());
        WriteAtomicJson(SchemaRebuildRequiredPath, marker);
        return backupDirectory;
    }

    private SchemaRebuildRequiredMarker ReadSchemaRebuildMarker()
    {
        try
        {
            var marker = JsonSerializer.Deserialize<SchemaRebuildRequiredMarker>(
                File.ReadAllBytes(SchemaRebuildRequiredPath), _jsonOptions);
            if (marker is null || marker.SchemaVersion != 1 || marker.TargetSchema != LedgerSchemaVersion
                || marker.DetectedSchema <= 0 || string.IsNullOrWhiteSpace(marker.BackupDirectory))
                throw new InvalidDataException("Account ledger rebuild marker is invalid.");
            return marker;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("Account ledger rebuild marker cannot be verified; writes are blocked.", ex);
        }
    }

    private void WriteAtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(value, _jsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private byte[] GetIdentityKeyAfterLockHeld()
    {
        // Called only for a genuinely empty ledger while the identity lock is held.
        if (File.Exists(IdentityKeyPath)) return LoadIdentityKeyEnvelope();
        if (HasExistingLedgerArtifacts())
            throw new AccountLedgerIdentityKeyUnavailableException("已有台账但身份密钥缺失；已 fail closed。");
        var clear = RandomNumberGenerator.GetBytes(32);
        var temp = IdentityKeyPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var envelope = new IdentityKeyEnvelope(1, "HMAC-SHA-256/DPAPI-CurrentUser",
                DeriveIdentityKeyId(clear), Convert.ToBase64String(ProtectedData.Protect(clear, IdentityEntropy, DataProtectionScope.CurrentUser)));
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(envelope, _jsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            RestrictIdentityKeyAcl(temp);
            File.Move(temp, IdentityKeyPath, false);
            return LoadIdentityKeyEnvelope();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
            throw new AccountLedgerIdentityKeyUnavailableException("身份密钥原子创建失败；已 fail closed。", ex);
        }
        finally { CryptographicOperations.ZeroMemory(clear); if (File.Exists(temp)) File.Delete(temp); }
    }

    private byte[] LoadIdentityKeyEnvelope()
    {
        try
        {
            var material = ReadIdentityKeyEnvelopeMaterial();
            var expectedIds = ReadExpectedIdentityKeyIds();
            if (expectedIds.Count > 0 && (expectedIds.Count != 1 || !expectedIds.Contains(material.KeyId)))
            {
                CryptographicOperations.ZeroMemory(material.ClearKey);
                throw new CryptographicException("身份 keyId 与既有 v3 台账不一致。");
            }
            if (_identityKey is not null) CryptographicOperations.ZeroMemory(_identityKey);
            _identityKey = material.ClearKey;
            _identityKeyId = material.KeyId;
            _identityKeyState = AccountLedgerIdentityKeyState.Available;
            return material.ClearKey;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException)
        {
            _identityKeyState = AccountLedgerIdentityKeyState.Unavailable;
            _identityKeyUnavailableLatched = true;
            throw new AccountLedgerIdentityKeyUnavailableException("身份密钥 envelope 不可用；已 fail closed，未生成替代密钥。", ex);
        }
    }

    private IdentityKeyMaterial ReadIdentityKeyEnvelopeMaterial()
    {
        VerifyIdentityKeyAcl(IdentityKeyPath);
        var envelope = JsonSerializer.Deserialize<IdentityKeyEnvelope>(File.ReadAllBytes(IdentityKeyPath), _jsonOptions)
                       ?? throw new CryptographicException("身份密钥 envelope 为空。 ");
        if (envelope.SchemaVersion != 1
            || !string.Equals(envelope.Algorithm, "HMAC-SHA-256/DPAPI-CurrentUser", StringComparison.Ordinal)
            || envelope.KeyId.Length != 32 || !envelope.KeyId.All(Uri.IsHexDigit))
            throw new CryptographicException("身份密钥 envelope schema/algorithm/keyId 非法。 ");
        var clear = ProtectedData.Unprotect(Convert.FromBase64String(envelope.ProtectedKey), IdentityEntropy,
            DataProtectionScope.CurrentUser);
        if (clear.Length != 32) { CryptographicOperations.ZeroMemory(clear); throw new CryptographicException("身份密钥长度非法。 "); }
        var derivedId = DeriveIdentityKeyId(clear);
        if (!FixedHexEquals64(derivedId, envelope.KeyId))
        {
            CryptographicOperations.ZeroMemory(clear);
            throw new CryptographicException("身份 keyId 与解密密钥不匹配。 ");
        }
        return new IdentityKeyMaterial(envelope.KeyId.ToUpperInvariant(), clear);
    }

    private HashSet<string> ReadExpectedIdentityKeyIds()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        IdentityDomainManifest? manifest = null;
        if (File.Exists(IdentityDomainPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<IdentityDomainManifest>(File.ReadAllBytes(IdentityDomainPath), _jsonOptions);
                if (manifest is null || manifest.SchemaVersion is not (1 or 2) || manifest.KeyId.Length != 32
                    || !manifest.KeyId.All(Uri.IsHexDigit)) throw new CryptographicException("identity domain manifest is invalid");
                if (manifest.SchemaVersion == 2 && IsSha256Hex(manifest.LedgerCheckpoint)
                    && FixedHexEquals(manifest.LedgerCheckpoint, ComputeIdentityLedgerCheckpoint()))
                {
                    result.Add(manifest.KeyId.ToUpperInvariant());
                    return result;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { throw new CryptographicException("identity domain manifest cannot be read", ex); }
        }
        foreach (var path in GetSegments("account-token-attempts-*.jsonl", "account-token-attempts.jsonl")
                     .Concat(GetSegments("account-quota-snapshots-*.jsonl", "account-quota-snapshots.jsonl")))
        {
            ScanIdentityKeyIds(path, result);
        }
        if (manifest is not null)
        {
            var manifestId = manifest.KeyId.ToUpperInvariant();
            if (result.Count > 0 && (result.Count != 1 || !result.Contains(manifestId)))
                throw new CryptographicException("identity domain manifest does not match the keyed ledger facts");
            result.Add(manifestId);
        }
        return result;
    }

    private void ScanIdentityKeyIds(string path, ISet<string> result)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream(4096);
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    if (line.Length >= MaximumSourceLineBytes)
                        throw new CryptographicException("identity key-domain row exceeds the bounded line limit");
                    line.WriteByte(buffer[index]);
                    continue;
                }
                ReadIdentityKeyIdsFromLine(line.ToArray(), result);
                line.SetLength(0);
            }
        }
        if (line.Length != 0)
        {
            var tail = line.ToArray();
            try
            {
                // A complete final JSON object remains identity evidence even if a crash omitted
                // only its newline. The normal ledger refresh will append that delimiter.
                ReadIdentityKeyIdsFromLine(tail, result);
            }
            catch (CryptographicException)
            {
                try
                {
                    // Structurally complete but invalid identity evidence must still fail closed.
                    using var _ = JsonDocument.Parse(tail);
                    throw;
                }
                catch (JsonException)
                {
                    // A syntactically incomplete final fragment is not a committed fact. Ignore it
                    // for key-domain discovery; the locked ledger refresh truncates it before use.
                }
            }
        }
    }

    private static void ReadIdentityKeyIdsFromLine(byte[] bytes, ISet<string> result)
    {
        if (bytes.Length == 0) return;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CryptographicException("identity key-domain row must be a JSON object");
            if (!root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaValue))
                throw new CryptographicException("identity key-domain row has no valid schemaVersion");
            if (schemaValue != LedgerSchemaVersion)
                throw new CryptographicException($"legacy ledger schema {schemaValue} requires explicit migration before v{LedgerSchemaVersion}");
            var isAttempt = root.TryGetProperty("requestIdentity", out _) || root.TryGetProperty("attemptOrdinal", out _);
            var isQuota = root.TryGetProperty("observationBatch", out _) || root.TryGetProperty("periodKey", out _);
            if (isAttempt == isQuota)
                throw new CryptographicException("identity key-domain row kind is missing or ambiguous");
            if (isAttempt)
            {
                result.Add(ReadRequiredKeyId(root, "requestKeyId"));
                return;
            }
            if (!root.TryGetProperty("accountAttributed", out var attributed)
                || attributed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new CryptographicException("quota identity row has no valid accountAttributed flag");
            if (attributed.ValueKind == JsonValueKind.True)
                result.Add(ReadRequiredKeyId(root, "accountKeyId"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { throw new CryptographicException("identity key-domain row is malformed", ex); }
    }

    private static string ReadRequiredKeyId(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { Length: 32 } value
            || !value.All(Uri.IsHexDigit))
            throw new CryptographicException($"identity key-domain row has no valid {propertyName}");
        return value.ToUpperInvariant();
    }

    private void EnsureIdentityDomainManifest(string keyId)
    {
        WriteIdentityDomainManifest(keyId);
    }

    private void UpdateIdentityDomainManifestAfterCommit()
    {
        var keyId = _identityKeyId
                    ?? throw new AccountLedgerIdentityKeyUnavailableException("identity keyId unavailable after durable commit");
        using var identityLock = AcquireIdentityKeyLock();
        WriteIdentityDomainManifest(keyId);
    }

    private void WriteIdentityDomainManifest(string keyId)
    {
        var temp = IdentityDomainPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(new IdentityDomainManifest(
                2, keyId, ComputeIdentityLedgerCheckpoint(), UtcNow()), _jsonOptions));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            RestrictIdentityKeyAcl(temp);
            File.Move(temp, IdentityDomainPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string ComputeIdentityLedgerCheckpoint()
    {
        var segments = GetSegments("account-token-attempts-*.jsonl", "account-token-attempts.jsonl")
            .Concat(GetSegments("account-quota-snapshots-*.jsonl", "account-quota-snapshots.jsonl"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var info = new FileInfo(path);
                var prefixLength = (int)Math.Min(PrefixWindowBytes, stream.Length);
                var tailLength = (int)Math.Min(AnchorWindowBytes, stream.Length);
                return new
                {
                    name = Path.GetFileName(path),
                    length = stream.Length,
                    creationUtcTicks = info.CreationTimeUtc.Ticks,
                    lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    identity = GetFileIdentity(stream, path),
                    prefix = ReadWindowHash(stream, 0, prefixLength),
                    tail = ReadWindowHash(stream, stream.Length - tailLength, tailLength)
                };
            }).ToArray();
        return Hash(Canonical(segments));
    }

    private static string DeriveIdentityKeyId(byte[] clear)
    {
        var domain = Utf8NoBom.GetBytes("CodexModelManager:AccountKeyId:v1\0");
        var payload = new byte[domain.Length + clear.Length];
        Buffer.BlockCopy(domain, 0, payload, 0, domain.Length);
        Buffer.BlockCopy(clear, 0, payload, domain.Length, clear.Length);
        var full = SHA256.HashData(payload);
        return Convert.ToHexString(full.AsSpan(0, 16));
    }

    private static bool FixedHexEquals64(string left, string right)
    {
        if (left.Length != 32 || right.Length != 32 || !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private FileStream AcquireIdentityKeyLock()
    {
        // A fresh ledger may reach the identity-key lock before any segment or metadata
        // has created its directory. DirectoryNotFoundException is an IOException, so
        // allowing it into the retry loop would misreport a missing parent as a held
        // cross-process lock after ten seconds.
        Directory.CreateDirectory(_dataDirectory);
        var lockPath = IdentityKeyPath + ".lock";
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1)) { Thread.Sleep(25); }
            catch (IOException ex) { throw new AccountLedgerIdentityKeyUnavailableException("等待身份密钥跨进程锁超时。", ex); }
        }
    }

    private bool HasExistingLedgerArtifacts() => File.Exists(CursorPath) || File.Exists(CursorRecoveryPath)
        || File.Exists(SourceInitializedPath) || File.Exists(SourceIntegrityStatePath)
        || File.Exists(AnomalyPath) || File.Exists(IdentityDomainPath) || File.Exists(SchemaRebuildRequiredPath)
        || File.Exists(ProjectionCheckpointPath)
        || File.Exists(Path.Combine(_dataDirectory, "account-token-attempts-v1.idx"))
        || File.Exists(Path.Combine(_dataDirectory, "account-quota-facts-v1.idx"))
        || File.Exists(Path.Combine(_dataDirectory, "account-quota-prepares-v1.idx"))
        || File.Exists(Path.Combine(_dataDirectory, "account-quota-commits-v1.idx"))
        || File.Exists(Path.Combine(_dataDirectory, "account-request-membership-v1.idx"))
        || File.Exists(Path.Combine(_dataDirectory, "request-scope-membership-v1.idx"))
        || Directory.Exists(_dataDirectory) && Directory.EnumerateFiles(_dataDirectory, "*.append-seal.json",
            SearchOption.TopDirectoryOnly).Any()
        || GetSegments("account-token-attempts-*.jsonl", "account-token-attempts.jsonl").Count > 0
        || GetSegments("account-quota-snapshots-*.jsonl", "account-quota-snapshots.jsonl").Count > 0
        || GetSegments("account-quota-prepares-*.jsonl", "account-quota-prepares.jsonl").Count > 0
        || GetSegments("account-quota-commits-*.jsonl", "account-quota-commits.jsonl").Count > 0;

    private static void RestrictIdentityKeyAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = WindowsIdentity.GetCurrent().User
                  ?? throw new UnauthorizedAccessException("无法解析当前 Windows 用户 SID。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        var file = new FileInfo(path);
        file.SetAccessControl(security);

        VerifyIdentityKeyAcl(path);
    }

    private static void VerifyIdentityKeyAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User
                  ?? throw new UnauthorizedAccessException("无法解析当前 Windows 用户 SID。");
        // Elevated Windows tokens (including WDAGUtilityAccount in Windows Sandbox)
        // can naturally assign newly created files to the token owner, commonly
        // the built-in Administrators SID. Accept only the current user or this
        // token's own owner; the protected single-user DACL remains mandatory.
        var tokenOwner = identity.Owner ?? sid;
        var file = new FileInfo(path);
        // A CreateNew file is already owned by the process identity. Explicitly calling
        // SetOwner asks Windows for WRITE_OWNER even when the owner would not change, which
        // fails under the deliberately restricted desktop token. Verify the natural owner
        // instead of trying to rewrite it, and keep the operation fail-closed.
        var applied = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = applied.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var rules = applied.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .ToArray();
        if (owner is null || (!sid.Equals(owner) && !tokenOwner.Equals(owner))
            || !applied.AreAccessRulesProtected
            || rules.Length != 1
            || rules[0].AccessControlType != AccessControlType.Allow
            || rules[0].IdentityReference is not SecurityIdentifier ruleSid
            || !sid.Equals(ruleSid)
            || (rules[0].FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
            throw new UnauthorizedAccessException("身份密钥 ACL 未收敛到当前 Windows 用户；拒绝继续。");
    }

    private AccountUsageAnomaly CollisionAnomaly(AccountUsageAttemptFact prior, AccountUsageAttemptFact incoming) => new(
        LedgerSchemaVersion, "idempotency_payload_collision", incoming.IdempotencyKey,
        prior.PayloadHash, incoming.PayloadHash, null, incoming.Source,
        "同一事件键出现不同 payloadHash；保留首条，不重复计费", UtcNow());

    private AccountUsageAnomaly CollisionAnomaly(AccountQuotaSnapshotFact prior, AccountQuotaSnapshotFact incoming) => new(
        LedgerSchemaVersion, "quota_idempotency_payload_collision", incoming.IdempotencyKey,
        prior.PayloadHash, incoming.PayloadHash, null, incoming.Source,
        "同一额度事件键出现不同 payloadHash；保留首条", UtcNow());

    private AccountUsageAnomaly SourceAnomaly(string kind, long? offset, string message) => new(
        LedgerSchemaVersion, Safe(kind, "source_anomaly"), null, null, null, offset,
                    "总管家 request-log.jsonl", Safe(message, "源日志异常"), UtcNow());

    private void PublishIfFullyInitialized(int recentAttemptLimit, bool quotaSourceReadFailed)
    {
        if (!_attemptIndex.Initialized || !_quotaIndex.Initialized
            || !_quotaPrepareIndex.Initialized || !_quotaCommitIndex.Initialized) return;
        PublishSnapshot(BuildSnapshot(recentAttemptLimit, quotaSourceReadFailed));
    }

    private AccountUsageLedgerSnapshot PublishSnapshot(AccountUsageLedgerSnapshot snapshot)
    {
        lock (_snapshotStateGate) return PublishSnapshotUnderStateLock(snapshot);
    }

    private AccountUsageLedgerSnapshot PublishSnapshotUnderStateLock(AccountUsageLedgerSnapshot snapshot)
    {
        var status = Volatile.Read(ref _importerStatus);
        var committed = snapshot with
        {
            Revision = checked(++_snapshotRevision),
            ImporterStatus = status,
            TokenSourceStale = _tokenSourceStale
        };
        Volatile.Write(ref _lastSnapshot, committed);
        SnapshotSubscriber[] subscribers;
        lock (_snapshotSubscriberGate) subscribers = _snapshotSubscribers.ToArray();
        foreach (var subscriber in subscribers) subscriber.Enqueue(committed);
        return committed;
    }

    private sealed class SnapshotSubscriber
    {
        private readonly AccountUsageLedgerService _owner;
        private readonly object _gate = new();
        private AccountUsageLedgerSnapshot? _pending;
        private bool _running;
        private bool _detached;

        public SnapshotSubscriber(AccountUsageLedgerService owner, EventHandler<AccountUsageLedgerSnapshot> handler)
        {
            _owner = owner;
            Handler = handler;
        }

        public EventHandler<AccountUsageLedgerSnapshot> Handler { get; }
        public bool IsRunning { get { lock (_gate) return _running; } }
        public bool HasPending { get { lock (_gate) return _pending is not null; } }

        public void Enqueue(AccountUsageLedgerSnapshot snapshot)
        {
            lock (_gate)
            {
                if (_detached) return;
                if (_pending is null || snapshot.Revision > _pending.Revision) _pending = snapshot;
                if (_running) return;
                _running = true;
            }
            _ = Task.Run(Drain);
        }

        public void Detach()
        {
            lock (_gate) { _detached = true; _pending = null; }
        }

        private void Drain()
        {
            while (true)
            {
                AccountUsageLedgerSnapshot snapshot;
                lock (_gate)
                {
                    if (_detached || _pending is null) { _running = false; return; }
                    snapshot = _pending;
                    _pending = null;
                }
                try { Handler(_owner, snapshot); } catch { }
            }
        }
    }

    private void PublishSignal(EventHandler? signal)
    {
        if (signal is null) return;
        foreach (EventHandler handler in signal.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch { }
    }

    private IReadOnlyList<string> GetSegments(string pattern, string legacyName)
    {
        if (!Directory.Exists(_dataDirectory)) return Array.Empty<string>();
        var segments = Directory.GetFiles(_dataDirectory, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var legacy = Path.Combine(_dataDirectory, legacyName);
        if (File.Exists(legacy)) segments.Insert(0, legacy);
        return segments;
    }

    private string AttemptSegmentPath(DateTimeOffset utc) =>
        Path.Combine(_dataDirectory, $"account-token-attempts-{utc:yyyy-MM}.jsonl");
    private string QuotaSegmentPath(DateTimeOffset utc) =>
        Path.Combine(_dataDirectory, $"account-quota-snapshots-{utc:yyyy-MM}.jsonl");
    private string QuotaPrepareSegmentPath(DateTimeOffset utc) =>
        Path.Combine(_dataDirectory, $"account-quota-prepares-{utc:yyyy-MM}.jsonl");
    private string QuotaCommitSegmentPath(DateTimeOffset utc) =>
        Path.Combine(_dataDirectory, $"account-quota-commits-{utc:yyyy-MM}.jsonl");
    private DateTimeOffset UtcNow() => Utc(_clock());
    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();

    private static string NormalizeProvider(string? value, bool linked) => linked
        ? Safe(value, "unknown").Trim().ToLowerInvariant()
        : UnlinkedProviderId;
    private static string NormalizeObservationScope(string? value)
    {
        var normalized = Safe(value, "source:unscoped").Trim().ToLowerInvariant();
        return ObservationScopeIsValid(normalized) ? normalized : "source:unscoped";
    }
    private static bool ObservationScopeIsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumPersistedStringLength
            || value.Any(char.IsControl) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return false;
        return value.StartsWith("pool:", StringComparison.Ordinal)
               || value.StartsWith("source:", StringComparison.Ordinal);
    }
    private static string SafePeriodKey(string? value, AccountQuotaAvailability availability) =>
        availability == AccountQuotaAvailability.Provided
            ? Safe(value, "unknown").Trim().ToLowerInvariant()
            : string.Empty;
    private static string Safe(string? value, string fallback)
    {
        var redacted = RuntimeTruthSanitizer.Redact(value);
        if (string.IsNullOrWhiteSpace(redacted)) return fallback;
        var clean = new string(redacted.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return clean.Length == 0 ? fallback : clean[..Math.Min(clean.Length, MaximumPersistedStringLength)];
    }
    private static string SafeOpaque(string? value) => SafeOpaqueNullable(value) ?? "unknown";
    private static string? SafeOpaqueNullable(string? value)
    {
        if (value is null) return null;
        var redacted = RuntimeTruthSanitizer.Redact(value);
        if (redacted is null || redacted.All(char.IsWhiteSpace)) return null;
        var clean = new string(redacted.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return clean[..Math.Min(clean.Length, MaximumPersistedStringLength)];
    }
    private static string? SafeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : Safe(value, "unknown");
    private static AttemptTokenUsageFact? SanitizeUsage(AttemptTokenUsageFact? usage) => usage is null ? null : usage with
    {
        ValidationMessage = Safe(usage.ValidationMessage, "校验状态未知"),
        SourcePath = Safe(usage.SourcePath, "usage")
    };
    private string Canonical(object value) => JsonSerializer.Serialize(value, _jsonOptions);
    private static string Hash(string value) => Hash(Utf8NoBom.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static bool IsSha256Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool FixedHexEquals(string? left, string? right)
    {
        if (!IsSha256Hex(left) || !IsSha256Hex(right)) return false;
        try
        {
            var a = Convert.FromHexString(left!);
            var b = Convert.FromHexString(right!);
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException) { return false; }
    }
    private static string ReadWindowHash(FileStream stream, long start, int length)
    {
        if (length == 0) return Hash(Array.Empty<byte>());
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[length]; var read = 0;
        while (read < length) { var count = stream.Read(bytes, read, length - read); if (count == 0) break; read += count; }
        return read == length ? Hash(bytes) : string.Empty;
    }

    private static string ReadRangeHash(FileStream stream, long start, long length)
    {
        stream.Seek(start, SeekOrigin.Begin);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) return string.Empty;
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string GetFileIdentity(FileStream stream, string path)
    {
        if (OperatingSystem.IsWindows() && GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            return $"win:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        var file = new FileInfo(path);
        return $"fallback:{file.CreationTimeUtc.Ticks}:{Path.GetFileName(path)}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public void Dispose()
    {
        CloseSourceContinuation();
        if (_identityKey is not null) CryptographicOperations.ZeroMemory(_identityKey);
        _gate.Dispose();
    }

    private sealed record ExecutionEnvelope(
        RuntimeRouteExecution Execution,
        string SourceNamespace,
        string EventIdentity,
        string ProvenanceIdentity,
        bool StableIdentity);
    private sealed record AppendResult(int CandidateCount, int AppendedCount, int DuplicateCount, int ConflictingReplayCount);
    private sealed record IncrementalRead<T>(IReadOnlyList<T> Items, int BadLineCount, long ParsedLength, bool HasIncompleteTail);
    private sealed record StreamingRead(int BadLineCount, long ParsedLength, bool HasIncompleteTail);
    private sealed record SegmentState(long ParsedLength, long FileLength, long LastWriteUtcTicks, long CreationUtcTicks,
        string FileIdentity, int PrefixLength, string PrefixHash, int TailLength, string TailHash,
        string ParsedPrefixHash, string AppendChainDigest, int CommitmentBlockBytes,
        string[] ParsedBlockHashes, string ParsedContentCommitment, string AppendSealCommitment);
    private sealed record IndexCheckpoint(
        bool Initialized,
        long Version,
        long RebuildGeneration,
        int BadLineCount,
        int IntegrityFailureCount,
        int CollisionCount,
        int DuplicateCount,
        long DiskCount,
        long DiskLength,
        string DiskSha256,
        Dictionary<string, SegmentState> Segments);
    private sealed record MembershipCheckpoint(long Count, long Length, string Sha256);
    private sealed record ProjectionCheckpointEnvelope(
        int SchemaVersion, string PayloadBase64, string PayloadSha256, string PayloadHmac);
    private sealed record ProjectionCheckpoint(
        int SchemaVersion,
        int LedgerSchemaVersion,
        string IdentityKeyId,
        DateTimeOffset CreatedAt,
        IndexCheckpoint AttemptIndex,
        IndexCheckpoint QuotaIndex,
        IndexCheckpoint QuotaPrepareIndex,
        IndexCheckpoint QuotaCommitIndex,
        MembershipCheckpoint AccountRequestMembership,
        MembershipCheckpoint ScopeRequestMembership,
        AttemptProjectionCheckpoint AttemptProjection,
        QuotaProjectionCheckpoint QuotaProjection,
        AccountUsageLedgerSnapshot Snapshot,
        bool QuotaSourceReadFailed);
    private sealed record AppendSeal(int SchemaVersion, string FileIdentity, long StartLength, long Length,
        string PreviousChainDigest, string AppendedHash, string ChainDigest, DateTimeOffset UpdatedAt);
    private sealed record SourceCursor(int SchemaVersion, long Offset, long AnchorStart, int AnchorLength, string AnchorHash,
        long SourceLength, long SourceLastWriteUtcTicks, string SourceIdentity, string Generation, DateTimeOffset UpdatedAt,
        string SourceContract,
        bool GenerationMarkerOnly = false);
    private sealed record SourceInitializedMarker(int SchemaVersion, string SourceIdentity, long SourceLength,
        long ObservedOffset, string Generation, string SourceContract, string PrefixHash, int DigestBlockBytes,
        IReadOnlyList<string> ContentBlockHashes, DateTimeOffset InitializedAt);
    private sealed record LegacySourceCursor(int SchemaVersion, long Offset, long AnchorStart, int AnchorLength, string AnchorHash,
        long SourceLength, long SourceLastWriteUtcTicks, string SourceIdentity, string Generation, DateTimeOffset UpdatedAt,
        bool GenerationMarkerOnly = false);
    private sealed record LegacySourceInitializedMarker(int SchemaVersion, string SourceIdentity, long SourceLength,
        long ObservedOffset, string Generation, string PrefixHash, int DigestBlockBytes,
        IReadOnlyList<string> ContentBlockHashes, DateTimeOffset InitializedAt);
    private sealed record StickyIntegrityEvidence(
        DateTimeOffset FirstSeen, string ErrorClass, string Message, DateTimeOffset UpdatedAt);
    private sealed record SourceIntegrityState(int SchemaVersion, StickyIntegrityEvidence? CoverageGap,
        StickyIntegrityEvidence? SourceMalformed, DateTimeOffset UpdatedAt);
    private sealed record CursorRead(SourceCursor? Cursor, AccountUsageAnomaly? Anomaly, bool LegacyContractMigration = false);
    private sealed record SourceLine(long Offset, string Text);
    private sealed record BadSourceLine(string Kind, long Offset, string Message);
    private sealed record CompleteLineRead(
        IReadOnlyList<SourceLine> Lines,
        IReadOnlyList<long> OversizedLines,
        long NextOffset,
        bool StoppedAtLimit,
        bool HasIncompleteTail,
        long ObservedSourceLength)
    {
        public int OversizedLineCount => OversizedLines.Count;
    }
    private sealed record SourceScan(IReadOnlyList<SourceLine> Lines, IReadOnlyList<BadSourceLine> BadLines,
        SourceCursor? NextCursor, bool SourceResetDetected, string Generation, AccountUsageSourceAvailability Availability,
        bool CoverageGapDetected = false,
        long? PreviousOffset = null,
        string PrefixHash = "",
        IReadOnlyList<string>? ContentBlockHashesValue = null,
        bool SourceContractMigrated = false)
    {
        public IReadOnlyList<string> ContentBlockHashes => ContentBlockHashesValue ?? Array.Empty<string>();
        public static SourceScan Missing { get; } = new(Array.Empty<SourceLine>(), Array.Empty<BadSourceLine>(), null, false,
            string.Empty, AccountUsageSourceAvailability.Missing);
        public static SourceScan Disabled { get; } = new(Array.Empty<SourceLine>(), Array.Empty<BadSourceLine>(), null, false,
            string.Empty, AccountUsageSourceAvailability.Disabled);
    }
    private sealed record ArchivedSourceResume(
        FileStream Stream,
        bool TailComplete,
        bool CoverageGap,
        long CreationUtcTicks,
        long LastWriteUtcTicks);
    private sealed record IdentityKeyEnvelope(int SchemaVersion, string Algorithm, string KeyId, string ProtectedKey);
    private sealed record IdentityDomainManifest(int SchemaVersion, string KeyId, string LedgerCheckpoint, DateTimeOffset UpdatedAt);
    private sealed record SchemaBackupEntry(string FileName, long Length, string Sha256);
    private sealed record SchemaBackupManifest(int SchemaVersion, int DetectedSchema, int TargetSchema,
        string DetectedFile, DateTimeOffset CreatedAt, IReadOnlyList<SchemaBackupEntry> Files);
    private sealed record SchemaRebuildRequiredMarker(int SchemaVersion, int DetectedSchema, int TargetSchema,
        string BackupDirectory, string RecoveryInstruction, DateTimeOffset CreatedAt);
    private sealed record SchemaArtifactStamp(long Length, long CreationUtcTicks, long LastWriteUtcTicks,
        string FileIdentity, string PrefixHash, string TailHash);
    private sealed record IdentityKeyMaterial(string KeyId, byte[] ClearKey);
    private sealed record AccountIdentity(string DisplayLabel, int KeyVersion, string KeyId, string StableIdentity);
    private sealed record RequestIdentity(string Value, string DisplayLabel, int KeyVersion, string KeyId, bool Verified);
    private readonly record struct AccountGroupKey(string ProviderId, string StableAccountIdentity, bool Attributed);
    private sealed record AccountAccumulatorCheckpoint(AccountGroupKey Key, AccountTokenAggregate Aggregate);
    private sealed record AttemptProjectionCheckpoint(
        IReadOnlyList<AccountAccumulatorCheckpoint> Accounts,
        RequestScopeUsageAggregate RequestScope,
        RequestScopeUsageAggregate UnverifiedScope,
        IReadOnlyList<AccountUsageAttemptFact> Recent,
        int StoredAttemptCount);
    private readonly record struct QuotaAccountGroupKey(
        string ProviderId, string ObservationScope, string StableAccountIdentity, bool Attributed);
    private sealed record QuotaAccountProjectionCheckpoint(
        QuotaAccountGroupKey Key,
        AccountQuotaSnapshotFact[] Latest,
        AccountQuotaSnapshotFact[] LatestNonFailed);
    private sealed record QuotaProjectionCheckpoint(
        AccountQuotaSnapshotFact[] OpenFacts,
        AccountQuotaBatchPrepare[] OpenPrepares,
        AccountQuotaBatchCommit[] OpenCommits,
        QuotaAccountProjectionCheckpoint[] Accounts,
        int CommittedFactCount,
        int InvalidQuotaValueCount,
        int StructuralIntegrityCount);
    private readonly record struct QuotaBatchKey(
        string ProviderId, string ObservationScope, string StableAccountIdentity, bool Attributed, string ObservationBatch);

    private sealed class AttemptProjectionCache
    {
        private const int RecentLimit = 80;
        private readonly MembershipIndex _accountRequests;
        private readonly MembershipIndex _scopeRequests;
        private readonly Dictionary<AccountGroupKey, AccountAccumulator> _accounts = new();
        private readonly ScopeAccumulator _requestScope;
        private readonly ScopeAccumulator _unverifiedScope;
        private readonly List<AccountUsageAttemptFact> _recent = new(RecentLimit);

        public AttemptProjectionCache(MembershipIndex accountRequests, MembershipIndex scopeRequests)
        {
            _accountRequests = accountRequests;
            _scopeRequests = scopeRequests;
            _requestScope = new ScopeAccumulator(countRequests: true, scopeRequests);
            _unverifiedScope = new ScopeAccumulator(countRequests: false, null);
        }

        public int StoredAttemptCount { get; private set; }
        public RequestScopeUsageAggregate RequestScope => _requestScope.Project();
        public RequestScopeUsageAggregate UnverifiedScope => _unverifiedScope.Project();
        public IReadOnlyList<AccountUsageAttemptFact> Recent => _recent
            .OrderByDescending(item => item.OccurredAt ?? item.RecordedAt)
            .ThenByDescending(item => item.RecordedAt).ToArray();

        public void Reset()
        {
            _accounts.Clear();
            _accountRequests.Clear();
            _scopeRequests.Clear();
            _requestScope.Reset();
            _unverifiedScope.Reset();
            _recent.Clear();
            StoredAttemptCount = 0;
        }

        public void Append(IEnumerable<AccountUsageAttemptFact> facts)
        {
            foreach (var fact in facts) AppendOne(fact);
            _accountRequests.FlushOverlay();
            _scopeRequests.FlushOverlay();
        }

        public void AppendOne(AccountUsageAttemptFact fact)
        {
            if (fact.RequestLevelUsage)
            {
                _requestScope.Add(fact);
                return;
            }
            StoredAttemptCount++;
            AddRecent(ProjectAttemptForDisplay(fact));
            if (!fact.IdentityVerified)
            {
                _unverifiedScope.Add(fact);
                return;
            }
            var key = new AccountGroupKey(fact.ProviderId, fact.StableAccountIdentity, fact.AccountAttributed);
            if (!_accounts.TryGetValue(key, out var account))
                _accounts[key] = account = new AccountAccumulator(key);
            var membershipKey = Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "account-request-membership:v1",
                key.ProviderId,
                key.StableAccountIdentity,
                key.Attributed,
                fact.RequestIdentity
            }));
            account.Add(fact, _accountRequests.Add(membershipKey, fact.IdempotencyKey));
        }

        public void FlushMembership()
        {
            _accountRequests.FlushOverlay();
            _scopeRequests.FlushOverlay();
        }

        public AttemptProjectionCheckpoint Capture() => new(
            _accounts.Select(item => new AccountAccumulatorCheckpoint(item.Key, item.Value.Project())).ToArray(),
            RequestScope, UnverifiedScope, Recent.ToArray(), StoredAttemptCount);

        public void Restore(AttemptProjectionCheckpoint checkpoint)
        {
            _accounts.Clear();
            foreach (var item in checkpoint.Accounts)
                _accounts[item.Key] = new AccountAccumulator(item.Key, item.Aggregate);
            _requestScope.Restore(checkpoint.RequestScope);
            _unverifiedScope.Restore(checkpoint.UnverifiedScope);
            _recent.Clear();
            _recent.AddRange(checkpoint.Recent.Take(RecentLimit));
            StoredAttemptCount = checkpoint.StoredAttemptCount;
        }

        public IReadOnlyList<AccountTokenAggregate> ProjectAccounts(out int overflowCount)
        {
            var rows = _accounts.Values.Select(item => item.Project()).ToArray();
            overflowCount = rows.Sum(row => row.OverflowMetricCount);
            return rows.OrderBy(item => item.AccountAttributed ? 0 : 1)
                .ThenBy(item => item.ProviderId, StringComparer.Ordinal)
                .ThenBy(item => item.AccountId, StringComparer.Ordinal).ToArray();
        }

        private void AddRecent(AccountUsageAttemptFact fact)
        {
            if (_recent.Count < RecentLimit)
            {
                _recent.Add(fact);
                return;
            }
            var oldestIndex = 0;
            for (var index = 1; index < _recent.Count; index++)
                if (CompareRecent(_recent[index], _recent[oldestIndex]) < 0) oldestIndex = index;
            if (CompareRecent(fact, _recent[oldestIndex]) > 0) _recent[oldestIndex] = fact;
        }

        private static int CompareRecent(AccountUsageAttemptFact left, AccountUsageAttemptFact right)
        {
            var occurred = (left.OccurredAt ?? left.RecordedAt).CompareTo(right.OccurredAt ?? right.RecordedAt);
            return occurred != 0 ? occurred : left.RecordedAt.CompareTo(right.RecordedAt);
        }
    }

    private sealed class AccountAccumulator
    {
        private readonly AccountGroupKey _key;
        private int _requests;
        private readonly MetricAccumulator[] _metrics = Enumerable.Range(0, 7).Select(_ => new MetricAccumulator()).ToArray();
        private int _attempts;
        private int _success;
        private int _failed;
        private int _cancelled;
        private int _usage;
        private int _invalid;
        private int _mismatch;
        private DateTimeOffset? _lastSeen;

        public AccountAccumulator(AccountGroupKey key) => _key = key;

        public AccountAccumulator(AccountGroupKey key, AccountTokenAggregate aggregate)
        {
            _key = key;
            _attempts = aggregate.AttemptCount;
            _requests = aggregate.RequestCount;
            _success = aggregate.SuccessCount;
            _failed = aggregate.FailedCount;
            _cancelled = aggregate.CancelledCount;
            _usage = aggregate.UsageAttemptCount;
            _invalid = aggregate.InvalidUsageCount;
            _mismatch = aggregate.MismatchUsageCount;
            _lastSeen = aggregate.LastSeen;
            var values = new[] { aggregate.Input, aggregate.CachedInput, aggregate.CacheReadInput,
                aggregate.CacheCreationInput, aggregate.Output, aggregate.Reasoning, aggregate.Total };
            for (var index = 0; index < _metrics.Length; index++) _metrics[index].Restore(values[index]);
        }

        public void Add(AccountUsageAttemptFact fact, bool newRequest)
        {
            _attempts++;
            if (newRequest) _requests++;
            if (fact.Result == RuntimeExecutionOutcome.Succeeded) _success++;
            else if (fact.Result == RuntimeExecutionOutcome.Failed) _failed++;
            else if (fact.Result == RuntimeExecutionOutcome.Cancelled) _cancelled++;
            var validUsage = fact.Usage is not null && fact.Usage.TotalValidation != TokenTotalValidationState.InvalidValue;
            if (validUsage) _usage++;
            if (fact.Usage?.TotalValidation == TokenTotalValidationState.InvalidValue) _invalid++;
            if (fact.Usage?.TotalValidation == TokenTotalValidationState.Mismatch) _mismatch++;
            AddMetrics(_metrics, fact.Usage, validUsage);
            if (fact.OccurredAt is not null && (_lastSeen is null || fact.OccurredAt > _lastSeen)) _lastSeen = fact.OccurredAt;
        }

        public AccountTokenAggregate Project()
        {
            var metrics = _metrics.Select(item => item.Project(_attempts)).ToArray();
            return new AccountTokenAggregate(_key.ProviderId,
                DeriveAccountDisplay(_key.StableAccountIdentity, _key.Attributed), _key.Attributed,
                _attempts, _requests, _success, _failed, _cancelled, _usage, _invalid, _mismatch,
                metrics.Count(item => item.IsOverflow), metrics[0], metrics[1], metrics[2], metrics[3],
                metrics[4], metrics[5], metrics[6], _lastSeen);
        }
    }

    private sealed class ScopeAccumulator
    {
        private readonly bool _countRequests;
        private readonly MembershipIndex? _requests;
        private int _requestCount;
        private readonly MetricAccumulator[] _metrics = Enumerable.Range(0, 7).Select(_ => new MetricAccumulator()).ToArray();
        private int _facts;
        private int _invalid;
        private int _mismatch;

        public ScopeAccumulator(bool countRequests, MembershipIndex? requests)
        {
            _countRequests = countRequests;
            _requests = requests;
        }

        public void Reset()
        {
            _requestCount = 0;
            foreach (var metric in _metrics) metric.Reset();
            _facts = _invalid = _mismatch = 0;
        }

        public void Add(AccountUsageAttemptFact fact)
        {
            _facts++;
            if (_countRequests && _requests!.Add(Hash(JsonSerializer.SerializeToUtf8Bytes(new
                { kind = "request-scope-membership:v1", fact.RequestIdentity })), fact.IdempotencyKey)) _requestCount++;
            var validUsage = fact.Usage is not null && fact.Usage.TotalValidation != TokenTotalValidationState.InvalidValue;
            if (fact.Usage?.TotalValidation == TokenTotalValidationState.InvalidValue) _invalid++;
            if (fact.Usage?.TotalValidation == TokenTotalValidationState.Mismatch) _mismatch++;
            AddMetrics(_metrics, fact.Usage, validUsage);
        }

        public void Restore(RequestScopeUsageAggregate aggregate)
        {
            _facts = aggregate.FactCount;
            _requestCount = aggregate.RequestCount;
            _invalid = aggregate.InvalidUsageCount;
            _mismatch = aggregate.MismatchUsageCount;
            var values = new[] { aggregate.Input, aggregate.CachedInput, aggregate.CacheReadInput,
                aggregate.CacheCreationInput, aggregate.Output, aggregate.Reasoning, aggregate.Total };
            for (var index = 0; index < _metrics.Length; index++) _metrics[index].Restore(values[index]);
        }

        public RequestScopeUsageAggregate Project()
        {
            var metrics = _metrics.Select(item => item.Project(_facts)).ToArray();
            return new RequestScopeUsageAggregate(_facts, _countRequests ? _requestCount : 0, _invalid, _mismatch,
                metrics[0], metrics[1], metrics[2], metrics[3], metrics[4], metrics[5], metrics[6]);
        }
    }

    private sealed class MetricAccumulator
    {
        private BigInteger _sum;
        private int _provided;

        public void Add(long? value, bool validUsage)
        {
            if (!validUsage || value is null || value < 0) return;
            _sum += value.Value;
            _provided++;
        }

        public TokenMetricAggregate Project(int attempts) => new(
            _sum > long.MaxValue ? 0 : (long)_sum, _provided, attempts, _sum > long.MaxValue);

        public void Reset() { _sum = BigInteger.Zero; _provided = 0; }
        public void Restore(TokenMetricAggregate value)
        {
            _sum = value.IsOverflow ? new BigInteger(long.MaxValue) + 1 : new BigInteger(value.Sum);
            _provided = value.ProvidedAttemptCount;
        }
    }

    private static void AddMetrics(MetricAccumulator[] metrics, AttemptTokenUsageFact? usage, bool validUsage)
    {
        metrics[0].Add(usage?.InputTokens, validUsage);
        metrics[1].Add(usage?.CachedInputTokens, validUsage);
        metrics[2].Add(usage?.CacheReadInputTokens, validUsage);
        metrics[3].Add(usage?.CacheCreationInputTokens, validUsage);
        metrics[4].Add(usage?.OutputTokens, validUsage);
        metrics[5].Add(usage?.ReasoningTokens, validUsage);
        metrics[6].Add(usage?.TotalTokens, validUsage);
    }

    private sealed class QuotaProjectionCache
    {
        private readonly Dictionary<QuotaBatchKey, List<AccountQuotaSnapshotFact>> _facts = new();
        private readonly Dictionary<QuotaBatchKey, AccountQuotaBatchPrepare> _prepares = new();
        private readonly Dictionary<QuotaBatchKey, AccountQuotaBatchCommit> _commits = new();
        private readonly Dictionary<QuotaAccountGroupKey, QuotaAccountProjection> _accounts = new();
        private readonly Dictionary<QuotaBatchKey, int> _incomplete = new();
        private readonly HashSet<QuotaBatchKey> _orphanCommits = new();
        private readonly HashSet<QuotaBatchKey> _orphanPrepares = new();

        public int CommittedFactCount { get; private set; }
        public int IncompleteFactCount { get; private set; }
        public int OrphanCommitCount => _orphanCommits.Count;
        public int OrphanPrepareCount => _orphanPrepares.Count;
        public int InvalidQuotaValueCount { get; private set; }
        public int StructuralIntegrityCount { get; private set; }
        public long FallbackSelectionCount { get; private set; }
        public int RetainedFactCount => _facts.Values.Sum(items => items.Count)
                                        + _accounts.Values.Sum(item => item.RetainedFactCount);

        public QuotaProjectionCheckpoint Capture() => new(
            _facts.SelectMany(item => item.Value).ToArray(),
            _prepares.Values.ToArray(),
            _commits.Values.ToArray(),
            _accounts.Select(item => item.Value.Capture(item.Key)).ToArray(),
            CommittedFactCount, InvalidQuotaValueCount, StructuralIntegrityCount);

        public void Restore(QuotaProjectionCheckpoint checkpoint)
        {
            Reset();
            CommittedFactCount = checkpoint.CommittedFactCount;
            InvalidQuotaValueCount = checkpoint.InvalidQuotaValueCount;
            StructuralIntegrityCount = checkpoint.StructuralIntegrityCount;
            foreach (var account in checkpoint.Accounts)
            {
                var projection = new QuotaAccountProjection();
                projection.Attach(this);
                projection.Restore(account);
                _accounts[account.Key] = projection;
            }
            Append(checkpoint.OpenFacts, checkpoint.OpenPrepares, checkpoint.OpenCommits);
        }

        public IReadOnlyList<AccountQuotaSnapshotFact> GetOpenFacts(QuotaBatchKey key) =>
            _facts.TryGetValue(key, out var facts) ? facts : Array.Empty<AccountQuotaSnapshotFact>();

        public IReadOnlyList<IReadOnlyList<AccountQuotaSnapshotFact>> GetRecoverablePreparedBatches()
        {
            var result = new List<IReadOnlyList<AccountQuotaSnapshotFact>>();
            foreach (var (key, prepare) in _prepares)
            {
                if (_commits.ContainsKey(key) || !_facts.TryGetValue(key, out var sourceFacts)) continue;
                var facts = sourceFacts.OrderBy(item => item.PeriodKey, StringComparer.Ordinal).ToArray();
                if (facts.Length != prepare.ExpectedFactCount) continue;
                var digest = Hash(JsonSerializer.Serialize(
                    facts.Select(item => item.PayloadHash)
                        .OrderBy(value => value, StringComparer.Ordinal).ToArray()));
                if (!FixedHexEquals(prepare.FactsDigest, digest)) continue;
                result.Add(facts);
            }
            return result;
        }

        public void Reset()
        {
            _facts.Clear(); _prepares.Clear(); _commits.Clear(); _accounts.Clear();
            _incomplete.Clear(); _orphanCommits.Clear(); _orphanPrepares.Clear();
            CommittedFactCount = IncompleteFactCount = InvalidQuotaValueCount = StructuralIntegrityCount = 0;
            FallbackSelectionCount = 0;
        }

        public void Append(IEnumerable<AccountQuotaSnapshotFact> facts,
            IEnumerable<AccountQuotaBatchPrepare> prepares, IEnumerable<AccountQuotaBatchCommit> commits)
        {
            var affected = new HashSet<QuotaBatchKey>();
            foreach (var fact in facts)
            {
                var key = Key(fact);
                if (!_facts.TryGetValue(key, out var rows)) _facts[key] = rows = new List<AccountQuotaSnapshotFact>();
                rows.Add(fact);
                affected.Add(key);
            }
            foreach (var prepare in prepares)
            {
                var key = Key(prepare);
                _prepares[key] = prepare;
                affected.Add(key);
            }
            foreach (var commit in commits)
            {
                var key = Key(commit);
                _commits[key] = commit;
                affected.Add(key);
            }
            foreach (var key in affected) Reevaluate(key);
        }

        public void AppendFact(AccountQuotaSnapshotFact fact)
        {
            var key = Key(fact);
            if (!_facts.TryGetValue(key, out var rows)) _facts[key] = rows = new List<AccountQuotaSnapshotFact>();
            rows.Add(fact);
            Reevaluate(key);
        }

        public void AppendPrepare(AccountQuotaBatchPrepare prepare)
        {
            var key = Key(prepare);
            _prepares[key] = prepare;
            Reevaluate(key);
        }

        public void AppendCommit(AccountQuotaBatchCommit commit)
        {
            var key = Key(commit);
            _commits[key] = commit;
            Reevaluate(key);
        }

        public IReadOnlyList<AccountQuotaSnapshotView> ProjectViews(bool globalReadFailed)
        {
            var views = _accounts.Values.SelectMany(account => account.Project(globalReadFailed))
                .OrderBy(item => item.Fact.ProviderId, StringComparer.Ordinal)
                .ThenBy(item => item.Fact.ObservationScope, StringComparer.Ordinal)
                .ThenBy(item => item.Fact.AccountId, StringComparer.Ordinal)
                .ThenBy(item => item.Fact.PeriodKey, StringComparer.Ordinal).ToArray();
            return views;
        }

        public void RecordFallback() => FallbackSelectionCount++;

        private void Reevaluate(QuotaBatchKey key)
        {
            if (_incomplete.Remove(key, out var oldIncomplete)) IncompleteFactCount -= oldIncomplete;

            var facts = _facts.TryGetValue(key, out var sourceFacts)
                ? sourceFacts.OrderBy(item => item.PeriodKey, StringComparer.Ordinal).ToArray()
                : Array.Empty<AccountQuotaSnapshotFact>();
            var digest = facts.Length == 0 ? string.Empty : Hash(JsonSerializer.Serialize(
                facts.Select(item => item.PayloadHash).OrderBy(value => value, StringComparer.Ordinal).ToArray()));
            var valid = facts.Length > 0
                        && _prepares.TryGetValue(key, out var prepare)
                        && _commits.TryGetValue(key, out var commit)
                        && prepare.ExpectedFactCount == facts.Length
                        && commit.ExpectedFactCount == facts.Length
                        && prepare.ExpectedFactCount == commit.ExpectedFactCount
                        && FixedHexEquals(prepare.FactsDigest, digest)
                        && FixedHexEquals(commit.FactsDigest, digest)
                        && FixedHexEquals(prepare.FactsDigest, commit.FactsDigest);
            if (valid)
            {
                CommittedFactCount += facts.Length;
                InvalidQuotaValueCount += facts.Count(item => item.ValueValidation == QuotaValueValidationState.InvalidRange);
                StructuralIntegrityCount += facts.Count(IsStructuralFailure);
                Account(key).Observe(key, facts);
                _facts.Remove(key);
                _prepares.Remove(key);
                _commits.Remove(key);
                _orphanCommits.Remove(key);
                _orphanPrepares.Remove(key);
                return;
            }
            else if (facts.Length > 0)
            {
                _incomplete[key] = facts.Length;
                IncompleteFactCount += facts.Length;
            }
            if (_commits.ContainsKey(key) && !valid) _orphanCommits.Add(key); else _orphanCommits.Remove(key);
            if (_prepares.ContainsKey(key) && !_commits.ContainsKey(key)) _orphanPrepares.Add(key); else _orphanPrepares.Remove(key);
        }

        private QuotaAccountProjection Account(QuotaBatchKey key)
        {
            var accountKey = new QuotaAccountGroupKey(key.ProviderId, key.ObservationScope,
                key.StableAccountIdentity, key.Attributed);
            if (!_accounts.TryGetValue(accountKey, out var account))
            {
                account = new QuotaAccountProjection();
                account.Attach(this);
                _accounts[accountKey] = account;
            }
            return account;
        }

        private static bool IsStructuralFailure(AccountQuotaSnapshotFact fact) =>
            fact.Availability == AccountQuotaAvailability.ReadFailed
            && string.Equals(fact.ErrorClass, "MissingPeriodKey", StringComparison.Ordinal);

        private static QuotaBatchKey Key(AccountQuotaSnapshotFact item) => new(
            item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch);
        private static QuotaBatchKey Key(AccountQuotaBatchPrepare item) => new(
            item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch);
        private static QuotaBatchKey Key(AccountQuotaBatchCommit item) => new(
            item.ProviderId, item.ObservationScope, item.StableAccountIdentity, item.AccountAttributed, item.ObservationBatch);
    }

    private sealed class QuotaAccountProjection
    {
        private QuotaProjectedBatch? _latest;
        private QuotaProjectedBatch? _latestNonFailed;
        private QuotaProjectionCache? _owner;

        public int RetainedFactCount => (_latest?.Facts.Length ?? 0) + (_latestNonFailed?.Facts.Length ?? 0);
        public void Attach(QuotaProjectionCache owner) => _owner = owner;

        public QuotaAccountProjectionCheckpoint Capture(QuotaAccountGroupKey key) => new(
            key, _latest?.Facts ?? Array.Empty<AccountQuotaSnapshotFact>(),
            _latestNonFailed?.Facts ?? Array.Empty<AccountQuotaSnapshotFact>());

        public void Restore(QuotaAccountProjectionCheckpoint checkpoint)
        {
            _latest = checkpoint.Latest.Length == 0 ? null : Project(checkpoint.Latest);
            _latestNonFailed = checkpoint.LatestNonFailed.Length == 0 ? null : Project(checkpoint.LatestNonFailed);
        }

        public void Observe(QuotaBatchKey key, AccountQuotaSnapshotFact[] facts)
        {
            var rank = new QuotaBatchRank(key,
                facts.Max(item => item.LocalObservedAt), facts.Max(item => item.RecordedAt));
            var projected = new QuotaProjectedBatch(rank, facts);
            if (_latest is null || QuotaBatchRankComparer.Instance.Compare(rank, _latest.Rank) < 0)
                _latest = projected;
            if (facts.All(item => item.Availability != AccountQuotaAvailability.ReadFailed)
                && (_latestNonFailed is null
                    || QuotaBatchRankComparer.Instance.Compare(rank, _latestNonFailed.Rank) < 0))
                _latestNonFailed = projected;
        }

        private static QuotaProjectedBatch Project(AccountQuotaSnapshotFact[] facts)
        {
            var first = facts[0];
            var key = new QuotaBatchKey(first.ProviderId, first.ObservationScope,
                first.StableAccountIdentity, first.AccountAttributed, first.ObservationBatch);
            return new QuotaProjectedBatch(new QuotaBatchRank(key,
                facts.Max(item => item.LocalObservedAt), facts.Max(item => item.RecordedAt)), facts);
        }

        public IEnumerable<AccountQuotaSnapshotView> Project(bool globalReadFailed)
        {
            if (_latest is null) yield break;
            var latest = _latest.Facts;
            var selected = latest;
            var latestFailure = latest.FirstOrDefault(item => item.Availability == AccountQuotaAvailability.ReadFailed);
            if (latestFailure is not null && _latestNonFailed is not null)
            {
                selected = _latestNonFailed.Facts;
                _owner?.RecordFallback();
            }
            foreach (var fact in selected)
            {
                var stale = globalReadFailed || fact.SourceStale || latestFailure is not null;
                var state = fact.ValueValidation == QuotaValueValidationState.InvalidRange
                    ? "invalid upstream quota value"
                    : globalReadFailed
                        ? "stale · latest quota read failed; retained prior value"
                        : latestFailure is not null
                            ? $"stale · source read failed; retained prior value · {latestFailure.ErrorClass ?? "Unknown"}"
                            : fact.SourceStale ? "stale · upstream marked stale"
                            : fact.Availability == AccountQuotaAvailability.Provided ? "provided" : "source explicitly did not provide quota";
                yield return new AccountQuotaSnapshotView(ProjectQuotaForDisplay(fact), stale, state);
            }
        }
    }

    private sealed record QuotaProjectedBatch(QuotaBatchRank Rank, AccountQuotaSnapshotFact[] Facts);

    private readonly record struct QuotaBatchRank(QuotaBatchKey Key, DateTimeOffset LocalObservedAt, DateTimeOffset RecordedAt);
    private sealed class QuotaBatchRankComparer : IComparer<QuotaBatchRank>
    {
        public static QuotaBatchRankComparer Instance { get; } = new();
        public int Compare(QuotaBatchRank left, QuotaBatchRank right)
        {
            var result = right.LocalObservedAt.CompareTo(left.LocalObservedAt);
            if (result != 0) return result;
            result = right.RecordedAt.CompareTo(left.RecordedAt);
            if (result != 0) return result;
            result = string.Compare(left.Key.ObservationBatch, right.Key.ObservationBatch, StringComparison.Ordinal);
            if (result != 0) return result;
            result = string.Compare(left.Key.ProviderId, right.Key.ProviderId, StringComparison.Ordinal);
            if (result != 0) return result;
            result = string.Compare(left.Key.ObservationScope, right.Key.ObservationScope, StringComparison.Ordinal);
            if (result != 0) return result;
            result = string.Compare(left.Key.StableAccountIdentity, right.Key.StableAccountIdentity, StringComparison.Ordinal);
            return result != 0 ? result : left.Key.Attributed.CompareTo(right.Key.Attributed);
        }
    }

    private sealed class LegacyMembershipIndex
    {
        private const int RecordBytes = 32;
        private readonly HashSet<Hash256Value> _overlay = new();
        private string? _diskPath;
        private long _diskCount;

        public long Count => _diskCount + _overlay.Count;
        public string DiskPath => _diskPath ?? throw new InvalidOperationException("Membership index is not configured.");
        public void Configure(string diskPath) => _diskPath = diskPath;
        public MembershipCheckpoint Capture()
        {
            FlushOverlay();
            var length = File.Exists(DiskPath) ? new FileInfo(DiskPath).Length : 0;
            return new MembershipCheckpoint(_diskCount, length,
                length == 0 ? Hash(Array.Empty<byte>()) : FileHash(DiskPath));
        }
        public bool Add(string key)
        {
            var value = Hash256Value.Parse(key);
            if (_overlay.Contains(value) || ContainsDisk(value)) return false;
            _overlay.Add(value);
            if (_overlay.Count >= 8192) FlushOverlay();
            return true;
        }

        public void FlushOverlay()
        {
            if (_overlay.Count == 0 || string.IsNullOrWhiteSpace(_diskPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_diskPath)!);
            var ordered = _overlay.Order().ToArray();
            var temp = _diskPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough);
                using var input = File.Exists(_diskPath)
                    ? new FileStream(_diskPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.SequentialScan)
                    : null;
                var overlayIndex = 0;
                var recordBytes = new byte[RecordBytes];
                var hasDisk = TryRead(input, out var diskValue);
                long written = 0;
                while (hasDisk || overlayIndex < ordered.Length)
                {
                    Hash256Value selected;
                    if (!hasDisk || (overlayIndex < ordered.Length && ordered[overlayIndex].CompareTo(diskValue) < 0))
                        selected = ordered[overlayIndex++];
                    else if (overlayIndex >= ordered.Length || diskValue.CompareTo(ordered[overlayIndex]) < 0)
                    {
                        selected = diskValue;
                        hasDisk = TryRead(input, out diskValue);
                    }
                    else
                    {
                        selected = ordered[overlayIndex++];
                        hasDisk = TryRead(input, out diskValue);
                    }
                    selected.WriteBytes(recordBytes);
                    output.Write(recordBytes);
                    written++;
                }
                output.Flush(true);
                input?.Dispose();
                output.Dispose();
                File.Move(temp, _diskPath, true);
                _diskCount = written;
                _overlay.Clear();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public void Clear()
        {
            _overlay.Clear();
            _diskCount = 0;
            if (!string.IsNullOrWhiteSpace(_diskPath) && File.Exists(_diskPath)) File.Delete(_diskPath);
        }

        public void Restore(MembershipCheckpoint checkpoint)
        {
            _overlay.Clear();
            if (checkpoint.Count < 0 || checkpoint.Length != checked(checkpoint.Count * RecordBytes)
                || string.IsNullOrWhiteSpace(_diskPath)
                || (checkpoint.Length > 0 && (!File.Exists(_diskPath)
                    || new FileInfo(_diskPath).Length != checkpoint.Length
                    || !FixedHexEquals(FileHash(_diskPath), checkpoint.Sha256)))
                || (checkpoint.Length == 0 && !FixedHexEquals(checkpoint.Sha256, Hash(Array.Empty<byte>()))))
                throw new InvalidDataException("Membership index length does not match its checkpoint.");
            _diskCount = checkpoint.Count;
        }

        private bool ContainsDisk(Hash256Value value)
        {
            if (_diskCount == 0 || string.IsNullOrWhiteSpace(_diskPath) || !File.Exists(_diskPath)) return false;
            using var stream = new FileStream(_diskPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            long low = 0, high = _diskCount - 1;
            Span<byte> bytes = stackalloc byte[RecordBytes];
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                stream.Position = middle * RecordBytes;
                stream.ReadExactly(bytes);
                var candidate = Hash256Value.FromBytes(bytes);
                var compare = candidate.CompareTo(value);
                if (compare == 0) return true;
                if (compare < 0) low = middle + 1; else high = middle - 1;
            }
            return false;
        }

        private static bool TryRead(Stream? stream, out Hash256Value value)
        {
            value = default;
            if (stream is null || stream.Position >= stream.Length) return false;
            Span<byte> bytes = stackalloc byte[RecordBytes];
            stream.ReadExactly(bytes);
            value = Hash256Value.FromBytes(bytes);
            return true;
        }

        private static string FileHash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }

    private sealed class LegacyJsonIndex<T> where T : class
    {
        private const int RecordBytes = 64;
        public bool Initialized { get; set; }
        public long Version { get; set; }
        public long RebuildGeneration { get; private set; }
        public int BadLineCount { get; set; }
        public int IntegrityFailureCount { get; set; }
        public int CollisionCount { get; set; }
        public int DuplicateCount { get; set; }
        public List<T> Items { get; } = new();
        private Dictionary<Hash256Value, Hash256Value> Known { get; } = new();
        private string? _diskPath;
        private long _diskCount;
        public int KnownCount => checked((int)Math.Min(int.MaxValue, _diskCount + Known.Count));
        public Dictionary<string, SegmentState> Segments { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Configure(string diskPath) => _diskPath = diskPath;
        public IndexCheckpoint Capture()
        {
            FlushOverlay();
            var length = !string.IsNullOrWhiteSpace(_diskPath) && File.Exists(_diskPath)
                ? new FileInfo(_diskPath).Length : 0;
            return new IndexCheckpoint(Initialized, Version, RebuildGeneration, BadLineCount,
                IntegrityFailureCount, CollisionCount, DuplicateCount, _diskCount, length,
                length == 0 ? Hash(Array.Empty<byte>()) : FileHash(_diskPath!),
                new Dictionary<string, SegmentState>(Segments, StringComparer.OrdinalIgnoreCase));
        }

        public void Restore(IndexCheckpoint checkpoint)
        {
            if (checkpoint.DiskCount < 0 || checkpoint.DiskLength != checked(checkpoint.DiskCount * RecordBytes)
                || string.IsNullOrWhiteSpace(_diskPath)
                || (checkpoint.DiskLength > 0 && (!File.Exists(_diskPath)
                    || new FileInfo(_diskPath).Length != checkpoint.DiskLength
                    || !FixedHexEquals(FileHash(_diskPath), checkpoint.DiskSha256))))
                throw new InvalidDataException("Derived idempotency index failed checkpoint validation.");
            if (checkpoint.DiskLength == 0 && !FixedHexEquals(checkpoint.DiskSha256, Hash(Array.Empty<byte>())))
                throw new InvalidDataException("Empty idempotency index hash is invalid.");
            Known.Clear();
            Items.Clear();
            Segments.Clear();
            foreach (var item in checkpoint.Segments) Segments[item.Key] = item.Value;
            Initialized = checkpoint.Initialized;
            Version = checkpoint.Version;
            RebuildGeneration = checkpoint.RebuildGeneration;
            BadLineCount = checkpoint.BadLineCount;
            IntegrityFailureCount = checkpoint.IntegrityFailureCount;
            CollisionCount = checkpoint.CollisionCount;
            DuplicateCount = checkpoint.DuplicateCount;
            _diskCount = checkpoint.DiskCount;
        }
        public bool TryGetPayload(string key, out string payloadHash)
        {
            var parsed = Hash256Value.Parse(key);
            if (Known.TryGetValue(parsed, out var value) || TryReadDisk(parsed, out value))
            {
                payloadHash = value.ToHex();
                return true;
            }
            payloadHash = string.Empty;
            return false;
        }
        public void Remember(string key, string payloadHash)
        {
            Known[Hash256Value.Parse(key)] = Hash256Value.Parse(payloadHash);
            if (Known.Count >= 8192) FlushOverlay();
        }
        public void FlushOverlay()
        {
            if (Known.Count == 0 || string.IsNullOrWhiteSpace(_diskPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_diskPath)!);
            var ordered = Known.OrderBy(item => item.Key).ToArray();
            var temp = _diskPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.WriteThrough);
                using var input = File.Exists(_diskPath)
                    ? new FileStream(_diskPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.SequentialScan)
                    : null;
                var overlayIndex = 0;
                var hasDisk = TryReadRecord(input, out var diskKey, out var diskPayload);
                long written = 0;
                while (hasDisk || overlayIndex < ordered.Length)
                {
                    if (!hasDisk || (overlayIndex < ordered.Length && ordered[overlayIndex].Key.CompareTo(diskKey) < 0))
                    {
                        WriteRecord(output, ordered[overlayIndex].Key, ordered[overlayIndex].Value);
                        overlayIndex++;
                    }
                    else if (overlayIndex >= ordered.Length || diskKey.CompareTo(ordered[overlayIndex].Key) < 0)
                    {
                        WriteRecord(output, diskKey, diskPayload);
                        hasDisk = TryReadRecord(input, out diskKey, out diskPayload);
                    }
                    else
                    {
                        WriteRecord(output, ordered[overlayIndex].Key, ordered[overlayIndex].Value);
                        overlayIndex++;
                        hasDisk = TryReadRecord(input, out diskKey, out diskPayload);
                    }
                    written++;
                }
                output.Flush(true);
                input?.Dispose();
                output.Dispose();
                File.Move(temp, _diskPath, true);
                _diskCount = written;
                Known.Clear();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void Clear()
        {
            Initialized = false; BadLineCount = 0; IntegrityFailureCount = 0; CollisionCount = 0; DuplicateCount = 0;
            Items.Clear(); Known.Clear(); Segments.Clear();
            _diskCount = 0;
            if (!string.IsNullOrWhiteSpace(_diskPath) && File.Exists(_diskPath)) File.Delete(_diskPath);
            RebuildGeneration++;
        }
        public JsonLineRead<T> Read() => new(Items, BadLineCount, IntegrityFailureCount);

        private bool TryReadDisk(Hash256Value key, out Hash256Value payload)
        {
            payload = default;
            if (_diskCount <= 0 || string.IsNullOrWhiteSpace(_diskPath) || !File.Exists(_diskPath)) return false;
            using var stream = new FileStream(_diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.RandomAccess);
            long low = 0, high = _diskCount - 1;
            var buffer = new byte[RecordBytes];
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                stream.Position = middle * RecordBytes;
                stream.ReadExactly(buffer);
                var candidate = Hash256Value.FromBytes(buffer.AsSpan(0, 32));
                var compare = candidate.CompareTo(key);
                if (compare == 0)
                {
                    payload = Hash256Value.FromBytes(buffer.AsSpan(32, 32));
                    return true;
                }
                if (compare < 0) low = middle + 1; else high = middle - 1;
            }
            return false;
        }

        private static bool TryReadRecord(Stream? stream, out Hash256Value key, out Hash256Value payload)
        {
            key = payload = default;
            if (stream is null || stream.Position >= stream.Length) return false;
            Span<byte> buffer = stackalloc byte[RecordBytes];
            stream.ReadExactly(buffer);
            key = Hash256Value.FromBytes(buffer[..32]);
            payload = Hash256Value.FromBytes(buffer[32..]);
            return true;
        }

        private static void WriteRecord(Stream stream, Hash256Value key, Hash256Value payload)
        {
            Span<byte> buffer = stackalloc byte[RecordBytes];
            key.WriteBytes(buffer[..32]);
            payload.WriteBytes(buffer[32..]);
            stream.Write(buffer);
        }

        private static string FileHash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
    private sealed class MembershipIndex
    {
        private readonly Dictionary<Hash256Value, Hash256Value> _pending = new();
        private readonly DiskHashTable _table = new(valueBytes: 32);

        public long Count => _table.Count + _pending.Count;
        public long BytesWritten => _table.BytesWritten;
        public long ReplacementCount => _table.ReplacementCount;
        public string DiskPath => _table.Path;
        public void Configure(string diskPath) => _table.Configure(diskPath);
        public IDisposable OpenSession() => _table.OpenSession();

        public MembershipCheckpoint Capture()
        {
            FlushOverlay();
            var length = File.Exists(DiskPath) ? new FileInfo(DiskPath).Length : 0;
            return new MembershipCheckpoint(_table.Count, length,
                length == 0 ? Hash(Array.Empty<byte>()) : FileHash(DiskPath));
        }

        public bool Add(string key, string witness)
        {
            var parsed = Hash256Value.Parse(key);
            var parsedWitness = Hash256Value.Parse(witness);
            if (_pending.TryGetValue(parsed, out var pendingWitness)) return pendingWitness.Equals(parsedWitness);
            if (_table.TryGet(parsed, out var existing))
            {
                if (existing.Length != 32) throw new InvalidDataException("Membership witness length is invalid.");
                return Hash256Value.FromBytes(existing).Equals(parsedWitness);
            }
            _pending.Add(parsed, parsedWitness);
            if (_pending.Count >= 8192) FlushOverlay();
            return true;
        }

        public void FlushOverlay()
        {
            if (_pending.Count == 0) return;
            _table.WriteBatch(_pending.Select(item =>
            {
                var value = new byte[32];
                item.Value.WriteBytes(value);
                return new DiskHashRecord(item.Key, value);
            }));
            _pending.Clear();
        }

        public void Clear()
        {
            _pending.Clear();
            _table.Clear();
        }

        public void Restore(MembershipCheckpoint checkpoint)
        {
            _pending.Clear();
            if (checkpoint.Count < 0 || checkpoint.Length < 0
                || (checkpoint.Length > 0 && (!File.Exists(DiskPath)
                    || new FileInfo(DiskPath).Length != checkpoint.Length
                    || !FixedHexEquals(FileHash(DiskPath), checkpoint.Sha256)))
                || (checkpoint.Length == 0 && !FixedHexEquals(checkpoint.Sha256, Hash(Array.Empty<byte>()))))
                throw new InvalidDataException("Membership index failed checkpoint validation.");
            _table.Restore(checkpoint.Count);
        }
    }

    private sealed class JsonIndex<T> where T : class
    {
        public bool Initialized { get; set; }
        public long Version { get; set; }
        public long RebuildGeneration { get; private set; }
        public int BadLineCount { get; set; }
        public int IntegrityFailureCount { get; set; }
        public int CollisionCount { get; set; }
        public int DuplicateCount { get; set; }
        public List<T> Items { get; } = new();
        private readonly Dictionary<Hash256Value, IndexValue> _pending = new();
        private readonly DiskHashTable _table = new(valueBytes: 64);
        public int KnownCount => checked((int)Math.Min(int.MaxValue, _table.Count + _pending.Count));
        public long BytesWritten => _table.BytesWritten;
        public long ReplacementCount => _table.ReplacementCount;
        public Dictionary<string, SegmentState> Segments { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Configure(string diskPath) => _table.Configure(diskPath);
        public IDisposable OpenSession() => _table.OpenSession();

        public IndexCheckpoint Capture()
        {
            FlushOverlay();
            var length = File.Exists(_table.Path) ? new FileInfo(_table.Path).Length : 0;
            return new IndexCheckpoint(Initialized, Version, RebuildGeneration, BadLineCount,
                IntegrityFailureCount, CollisionCount, DuplicateCount, _table.Count, length,
                length == 0 ? Hash(Array.Empty<byte>()) : FileHash(_table.Path),
                new Dictionary<string, SegmentState>(Segments, StringComparer.OrdinalIgnoreCase));
        }

        public void Restore(IndexCheckpoint checkpoint)
        {
            if (checkpoint.DiskCount < 0 || checkpoint.DiskLength < 0
                || (checkpoint.DiskLength > 0 && (!File.Exists(_table.Path)
                    || new FileInfo(_table.Path).Length != checkpoint.DiskLength
                    || !FixedHexEquals(FileHash(_table.Path), checkpoint.DiskSha256)))
                || (checkpoint.DiskLength == 0 && !FixedHexEquals(checkpoint.DiskSha256, Hash(Array.Empty<byte>()))))
                throw new InvalidDataException("Derived idempotency index failed checkpoint validation.");
            _pending.Clear();
            Items.Clear();
            Segments.Clear();
            var ledgerDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetDirectoryName(Path.GetFullPath(_table.Path)) ?? string.Empty);
            foreach (var item in checkpoint.Segments)
            {
                var segmentPath = Path.GetFullPath(item.Key);
                if (!string.Equals(Path.TrimEndingDirectorySeparator(
                            Path.GetDirectoryName(segmentPath) ?? string.Empty), ledgerDirectory,
                        StringComparison.OrdinalIgnoreCase)
                    || !Segments.TryAdd(segmentPath, item.Value))
                    throw new InvalidDataException("Derived index checkpoint segment path is outside or ambiguous for the current ledger directory.");
            }
            Initialized = checkpoint.Initialized;
            Version = checkpoint.Version;
            RebuildGeneration = checkpoint.RebuildGeneration;
            BadLineCount = checkpoint.BadLineCount;
            IntegrityFailureCount = checkpoint.IntegrityFailureCount;
            CollisionCount = checkpoint.CollisionCount;
            DuplicateCount = checkpoint.DuplicateCount;
            _table.Restore(checkpoint.DiskCount);
        }

        public bool TryGetPayload(string key, out string payloadHash) =>
            TryGetPayload(key, out payloadHash, out _);

        public bool TryGetPayload(string key, out string payloadHash, out string occurrence)
        {
            var parsed = Hash256Value.Parse(key);
            if (_pending.TryGetValue(parsed, out var pending))
            {
                payloadHash = pending.Payload.ToHex();
                occurrence = pending.Occurrence.ToHex();
                return true;
            }
            if (_table.TryGet(parsed, out var bytes))
            {
                if (bytes.Length != 64) throw new InvalidDataException("Idempotency index value length is invalid.");
                payloadHash = Hash256Value.FromBytes(bytes.AsSpan(0, 32)).ToHex();
                occurrence = Hash256Value.FromBytes(bytes.AsSpan(32, 32)).ToHex();
                return true;
            }
            payloadHash = occurrence = string.Empty;
            return false;
        }

        public void Remember(string key, string payloadHash, string occurrence)
        {
            _pending[Hash256Value.Parse(key)] = new IndexValue(
                Hash256Value.Parse(payloadHash), Hash256Value.Parse(occurrence));
            if (_pending.Count >= 8192) FlushOverlay();
        }

        public void FlushOverlay()
        {
            if (_pending.Count == 0) return;
            _table.WriteBatch(_pending.Select(item =>
            {
                var value = new byte[64];
                item.Value.Payload.WriteBytes(value.AsSpan(0, 32));
                item.Value.Occurrence.WriteBytes(value.AsSpan(32, 32));
                return new DiskHashRecord(item.Key, value);
            }));
            _pending.Clear();
        }

        public void Clear()
        {
            Initialized = false; BadLineCount = 0; IntegrityFailureCount = 0; CollisionCount = 0; DuplicateCount = 0;
            Items.Clear(); _pending.Clear(); Segments.Clear();
            _table.Clear();
            RebuildGeneration++;
        }

        public JsonLineRead<T> Read() => new(Items, BadLineCount, IntegrityFailureCount);
        private sealed record IndexValue(Hash256Value Payload, Hash256Value Occurrence);
    }

    private sealed record DiskHashRecord(Hash256Value Key, byte[] Value);

    private sealed class DiskHashTable
    {
        private const ulong Magic = 0x434D4D4944583201UL;
        private const int Schema = 2;
        private const int HeaderBytes = 64;
        private const long InitialCapacity = 16_384;
        private readonly int _valueBytes;
        private FileStream? _session;
        private int _sessionDepth;
        private long _capacity;
        private long _count;
        private long _generation;
        private string? _path;

        public DiskHashTable(int valueBytes) => _valueBytes = valueBytes;
        public string Path => _path ?? throw new InvalidOperationException("Disk hash table is not configured.");
        public long Count => _count;
        public long BytesWritten { get; private set; }
        public long ReplacementCount { get; private set; }
        private int RecordBytes => 40 + _valueBytes;

        public void Configure(string path) => _path = path;

        public IDisposable OpenSession()
        {
            if (_sessionDepth++ == 0 && File.Exists(Path))
            {
                _session = OpenValidated(Path);
                ReadHeader(_session);
            }
            return new SessionScope(this);
        }

        public bool TryGet(Hash256Value key, out byte[] value)
        {
            var owns = _sessionDepth == 0;
            using var scope = owns ? OpenSession() : null;
            value = Array.Empty<byte>();
            if (_session is null) return false;
            var found = FindSlot(_session, key, out _, out var existing);
            if (!found) return false;
            value = existing;
            return true;
        }

        public void WriteBatch(IEnumerable<DiskHashRecord> records)
        {
            var batch = records.ToArray();
            if (batch.Length == 0) return;
            var owns = _sessionDepth == 0;
            using var scope = owns ? OpenSession() : null;
            EnsureCreated();
            EnsureCapacity(batch.Length);
            foreach (var record in batch)
            {
                if (record.Value.Length != _valueBytes)
                    throw new InvalidDataException("Disk hash table value length is invalid.");
                if (FindSlot(_session!, record.Key, out var slot, out var existing))
                {
                    if (!CryptographicOperations.FixedTimeEquals(existing, record.Value))
                        throw new InvalidDataException("Derived index contains a conflicting value for the same key.");
                    continue;
                }
                WriteSlot(_session!, slot, record);
                _count++;
                BytesWritten += RecordBytes;
            }
            WriteHeader(_session!);
            BytesWritten += HeaderBytes;
            _session!.Flush(true);
        }

        public void Restore(long expectedCount)
        {
            using var scope = OpenSession();
            if (expectedCount == 0 && _session is null) { _count = 0; return; }
            if (_session is null || _count != expectedCount)
                throw new InvalidDataException("Derived index count does not match its authenticated checkpoint.");
        }

        public void Clear()
        {
            CloseSession(force: true);
            _capacity = _count = _generation = 0;
            if (_path is not null && File.Exists(_path)) File.Delete(_path);
        }

        private void EnsureCreated()
        {
            if (_session is not null) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                           64 * 1024, FileOptions.WriteThrough | FileOptions.RandomAccess))
                {
                    _capacity = InitialCapacity;
                    _count = 0;
                    _generation = 1;
                    stream.SetLength(checked(HeaderBytes + _capacity * RecordBytes));
                    WriteHeader(stream);
                    stream.Flush(true);
                }
                File.Move(temp, Path, false);
                ReplacementCount++;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _session = OpenValidated(Path);
            ReadHeader(_session);
        }

        private void EnsureCapacity(int incoming)
        {
            while (checked(_count + incoming) * 10 >= _capacity * 6) Resize(checked(_capacity * 2));
        }

        private void Resize(long newCapacity)
        {
            var old = _session ?? throw new InvalidOperationException("Disk hash table session is unavailable.");
            var temp = Path + $".{Guid.NewGuid():N}.resize.tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                           64 * 1024, FileOptions.WriteThrough | FileOptions.RandomAccess))
                {
                    output.SetLength(checked(HeaderBytes + newCapacity * RecordBytes));
                    var oldCapacity = _capacity;
                    var oldCount = _count;
                    _capacity = newCapacity;
                    _count = 0;
                    _generation++;
                    for (long slot = 0; slot < oldCapacity; slot++)
                    {
                        var record = ReadSlot(old, slot);
                        if (record is null) continue;
                        FindSlot(output, record.Key, out var target, out _);
                        WriteSlot(output, target, record);
                        _count++;
                    }
                    if (_count != oldCount) throw new InvalidDataException("Derived index resize lost records.");
                    WriteHeader(output);
                    output.Flush(true);
                    BytesWritten += output.Length;
                }
                old.Dispose();
                _session = null;
                File.Move(temp, Path, true);
                ReplacementCount++;
                _session = OpenValidated(Path);
                ReadHeader(_session);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private bool FindSlot(FileStream stream, Hash256Value key, out long slot, out byte[] value)
        {
            var start = (long)((key.A ^ key.C) & (ulong)(_capacity - 1));
            for (long probe = 0; probe < _capacity; probe++)
            {
                slot = (start + probe) & (_capacity - 1);
                var record = ReadSlot(stream, slot);
                if (record is null) { value = Array.Empty<byte>(); return false; }
                if (record.Key.Equals(key)) { value = record.Value; return true; }
            }
            throw new InvalidDataException("Derived index has no free slot.");
        }

        private DiskHashRecord? ReadSlot(FileStream stream, long slot)
        {
            var buffer = new byte[RecordBytes];
            stream.Position = checked(HeaderBytes + slot * RecordBytes);
            stream.ReadExactly(buffer);
            if (buffer[0] == 0) return null;
            if (buffer[0] != 1) throw new InvalidDataException("Derived index slot marker is invalid.");
            return new DiskHashRecord(Hash256Value.FromBytes(buffer.AsSpan(8, 32)),
                buffer.AsSpan(40, _valueBytes).ToArray());
        }

        private void WriteSlot(FileStream stream, long slot, DiskHashRecord record)
        {
            var buffer = new byte[RecordBytes];
            buffer[0] = 1;
            record.Key.WriteBytes(buffer.AsSpan(8, 32));
            record.Value.CopyTo(buffer.AsSpan(40, _valueBytes));
            stream.Position = checked(HeaderBytes + slot * RecordBytes);
            stream.Write(buffer);
        }

        private FileStream OpenValidated(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.WriteThrough | FileOptions.RandomAccess);
            try { ReadHeader(stream); return stream; }
            catch { stream.Dispose(); throw; }
        }

        private void ReadHeader(FileStream stream)
        {
            if (stream.Length < HeaderBytes) throw new InvalidDataException("Derived index header is truncated.");
            Span<byte> header = stackalloc byte[HeaderBytes];
            stream.Position = 0;
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt64BigEndian(header) != Magic
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Schema
                || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != _valueBytes)
                throw new InvalidDataException("Derived index header schema is invalid.");
            _capacity = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
            _count = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
            _generation = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
            if (_capacity < InitialCapacity || (_capacity & (_capacity - 1)) != 0
                || _count < 0 || _count * 10 >= _capacity * 7
                || stream.Length != checked(HeaderBytes + _capacity * RecordBytes))
                throw new InvalidDataException("Derived index header bounds are invalid.");
        }

        private void WriteHeader(FileStream stream)
        {
            Span<byte> header = stackalloc byte[HeaderBytes];
            BinaryPrimitives.WriteUInt64BigEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], Schema);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], _valueBytes);
            BinaryPrimitives.WriteInt64LittleEndian(header[16..], _capacity);
            BinaryPrimitives.WriteInt64LittleEndian(header[24..], _count);
            BinaryPrimitives.WriteInt64LittleEndian(header[32..], _generation);
            stream.Position = 0;
            stream.Write(header);
        }

        private void CloseSession(bool force = false)
        {
            if (!force && --_sessionDepth > 0) return;
            _sessionDepth = 0;
            _session?.Dispose();
            _session = null;
        }

        private sealed class SessionScope : IDisposable
        {
            private DiskHashTable? _owner;
            public SessionScope(DiskHashTable owner) => _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.CloseSession();
        }
    }

    private static string FileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private readonly record struct Hash256Value(ulong A, ulong B, ulong C, ulong D) : IComparable<Hash256Value>
    {
        public static Hash256Value Parse(string value)
        {
            if (!IsSha256Hex(value)) throw new InvalidDataException("Expected a SHA-256 hexadecimal value.");
            var bytes = Convert.FromHexString(value);
            return new Hash256Value(
                BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(0, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(16, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(24, 8)));
        }

        public static Hash256Value FromBytes(ReadOnlySpan<byte> bytes) => new(
            BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(16, 8)),
            BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(24, 8)));

        public void WriteBytes(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt64BigEndian(destination[..8], A);
            BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), B);
            BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(16, 8), C);
            BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(24, 8), D);
        }

        public int CompareTo(Hash256Value other)
        {
            var result = A.CompareTo(other.A);
            if (result != 0) return result;
            result = B.CompareTo(other.B);
            if (result != 0) return result;
            result = C.CompareTo(other.C);
            return result != 0 ? result : D.CompareTo(other.D);
        }

        public string ToHex()
        {
            Span<byte> bytes = stackalloc byte[32];
            BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], A);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(8, 8), B);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(16, 8), C);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.Slice(24, 8), D);
            return Convert.ToHexString(bytes);
        }
    }
    private sealed record JsonLineRead<T>(IReadOnlyList<T> Items, int BadLineCount, int IntegrityFailureCount);
}
