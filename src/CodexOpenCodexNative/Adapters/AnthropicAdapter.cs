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
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
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
                StatusCode = (int)response.StatusCode
            };
        }

        if (request.Stream)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new AdapterResponse
            {
                Streaming = true,
                ContentType = "text/event-stream",
                Events = ParseSseStream(stream, cancellationToken)
            };
        }

        var jsonBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new AdapterResponse
        {
            Streaming = false,
            ContentType = "application/json",
            JsonBody = jsonBody
        };
    }

    public static string BuildRequestJson(OcxParsedRequest request, string modelId)
    {
        var root = new JsonObject
        {
            ["model"] = modelId,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["stream"] = request.Stream
        };

        var system = new JsonArray();
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case "system":
                case "developer":
                {
                    system.Add(message.Content?.ToString() ?? string.Empty);
                    break;
                }
                case "tool":
                {
                    var result = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = message.ToolCallId ?? string.Empty,
                            ["content"] = message.Content?.ToString() ?? string.Empty
                        }
                    };
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = result });
                    break;
                }
                case "assistant" when message.ToolCalls is { Count: > 0 }:
                {
                    var blocks = new JsonArray();
                    if (message.Content is { } content && !string.IsNullOrWhiteSpace(content.ToString()))
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = content.ToString() });
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
                        ["content"] = message.Content?.ToString() ?? string.Empty
                    });
                    break;
                }
            }
        }

        if (system.Count > 0)
            root["system"] = system.Count == 1 ? system[0] : system;
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

        return root.ToJsonString();
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
                case "content_block_delta":
                {
                    if (root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("type", out var deltaType)
                        && deltaType.GetString() == "text_delta"
                        && delta.TryGetProperty("text", out var text))
                    {
                        yield return new AdapterEvent { Type = "text", Text = text.GetString() ?? string.Empty, Role = "assistant" };
                    }
                    break;
                }
                case "message_delta":
                {
                    if (root.TryGetProperty("usage", out var usageNode))
                    {
                        usage.CompletionTokens = ReadLong(usageNode, "output_tokens");
                        usage.PromptTokens = ReadLong(usageNode, "input_tokens");
                        usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                    }
                    break;
                }
                case "message_stop":
                {
                    yield return new AdapterEvent { Type = "done", Usage = usage };
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
}
