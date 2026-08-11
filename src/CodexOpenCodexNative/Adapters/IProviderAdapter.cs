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

public sealed class AdapterResponse
{
    public bool Streaming { get; init; }
    public required string ContentType { get; init; }
    public string? JsonBody { get; init; }
    public IAsyncEnumerable<AdapterEvent>? Events { get; init; }
    public OcxUsage? Usage { get; init; }
    public int StatusCode { get; init; } = 200;
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
    [JsonPropertyName("tool_calls")] public List<JsonElement>? ToolCalls { get; set; }
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
