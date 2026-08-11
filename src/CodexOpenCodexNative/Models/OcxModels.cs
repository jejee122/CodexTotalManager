using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexOpenCodexNative.Models;

public sealed class OcxMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public object? Content { get; set; }
    [JsonPropertyName("tool_calls")] public List<OcxToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class OcxToolCall
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public OcxToolCallFunction? Function { get; set; }
}

public sealed class OcxToolCallFunction
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

public sealed class OcxTool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public OcxToolFunction? Function { get; set; }
}

public sealed class OcxToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public JsonElement? Parameters { get; set; }
}

public sealed class OcxParsedRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<OcxMessage> Messages { get; set; } = new();
    [JsonPropertyName("tools")] public List<OcxTool>? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public JsonElement? ToolChoice { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("max_tokens")] public long? MaxTokens { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("extra_body")] public Dictionary<string, JsonElement>? ExtraBody { get; set; }
    [JsonIgnore] public string? RawBody { get; set; }
    [JsonIgnore] public bool IsResponsesRequest { get; set; }
    [JsonIgnore] public Dictionary<string, string>? ForwardHeaders { get; set; }
    [JsonIgnore] public string? PreviousResponseId { get; set; }
}

public sealed class OcxUsage
{
    [JsonPropertyName("prompt_tokens")] public long PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public long CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public long TotalTokens { get; set; }
}

public sealed class OcxModelEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("object")] public string Object { get; set; } = "model";
    [JsonPropertyName("owned_by")] public string OwnedBy { get; set; } = "native-proxy";
    [JsonPropertyName("namespaced")] public string? Namespaced { get; set; }
}
