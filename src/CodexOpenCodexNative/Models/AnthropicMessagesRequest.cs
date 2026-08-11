using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexOpenCodexNative.Models;

public sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("system")] public JsonElement? System { get; set; }
    [JsonPropertyName("messages")] public List<AnthropicMessage> Messages { get; set; } = new();
    [JsonPropertyName("tools")] public List<AnthropicTool>? Tools { get; set; }
    [JsonPropertyName("max_tokens")] public long? MaxTokens { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
}

public sealed class AnthropicMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public JsonElement Content { get; set; }
}

public sealed class AnthropicTool
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; set; }
}
