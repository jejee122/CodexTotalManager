using System.Text.Json;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Responses;

public static class AnthropicParser
{
    public static OcxParsedRequest Parse(AnthropicMessagesRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            throw new InvalidOperationException("messages 不能为空（Anthropic 协议要求至少一条消息）。");
        var parsed = new OcxParsedRequest
        {
            Model = request.Model,
            Stream = request.Stream,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens
        };

        if (request.System is { ValueKind: JsonValueKind.String } systemString)
        {
            parsed.Messages.Add(new OcxMessage
            {
                Role = "developer",
                Content = systemString.GetString()
            });
        }
        else if (request.System is { ValueKind: JsonValueKind.Array } systemArray)
        {
            var parts = new List<string>();
            foreach (var block in systemArray.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(text.GetString()))
                    parts.Add(text.GetString()!);
            }
            if (parts.Count > 0)
            {
                parsed.Messages.Add(new OcxMessage
                {
                    Role = "developer",
                    Content = string.Join("\n\n", parts)
                });
            }
        }

        foreach (var message in request.Messages)
        {
            parsed.Messages.AddRange(MapMessages(message));
        }

        if (request.Tools is { Count: > 0 })
        {
            parsed.Tools = request.Tools.Select(tool => new OcxTool
            {
                Type = "function",
                Function = new OcxToolFunction
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.InputSchema is { ValueKind: not JsonValueKind.Undefined }
                        ? tool.InputSchema.Value
                        : null
                }
            }).ToList();
        }

        return parsed;
    }

    private static IReadOnlyList<OcxMessage> MapMessages(AnthropicMessage message)
    {
        var text = new System.Text.StringBuilder();
        var toolUses = new List<OcxToolCall>();
        var userMessages = new List<OcxMessage>();

        void FlushUserText()
        {
            if (text.Length == 0) return;
            userMessages.Add(new OcxMessage { Role = "user", Content = text.ToString() });
            text.Clear();
        }

        if (message.Content.ValueKind == JsonValueKind.String)
        {
            text.Append(message.Content.GetString());
        }
        else if (message.Content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in message.Content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.String)
                {
                    text.Append(block.GetString());
                    continue;
                }
                if (block.ValueKind != JsonValueKind.Object) continue;
                var type = block.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                switch (type)
                {
                    case "text":
                    {
                        if (block.TryGetProperty("text", out var blockText))
                            text.Append(blockText.GetString());
                        break;
                    }
                    case "tool_use":
                    {
                        var callId = block.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                        var name = block.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                        var input = block.TryGetProperty("input", out var inputValue)
                            ? inputValue.ToString()
                            : string.Empty;
                        toolUses.Add(new OcxToolCall
                        {
                            Id = callId,
                            Function = new OcxToolCallFunction { Name = name, Arguments = input }
                        });
                        break;
                    }
                    case "tool_result":
                    {
                        FlushUserText();
                        var toolResultCallId = block.TryGetProperty("tool_use_id", out var idValue)
                                               && idValue.ValueKind == JsonValueKind.String
                            ? idValue.GetString()
                            : null;
                        userMessages.Add(new OcxMessage
                        {
                            Role = "tool",
                            ToolCallId = toolResultCallId,
                            Content = block.TryGetProperty("content", out var content)
                                ? ExtractToolResultText(content)
                                : string.Empty
                        });
                        break;
                    }
                }
            }
        }

        if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            FlushUserText();
            if (userMessages.Count == 0)
                userMessages.Add(new OcxMessage { Role = "user", Content = string.Empty });
            return userMessages;
        }

        if (toolUses.Count > 0)
        {
            return
            [
                new OcxMessage
                {
                    Role = "assistant",
                    Content = text.Length > 0 ? text.ToString() : string.Empty,
                    ToolCalls = toolUses
                }
            ];
        }

        return
        [
            new OcxMessage
            {
                Role = "assistant",
                Content = text.ToString()
            }
        ];
    }

    private static string ExtractToolResultText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? string.Empty
                : content.GetRawText();

        var builder = new System.Text.StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var partText)
                && partText.ValueKind == JsonValueKind.String)
                builder.Append(partText.GetString());
        }
        return builder.ToString();
    }
}
