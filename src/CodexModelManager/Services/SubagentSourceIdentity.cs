using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public static class SubagentSourceIdentity
{
    public const string OpenAiChatAdapter = "openai-chat";
    private static readonly Regex SafeSourceId = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeRoutePrefix = new(
        "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}/$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string CliSourceId(string poolId) => $"gateway-cli:{poolId}";
    public static string NativeSourceId(string poolId) => $"native:{poolId}";

    public static string ComputeForPool(
        PoolDefinition pool,
        string sourceId,
        SubagentSourceKind kind,
        string routePrefix,
        string adapter,
        string? credentialSlot = null,
        string? credentialIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return Compute(
            sourceId,
            kind.ToString(),
            NormalizeEndpoint(pool.Transport == PoolTransport.CliProxyApi
                ? PoolCatalogService.BuildCliBaseUrl(pool)
                : pool.BaseUrl),
            adapter,
            credentialSlot ?? pool.ProviderId ?? string.Empty,
            routePrefix,
            credentialIdentity ?? string.Empty);
    }

    public static string Compute(
        string sourceId,
        string sourceKind,
        string endpoint,
        string adapter,
        string credentialSlot,
        string routePrefix,
        string credentialIdentity = "")
    {
        var normalizedSourceId = sourceId ?? string.Empty;
        var normalizedRoutePrefix = routePrefix ?? string.Empty;
        if (!SafeSourceId.IsMatch(normalizedSourceId))
            throw new InvalidOperationException("子代理来源 ID 格式不安全。");
        if (!SafeRoutePrefix.IsMatch(normalizedRoutePrefix))
            throw new InvalidOperationException("子代理来源路由前缀格式不安全。");
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        var canonical = string.Join("\n", new[]
        {
            "cmm-source-v1",
            normalizedSourceId.Trim().ToLowerInvariant(),
            (sourceKind ?? string.Empty).Trim().ToLowerInvariant(),
            normalizedEndpoint,
            (adapter ?? string.Empty).Trim().ToLowerInvariant(),
            (credentialSlot ?? string.Empty).Trim(),
            (credentialIdentity ?? string.Empty).Trim().ToLowerInvariant(),
            normalizedRoutePrefix.Trim().ToLowerInvariant()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string NormalizeEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !uri.IsLoopback)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("子代理来源端点必须是 HTTPS，或本机回环 HTTP，且不能包含账号、查询参数或片段。");

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };
        var path = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path) ? "/" : path;
        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped).TrimEnd('/');
    }

    public static bool IsRouteIdentityValid(UnifiedGatewayRoute? route)
    {
        if (route is null || string.IsNullOrWhiteSpace(route.SourceFingerprint)) return false;
        try
        {
            var expected = Compute(
                route.SourceId,
                route.SourceKind,
                route.BaseUrl,
                route.Adapter,
                route.SecretName ?? string.Empty,
                route.RoutePrefix,
                route.CredentialIdentity);
            return FixedTimeEquals(expected, route.SourceFingerprint);
        }
        catch
        {
            return false;
        }
    }

    public static bool FixedTimeEquals(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return false;
        var left = Encoding.UTF8.GetBytes(expected.Trim());
        var right = Encoding.UTF8.GetBytes(actual.Trim());
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
