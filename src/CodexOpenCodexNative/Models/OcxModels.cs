using System.Text.Json;
using System.Text.Json.Nodes;
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
    [JsonPropertyName("function"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OcxToolFunction? Function { get; set; }

    // The Responses API places these fields directly on the tool object while
    // Chat Completions nests them under "function". Keep both shapes readable;
    // ResponsesParser normalizes the flat form before a third-party adapter is
    // selected.
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("parameters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Parameters { get; set; }
    [JsonPropertyName("strict"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }
}

public sealed class OcxToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public JsonElement? Parameters { get; set; }
    [JsonPropertyName("strict"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }
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
    [JsonIgnore] public JsonElement? Reasoning { get; set; }
    [JsonIgnore] public bool? ParallelToolCalls { get; set; }
}

public static class OcxMessageContent
{
    public static string ExtractText(object? content)
    {
        if (content is null) return string.Empty;
        if (content is string text) return text;
        JsonElement element;
        try { element = content is JsonElement json ? json : JsonSerializer.SerializeToElement(content); }
        catch { return content.ToString() ?? string.Empty; }
        return ExtractText(element);
    }

    public static JsonNode ToChatNode(object? content)
    {
        if (content is null) return JsonValue.Create(string.Empty)!;
        if (content is string text) return JsonValue.Create(text)!;
        JsonElement element;
        try { element = content is JsonElement json ? json : JsonSerializer.SerializeToElement(content); }
        catch { return JsonValue.Create(content.ToString() ?? string.Empty)!; }
        if (element.ValueKind != JsonValueKind.Array)
            return JsonSerializer.SerializeToNode(element) ?? JsonValue.Create(string.Empty)!;

        var parts = new JsonArray();
        var hasImage = false;
        foreach (var part in element.EnumerateArray())
        {
            var type = ReadString(part, "type");
            if (type is "input_image" or "image_url")
            {
                hasImage = true;
                var url = ReadImageUrl(part);
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = url }
                });
                continue;
            }
            var partText = ExtractText(part);
            if (!string.IsNullOrEmpty(partText))
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = partText });
        }
        if (!hasImage)
            return JsonValue.Create(string.Concat(parts.Select(part => part?["text"]?.GetValue<string>())))!;
        return parts;
    }

    public static JsonNode ToResponsesNode(object? content)
    {
        if (content is null) return JsonValue.Create(string.Empty)!;
        if (content is string text) return JsonValue.Create(text)!;
        JsonElement element;
        try { element = content is JsonElement json ? json : JsonSerializer.SerializeToElement(content); }
        catch { return JsonValue.Create(content.ToString() ?? string.Empty)!; }
        if (element.ValueKind != JsonValueKind.Array)
            return JsonSerializer.SerializeToNode(element) ?? JsonValue.Create(string.Empty)!;

        var parts = new JsonArray();
        foreach (var part in element.EnumerateArray())
        {
            var type = ReadString(part, "type");
            if (type is "image_url" or "input_image")
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = ReadImageUrl(part)
                });
                continue;
            }
            var partText = ExtractText(part);
            if (!string.IsNullOrEmpty(partText))
                parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = partText });
        }
        return parts;
    }

    private static string ExtractText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                return string.Concat(element.EnumerateArray().Select(ExtractText));
            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var text)) return ExtractText(text);
                if (element.TryGetProperty("output", out var output)) return ExtractText(output);
                if (element.TryGetProperty("content", out var content)) return ExtractText(content);
                return string.Empty;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetRawText();
            default:
                return string.Empty;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadImageUrl(JsonElement part)
    {
        if (!part.TryGetProperty("image_url", out var imageUrl)) return string.Empty;
        if (imageUrl.ValueKind == JsonValueKind.String) return imageUrl.GetString() ?? string.Empty;
        return imageUrl.ValueKind == JsonValueKind.Object
               && imageUrl.TryGetProperty("url", out var url)
               && url.ValueKind == JsonValueKind.String
            ? url.GetString() ?? string.Empty
            : string.Empty;
    }
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
