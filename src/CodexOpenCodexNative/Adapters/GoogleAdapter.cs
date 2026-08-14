using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Adapters;

public sealed class GoogleAdapter : IProviderAdapter
{
    private readonly HttpClient _http;

    public GoogleAdapter(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
    }

    public string AdapterId => "google";

    public async Task<AdapterResponse> FetchAsync(
        ProviderDefinition provider,
        OcxParsedRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var action = request.Stream ? ":streamGenerateContent?alt=sse" : ":generateContent";
        var url = $"{baseUrl}/v1beta/models/{Uri.EscapeDataString(modelId)}{action}";
        var body = BuildRequestJson(request);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            httpRequest.Headers.TryAddWithoutValidation("x-goog-api-key", provider.ApiKey);

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

    internal static GoogleNormalizedResponse ParseNonStreamingResponse(string jsonBody)
    {
        using var document = JsonDocument.Parse(jsonBody);
        var root = document.RootElement;
        var message = new OcxMessage { Role = "assistant" };
        var text = new StringBuilder();
        var calls = new List<OcxToolCall>();
        string? finishReason = null;
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                finishReason ??= candidate.TryGetProperty("finishReason", out var reason)
                    ? reason.GetString()
                    : null;
                if (!candidate.TryGetProperty("content", out var content)
                    || !content.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textNode))
                        text.Append(textNode.GetString());
                    if (!part.TryGetProperty("functionCall", out var functionCall)) continue;
                    calls.Add(new OcxToolCall
                    {
                        Id = "call_" + Guid.NewGuid().ToString("N")[..24],
                        Function = new OcxToolCallFunction
                        {
                            Name = functionCall.TryGetProperty("name", out var nameNode)
                                ? nameNode.GetString()
                                : null,
                            Arguments = functionCall.TryGetProperty("args", out var args)
                                ? args.GetRawText()
                                : "{}"
                        }
                    });
                }
            }
        }
        message.Content = text.ToString();
        if (calls.Count > 0) message.ToolCalls = calls;
        var usage = new OcxUsage();
        if (root.TryGetProperty("usageMetadata", out var usageNode))
        {
            usage.PromptTokens = ReadLong(usageNode, "promptTokenCount");
            usage.CompletionTokens = ReadLong(usageNode, "candidatesTokenCount");
            usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
        }
        var normalizedFinish = calls.Count > 0
            ? "tool_calls"
            : string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)
                ? "length"
                : "stop";
        return new GoogleNormalizedResponse(message, normalizedFinish, usage);
    }

    public static string BuildRequestJson(OcxParsedRequest request)
    {
        var systemParts = new JsonArray();
        var contents = new JsonArray();
        foreach (var message in request.Messages)
        {
            var text = message.Content?.ToString() ?? string.Empty;
            switch (message.Role)
            {
                case "system":
                case "developer":
                {
                    if (!string.IsNullOrWhiteSpace(text))
                        systemParts.Add(new JsonObject { ["text"] = text });
                    break;
                }
                case "tool":
                {
                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["functionResponse"] = new JsonObject
                                {
                                    ["name"] = message.ToolCallId ?? "tool",
                                    ["response"] = new JsonObject { ["output"] = text }
                                }
                            }
                        }
                    });
                    break;
                }
                case "assistant" when message.ToolCalls is { Count: > 0 }:
                {
                    var parts = new JsonArray();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(new JsonObject { ["text"] = text });
                    foreach (var call in message.ToolCalls)
                    {
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = call.Function?.Name ?? string.Empty,
                                ["args"] = ParseArguments(call.Function?.Arguments)
                            }
                        });
                    }
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;
                }
                default:
                {
                    contents.Add(new JsonObject
                    {
                        ["role"] = message.Role == "assistant" ? "model" : "user",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = text } }
                    });
                    break;
                }
            }
        }

        var root = new JsonObject
        {
            ["contents"] = contents
        };
        if (systemParts.Count > 0)
            root["systemInstruction"] = new JsonObject { ["parts"] = systemParts };
        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools.Where(t => t.Function is not null))
            {
                tools.Add(new JsonObject
                {
                    ["functionDeclarations"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = tool.Function!.Name,
                            ["description"] = tool.Function.Description ?? string.Empty,
                            ["parameters"] = tool.Function.Parameters is { ValueKind: not JsonValueKind.Undefined }
                                ? JsonSerializer.SerializeToNode(tool.Function.Parameters.Value)
                                : new JsonObject()
                        }
                    }
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
        var usage = new OcxUsage();
        var sawAny = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0) continue;
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(payload).RootElement;
            }
            catch
            {
                continue;
            }
            sawAny = true;

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageValue)
                    ? messageValue.GetString() ?? "上游错误"
                    : "上游错误";
                yield return new AdapterEvent { Type = "error", Text = message };
                yield break;
            }

            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (!candidate.TryGetProperty("content", out var content)) continue;
                    if (!content.TryGetProperty("parts", out var parts)) continue;
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (!part.TryGetProperty("text", out var text)) continue;
                        var textValue = text.GetString();
                        if (!string.IsNullOrEmpty(textValue))
                            yield return new AdapterEvent { Type = "text", Text = textValue, Role = "assistant" };
                    }
                }
            }

            if (root.TryGetProperty("usageMetadata", out var usageMetadata))
            {
                usage.PromptTokens = ReadLong(usageMetadata, "promptTokenCount");
                usage.CompletionTokens = ReadLong(usageMetadata, "candidatesTokenCount");
                usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
            }
        }

        if (!sawAny)
        {
            yield return new AdapterEvent { Type = "incomplete", Text = "上游没有返回任何数据" };
            yield break;
        }
        yield return new AdapterEvent { Type = "done", Usage = usage };
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

    internal sealed record GoogleNormalizedResponse(
        OcxMessage Message,
        string FinishReason,
        OcxUsage Usage);
}
