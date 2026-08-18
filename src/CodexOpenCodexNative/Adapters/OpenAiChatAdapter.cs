using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexOpenCodexNative.Models;

namespace CodexOpenCodexNative.Adapters;

public sealed class OpenAiChatAdapter : IProviderAdapter
{
    private readonly HttpClient _http;

    public OpenAiChatAdapter(HttpClient? http = null)
    {
        _http = http ?? AdapterHttpTransport.Shared;
    }

    public string AdapterId => "openai-chat";

    public async Task<AdapterResponse> FetchAsync(
        ProviderDefinition provider,
        OcxParsedRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        var url = provider.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = BuildRequestJson(provider, request, modelId);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            httpRequest.Headers.Authorization = new("Bearer", provider.ApiKey);

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
                Events = ParseSseStream(stream, modelId, cancellationToken),
                Owner = response
            };
        }

        var jsonBody = await response.Content.ReadAsStringAsync(cancellationToken);
        ChatCompletionResponse? normalized = null;
        try { normalized = JsonSerializer.Deserialize<ChatCompletionResponse>(jsonBody); }
        catch (JsonException) { }
        var choice = normalized?.Choices.FirstOrDefault();
        return new AdapterResponse
        {
            Streaming = false,
            ContentType = "application/json",
            JsonBody = jsonBody,
            Message = choice?.Message,
            FinishReason = choice?.FinishReason,
            Usage = normalized?.Usage,
            Owner = response
        };
    }

    public static string BuildRequestJson(ProviderDefinition provider, OcxParsedRequest request, string modelId)
    {
        var root = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = SerializeMessages(request.Messages),
            ["stream"] = request.Stream
        };
        if (request.Tools is { Count: > 0 })
            root["tools"] = JsonSerializer.SerializeToNode(request.Tools);
        if (request.ToolChoice is { ValueKind: not JsonValueKind.Undefined })
            root["tool_choice"] = JsonSerializer.SerializeToNode(request.ToolChoice);
        if (request.ParallelToolCalls is not null)
            root["parallel_tool_calls"] = request.ParallelToolCalls;
        if (request.Temperature is not null)
            root["temperature"] = request.Temperature;
        if (request.MaxTokens is not null)
            root["max_tokens"] = request.MaxTokens;
        if (request.ExtraBody is { Count: > 0 })
        {
            foreach (var (key, value) in request.ExtraBody)
            {
                if (!root.ContainsKey(key))
                    root[key] = JsonSerializer.SerializeToNode(value);
            }
        }
        return root.ToJsonString();
    }

    private static JsonArray SerializeMessages(List<OcxMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject { ["role"] = message.Role };
            node["content"] = OcxMessageContent.ToChatNode(message.Content);
            if (message.ToolCalls is { Count: > 0 })
                node["tool_calls"] = JsonSerializer.SerializeToNode(message.ToolCalls);
            if (message.ToolCallId is not null)
                node["tool_call_id"] = message.ToolCallId;
            if (message.Name is not null)
                node["name"] = message.Name;
            array.Add(node);
        }
        return array;
    }

    private static async IAsyncEnumerable<AdapterEvent> ParseSseStream(
        Stream raw,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(raw, Encoding.UTF8);
        var pendingToolCalls = new Dictionary<int, PendingToolCall>();
        var pendingReasoning = new StringBuilder();
        var hasReasoning = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                yield return new AdapterEvent { Type = "done" };
                yield break;
            }
            ChatCompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(payload);
            }
            catch
            {
                continue;
            }
            if (chunk is null) continue;
            if (chunk.Usage is not null)
                yield return new AdapterEvent { Type = "usage", Usage = chunk.Usage };
            if (chunk.Choices is { Count: 0 }) continue;

            var delta = chunk.Choices[0].Delta;
            if (delta.Content is not null)
                yield return new AdapterEvent { Type = "text", Text = delta.Content, Role = delta.Role };
            if (delta.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in delta.ToolCalls)
                {
                    var index = toolCall.Index;
                    var id = toolCall.Id;
                    var name = toolCall.Function.Name;
                    var arguments = toolCall.Function.Arguments;
                    if (!pendingToolCalls.TryGetValue(index, out var pending))
                    {
                        pending = new PendingToolCall(index);
                        pendingToolCalls[index] = pending;
                    }
                    if (!string.IsNullOrEmpty(id))
                        pending.CallId = id;
                    if (!string.IsNullOrEmpty(name))
                        pending.Name = name;
                    if (!string.IsNullOrEmpty(arguments))
                        pending.Arguments.Append(arguments);
                    yield return new AdapterEvent
                    {
                        Type = "function_call",
                        CallId = id,
                        FunctionName = name,
                        Arguments = arguments,
                        ToolCallIndex = index
                    };
                }
            }
            if (chunk.Choices[0].FinishReason is not null)
            {
                if (pendingToolCalls.Count > 0)
                {
                    foreach (var call in pendingToolCalls.Values.OrderBy(call => call.Index))
                    {
                        yield return new AdapterEvent
                        {
                            Type = "function_call_done",
                            CallId = call.CallId,
                            FunctionName = call.Name,
                            Arguments = call.Arguments.ToString(),
                            ToolCallIndex = call.Index
                        };
                    }
                    pendingToolCalls.Clear();
                }
                if (hasReasoning)
                {
                    yield return new AdapterEvent { Type = "reasoning_done", Reasoning = pendingReasoning.ToString() };
                    pendingReasoning.Clear();
                    hasReasoning = false;
                }
                yield return new AdapterEvent
                {
                    Type = "finish",
                    FinishReason = chunk.Choices[0].FinishReason
                };
            }
        }
    }

    private static string BuildErrorBody(int status, string upstreamBody) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                message = $"上游返回 {(int)status}：{Truncate(upstreamBody, 500)}",
                type = "upstream_error",
                code = (int)status
            }
        });

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed class PendingToolCall(int index)
    {
        public int Index { get; } = index;
        public string? CallId { get; set; }
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}
