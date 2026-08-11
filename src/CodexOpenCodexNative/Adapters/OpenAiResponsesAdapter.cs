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
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
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
        if (hasConfiguredKey)
            httpRequest.Headers.Authorization = new("Bearer", provider.ApiKey);
        if (request.ForwardHeaders is { Count: > 0 })
        {
            foreach (var (name, value) in request.ForwardHeaders)
            {
                if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    // 配置了 provider 密钥时，入站 Authorization 不得覆盖上游凭据；
                    // 仅在 provider 无密钥时透传入站认证（OAuth 转发模式）。
                    if (!hasConfiguredKey)
                    {
                        httpRequest.Headers.Remove("Authorization");
                        httpRequest.Headers.TryAddWithoutValidation("Authorization", value);
                    }
                    continue;
                }
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

    private static string BuildUrl(ProviderDefinition provider)
    {
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        if (baseUrl.EndsWith("/backend-api/codex", StringComparison.OrdinalIgnoreCase))
            return baseUrl + "/responses";
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/responses"
            : baseUrl + "/v1/responses";
    }

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
                    ["content"] = JsonSerializer.SerializeToNode(message.Content ?? string.Empty)
                });
            }
            else if (message.Role is "tool")
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ToolCallId ?? string.Empty,
                    ["output"] = JsonSerializer.SerializeToNode(message.Content ?? string.Empty)
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
                    ["content"] = JsonSerializer.SerializeToNode(message.Content ?? string.Empty)
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
            root["tools"] = JsonSerializer.SerializeToNode(request.Tools);
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
                    if (!string.IsNullOrEmpty(delta)) deltas.Append(delta);
                    break;
                }
                case "response.output_text.done":
                {
                    var done = ReadString(root, "text");
                    if (!string.IsNullOrEmpty(done)) doneText.Append(done);
                    break;
                }
                case "response.completed":
                {
                    snapshot = ExtractText(root.TryGetProperty("response", out var response) ? response : root);
                    usage = ExtractUsage(root.TryGetProperty("response", out var responseUsage) ? responseUsage : root);
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

        var text = snapshot.Length > 0 ? snapshot : doneText.Length > 0 ? doneText.ToString() : deltas.ToString();
        if (text.Length > 0)
            yield return new AdapterEvent { Type = "text", Text = text, Role = "assistant" };
        if (!sawCompleted)
        {
            yield return new AdapterEvent { Type = "incomplete", Text = "上游流未以 response.completed 结束" };
            yield break;
        }
        yield return new AdapterEvent { Type = "done", Usage = usage };
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "output_text") continue;
            var text = item.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
            if (!string.IsNullOrEmpty(text)) builder.Append(text);
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
}
