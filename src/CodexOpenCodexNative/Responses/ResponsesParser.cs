using System.Text.Json;
using System.Text.Json.Nodes;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Responses;

public static class ResponsesParser
{
    public static OcxParsedRequest Parse(ResponsesRequest request)
    {
        var parsed = new OcxParsedRequest
        {
            Model = request.Model,
            Stream = request.Stream,
            Tools = NormalizeTools(request.Tools),
            ToolChoice = request.ToolChoice,
            ParallelToolCalls = request.ParallelToolCalls,
            Reasoning = request.Reasoning,
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
            PreviousResponseId = request.PreviousResponseId
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            parsed.Messages.Add(new OcxMessage
            {
                Role = "developer",
                Content = request.Instructions
            });
        }

        if (request.Input is { ValueKind: JsonValueKind.String } inputString)
        {
            parsed.Messages.Add(new OcxMessage
            {
                Role = "user",
                Content = inputString.GetString()
            });
        }
        else if (request.Input is { ValueKind: JsonValueKind.Array } inputArray)
        {
            foreach (var item in inputArray.EnumerateArray())
            {
                var message = MapInputItem(item);
                if (message is not null)
                    parsed.Messages.Add(message);
            }
        }

        return parsed;
    }

    private static List<OcxTool>? NormalizeTools(IReadOnlyList<OcxTool>? tools)
    {
        if (tools is null) return null;
        var normalized = new List<OcxTool>();
        foreach (var tool in tools)
        {
            if (tool.Function is not null)
            {
                normalized.Add(tool);
                continue;
            }
            if (!string.Equals(tool.Type, "function", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(tool.Name))
                continue;
            normalized.Add(new OcxTool
            {
                Type = "function",
                Function = new OcxToolFunction
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.Parameters,
                    Strict = tool.Strict
                }
            });
        }
        return normalized;
    }

    public static OcxMessage? MapInputItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var type = item.TryGetProperty("type", out var typeValue)
            ? typeValue.GetString()
            : null;

        switch (type)
        {
            case "message":
            case null:
            {
                var role = item.TryGetProperty("role", out var roleValue)
                    ? roleValue.GetString() ?? "user"
                    : "user";
                if (role is "system")
                    role = "developer";
                var content = item.TryGetProperty("content", out var contentValue)
                    ? contentValue
                    : default;
                return new OcxMessage
                {
                    Role = role,
                    Content = content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? string.Empty
                        : content
                };
            }
            case "input_text":
            case "text":
            {
                var text = item.TryGetProperty("text", out var textValue)
                    ? textValue.GetString() ?? string.Empty
                    : string.Empty;
                return new OcxMessage { Role = "user", Content = text };
            }
            case "input_image":
            {
                var imageUrl = item.TryGetProperty("image_url", out var urlValue)
                    ? urlValue.GetString()
                    : null;
                var parts = new List<JsonElement>();
                if (item.TryGetProperty("detail", out var detailValue) && detailValue.ValueKind is not JsonValueKind.Null)
                    parts.Add(detailValue);
                return new OcxMessage
                {
                    Role = "user",
                    Content = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = imageUrl ?? string.Empty }
                        }
                    }
                };
            }
            case "function_call_output":
            {
                var callId = item.TryGetProperty("call_id", out var callIdValue)
                    ? callIdValue.GetString()
                    : string.Empty;
                var output = item.TryGetProperty("output", out var outputValue)
                    ? outputValue
                    : default;
                return new OcxMessage
                {
                    Role = "tool",
                    ToolCallId = callId,
                    Content = output.ValueKind is JsonValueKind.Undefined ? string.Empty : output
                };
            }
            case "function_call":
            {
                var callId = item.TryGetProperty("call_id", out var callIdValue)
                    ? callIdValue.GetString()
                    : string.Empty;
                var name = item.TryGetProperty("name", out var nameValue)
                    ? nameValue.GetString() ?? string.Empty
                    : string.Empty;
                var arguments = item.TryGetProperty("arguments", out var argsValue)
                    ? argsValue.ToString()
                    : string.Empty;
                return new OcxMessage
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new OcxToolCall
                        {
                            Id = callId,
                            Function = new OcxToolCallFunction { Name = name, Arguments = arguments }
                        }
                    ]
                };
            }
            default:
                return null;
        }
    }
}
