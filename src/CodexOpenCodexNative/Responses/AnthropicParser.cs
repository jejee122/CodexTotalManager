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
            var builder = new System.Text.StringBuilder();
            foreach (var block in systemArray.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text))
                    builder.Append(text.GetString());
            }
            if (builder.Length > 0)
            {
                parsed.Messages.Add(new OcxMessage
                {
                    Role = "developer",
                    Content = builder.ToString()
                });
            }
        }

        foreach (var message in request.Messages)
        {
            parsed.Messages.Add(MapMessage(message));
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

    private static OcxMessage MapMessage(AnthropicMessage message)
    {
        var text = new System.Text.StringBuilder();
        var toolUses = new List<OcxToolCall>();
        string? toolResultCallId = null;
        var toolResultText = new System.Text.StringBuilder();

        if (message.Content.ValueKind == JsonValueKind.String)
        {
            text.Append(message.Content.GetString());
        }
        else if (message.Content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in message.Content.EnumerateArray())
            {
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
                        toolResultCallId = block.TryGetProperty("tool_use_id", out var idValue)
                            ? idValue.GetString()
                            : null;
                        if (block.TryGetProperty("content", out var content))
                        {
                            if (content.ValueKind == JsonValueKind.String)
                                toolResultText.Append(content.GetString());
                            else if (content.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var part in content.EnumerateArray())
                                {
                                    if (part.TryGetProperty("text", out var partText))
                                        toolResultText.Append(partText.GetString());
                                }
                            }
                        }
                        break;
                    }
                }
            }
        }

        if (toolResultCallId is not null)
        {
            return new OcxMessage
            {
                Role = "tool",
                ToolCallId = toolResultCallId,
                Content = toolResultText.ToString()
            };
        }

        if (toolUses.Count > 0)
        {
            return new OcxMessage
            {
                Role = "assistant",
                Content = text.Length > 0 ? text.ToString() : string.Empty,
                ToolCalls = toolUses
            };
        }

        return new OcxMessage
        {
            Role = message.Role == "assistant" ? "assistant" : "user",
            Content = text.ToString()
        };
    }
}
