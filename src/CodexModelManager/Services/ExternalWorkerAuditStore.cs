using System.Text;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public interface IExternalWorkerAuditSink
{
    ValueTask AppendAsync(ExternalWorkerAuditEntry entry, CancellationToken cancellationToken = default);
}

public interface IExternalWorkerRuntimeStateSink
{
    ValueTask RecordHandshakeAsync(
        string? clientName,
        string? clientVersion,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalWorkerAuditStore : IExternalWorkerAuditSink, IExternalWorkerRuntimeStateSink, IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExternalWorkerAuditStore(string? auditPath = null, string? statePath = null)
    {
        var localData = AppSettingsService.ResolveDefaultDataDirectory();
        AuditPath = auditPath ?? Path.Combine(localData, "external-worker-audit.jsonl");
        StatePath = statePath ?? Path.Combine(localData, "external-worker-state.json");
    }

    public string AuditPath { get; }
    public string StatePath { get; }

    public async ValueTask AppendAsync(ExternalWorkerAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken);
            var directory = Path.GetDirectoryName(AuditPath)
                            ?? throw new InvalidOperationException("外部工人审计路径没有父目录。");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;
            await using var stream = new FileStream(
                AuditPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var bytes = Utf8NoBom.GetBytes(line);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            if (entry.Event.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                var current = ReadStateCore();
                var next = current with
                {
                    LastCallAt = entry.Timestamp,
                    LastCallSucceeded = entry.Status.Equals("success", StringComparison.OrdinalIgnoreCase),
                    LastRoleId = entry.RoleId,
                    LastRequestedModel = entry.ConfiguredModel,
                    LastResolvedModel = entry.ResolvedModel,
                    LastHttpStatus = entry.HttpStatusCode,
                    InputTokens = entry.PromptTokens,
                    OutputTokens = entry.CompletionTokens,
                    LastError = entry.ErrorCode,
                    LastAccountSource = entry.AccountSource
                };
                WriteStateCore(next);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask RecordHandshakeAsync(
        string? clientName,
        string? clientVersion,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken);
            var current = ReadStateCore();
            WriteStateCore(current with
            {
                LastHandshakeAt = DateTimeOffset.UtcNow,
                LastHandshakeClient = Limit(clientName, 120),
                LastHandshakeClientVersion = Limit(clientVersion, 80)
            });
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ExternalWorkerRuntimeState ReadState() => ReadStateCore();

    private ExternalWorkerRuntimeState ReadStateCore()
    {
        if (!File.Exists(StatePath)) return EmptyState();
        try
        {
            using var stream = new FileStream(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ExternalWorkerRuntimeState>(stream, _jsonOptions)
                   ?? EmptyState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return EmptyState() with { LastError = "runtime_state_unreadable" };
        }
    }

    private void WriteStateCore(ExternalWorkerRuntimeState state)
    {
        var directory = Path.GetDirectoryName(StatePath)
                        ?? throw new InvalidOperationException("外部工人状态路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temp = StatePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, _jsonOptions), Utf8NoBom);
            File.Move(temp, StatePath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static ExternalWorkerRuntimeState EmptyState() => new(
        null, null, null, null, null, null, null, null, null, null, null, null);

    private static string? Limit(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    private async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(AuditPath)
                        ?? throw new InvalidOperationException("外部工人审计路径没有父目录。");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, "external-worker-audit.lock");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new IOException("等待 外部工人跨进程审计锁超时。", ex);
            }
        }
    }

    public void Dispose()
    {
        _writeGate.Dispose();
    }
}
