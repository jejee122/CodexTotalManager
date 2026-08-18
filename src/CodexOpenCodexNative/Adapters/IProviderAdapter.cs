using System.Text.Json;
using System.Text.Json.Serialization;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Adapters;

public interface IProviderAdapter
{
    string AdapterId { get; }

    Task<AdapterResponse> FetchAsync(
        ProviderDefinition provider,
        OcxParsedRequest request,
        string modelId,
        CancellationToken cancellationToken);
}

public static class AdapterHttpTransport
{
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
        new HttpClient(CreateHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(300)
        });

    internal static HttpClient Shared => SharedClient.Value;

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true,
        // Every adapter can be instantiated directly as well as through the
        // Native Proxy host. A provider credential must never follow a 3xx to
        // a different endpoint on either path.
        AllowAutoRedirect = false
    };
}

public sealed class AdapterResponse : IAsyncDisposable
{
    public bool Streaming { get; init; }
    public required string ContentType { get; init; }
    public string? JsonBody { get; init; }
    public IAsyncEnumerable<AdapterEvent>? Events { get; init; }
    public Stream? RawStream { get; init; }
    public OcxMessage? Message { get; init; }
    public string? FinishReason { get; init; }
    public OcxUsage? Usage { get; init; }
    public int StatusCode { get; init; } = 200;
    internal HttpResponseMessage? Owner { get; init; }

    public ValueTask DisposeAsync()
    {
        Owner?.Dispose();
        if (Owner is null)
            RawStream?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class AdapterEvent
{
    public string Type { get; init; } = "text";
    public string? Text { get; init; }
    public string? Role { get; init; }
    public OcxUsage? Usage { get; init; }
    public string? FinishReason { get; init; }
    public string? ErrorType { get; init; }
    public string? Reasoning { get; init; }
    public string? CallId { get; init; }
    public string? FunctionName { get; init; }
    public string? Arguments { get; init; }
    public string? ThoughtSignature { get; init; }
    public int ToolCallIndex { get; init; }
}

public sealed class ChatCompletionChunk
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion.chunk";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("choices")] public List<ChunkChoice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public OcxUsage? Usage { get; set; }
}

public sealed class ChunkChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delta")] public ChunkDelta Delta { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class ChunkDelta
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<ChatToolCallDelta>? ToolCalls { get; set; }
}

public sealed class ChatToolCallDelta
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }
    [JsonPropertyName("function")] public ChatToolCallFunctionDelta Function { get; set; } = new();
}

public sealed class ChatToolCallFunctionDelta
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("choices")] public List<ChatChoice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public OcxUsage? Usage { get; set; }
}

public sealed class ChatChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("message")] public OcxMessage Message { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}
