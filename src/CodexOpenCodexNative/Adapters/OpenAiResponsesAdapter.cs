using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Adapters;

public sealed class OpenAiResponsesAdapter : IProviderAdapter
{
    private readonly HttpClient _http;
    public OpenAiResponsesAdapter(HttpClient? http = null)
    {
        _http = http ?? AdapterHttpTransport.Shared;
    }

    public string AdapterId => "openai-responses";

    public async Task<AdapterResponse> FetchAsync(
        ProviderDefinition provider,
        OcxParsedRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        var url = BuildUrl(provider);
        var body = request.RawBody ?? RebuildRequestJson(request, modelId);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        var hasConfiguredKey = !string.IsNullOrWhiteSpace(provider.ApiKey);
        var officialPassThrough = IsOfficialCodexProvider(provider);
        if (hasConfiguredKey)
            httpRequest.Headers.Authorization = new("Bearer", provider.ApiKey);
        if (!hasConfiguredKey && !officialPassThrough)
            throw new InvalidOperationException(
                $"自定义 Provider {provider.Id} 没有独立 API Key；已拒绝借用 Codex/ChatGPT 登录凭据。");
        if (request.ForwardHeaders is { Count: > 0 })
        {
            foreach (var (name, value) in request.ForwardHeaders)
            {
                if (!officialPassThrough) continue;
                if (!OfficialForwardHeaders.Contains(name)) continue;
                if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) && hasConfiguredKey)
                    continue;
                httpRequest.Headers.Remove(name);
                httpRequest.Headers.TryAddWithoutValidation(name, value);
            }
        }

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
            if (officialPassThrough)
            {
                return new AdapterResponse
                {
                    Streaming = true,
                    ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/event-stream",
                    RawStream = stream,
                    Owner = response
                };
            }
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

    internal static ResponsesNormalizedResponse ParseNonStreamingResponse(string jsonBody)
    {
        using var document = JsonDocument.Parse(jsonBody);
        var root = document.RootElement;
        var message = new OcxMessage { Role = "assistant" };
        var text = new StringBuilder();
        var calls = new List<OcxToolCall>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = ReadString(item, "type");
                if (type == "function_call")
                {
                    calls.Add(new OcxToolCall
                    {
                        Id = ReadString(item, "call_id") ?? ReadString(item, "id"),
                        Function = new OcxToolCallFunction
                        {
                            Name = ReadString(item, "name"),
                            Arguments = ReadString(item, "arguments") ?? "{}"
                        }
                    });
                    continue;
                }
                if (type != "message" || !item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textNode))
                        text.Append(textNode.GetString());
                }
            }
        }
        message.Content = text.ToString();
        if (calls.Count > 0) message.ToolCalls = calls;
        var usage = ExtractUsage(root) ?? new OcxUsage();
        var status = ReadString(root, "status");
        var finishReason = calls.Count > 0
            ? "tool_calls"
            : string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase)
                ? "length"
                : "stop";
        return new ResponsesNormalizedResponse(message, finishReason, usage);
    }

    private static readonly HashSet<string> OfficialForwardHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "chatgpt-account-id",
        "openai-beta",
        "originator",
        "session_id",
        "session-id",
        "thread-id",
        "x-client-request-id",
        "x-codex-beta-features",
        "x-codex-installation-id",
        "x-codex-parent-thread-id",
        "x-codex-turn-metadata",
        "x-codex-turn-state",
        "x-codex-window-id",
        "x-oai-attestation",
        "x-openai-subagent",
        "x-responsesapi-include-timing-metrics"
    };

    internal static bool IsOfficialCodexProvider(ProviderDefinition provider) =>
        provider.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
        && TryBuildResponsesUri(provider.BaseUrl, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443
        && uri.AbsolutePath.Equals("/backend-api/codex/responses", StringComparison.Ordinal);

    internal static bool TryBuildResponsesUri(string baseUrl, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        var basePath = parsed.AbsolutePath.TrimEnd('/');
        var suffix = basePath.EndsWith("/backend-api/codex", StringComparison.OrdinalIgnoreCase)
                     || basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "/responses"
            : "/v1/responses";
        var builder = new UriBuilder(parsed)
        {
            Path = basePath + suffix,
            Query = string.Empty,
            Fragment = string.Empty
        };
        uri = builder.Uri;
        return uri.Scheme.Equals(parsed.Scheme, StringComparison.OrdinalIgnoreCase)
               && uri.Host.Equals(parsed.Host, StringComparison.OrdinalIgnoreCase)
               && uri.Port == parsed.Port;
    }

    private static Uri BuildUrl(ProviderDefinition provider) =>
        TryBuildResponsesUri(provider.BaseUrl, out var uri)
            ? uri
            : throw new InvalidOperationException($"Provider {provider.Id} 的上游地址不安全或格式不正确。");

    private static string RebuildRequestJson(OcxParsedRequest request, string modelId)
    {
        var input = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role is "developer")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "developer",
                    ["content"] = OcxMessageContent.ToResponsesNode(message.Content)
                });
            }
            else if (message.Role is "tool")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ToolCallId ?? string.Empty,
                    ["output"] = OcxMessageContent.ExtractText(message.Content)
                });
            }
            else if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.Id ?? string.Empty,
                        ["name"] = call.Function?.Name ?? string.Empty,
                        ["arguments"] = call.Function?.Arguments ?? string.Empty
                    });
                }
            }
            else
            {
                input.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = message.Role,
                    ["content"] = OcxMessageContent.ToResponsesNode(message.Content)
                });
            }
        }

        var root = new JsonObject
        {
            ["model"] = modelId,
            ["input"] = input,
            ["stream"] = request.Stream
        };
        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools.Where(item => item.Function is not null))
            {
                var function = tool.Function!;
                var definition = new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    ["parameters"] = function.Parameters is { ValueKind: not JsonValueKind.Undefined }
                        ? JsonSerializer.SerializeToNode(function.Parameters.Value)
                        : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                };
                if (function.Strict is not null) definition["strict"] = function.Strict;
                tools.Add(definition);
            }
            if (tools.Count > 0) root["tools"] = tools;
        }
        if (request.ToolChoice is { ValueKind: not JsonValueKind.Undefined })
            root["tool_choice"] = JsonSerializer.SerializeToNode(request.ToolChoice.Value);
        if (request.ParallelToolCalls is not null)
            root["parallel_tool_calls"] = request.ParallelToolCalls;
        if (request.Reasoning is { ValueKind: not JsonValueKind.Undefined })
            root["reasoning"] = JsonSerializer.SerializeToNode(request.Reasoning.Value);
        if (request.Temperature is not null)
            root["temperature"] = request.Temperature;
        if (request.MaxTokens is not null)
            root["max_output_tokens"] = request.MaxTokens;
        if (!string.IsNullOrWhiteSpace(request.PreviousResponseId))
            root["previous_response_id"] = request.PreviousResponseId;
        return root.ToJsonString();
    }

    private static async IAsyncEnumerable<AdapterEvent> ParseSseStream(
        Stream raw,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(raw, Encoding.UTF8);
        var deltas = new StringBuilder();
        var doneText = new StringBuilder();
        var snapshot = string.Empty;
        OcxUsage? usage = null;
        var sawCompleted = false;
        var tools = new Dictionary<string, PendingResponseToolCall>(StringComparer.Ordinal);
        var toolOrder = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(payload).RootElement;
            }
            catch
            {
                continue;
            }
            if (root.ValueKind != JsonValueKind.Object) continue;
            var type = ReadString(root, "type");

            switch (type)
            {
                case "response.output_text.delta":
                {
                    var delta = ReadString(root, "delta");
                    if (!string.IsNullOrEmpty(delta))
                    {
                        deltas.Append(delta);
                        yield return new AdapterEvent
                        {
                            Type = "text",
                            Text = delta,
                            Role = "assistant"
                        };
                    }
                    break;
                }
                case "response.output_text.done":
                {
                    var done = ReadString(root, "text");
                    if (!string.IsNullOrEmpty(done)) doneText.Append(done);
                    break;
                }
                case "response.output_item.added":
                {
                    if (!root.TryGetProperty("item", out var item)
                        || item.ValueKind != JsonValueKind.Object
                        || !string.Equals(ReadString(item, "type"), "function_call", StringComparison.Ordinal))
                        break;
                    var itemId = ReadString(item, "id");
                    var callId = ReadString(item, "call_id");
                    var key = !string.IsNullOrWhiteSpace(itemId)
                        ? itemId
                        : !string.IsNullOrWhiteSpace(callId)
                            ? callId
                            : "tool-" + toolOrder.Count;
                    var pending = new PendingResponseToolCall(
                        toolOrder.Count,
                        itemId,
                        callId,
                        ReadString(item, "name"));
                    var initialArguments = ReadString(item, "arguments");
                    if (!string.IsNullOrEmpty(initialArguments))
                        pending.Arguments.Append(initialArguments);
                    tools[key] = pending;
                    toolOrder.Add(key);
                    yield return new AdapterEvent
                    {
                        Type = "function_call",
                        CallId = pending.CallId,
                        FunctionName = pending.Name,
                        Arguments = initialArguments,
                        ToolCallIndex = pending.Index
                    };
                    break;
                }
                case "response.function_call_arguments.delta":
                {
                    var key = ResolveToolKey(root, tools, toolOrder);
                    if (key is null || !tools.TryGetValue(key, out var pending)) break;
                    var delta = ReadString(root, "delta");
                    if (!string.IsNullOrEmpty(delta)) pending.Arguments.Append(delta);
                    yield return new AdapterEvent
                    {
                        Type = "function_call",
                        Arguments = delta,
                        ToolCallIndex = pending.Index
                    };
                    break;
                }
                case "response.function_call_arguments.done":
                {
                    var key = ResolveToolKey(root, tools, toolOrder);
                    if (key is null || !tools.TryGetValue(key, out var pending)) break;
                    var finalArguments = ReadString(root, "arguments");
                    if (!string.IsNullOrEmpty(finalArguments))
                    {
                        pending.Arguments.Clear();
                        pending.Arguments.Append(finalArguments);
                    }
                    if (!pending.Done)
                    {
                        pending.Done = true;
                        yield return new AdapterEvent
                        {
                            Type = "function_call_done",
                            CallId = pending.CallId,
                            FunctionName = pending.Name,
                            Arguments = pending.Arguments.ToString(),
                            ToolCallIndex = pending.Index
                        };
                    }
                    break;
                }
                case "response.completed":
                {
                    var completed = root.TryGetProperty("response", out var response) ? response : root;
                    snapshot = ExtractText(completed);
                    usage = ExtractUsage(completed);
                    foreach (var completedCall in ExtractToolCalls(completed))
                    {
                        var existing = tools.Values.FirstOrDefault(candidate =>
                            (!string.IsNullOrWhiteSpace(completedCall.ItemId)
                             && string.Equals(candidate.ItemId, completedCall.ItemId, StringComparison.Ordinal))
                            || (!string.IsNullOrWhiteSpace(completedCall.CallId)
                                && string.Equals(candidate.CallId, completedCall.CallId, StringComparison.Ordinal)));
                        if (existing is not null)
                        {
                            if (!existing.Done)
                            {
                                existing.Done = true;
                                yield return new AdapterEvent
                                {
                                    Type = "function_call_done",
                                    CallId = existing.CallId,
                                    FunctionName = existing.Name,
                                    Arguments = completedCall.Arguments,
                                    ToolCallIndex = existing.Index
                                };
                            }
                            continue;
                        }
                        yield return new AdapterEvent
                        {
                            Type = "function_call",
                            CallId = completedCall.CallId,
                            FunctionName = completedCall.Name,
                            Arguments = completedCall.Arguments,
                            ToolCallIndex = completedCall.Index
                        };
                        yield return new AdapterEvent
                        {
                            Type = "function_call_done",
                            CallId = completedCall.CallId,
                            FunctionName = completedCall.Name,
                            Arguments = completedCall.Arguments,
                            ToolCallIndex = completedCall.Index
                        };
                    }
                    sawCompleted = true;
                    break;
                }
                case "response.failed":
                case "error":
                {
                    var message = ErrorMessage(root.TryGetProperty("response", out var failed) ? failed : root);
                    yield return new AdapterEvent { Type = "error", Text = message };
                    yield break;
                }
                case "response.incomplete":
                {
                    var reason = ErrorMessage(root.TryGetProperty("response", out var incomplete) ? incomplete : root);
                    yield return new AdapterEvent { Type = "incomplete", Text = reason };
                    yield break;
                }
            }
        }

        var text = doneText.Length > 0 ? doneText.ToString() : snapshot;
        if (deltas.Length == 0 && text.Length > 0)
            yield return new AdapterEvent { Type = "text", Text = text, Role = "assistant" };
        if (!sawCompleted)
        {
            yield return new AdapterEvent { Type = "incomplete", Text = "上游流未以 response.completed 结束" };
            yield break;
        }
        yield return new AdapterEvent { Type = "done", Usage = usage };
    }

    private static string? ResolveToolKey(
        JsonElement root,
        IReadOnlyDictionary<string, PendingResponseToolCall> tools,
        IReadOnlyList<string> order)
    {
        var itemId = ReadString(root, "item_id");
        if (!string.IsNullOrWhiteSpace(itemId) && tools.ContainsKey(itemId)) return itemId;
        if (root.TryGetProperty("output_index", out var outputIndex)
            && outputIndex.TryGetInt32(out var index)
            && index >= 0
            && index < order.Count)
            return order[index];
        return order.Count == 1 ? order[0] : null;
    }

    private static IReadOnlyList<CompletedResponseToolCall> ExtractToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return Array.Empty<CompletedResponseToolCall>();
        var calls = new List<CompletedResponseToolCall>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !string.Equals(ReadString(item, "type"), "function_call", StringComparison.Ordinal))
                continue;
            calls.Add(new CompletedResponseToolCall(
                calls.Count,
                ReadString(item, "id"),
                ReadString(item, "call_id"),
                ReadString(item, "name"),
                ReadString(item, "arguments") ?? string.Empty));
        }
        return calls;
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            var type = ReadString(item, "type");
            if (type == "output_text")
            {
                var directText = ReadString(item, "text");
                if (!string.IsNullOrEmpty(directText)) builder.Append(directText);
                continue;
            }
            if (type != "message"
                || !item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object
                    || ReadString(part, "type") is not ("output_text" or "text"))
                    continue;
                var nestedText = ReadString(part, "text");
                if (!string.IsNullOrEmpty(nestedText)) builder.Append(nestedText);
            }
        }
        return builder.ToString();
    }

    private static OcxUsage? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        return new OcxUsage
        {
            PromptTokens = ReadLong(usage, "input_tokens"),
            CompletionTokens = ReadLong(usage, "output_tokens"),
            TotalTokens = ReadLong(usage, "total_tokens")
        };
    }

    private static string ErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        if (root.TryGetProperty("last_error", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            var message = last.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        return "上游响应失败";
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

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private sealed class PendingResponseToolCall(
        int index,
        string? itemId,
        string? callId,
        string? name)
    {
        public int Index { get; } = index;
        public string? ItemId { get; } = itemId;
        public string? CallId { get; } = callId;
        public string? Name { get; } = name;
        public StringBuilder Arguments { get; } = new();
        public bool Done { get; set; }
    }

    private sealed record CompletedResponseToolCall(
        int Index,
        string? ItemId,
        string? CallId,
        string? Name,
        string Arguments);

    internal sealed record ResponsesNormalizedResponse(
        OcxMessage Message,
        string FinishReason,
        OcxUsage Usage);
}
