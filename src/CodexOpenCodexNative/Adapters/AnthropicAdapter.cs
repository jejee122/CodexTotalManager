using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Adapters;

public sealed class AnthropicAdapter : IProviderAdapter
{
    private readonly HttpClient _http;

    public AnthropicAdapter(HttpClient? http = null)
    {
        _http = http ?? AdapterHttpTransport.Shared;
    }

    public string AdapterId => "anthropic";

    public async Task<AdapterResponse> FetchAsync(
        ProviderDefinition provider,
        OcxParsedRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        var url = provider.BaseUrl.TrimEnd('/') + "/v1/messages";
        var body = BuildRequestJson(request, modelId);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AdapterResponse
            {
                Streaming = false,
                ContentType = "application/json",
                JsonBody = BuildErrorBody((int)response.StatusCode, errorBody),
                StatusCode = (int)response.StatusCode,
                Owner = response
            };
        }

        if (request.Stream)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new AdapterResponse
            {
                Streaming = true,
                ContentType = "text/event-stream",
                Events = ParseSseStream(stream, cancellationToken),
                Owner = response
            };
        }

        var jsonBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var normalized = ParseNonStreamingResponse(jsonBody);
        return new AdapterResponse
        {
            Streaming = false,
            ContentType = "application/json",
            JsonBody = jsonBody,
            Message = normalized.Message,
            FinishReason = normalized.FinishReason,
            Usage = normalized.Usage,
            Owner = response
        };
    }

    internal static AnthropicNormalizedResponse ParseNonStreamingResponse(string jsonBody)
    {
        using var document = JsonDocument.Parse(jsonBody);
        var root = document.RootElement;
        var message = new OcxMessage { Role = "assistant" };
        var text = new StringBuilder();
        var calls = new List<OcxToolCall>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = ReadString(block, "type");
                if (type == "text")
                {
                    text.Append(ReadString(block, "text"));
                    continue;
                }
                if (type != "tool_use") continue;
                calls.Add(new OcxToolCall
                {
                    Id = ReadString(block, "id"),
                    Function = new OcxToolCallFunction
                    {
                        Name = ReadString(block, "name"),
                        Arguments = block.TryGetProperty("input", out var input)
                            ? input.GetRawText()
                            : "{}"
                    }
                });
            }
        }
        message.Content = text.ToString();
        if (calls.Count > 0) message.ToolCalls = calls;
        var usage = new OcxUsage();
        if (root.TryGetProperty("usage", out var usageNode))
        {
            usage.PromptTokens = ReadLong(usageNode, "input_tokens");
            usage.CompletionTokens = ReadLong(usageNode, "output_tokens");
            usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
        }
        var stopReason = ReadString(root, "stop_reason");
        return new AnthropicNormalizedResponse(
            message,
            stopReason == "tool_use" ? "tool_calls" : stopReason == "max_tokens" ? "length" : "stop",
            usage);
    }

    public static string BuildRequestJson(OcxParsedRequest request, string modelId)
    {
        var root = new JsonObject
        {
            ["model"] = modelId,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["stream"] = request.Stream
        };

        var system = new List<string>();
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case "system":
                case "developer":
                {
                    var text = OcxMessageContent.ExtractText(message.Content).Trim();
                    if (text.Length > 0) system.Add(text);
                    break;
                }
                case "tool":
                {
                    var result = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId ?? string.Empty,
                        ["content"] = OcxMessageContent.ExtractText(message.Content)
                    };
                    if (messages.Count > 0
                        && messages[^1] is JsonObject previous
                        && previous["role"]?.GetValue<string>() == "user"
                        && previous["content"] is JsonArray previousBlocks
                        && previousBlocks.All(block => block?["type"]?.GetValue<string>() == "tool_result"))
                    {
                        previousBlocks.Add(result);
                    }
                    else
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray(result)
                        });
                    }
                    break;
                }
                case "assistant" when message.ToolCalls is { Count: > 0 }:
                {
                    var blocks = new JsonArray();
                    var assistantText = OcxMessageContent.ExtractText(message.Content);
                    if (!string.IsNullOrWhiteSpace(assistantText))
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = assistantText });
                    foreach (var call in message.ToolCalls)
                    {
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id ?? string.Empty,
                            ["name"] = call.Function?.Name ?? string.Empty,
                            ["input"] = ParseArguments(call.Function?.Arguments)
                        });
                    }
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    break;
                }
                default:
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = message.Role,
                        ["content"] = OcxMessageContent.ExtractText(message.Content)
                    });
                    break;
                }
            }
        }

        // Anthropic accepts one system string or an array of typed content
        // blocks. A raw JSON string array is not a valid Messages request.
        // Joining preserves the order of OpenAI system/developer messages and
        // avoids sending a shape that the real Anthropic endpoint rejects.
        if (system.Count > 0)
            root["system"] = string.Join("\n\n", system);
        root["messages"] = messages;

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools.Where(t => t.Function is not null))
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Function!.Name,
                    ["description"] = tool.Function.Description ?? string.Empty,
                    ["input_schema"] = tool.Function.Parameters is { ValueKind: not JsonValueKind.Undefined }
                        ? JsonSerializer.SerializeToNode(tool.Function.Parameters.Value)
                        : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                });
            }
            if (tools.Count > 0)
                root["tools"] = tools;
        }

        if (BuildToolChoice(request.ToolChoice, request.ParallelToolCalls) is { } toolChoice)
            root["tool_choice"] = toolChoice;
        if (request.Temperature is not null)
            root["temperature"] = request.Temperature;

        return root.ToJsonString();
    }

    private static JsonObject? BuildToolChoice(JsonElement? choice, bool? parallelToolCalls)
    {
        if (choice is not { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null }
            && parallelToolCalls is null)
            return null;

        var result = new JsonObject { ["type"] = "auto" };
        if (choice is { } selected)
        {
            if (selected.ValueKind == JsonValueKind.String)
            {
                result["type"] = selected.GetString()?.ToLowerInvariant() switch
                {
                    "required" => "any",
                    "none" => "none",
                    _ => "auto"
                };
            }
            else if (selected.ValueKind == JsonValueKind.Object)
            {
                var type = ReadString(selected, "type")?.ToLowerInvariant();
                if (type == "function")
                {
                    var name = ReadString(selected, "name");
                    if (string.IsNullOrWhiteSpace(name)
                        && selected.TryGetProperty("function", out var function))
                        name = ReadString(function, "name");
                    result["type"] = "tool";
                    result["name"] = name ?? string.Empty;
                }
                else
                {
                    result["type"] = type switch
                    {
                        "required" => "any",
                        "none" => "none",
                        _ => "auto"
                    };
                }
            }
        }
        if (parallelToolCalls == false)
            result["disable_parallel_tool_use"] = true;
        return result;
    }

    private static JsonNode ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new JsonObject();
        try
        {
            return JsonNode.Parse(arguments) ?? new JsonObject();
        }
        catch
        {
            return new JsonObject { ["input"] = arguments };
        }
    }

    private static async IAsyncEnumerable<AdapterEvent> ParseSseStream(
        Stream raw,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(raw, Encoding.UTF8);
        var currentEvent = string.Empty;
        var usage = new OcxUsage();
        var toolCalls = new Dictionary<int, PendingAnthropicToolCall>();
        string? stopReason = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(payload).RootElement;
            }
            catch
            {
                continue;
            }

            switch (currentEvent)
            {
                case "message_start":
                {
                    if (root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("usage", out var startUsage))
                    {
                        usage.PromptTokens = ReadLong(startUsage, "input_tokens");
                        usage.CompletionTokens = ReadLong(startUsage, "output_tokens");
                        usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                    }
                    break;
                }
                case "content_block_start":
                {
                    var index = ReadInt(root, "index");
                    if (!root.TryGetProperty("content_block", out var block)
                        || !string.Equals(ReadString(block, "type"), "tool_use", StringComparison.Ordinal))
                        break;
                    var pending = new PendingAnthropicToolCall(
                        index,
                        ReadString(block, "id"),
                        ReadString(block, "name"));
                    toolCalls[index] = pending;
                    yield return new AdapterEvent
                    {
                        Type = "function_call",
                        CallId = pending.CallId,
                        FunctionName = pending.Name,
                        ToolCallIndex = pending.Index
                    };
                    break;
                }
                case "content_block_delta":
                {
                    if (!root.TryGetProperty("delta", out var delta)) break;
                    var deltaType = ReadString(delta, "type");
                    if (deltaType == "text_delta" && delta.TryGetProperty("text", out var text))
                    {
                        yield return new AdapterEvent { Type = "text", Text = text.GetString() ?? string.Empty, Role = "assistant" };
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var index = ReadInt(root, "index");
                        if (!toolCalls.TryGetValue(index, out var pending)) break;
                        var arguments = ReadString(delta, "partial_json") ?? string.Empty;
                        pending.Arguments.Append(arguments);
                        yield return new AdapterEvent
                        {
                            Type = "function_call",
                            Arguments = arguments,
                            ToolCallIndex = pending.Index
                        };
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var index = ReadInt(root, "index");
                    if (!toolCalls.TryGetValue(index, out var pending) || pending.Done) break;
                    pending.Done = true;
                    yield return new AdapterEvent
                    {
                        Type = "function_call_done",
                        CallId = pending.CallId,
                        FunctionName = pending.Name,
                        Arguments = pending.Arguments.ToString(),
                        ToolCallIndex = pending.Index
                    };
                    break;
                }
                case "message_delta":
                {
                    if (root.TryGetProperty("delta", out var messageDelta))
                        stopReason = ReadString(messageDelta, "stop_reason") ?? stopReason;
                    if (root.TryGetProperty("usage", out var usageNode))
                    {
                        var output = ReadLong(usageNode, "output_tokens");
                        var input = ReadLong(usageNode, "input_tokens");
                        if (output > 0) usage.CompletionTokens = output;
                        if (input > 0) usage.PromptTokens = input;
                        usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                    }
                    break;
                }
                case "message_stop":
                {
                    foreach (var pending in toolCalls.Values.Where(call => !call.Done).OrderBy(call => call.Index))
                    {
                        yield return new AdapterEvent
                        {
                            Type = "function_call_done",
                            CallId = pending.CallId,
                            FunctionName = pending.Name,
                            Arguments = pending.Arguments.ToString(),
                            ToolCallIndex = pending.Index
                        };
                    }
                    yield return new AdapterEvent { Type = "usage", Usage = usage };
                    yield return new AdapterEvent
                    {
                        Type = "finish",
                        FinishReason = stopReason == "max_tokens" ? "max_tokens" : stopReason == "tool_use" ? "tool_calls" : "stop"
                    };
                    yield break;
                }
                case "error":
                {
                    var message = root.TryGetProperty("error", out var error)
                        && error.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString() ?? "上游错误"
                        : "上游错误";
                    yield return new AdapterEvent { Type = "error", Text = message };
                    yield break;
                }
            }
        }

        yield return new AdapterEvent { Type = "incomplete", Text = "上游流未以 message_stop 结束" };
    }

    private static string BuildErrorBody(int status, string upstreamBody) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                message = $"上游返回 {status}：{Truncate(upstreamBody, 500)}",
                type = "upstream_error",
                code = (int)status
            }
        });

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class PendingAnthropicToolCall(int index, string? callId, string? name)
    {
        public int Index { get; } = index;
        public string? CallId { get; } = callId;
        public string? Name { get; } = name;
        public StringBuilder Arguments { get; } = new();
        public bool Done { get; set; }
    }

    internal sealed record AnthropicNormalizedResponse(
        OcxMessage Message,
        string FinishReason,
        OcxUsage Usage);
}
