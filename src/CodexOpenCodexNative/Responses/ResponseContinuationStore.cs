using System.Text;
using System.Text.Json;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Responses;

/// <summary>
/// Bounded, in-memory continuation state for responses bridged to providers that
/// cannot resolve a Codex response id themselves. It stores message content only;
/// request headers, API keys and authorization values never enter this type.
/// </summary>
public sealed class ResponseContinuationStore
{
    private const int MaxEntries = 128;
    private const int MaxEntryBytes = 1_000_000;
    private const int MaxTotalBytes = 16_000_000;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _totalBytes;

    public bool TryExpand(
        string? previousResponseId,
        IReadOnlyList<OcxMessage> currentMessages,
        out List<OcxMessage> expanded)
    {
        expanded = CloneMessages(currentMessages);
        if (string.IsNullOrWhiteSpace(previousResponseId)) return false;
        lock (_gate)
        {
            PruneExpired();
            if (!_entries.TryGetValue(previousResponseId, out var entry)) return false;
            expanded = CloneMessages(entry.Messages);
            expanded.AddRange(CloneMessages(currentMessages));
            return true;
        }
    }

    public void Save(
        string? responseId,
        IReadOnlyList<OcxMessage> requestMessages,
        IReadOnlyList<OcxMessage> outputMessages)
    {
        if (string.IsNullOrWhiteSpace(responseId) || outputMessages.Count == 0) return;
        var transcript = CloneMessages(requestMessages);
        transcript.AddRange(CloneMessages(outputMessages));
        var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(transcript));
        if (bytes <= 0 || bytes > MaxEntryBytes) return;

        lock (_gate)
        {
            PruneExpired();
            if (_entries.Remove(responseId, out var previous)) _totalBytes -= previous.Bytes;
            while (_entries.Count >= MaxEntries || _totalBytes + bytes > MaxTotalBytes)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.CreatedAt).FirstOrDefault();
                if (oldest.Key is null) break;
                _entries.Remove(oldest.Key);
                _totalBytes -= oldest.Value.Bytes;
            }
            _entries[responseId] = new Entry(transcript, DateTimeOffset.UtcNow, bytes);
            _totalBytes += bytes;
        }
    }

    public void SaveFromResponseJson(string? responseJson, IReadOnlyList<OcxMessage> requestMessages)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return;
        try
        {
            using var json = JsonDocument.Parse(responseJson);
            var root = json.RootElement;
            var responseId = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return;
            var messages = new List<OcxMessage>();
            foreach (var item in output.EnumerateArray())
            {
                var mapped = ResponsesParser.MapInputItem(item);
                if (mapped is not null) messages.Add(mapped);
            }
            Save(responseId, requestMessages, messages);
        }
        catch (JsonException)
        {
            // Malformed upstream output is handled by the normal response path.
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - EntryTtl;
        foreach (var id in _entries.Where(pair => pair.Value.CreatedAt < cutoff)
                     .Select(pair => pair.Key).ToArray())
        {
            _totalBytes -= _entries[id].Bytes;
            _entries.Remove(id);
        }
    }

    private static List<OcxMessage> CloneMessages(IReadOnlyList<OcxMessage> source)
    {
        if (source.Count == 0) return new List<OcxMessage>();
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<List<OcxMessage>>(json) ?? new List<OcxMessage>();
    }

    private sealed record Entry(List<OcxMessage> Messages, DateTimeOffset CreatedAt, int Bytes);
}
