using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodexOpenCodexNative.Models;

public sealed class ResponsesRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("input")] public JsonElement? Input { get; set; }
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
    [JsonPropertyName("tools")] public List<OcxTool>? Tools { get; set; }
    [JsonPropertyName("reasoning")] public JsonElement? Reasoning { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("previous_response_id")] public string? PreviousResponseId { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("max_output_tokens")] public long? MaxOutputTokens { get; set; }
}

public sealed class ResponsesSseFrame
{
    public required string Event { get; init; }
    public JsonObject Data { get; init; } = new();
}

public sealed class ResponseItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = "message";
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public List<ResponseContentPart>? Content { get; set; }
}

public sealed class ResponseContentPart
{
    [JsonPropertyName("type")] public string Type { get; set; } = "output_text";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("annotations")] public List<JsonElement> Annotations { get; set; } = new();
}

public static class ResponsesUsage
{
    public static JsonObject Build(JsonElement? upstream)
    {
        long inputTokens = 0;
        long outputTokens = 0;
        long totalTokens = 0;
        if (upstream is { ValueKind: JsonValueKind.Object })
        {
            inputTokens = ReadLong(upstream.Value, "prompt_tokens");
            outputTokens = ReadLong(upstream.Value, "completion_tokens");
            totalTokens = ReadLong(upstream.Value, "total_tokens");
        }
        return new JsonObject
        {
            ["input_tokens"] = inputTokens,
            ["output_tokens"] = outputTokens,
            ["total_tokens"] = totalTokens,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }
        };
    }

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}

public static class ResponsesJson
{
    public static JsonObject Error(string type, string message, int? code = null)
    {
        var error = new JsonObject
        {
            ["type"] = type,
            ["message"] = message
        };
        if (code is not null)
            error["code"] = code;
        return error;
    }
}
