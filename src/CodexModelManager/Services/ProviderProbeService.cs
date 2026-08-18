using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class ProviderProbeService
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumModelCount = 10_000;
    private const int MaximumPaginationPages = 100;
    private readonly HashSet<int> _managedLoopbackPorts;

    public ProviderProbeService(IEnumerable<int>? managedLoopbackPorts = null)
    {
        _managedLoopbackPorts = managedLoopbackPorts?.ToHashSet()
                                ?? new HashSet<int> { LocalPortPolicy.DefaultNativeEnginePort };
    }

    public async Task<ProbeResult> ProbeAsync(
        string enteredUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
        => await ProbeAsync(enteredUrl, apiKey, "openai-chat", cancellationToken);

    public async Task<ProbeResult> ProbeAsync(
        string enteredUrl,
        string apiKey,
        string adapter,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请填写这个模型来源自己的 API Key。总管家不会借用 Codex 登录凭据。");
        if (adapter is not "openai-chat" and not "openai-responses" and not "anthropic" and not "google")
            throw new InvalidOperationException("这个接口类型暂时不受支持。");
        if (!Uri.TryCreate(enteredUrl.Trim(), UriKind.Absolute, out var input)
            || (input.Scheme != Uri.UriSchemeHttps && input.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("URL 必须以 https:// 或 http:// 开头。");
        if (!string.IsNullOrEmpty(input.UserInfo))
            throw new InvalidOperationException("URL 里不能带用户名或密码。");
        if (!string.IsNullOrEmpty(input.Query) || !string.IsNullOrEmpty(input.Fragment))
            throw new InvalidOperationException("URL 里不能带 ?参数 或 #片段，请只填写模型接口地址。");
        if (input.Scheme == Uri.UriSchemeHttp && !IsLocalAddress(input.Host))
            throw new InvalidOperationException("远程地址必须使用 https://，否则 API Key 可能泄露。");
        if (IsLocalAddress(input.Host) && _managedLoopbackPorts.Contains(input.Port))
            throw new InvalidOperationException("不能把总管家本机引擎自己当成上游模型地址，否则会形成死循环。");

        var candidates = BuildCandidates(input, adapter);
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            using var handler = CreateHandler();
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pageUrl = candidate.ModelsUrl;
                var totalBytes = 0;
                for (var pageNumber = 0; pageNumber < MaximumPaginationPages; pageNumber++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    ApplyAuthentication(request, apiKey.Trim(), adapter);
                    using var response = await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                    var payload = await ReadBoundedPayloadAsync(
                        response.Content,
                        MaximumResponseBytes - totalBytes,
                        cancellationToken);
                    totalBytes += payload.ByteCount;
                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"{pageUrl} 返回 {(int)response.StatusCode}");
                        collected.Clear();
                        break;
                    }

                    var page = ParseModelPage(payload.Text, adapter);
                    foreach (var model in page.Models)
                    {
                        collected.Add(model);
                        if (collected.Count > MaximumModelCount)
                            throw new InvalidOperationException("模型名单超过 10,000 个，已停止读取。 ");
                    }

                    var nextPage = BuildNextPageUrl(candidate.ModelsUrl, adapter, page);
                    if (nextPage is null)
                    {
                        if (collected.Count == 0)
                        {
                            errors.Add($"{candidate.ModelsUrl} 没有返回可对话的模型名单");
                            break;
                        }
                        return new ProbeResult(
                            candidate.BaseUrl,
                            collected.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                            stopwatch.ElapsedMilliseconds);
                    }
                    pageUrl = nextPage;
                }
                if (collected.Count > 0)
                    errors.Add($"{candidate.ModelsUrl} 分页超过 {MaximumPaginationPages} 页，已拒绝保存不完整名单");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{candidate.ModelsUrl} 连接超时");
            }
            catch (HttpRequestException ex)
            {
                errors.Add($"{candidate.ModelsUrl} 无法连接：{ex.Message}");
            }
            catch (JsonException)
            {
                errors.Add($"{candidate.ModelsUrl} 返回的不是模型名单");
            }
        }

        var detail = errors.Count == 0 ? "没有找到可用的 /models 接口。" : string.Join("；", errors);
        throw new InvalidOperationException($"连接测试没有通过：{detail}");
    }

    internal static IReadOnlyList<(string BaseUrl, string ModelsUrl)> BuildCandidates(Uri input, string adapter)
    {
        var normalized = input.ToString().TrimEnd('/');
        if (adapter == "google")
        {
            var baseUrl = TrimKnownSuffix(normalized, "/v1beta/models", "/v1beta");
            return new[] { (baseUrl, baseUrl + "/v1beta/models?pageSize=1000") };
        }
        if (adapter == "anthropic")
        {
            var baseUrl = TrimKnownSuffix(normalized, "/v1/models", "/v1", "/models");
            return new[] { (baseUrl, baseUrl + "/v1/models?limit=1000") };
        }
        if (input.Host.Equals("api.perplexity.ai", StringComparison.OrdinalIgnoreCase)
            && input.AbsolutePath.TrimEnd('/') is "" or "/v1" or "/models" or "/v1/models")
        {
            // Perplexity exposes model discovery at /v1/models, while chat
            // inference remains /chat/completions on the origin (without /v1).
            var origin = new UriBuilder(input) { Path = string.Empty, Query = string.Empty, Fragment = string.Empty }
                .Uri.ToString().TrimEnd('/');
            return new[] { (origin, origin + "/v1/models") };
        }
        if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = normalized[..^"/models".Length];
            return new[] { (baseUrl, normalized) };
        }

        var candidates = new List<(string, string)> { (normalized, normalized + "/models") };
        if (!normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            candidates.Add((normalized + "/v1", normalized + "/v1/models"));
        return candidates.Distinct().ToArray();
    }

    internal static void ApplyAuthentication(HttpRequestMessage request, string apiKey, string adapter)
    {
        switch (adapter)
        {
            case "anthropic":
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            case "google":
                request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                break;
        }
    }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        // API credentials must never follow a redirect to another host. Bearer
        // is commonly stripped by .NET, but provider-specific x-api-key and
        // x-goog-api-key headers are not guaranteed to be removed.
        AllowAutoRedirect = false
    };

    internal static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        CancellationToken cancellationToken)
        => (await ReadBoundedPayloadAsync(content, MaximumResponseBytes, cancellationToken)).Text;

    private static async Task<BoundedPayload> ReadBoundedPayloadAsync(
        HttpContent content,
        int remainingBytes,
        CancellationToken cancellationToken)
    {
        if (remainingBytes <= 0 || content.Headers.ContentLength is > 0
            && content.Headers.ContentLength > remainingBytes)
            throw new InvalidOperationException("模型名单响应超过 4 MB，已停止读取。");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > remainingBytes)
                throw new InvalidOperationException("模型名单响应超过 4 MB，已停止读取。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var byteCount = checked((int)destination.Length);
        return new BoundedPayload(
            System.Text.Encoding.UTF8.GetString(destination.GetBuffer(), 0, byteCount),
            byteCount);
    }

    private static string TrimKnownSuffix(string value, params string[] suffixes)
    {
        foreach (var suffix in suffixes.OrderByDescending(item => item.Length))
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return value[..^suffix.Length].TrimEnd('/');
        }
        return value;
    }

    internal static ModelPage ParseModelPage(string jsonText, string adapter)
    {
        using var json = JsonDocument.Parse(jsonText);
        JsonElement list;
        if (json.RootElement.ValueKind == JsonValueKind.Array)
        {
            list = json.RootElement;
        }
        else if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            list = data;
        }
        else if (json.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            list = models;
        }
        else
        {
            return new ModelPage(Array.Empty<string>(), null, null, false);
        }

        var ids = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (adapter == "google"
                && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("supportedGenerationMethods", out var methods)
                && methods.ValueKind == JsonValueKind.Array
                && !methods.EnumerateArray().Any(method =>
                    method.ValueKind == JsonValueKind.String
                    && method.GetString()?.Equals("generateContent", StringComparison.OrdinalIgnoreCase) == true))
                continue;

            string? id = null;
            if (item.ValueKind == JsonValueKind.String) id = item.GetString();
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String)
                    id = idValue.GetString();
                else if (item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
                {
                    id = nameValue.GetString();
                    if (id?.StartsWith("models/", StringComparison.OrdinalIgnoreCase) == true)
                        id = id["models/".Length..];
                }
            }
            if (!string.IsNullOrWhiteSpace(id) && id.Trim().Length <= 200)
                ids.Add(id.Trim());
        }

        var nextPageToken = json.RootElement.ValueKind == JsonValueKind.Object
                            && json.RootElement.TryGetProperty("nextPageToken", out var nextPage)
                            && nextPage.ValueKind == JsonValueKind.String
            ? nextPage.GetString()
            : null;
        var hasMore = json.RootElement.ValueKind == JsonValueKind.Object
                      && json.RootElement.TryGetProperty("has_more", out var hasMoreNode)
                      && hasMoreNode.ValueKind is JsonValueKind.True or JsonValueKind.False
                      && hasMoreNode.GetBoolean();
        var lastId = json.RootElement.ValueKind == JsonValueKind.Object
                     && json.RootElement.TryGetProperty("last_id", out var lastIdNode)
                     && lastIdNode.ValueKind == JsonValueKind.String
            ? lastIdNode.GetString()
            : ids.LastOrDefault();
        return new ModelPage(
            ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            nextPageToken,
            lastId,
            hasMore);
    }

    private static string? BuildNextPageUrl(string firstPageUrl, string adapter, ModelPage page)
    {
        if (adapter == "google" && !string.IsNullOrWhiteSpace(page.NextPageToken))
        {
            var builder = new UriBuilder(firstPageUrl)
            {
                Query = $"pageSize=1000&pageToken={Uri.EscapeDataString(page.NextPageToken)}"
            };
            return builder.Uri.ToString();
        }
        if (adapter == "anthropic" && page.HasMore && !string.IsNullOrWhiteSpace(page.LastId))
        {
            var builder = new UriBuilder(firstPageUrl)
            {
                Query = $"limit=1000&after_id={Uri.EscapeDataString(page.LastId)}"
            };
            return builder.Uri.ToString();
        }
        return null;
    }

    private sealed record BoundedPayload(string Text, int ByteCount);
    internal sealed record ModelPage(
        IReadOnlyList<string> Models,
        string? NextPageToken,
        string? LastId,
        bool HasMore);

    private static bool IsLocalAddress(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1";
}
