using System.Security.Cryptography;

namespace CodexModelManager.Services;

public sealed record AccountUsageLedgerDiagnostics(
    long FullIndexRebuildCount,
    long IncrementalSegmentReadCount,
    long ParsedLedgerLineCount,
    long NoChangeRefreshCount,
    long SourceImportCount,
    long LedgerVerificationBytes,
    long AttemptProjectionRowsProcessed,
    long QuotaProjectionRowsProcessed,
    long QuotaWriteIndexRowsProcessed)
{
    public long CheckpointLoadCount { get; init; }
    public long CheckpointRebuildCount { get; init; }
    public long CheckpointPublishCount { get; init; }
    public long CheckpointValidationFailureCount { get; init; }
    public long InMemoryFactObjectCount { get; init; }
    public long CompactIdempotencyEntryCount { get; init; }
    public long QuotaFallbackSelectionCount { get; init; }
    public long QuotaFallbackCandidateRowsExamined { get; init; }
    public long DerivedIndexBytesWritten { get; init; }
    public long DerivedIndexReplacementCount { get; init; }
    public int SnapshotSubscriberCount { get; init; }
    public int ActiveSnapshotSubscriberWorkers { get; init; }
    public int PendingSnapshotSubscriberMailboxes { get; init; }
}

public enum AccountLedgerIdentityKeyState { Uninitialized, Available, Unavailable }

public sealed class AccountLedgerIdentityKeyUnavailableException : CryptographicException
{
    public AccountLedgerIdentityKeyUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class AccountLedgerSchemaMigrationRequiredException : IOException
{
    public AccountLedgerSchemaMigrationRequiredException(string backupDirectory, int detectedSchema)
        : base($"Account usage ledger schema {detectedSchema} cannot be mixed with schema 4. "
               + $"A byte-preserving backup was created at '{backupDirectory}'. Explicit migration or source rebuild is required.")
    {
        BackupDirectory = backupDirectory;
        DetectedSchema = detectedSchema;
    }

    public string BackupDirectory { get; }
    public int DetectedSchema { get; }
}
