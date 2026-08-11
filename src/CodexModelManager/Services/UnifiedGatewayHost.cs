using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexModelManager.Services;

public static class UnifiedGatewayHost
{
    private const string AdmissionSecretName = "unified-gateway:client";
    public const string SourceFingerprintHeader = "X-CMM-Source-Fingerprint";
    public const int RouteGuardVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string configurationPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var initial = LoadConfiguration(configurationPath);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(initial.Port));
            builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                EnableMultipleHttp2Connections = true
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            });

            var app = builder.Build();
            app.MapGet("/health", async context =>
            {
                var configuration = LoadConfiguration(configurationPath);
                await WriteJsonAsync(context, StatusCodes.Status200OK, new
                {
                    product = "CodexTotalManager",
                    productVersion = typeof(UnifiedGatewayHost).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(UnifiedGatewayHost).Assembly.GetName().Version?.ToString()
                        ?? "unknown",
                    service = configuration.Service,
                    status = "ok",
                    routeGuardVersion = RouteGuardVersion,
                    routeCount = configuration.Routes.Count,
                    port = configuration.Port,
                    configurationFingerprint = configuration.ConfigurationFingerprint,
                    pid = Environment.ProcessId
                });
            });
            app.MapGet("/v1/models", context => WriteModelsAsync(context, configurationPath));
            app.MapMethods(
                "/v1/{**path}",
                new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" },
                context => ProxyAsync(context, configurationPath));

            await app.RunAsync(cancellationToken);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static async Task WriteModelsAsync(HttpContext context, string configurationPath)
    {
        var configuration = LoadConfiguration(configurationPath);
        if (!Authorize(context, configuration)) return;
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var data = configuration.Routes
            .Select(route => route.GatewayModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .Select(model => new { id = model, @object = "model", created, owned_by = "codex-total-manager" })
            .ToArray();
        await WriteJsonAsync(context, StatusCodes.Status200OK, new { @object = "list", data });
    }

    private static async Task ProxyAsync(HttpContext context, string configurationPath)
    {
        UnifiedGatewayConfiguration configuration;
        try { configuration = LoadConfiguration(configurationPath); }
        catch (Exception ex)
        {
            await WriteOpenAiErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "gateway_configuration_error", ex.Message);
            return;
        }
        if (!Authorize(context, configuration)) return;
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        if (!context.Request.HasJsonContentType())
        {
            await WriteOpenAiErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "统一网关目前只接受 JSON API 请求。");
            return;
        }

        byte[] body;
        JsonNode? root;
        try
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = buffer.ToArray();
            root = JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            await WriteOpenAiErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_json", ex.Message);
            return;
        }
        if (root is not JsonObject json || json["model"]?.GetValue<string>() is not { Length: > 0 } requestedModel)
        {
            await WriteOpenAiErrorAsync(context, StatusCodes.Status400BadRequest, "model_required", "请求必须明确填写带号池前缀的 model。");
            return;
        }
        var route = configuration.Routes.FirstOrDefault(item =>
            item.GatewayModel.Equals(requestedModel, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            await WriteOpenAiErrorAsync(context, StatusCodes.Status404NotFound, "model_not_found", $"没有找到精确路由：{requestedModel}。网关不会跨号池兜底。");
            return;
        }
        var suppliedSourceFingerprint = context.Request.Headers[SourceFingerprintHeader].ToString();
        // 所有带来源指纹的路由都必须携带指纹头（含 NativeCodexAccount），缺失即 401。
        var identityProtected = !string.IsNullOrWhiteSpace(route.SourceFingerprint);
        if (identityProtected && string.IsNullOrWhiteSpace(suppliedSourceFingerprint))
        {
            await WriteOpenAiErrorAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "source_fingerprint_required",
                "该路由受来源身份保护，请求必须携带来源指纹头。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(suppliedSourceFingerprint)
            && !SubagentSourceIdentity.FixedTimeEquals(route.SourceFingerprint, suppliedSourceFingerprint))
        {
            await WriteOpenAiErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "source_identity_changed",
                "获准的来源身份与当前精确路由不一致，已在发送上游请求前停止。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(suppliedSourceFingerprint)
            && ValidateFreshWorkerSource(configuration, route) is { Length: > 0 } sourceError)
        {
            await WriteOpenAiErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "source_state_changed",
                sourceError);
            return;
        }
        if (route.SourceKind.Equals(SubagentSourceKind.CliProxyPool.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            var currentCredentialIdentity = CliCredentialIdentity.Read(
                configuration.DataDirectory, route.PoolId);
            if (currentCredentialIdentity is null
                || !SubagentSourceIdentity.FixedTimeEquals(
                    route.CredentialIdentity, currentCredentialIdentity))
            {
                await WriteOpenAiErrorAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "credential_identity_changed",
                    "CLIProxy 唯一账号身份已变化或无法验证，已在发送上游请求前停止。");
                return;
            }
        }

        json["model"] = route.UpstreamModel;
        body = Encoding.UTF8.GetBytes(json.ToJsonString(JsonOptions));
        var relativePath = context.Request.Path.Value?.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) == true
            ? context.Request.Path.Value[4..]
            : context.Request.Path.Value?.TrimStart('/') ?? string.Empty;
        var upstreamBase = new Uri(route.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var target = new Uri(upstreamBase, relativePath + context.Request.QueryString.Value);

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        foreach (var header in context.Request.Headers)
        {
            if (IsHopByHop(header.Key) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                                        || header.Key.Equals(SourceFingerprintHeader, StringComparison.OrdinalIgnoreCase)
                                        || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                                        || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                                        || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(route.SecretName))
            {
                var upstreamKey = new SecretStore(configuration.DataDirectory).Read(route.SecretName)
                                  ?? throw new InvalidOperationException($"路由 {route.PoolId} 缺少上游 API Key。");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstreamKey);
            }
            var client = context.RequestServices.GetRequiredService<HttpClient>();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response.Headers, context.Response.Headers);
            CopyResponseHeaders(response.Content.Headers, context.Response.Headers);
            context.Response.Headers.Remove("transfer-encoding");
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller went away. There is no response left to write.
        }
        catch (Exception ex)
        {
            if (!context.Response.HasStarted)
                await WriteOpenAiErrorAsync(context, StatusCodes.Status502BadGateway, "upstream_error", ex.Message);
        }
    }

    private static bool Authorize(HttpContext context, UnifiedGatewayConfiguration configuration)
    {
        var expected = new SecretStore(configuration.DataDirectory).ReadInternal(AdmissionSecretName);
        var supplied = context.Request.Headers.Authorization.ToString();
        if (supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) supplied = supplied[7..].Trim();
        else supplied = string.Empty;
        if (!string.IsNullOrWhiteSpace(expected) && FixedTimeEquals(expected, supplied)) return true;
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.WriteAsync("{\"error\":{\"type\":\"authentication_error\",\"message\":\"API Key 不正确。\"}}").GetAwaiter().GetResult();
        return false;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string? ValidateFreshWorkerSource(
        UnifiedGatewayConfiguration configuration,
        UnifiedGatewayRoute route)
    {
        try
        {
            PoolDefinition? pool;
            SubagentSourceKind kind;
            if (route.SourceKind.Equals(
                         SubagentSourceKind.CliProxyPool.ToString(),
                         StringComparison.OrdinalIgnoreCase))
            {
                kind = SubagentSourceKind.CliProxyPool;
                var configured = PoolCatalogService.FindFreshInDirectory(
                    configuration.DataDirectory, route.PoolId);
                pool = configured?.Transport == PoolTransport.CliProxyApi ? configured : null;
                if (pool?.Transport != PoolTransport.CliProxyApi || pool.Enabled != true) pool = null;
            }
            else
            {
                return "该来源类型没有获准作为外部纯文本工人。";
            }

            if (pool is null) return "来源已停用、移除或最新号池身份无效，已停止请求。";
            var currentFingerprint = SubagentSourceIdentity.ComputeForPool(
                pool,
                route.SourceId,
                kind,
                route.RoutePrefix,
                route.Adapter,
                route.SecretName,
                route.CredentialIdentity);
            return SubagentSourceIdentity.FixedTimeEquals(
                route.SourceFingerprint, currentFingerprint)
                ? null
                : "来源端点、provider、凭据槽或启用状态已变化，已停止请求并要求重新授权。";
        }
        catch
        {
            return "无法重新验证最新号池状态，已按失败关闭且未发送上游请求。";
        }
    }

    private static UnifiedGatewayConfiguration LoadConfiguration(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var configuration = JsonSerializer.Deserialize<UnifiedGatewayConfiguration>(stream, JsonOptions)
                            ?? throw new InvalidOperationException("统一网关配置为空。");
        if (configuration.Service != "codex-unified-gateway")
            throw new InvalidOperationException("统一网关配置标识不正确。");
        if (configuration.Port is < 1024 or > 65535)
            throw new InvalidOperationException("统一网关端口不正确。");
        if (configuration.Routes is null)
            throw new InvalidOperationException("统一网关路由目录为空。");
        if (configuration.SchemaVersion < 4)
            throw new InvalidOperationException("统一网关配置缺少来源身份保护，请先在总管家中重新同步。");
        if (!UnifiedGatewayConfigurationIdentity.Matches(configuration))
            throw new InvalidOperationException("统一网关配置指纹不一致，已拒绝启动或继续代理。");
        if (configuration.Routes.Any(route => !SubagentSourceIdentity.IsRouteIdentityValid(route)))
            throw new InvalidOperationException("统一网关存在来源身份指纹不完整或已变化的路由。");
        var collisions = configuration.Routes
            .GroupBy(route => route.GatewayModel, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (collisions.Length > 0)
            throw new InvalidOperationException($"统一网关存在重复模型路由：{string.Join("、", collisions)}");
        return configuration;
    }

    private static void CopyResponseHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (IsHopByHop(header.Key)) continue;
            destination[header.Key] = header.Value.ToArray();
        }
    }

    private static bool IsHopByHop(string name) => name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static Task WriteOpenAiErrorAsync(HttpContext context, int statusCode, string type, string message) =>
        WriteJsonAsync(context, statusCode, new { error = new { type, message } });

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, object value)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, JsonOptions, context.RequestAborted);
    }
}
