using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class ProviderProbeService
{
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
    {
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
            throw new InvalidOperationException("不能把 OpenCodex 自己当成上游模型地址，否则会形成死循环。");

        var candidates = BuildCandidates(input);
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await http.GetAsync(candidate.ModelsUrl, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{candidate.ModelsUrl} 返回 {(int)response.StatusCode}");
                    continue;
                }
                var models = ParseModels(body);
                if (models.Count == 0)
                {
                    errors.Add($"{candidate.ModelsUrl} 没有返回模型名单");
                    continue;
                }
                return new ProbeResult(candidate.BaseUrl, models, stopwatch.ElapsedMilliseconds);
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

    private static IReadOnlyList<(string BaseUrl, string ModelsUrl)> BuildCandidates(Uri input)
    {
        var normalized = input.ToString().TrimEnd('/');
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

    private static IReadOnlyList<string> ParseModels(string jsonText)
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
            return Array.Empty<string>();
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            string? id = null;
            if (item.ValueKind == JsonValueKind.String) id = item.GetString();
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String)
                    id = idValue.GetString();
                else if (item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
                    id = nameValue.GetString()?.Replace("models/", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
        }
        return ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsLocalAddress(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "::1";
}
