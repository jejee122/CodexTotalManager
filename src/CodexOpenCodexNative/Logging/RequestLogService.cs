using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace CodexOpenCodexNative.Logging;

public sealed class RequestLogEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public long ElapsedMs { get; set; }
    public string? Path { get; set; }
    public string? RequestedModel { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public string? Status { get; set; }
    public int? HttpStatus { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public long? TotalTokens { get; set; }
    public string? Error { get; set; }
}

public sealed class RequestLogService
{
    private const int RingCapacity = 200;
    private readonly ConcurrentQueue<RequestLogEntry> _ring = new();
    private readonly string _journalPath;
    private readonly object _journalLock = new();
    private long _persistenceFailures;
    private string? _lastPersistenceError;
    private static readonly JsonSerializerOptions JournalJsonOptions = new(JsonSerializerDefaults.Web);

    public RequestLogService(string? dataRoot = null)
    {
        var root = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTotalManager", "runtime-v3", "native-proxy");
        Directory.CreateDirectory(root);
        _journalPath = Path.Combine(root, "request-log.jsonl");
    }

    public void Record(RequestLogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = Guid.NewGuid().ToString("N")[..16];
        entry.StartedAt = DateTimeOffset.Now;
        _ring.Enqueue(entry);
        while (_ring.Count > RingCapacity)
            _ring.TryDequeue(out _);
        try
        {
            lock (_journalLock)
            {
                File.AppendAllText(_journalPath,
                    JsonSerializer.Serialize(entry, JournalJsonOptions) + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Request logging is diagnostic evidence, not part of the inference
            // transaction. A full/locked/read-only disk must not turn an answer
            // that was already delivered to the client into a failed request.
            Interlocked.Increment(ref _persistenceFailures);
            Volatile.Write(ref _lastPersistenceError, ex.GetType().Name);
        }
    }

    public long PersistenceFailures => Interlocked.Read(ref _persistenceFailures);

    public string? LastPersistenceError => Volatile.Read(ref _lastPersistenceError);

    public IReadOnlyList<RequestLogEntry> Recent(int limit = 50) =>
        _ring.Reverse().Take(limit).ToList();

    public UsageSummary Summarize()
    {
        var summary = new UsageSummary();
        foreach (var entry in _ring)
        {
            summary.TotalRequests++;
            if (IsCompleted(entry)) summary.CompletedRequests++;
            if (entry.PromptTokens is not null) summary.PromptTokens += entry.PromptTokens.Value;
            if (entry.CompletionTokens is not null) summary.CompletionTokens += entry.CompletionTokens.Value;
            if (entry.TotalTokens is not null) summary.TotalTokens += entry.TotalTokens.Value;
            if (entry.Provider is not null)
            {
                var bucket = summary.ByProvider.GetValueOrDefault(entry.Provider)
                             ?? new UsageSummary();
                bucket.TotalRequests++;
                if (IsCompleted(entry)) bucket.CompletedRequests++;
                if (entry.PromptTokens is not null) bucket.PromptTokens += entry.PromptTokens.Value;
                if (entry.CompletionTokens is not null) bucket.CompletionTokens += entry.CompletionTokens.Value;
                if (entry.TotalTokens is not null) bucket.TotalTokens += entry.TotalTokens.Value;
                summary.ByProvider[entry.Provider] = bucket;
            }
        }
        return summary;
    }

    private static bool IsCompleted(RequestLogEntry entry) =>
        entry.Status == "completed"
        || entry.Status == "passed-through" && entry.HttpStatus is >= 200 and < 300;
}

public sealed class UsageSummary
{
    public long TotalRequests { get; set; }
    public long CompletedRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public Dictionary<string, UsageSummary> ByProvider { get; set; } = new();
}
