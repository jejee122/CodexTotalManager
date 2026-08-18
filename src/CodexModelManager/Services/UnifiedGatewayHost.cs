using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Models;
using CodexOpenCodexNative.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexModelManager.Services;

/// <summary>
/// 网关进程内的轮换运行时状态：候选账号池的冷却时间、轮换起点、会话粘性与近期失败统计。
/// 只存在内存里，进程重启即清空；重启后最多多打一次被限流的请求，不会串号。
/// </summary>
internal sealed class UnifiedGatewayRotationRuntimeState
{
    internal sealed record SessionAffinity(string PoolId, DateTimeOffset ExpiresAt);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntilByPool = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _rotationCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionAffinity> _affinityBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PoolFailureWindow> _failuresByPool = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SessionAffinityLifetime = TimeSpan.FromHours(2);
    private static readonly int SessionAffinityLimit = 512;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    private sealed record PoolFailureWindow(DateTimeOffset StartedAt, long Failures, long Total);

    public int NextRotationOffset(string groupModel, int candidateCount)
    {
        lock (_gate)
        {
            var next = _rotationCounters.TryGetValue(groupModel, out var value) ? value : 0;
            _rotationCounters[groupModel] = next + 1;
            return (int)(next % candidateCount);
        }
    }

    public bool IsCoolingDown(string poolId)
    {
        lock (_gate)
        {
            return _cooldownUntilByPool.TryGetValue(poolId, out var until)
                   && until > DateTimeOffset.UtcNow;
        }
    }

    public void MarkCooldown(string poolId, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromSeconds(1);
        lock (_gate)
        {
            var until = DateTimeOffset.UtcNow + duration;
            if (_cooldownUntilByPool.TryGetValue(poolId, out var existing) && existing >= until) return;
            _cooldownUntilByPool[poolId] = until;
        }
    }

    public DateTimeOffset EarliestCooldownExpiry(IEnumerable<string> poolIds)
    {
        lock (_gate)
        {
            var earliest = DateTimeOffset.MaxValue;
            foreach (var poolId in poolIds)
            {
                if (_cooldownUntilByPool.TryGetValue(poolId, out var until)
                    && until > DateTimeOffset.UtcNow
                    && until < earliest)
                    earliest = until;
            }
            return earliest == DateTimeOffset.MaxValue ? DateTimeOffset.UtcNow : earliest;
        }
    }

    public IReadOnlyList<string> CoolingPools()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return _cooldownUntilByPool
                .Where(pair => pair.Value > now)
                .Select(pair => pair.Key)
                .OrderBy(pool => pool, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>同一 previous_response_id 的对话尽量锁定同一个账号池，避免 Responses 续聊因换号而失忆。</summary>
    public string? TryGetAffinity(string sessionKey)
    {
        lock (_gate)
        {
            if (_affinityBySession.TryGetValue(sessionKey, out var affinity))
            {
                if (affinity.ExpiresAt > DateTimeOffset.UtcNow) return affinity.PoolId;
                _affinityBySession.Remove(sessionKey);
            }
            return null;
        }
    }

    public void BindAffinity(string sessionKey, string poolId)
    {
        lock (_gate)
        {
            if (_affinityBySession.Count >= SessionAffinityLimit
                && !_affinityBySession.ContainsKey(sessionKey))
            {
                foreach (var expired in _affinityBySession
                             .Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _affinityBySession.Remove(expired);
                }
                if (_affinityBySession.Count >= SessionAffinityLimit)
                {
                    var oldest = _affinityBySession.OrderBy(pair => pair.Value.ExpiresAt).First().Key;
                    _affinityBySession.Remove(oldest);
                }
            }
            _affinityBySession[sessionKey] = new SessionAffinity(poolId, DateTimeOffset.UtcNow + SessionAffinityLifetime);
        }
    }

    /// <summary>记录一次候选结果，用于按近期失败率排序（失败多者靠后）。10 分钟滚动窗口。</summary>
    public void RecordAttempt(string poolId, bool success)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!_failuresByPool.TryGetValue(poolId, out var window)
                || now - window.StartedAt >= FailureWindow)
            {
                window = new PoolFailureWindow(now, 0, 0);
            }
            _failuresByPool[poolId] = new PoolFailureWindow(
                window.StartedAt,
                window.Failures + (success ? 0 : 1),
                window.Total + 1);
        }
    }

    /// <summary>失败率得分 0..1；没有记录的池返回 0（视为最健康）。</summary>
    public double FailureScore(string poolId)
    {
        lock (_gate)
        {
            if (!_failuresByPool.TryGetValue(poolId, out var window)) return 0;
            if (DateTimeOffset.UtcNow - window.StartedAt >= FailureWindow) return 0;
            return window.Total == 0 ? 0 : (double)window.Failures / window.Total;
        }
    }
}

public static class UnifiedGatewayHost
{
    internal const long MaximumRequestBodyBytes = 32L * 1024 * 1024;
    public const string SourceFingerprintHeader = "X-CMM-Source-Fingerprint";
    public const string ServedByHeader = "X-CMM-Served-By";
    public const string RotationGroupHeader = "X-CMM-Rotation-Group";
    public const string RotationAttemptsHeader = "X-CMM-Rotation-Attempts";
    public const string RotationExhaustedHeader = "X-CMM-Rotation-Exhausted";
    public const string RetryAfterSecondsHeader = "X-CMM-Retry-After-Seconds";
    public const int RouteGuardVersion = 4;
    private static readonly HashSet<string> AllowedProxyPaths = new(StringComparer.Ordinal)
    {
        "/v1/responses",
        "/v1/chat/completions",
        "/v1/messages"
    };
    private static readonly HashSet<string> ForwardRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Encoding",
        "User-Agent"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly SemaphoreSlim RequestLogGate = new(1, 1);

    public static async Task<int> RunAsync(string configurationPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var initial = LoadConfiguration(configurationPath);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
                options.ListenLocalhost(initial.Port);
            });
            builder.Services.AddSingleton(new UnifiedGatewayRotationRuntimeState());
            builder.Services.AddSingleton(new HttpClient(CreateUpstreamHandler())
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
                    rotationGroupCount = configuration.RotationGroups.Count,
                    coolingPools = context.RequestServices
                        .GetRequiredService<UnifiedGatewayRotationRuntimeState>()
                        .CoolingPools(),
                    clientKeyCount = new SecretStore(configuration.DataDirectory)
                        .ListInternalNames(UnifiedGatewayKeys.ClientPrefix)
                        .Count,
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

    internal static SocketsHttpHandler CreateUpstreamHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true,
        // The gateway already constrains the first URI to the configured host.
        // Following an upstream redirect would bypass that boundary and could
        // forward the real pool Authorization header to another destination.
        AllowAutoRedirect = false
    };

    private static async Task WriteModelsAsync(HttpContext context, string configurationPath)
    {
        var configuration = LoadConfiguration(configurationPath);
        if (!await AuthorizeAsync(context, configuration)) return;
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var data = configuration.Routes
            .Select(route => route.GatewayModel)
            .Concat(configuration.RotationGroups.Select(group => group.GatewayModel))
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
        if (!await AuthorizeAsync(context, configuration)) return;
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        if (!TryGetAllowedUpstreamPath(context.Request.Path.Value, out var relativePath))
        {
            await WriteOpenAiErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                "unsupported_gateway_path",
                "统一网关只允许已声明的模型接口路径。");
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
            body = await ReadRequestBodyAsync(context.Request, context.RequestAborted);
            root = JsonNode.Parse(body);
        }
        catch (RequestBodyLimitExceededException)
        {
            await WriteOpenAiErrorAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "request_too_large",
                $"请求体不能超过 {MaximumRequestBodyBytes / 1024 / 1024} MB。");
            return;
        }
        catch (Exception ex)
        {
            await WriteOpenAiErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_json", ex.Message);
            return;
        }
        if (root is not JsonObject json || !TryGetNonEmptyString(json, "model", out var requestedModel))
        {
            await WriteOpenAiErrorAsync(context, StatusCodes.Status400BadRequest, "model_required", "请求必须明确填写带号池前缀的 model。");
            return;
        }
        var suppliedSourceFingerprint = context.Request.Headers[SourceFingerprintHeader].ToString();
        var route = configuration.Routes.FirstOrDefault(item =>
            item.GatewayModel.Equals(requestedModel, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            var rotationGroup = configuration.RotationGroups.FirstOrDefault(item =>
                item.GatewayModel.Equals(requestedModel, StringComparison.OrdinalIgnoreCase));
            if (rotationGroup is null)
            {
                await WriteOpenAiErrorAsync(context, StatusCodes.Status404NotFound, "model_not_found", $"没有找到精确路由：{requestedModel}。网关不会跨号池兜底。");
                return;
            }
            if (!string.IsNullOrWhiteSpace(suppliedSourceFingerprint))
            {
                await WriteOpenAiErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "fingerprint_not_supported_for_rotation_group",
                    "轮换组请求不携带来源指纹头；实际账号来源由网关在服务端校验，并用响应头标出。");
                return;
            }
            await ProxyRotationGroupAsync(context, configuration, rotationGroup, json, relativePath);
            return;
        }
        // A supplied fingerprint is an optimistic-concurrency assertion used by
        // the Worker broker. Ordinary authenticated harnesses do not know this
        // internal value, so absence is allowed; the gateway still reloads and
        // validates the current source state before every precise request.
        var identityProtected = !string.IsNullOrWhiteSpace(route.SourceFingerprint);
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
        if (identityProtected
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

        var client = context.RequestServices.GetRequiredService<HttpClient>();
        var exactStartedAt = DateTimeOffset.UtcNow;
        var (exactResponse, exactError) = await SendUpstreamAsync(
            context, client, configuration, route, json, relativePath);
        if (exactResponse is null)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            var (errorType, errorMessage) = exactError!.Value;
            await WriteOpenAiErrorAsync(context, StatusCodes.Status502BadGateway, errorType, errorMessage);
            await AppendRequestLogAsync(configuration, context, requestedModel, group: null,
                route.PoolId, 502, 1, "send_error", exactStartedAt);
            return;
        }
        int exactStatus;
        using (exactResponse)
        {
            exactStatus = (int)exactResponse.StatusCode;
            await CopyUpstreamResponseAsync(context, exactResponse, route, group: null, attempts: 0);
        }
        await AppendRequestLogAsync(configuration, context, requestedModel, group: null,
            route.PoolId, exactStatus, 1, exactStatus is >= 200 and < 400 ? "ok" : "upstream_error", exactStartedAt);
    }

    /// <summary>
    /// 轮换组请求：按冷却状态、近期失败率与轮转起点选择候选账号池，429/401/403/5xx 时冷却当前候选并尝试下一个。
    /// 带 previous_response_id 的 Responses 请求有会话粘性：同一对话尽量锁定同一账号，避免换号后续聊失忆；
    /// 粘性账号限流时才切号，切换后把粘性重新绑到新账号。每个候选发送前都重新执行与精确路由相同的服务端来源校验。
    /// </summary>
    private static async Task ProxyRotationGroupAsync(
        HttpContext context,
        UnifiedGatewayConfiguration configuration,
        UnifiedGatewayRotationGroup group,
        JsonObject payload,
        string relativePath)
    {
        var state = context.RequestServices.GetRequiredService<UnifiedGatewayRotationRuntimeState>();
        var client = context.RequestServices.GetRequiredService<HttpClient>();
        var candidates = new List<UnifiedGatewayRoute>();
        foreach (var candidateModel in group.Candidates)
        {
            var candidate = configuration.Routes.FirstOrDefault(route =>
                route.GatewayModel.Equals(candidateModel, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) candidates.Add(candidate);
        }
        if (candidates.Count == 0)
        {
            await WriteOpenAiErrorAsync(
                context, StatusCodes.Status503ServiceUnavailable,
                "rotation_no_candidate", "轮换组在当前配置里没有可用候选路由。");
            await AppendRequestLogAsync(configuration, context, group.GatewayModel, group: null,
                poolId: null, status: 503, attempts: 0, outcome: "no_candidate", startedAt: DateTimeOffset.UtcNow);
            return;
        }

        var sessionKey = ExtractSessionKey(payload);
        var stickyPoolId = sessionKey is null ? null : state.TryGetAffinity(sessionKey);
        var eligible = candidates.Where(candidate => !state.IsCoolingDown(candidate.PoolId)).ToList();
        if (eligible.Count == 0)
        {
            var earliest = state.EarliestCooldownExpiry(candidates.Select(candidate => candidate.PoolId));
            var retryAfter = Math.Max(1, (int)Math.Ceiling(Math.Max(0, (earliest - DateTimeOffset.UtcNow).TotalSeconds)));
            context.Response.Headers[RetryAfterSecondsHeader] =
                retryAfter.ToString(CultureInfo.InvariantCulture);
            await WriteOpenAiErrorAsync(
                context, StatusCodes.Status503ServiceUnavailable,
                "rotation_all_candidates_cooling_down",
                $"轮换组所有账号都在冷却中，最早约 {retryAfter} 秒后恢复。");
            await AppendRequestLogAsync(configuration, context, group.GatewayModel, group,
                poolId: null, status: 503, attempts: 0, outcome: "all_cooling_down", startedAt: DateTimeOffset.UtcNow);
            return;
        }

        // 候选顺序：粘性账号最优先，其余按近期失败率升序、同分再按轮转起点错开，尽量均摊负载。
        var offset = state.NextRotationOffset(group.GatewayModel, eligible.Count);
        var ordered = eligible
            .OrderBy(candidate => stickyPoolId is not null
                                  && candidate.PoolId.Equals(stickyPoolId, StringComparison.OrdinalIgnoreCase)
                ? 0 : 1)
            .ThenBy(candidate => state.FailureScore(candidate.PoolId))
            .ThenBy(candidate => candidates.IndexOf(candidate) >= offset
                ? candidates.IndexOf(candidate) - offset
                : candidates.IndexOf(candidate) + candidates.Count - offset)
            .ToArray();

        var startedAt = DateTimeOffset.UtcNow;
        HttpResponseMessage? lastFailureResponse = null;
        UnifiedGatewayRoute? lastFailureRoute = null;
        var attempts = 0;
        var attemptErrors = new List<string>();
        foreach (var candidate in ordered)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            attempts++;
            var validationError = ValidateRotationRouteServerSide(configuration, candidate);
            if (validationError is not null)
            {
                attemptErrors.Add($"{candidate.PoolId}:validation:{validationError}");
                state.MarkCooldown(candidate.PoolId, TimeSpan.FromMinutes(10));
                state.RecordAttempt(candidate.PoolId, success: false);
                continue;
            }
            var (response, sendError) = await SendUpstreamAsync(
                context, client, configuration, candidate, payload, relativePath);
            if (response is null)
            {
                if (context.RequestAborted.IsCancellationRequested) return;
                var (sendErrorType, sendErrorMessage) = sendError!.Value;
                attemptErrors.Add($"{candidate.PoolId}:send:{sendErrorType}:{sendErrorMessage}");
                state.MarkCooldown(candidate.PoolId, TimeSpan.FromSeconds(30));
                state.RecordAttempt(candidate.PoolId, success: false);
                continue;
            }
            if (IsRotatableStatus(response.StatusCode))
            {
                state.MarkCooldown(candidate.PoolId, CooldownFor(response));
                state.RecordAttempt(candidate.PoolId, success: false);
                lastFailureResponse?.Dispose();
                lastFailureResponse = response;
                lastFailureRoute = candidate;
                continue;
            }
            state.RecordAttempt(candidate.PoolId, success: true);
            if (sessionKey is not null) state.BindAffinity(sessionKey, candidate.PoolId);
            int servedStatus;
            using (response)
            {
                servedStatus = (int)response.StatusCode;
                await CopyUpstreamResponseAsync(context, response, candidate, group, attempts);
            }
            await AppendRequestLogAsync(configuration, context, group.GatewayModel, group,
                candidate.PoolId, servedStatus, attempts, "ok", startedAt);
            return;
        }

        if (lastFailureResponse is not null && lastFailureRoute is not null)
        {
            context.Response.Headers[RotationExhaustedHeader] = "true";
            int exhaustedStatus;
            using (lastFailureResponse)
            {
                exhaustedStatus = (int)lastFailureResponse.StatusCode;
                await CopyUpstreamResponseAsync(context, lastFailureResponse, lastFailureRoute, group, attempts);
            }
            await AppendRequestLogAsync(configuration, context, group.GatewayModel, group,
                lastFailureRoute.PoolId, exhaustedStatus, attempts, "exhausted", startedAt);
            return;
        }
        await WriteOpenAiErrorAsync(
            context, StatusCodes.Status503ServiceUnavailable,
            "rotation_no_candidate",
            "轮换组候选全部校验失败或发送失败，本次没有可用账号。详情：" +
            string.Join(" | ", attemptErrors.Take(4)));
        await AppendRequestLogAsync(configuration, context, group.GatewayModel, group,
            poolId: null, status: 503, attempts, outcome: "no_candidate", startedAt);
    }

    /// <summary>Responses 请求带 previous_response_id 时视为有状态对话，作为粘性键；Chat/Messages 每次带全量历史，无需粘性。</summary>
    private static string? ExtractSessionKey(JsonObject payload)
    {
        if (TryGetNonEmptyString(payload, "previous_response_id", out var sessionId))
            return "resp:" + sessionId;
        return null;
    }

    private static bool TryGetNonEmptyString(JsonObject payload, string propertyName, out string value)
    {
        value = string.Empty;
        if (payload[propertyName] is not JsonValue node
            || !node.TryGetValue<string>(out var parsed)
            || string.IsNullOrWhiteSpace(parsed))
            return false;
        value = parsed.Trim();
        return true;
    }

    /// <summary>网关请求账本：每行一条 JSON，记录时间、调用方钥匙、模型、实际扣费账号、状态与结果。失败不影响代理。</summary>
    private static async Task AppendRequestLogAsync(
        UnifiedGatewayConfiguration configuration,
        HttpContext context,
        string model,
        UnifiedGatewayRotationGroup? group,
        string? poolId,
        int status,
        int attempts,
        string outcome,
        DateTimeOffset startedAt)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = startedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                client = context.Items.TryGetValue(ClientLabelItem, out var label)
                    ? label as string ?? "unknown"
                    : "unknown",
                method = context.Request.Method,
                path = context.Request.Path.Value,
                model,
                group = group?.GatewayModel,
                poolId,
                status,
                attempts,
                outcome,
                durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            }, JsonOptions);
            await RequestLogGate.WaitAsync(CancellationToken.None);
            try
            {
                await File.AppendAllTextAsync(
                    Path.Combine(configuration.DataDirectory, "unified-gateway-request-log.jsonl"),
                    line + "\n",
                    CancellationToken.None);
            }
            finally
            {
                RequestLogGate.Release();
            }
        }
        catch
        {
            // 记账失败绝不影响代理本身。
        }
    }

    /// <summary>轮换候选的服务端来源校验：与精确路由相同，只是不需要客户端指纹头。</summary>
    private static string? ValidateRotationRouteServerSide(
        UnifiedGatewayConfiguration configuration,
        UnifiedGatewayRoute route)
    {
        try
        {
            if (ValidateFreshWorkerSource(configuration, route) is { Length: > 0 } sourceError)
                return sourceError;
            if (route.SourceKind.Equals(SubagentSourceKind.CliProxyPool.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                var currentCredentialIdentity = CliCredentialIdentity.Read(configuration.DataDirectory, route.PoolId);
                if (currentCredentialIdentity is null
                    || !SubagentSourceIdentity.FixedTimeEquals(route.CredentialIdentity, currentCredentialIdentity))
                    return "CLIProxy 唯一账号身份已变化或无法验证，已在发送上游请求前停止。";
            }
            return null;
        }
        catch
        {
            return "无法重新验证最新来源状态，已按失败关闭且未发送上游请求。";
        }
    }

    private static bool IsRotatableStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode is HttpStatusCode.TooManyRequests
                   or HttpStatusCode.Unauthorized
                   or HttpStatusCode.Forbidden
               || code is >= 500 and <= 599;
    }

    private static TimeSpan CooldownFor(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            if (response.Headers.TryGetValues("Retry-After", out var values))
            {
                foreach (var value in values)
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));
                }
            }
            return TimeSpan.FromSeconds(120);
        }
        return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? TimeSpan.FromMinutes(15)
            : TimeSpan.FromSeconds(30);
    }

    private static async Task<(HttpResponseMessage? Response, (string ErrorType, string ErrorMessage)? Error)> SendUpstreamAsync(
        HttpContext context,
        HttpClient client,
        UnifiedGatewayConfiguration configuration,
        UnifiedGatewayRoute route,
        JsonObject payload,
        string relativePath)
    {
        payload["model"] = route.UpstreamModel;
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions));
        Uri target;
        try
        {
            target = BuildConstrainedUpstreamUri(
                route.BaseUrl,
                relativePath,
                context.Request.QueryString.Value);
        }
        catch (Exception ex)
        {
            return (null, ("invalid_upstream_endpoint", ex.Message));
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        foreach (var header in context.Request.Headers)
        {
            if (!ForwardRequestHeaders.Contains(header.Key)) continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        try
        {
            if (route.SecretName?.Equals(
                    UnifiedGatewayKeys.NativeEngineAdmissionRouteSecretName,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                var admission = ReadNativeEngineAdmission(configuration, route);
                request.Headers.TryAddWithoutValidation("X-CMM-Admission", $"Bearer {admission}");
            }
            else if (!string.IsNullOrWhiteSpace(route.SecretName))
            {
                var upstreamKey = new SecretStore(configuration.DataDirectory).Read(route.SecretName)
                                  ?? throw new InvalidOperationException($"路由 {route.PoolId} 缺少上游 API Key。");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstreamKey);
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            return (response, null);
        }
        catch (Exception ex)
        {
            return (null, ("upstream_error", ex.Message));
        }
    }

    private static async Task<byte[]> ReadRequestBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumRequestBodyBytes)
            throw new RequestBodyLimitExceededException();

        using var buffer = new MemoryStream(
            request.ContentLength is > 0
                ? checked((int)Math.Min(request.ContentLength.Value, MaximumRequestBodyBytes))
                : 0);
        var chunk = new byte[81_920];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumRequestBodyBytes)
                throw new RequestBodyLimitExceededException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private static async Task CopyUpstreamResponseAsync(
        HttpContext context,
        HttpResponseMessage response,
        UnifiedGatewayRoute route,
        UnifiedGatewayRotationGroup? group,
        int attempts)
    {
        try
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response.Headers, context.Response.Headers);
            CopyResponseHeaders(response.Content.Headers, context.Response.Headers);
            context.Response.Headers.Remove("transfer-encoding");
            context.Response.Headers[ServedByHeader] = route.PoolId;
            if (group is not null)
            {
                context.Response.Headers[RotationGroupHeader] = group.GatewayModel;
                context.Response.Headers[RotationAttemptsHeader] =
                    attempts.ToString(CultureInfo.InvariantCulture);
            }
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception ex)
        {
            // 与既有网关行为一致：复制中途失败时，若响应尚未开始则报 502，否则视为客户端已离开、静默结束。
            if (!context.Response.HasStarted)
                await WriteOpenAiErrorAsync(context, StatusCodes.Status502BadGateway, "upstream_error", ex.Message);
        }
    }

    private sealed class RequestBodyLimitExceededException : IOException
    {
    }

    /// <summary>成功匹配的客户端 label 会放进 HttpContext.Items[ClientLabelItem]，供请求日志记账。</summary>
    public const string ClientLabelItem = "cmm.gateway.client-label";

    public static async Task<bool> AuthorizeAsync(
        HttpContext context,
        UnifiedGatewayConfiguration configuration)
    {
        var secrets = new SecretStore(configuration.DataDirectory);
        var supplied = context.Request.Headers.Authorization.ToString();
        if (supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) supplied = supplied[7..].Trim();
        else supplied = string.Empty;
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            // 主钥匙（兼容历史，label=master）始终有效；每个 harness 的独立钥匙单独校验、可单独吊销。
            var master = secrets.ReadInternal(UnifiedGatewayKeys.MasterSecretName);
            if (!string.IsNullOrWhiteSpace(master) && FixedTimeEquals(master, supplied))
            {
                context.Items[ClientLabelItem] = UnifiedGatewayKeys.MasterLabel;
                return true;
            }
            foreach (var internalName in secrets.ListInternalNames(UnifiedGatewayKeys.ClientPrefix))
            {
                var label = UnifiedGatewayKeys.LabelForSecretName(internalName);
                if (label is null) continue;
                var key = secrets.ReadInternal(internalName);
                if (!string.IsNullOrWhiteSpace(key) && FixedTimeEquals(key, supplied))
                {
                    context.Items[ClientLabelItem] = label;
                    return true;
                }
            }
        }
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            "{\"error\":{\"type\":\"authentication_error\",\"message\":\"API Key 不正确。\"}}",
            context.RequestAborted);
        return false;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    internal static bool TryGetAllowedUpstreamPath(string? requestPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrEmpty(requestPath)
            || requestPath.Contains('\\')
            || requestPath.Contains("//", StringComparison.Ordinal)
            || requestPath.Contains('%')
            || requestPath.Contains("..", StringComparison.Ordinal)
            || !AllowedProxyPaths.Contains(requestPath))
            return false;
        relativePath = requestPath[4..];
        return true;
    }

    internal static Uri BuildConstrainedUpstreamUri(
        string configuredBaseUrl,
        string relativePath,
        string? queryString)
    {
        if (relativePath is not ("responses" or "chat/completions" or "messages")
            || relativePath.Contains('\\')
            || relativePath.Contains("//", StringComparison.Ordinal)
            || relativePath.Contains('%')
            || relativePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("上游接口路径不在允许清单中。");
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var upstreamBase)
            || upstreamBase.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(upstreamBase.Host)
            || !string.IsNullOrEmpty(upstreamBase.UserInfo)
            || !string.IsNullOrEmpty(upstreamBase.Query)
            || !string.IsNullOrEmpty(upstreamBase.Fragment))
            throw new InvalidOperationException("上游基础地址不安全或格式不正确。");

        var basePath = upstreamBase.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(upstreamBase)
        {
            Path = $"{basePath}/{relativePath}",
            Query = (queryString ?? string.Empty).TrimStart('?'),
            Fragment = string.Empty
        };
        var target = builder.Uri;
        if (!target.Scheme.Equals(upstreamBase.Scheme, StringComparison.OrdinalIgnoreCase)
            || !target.Host.Equals(upstreamBase.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != upstreamBase.Port
            || !string.IsNullOrEmpty(target.UserInfo)
            || !target.AbsolutePath.Equals($"{basePath}/{relativePath}", StringComparison.Ordinal))
            throw new InvalidOperationException("上游目标越过了已配置的主机边界。");
        return target;
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
            else if (route.SourceKind.Equals(
                         SubagentSourceKind.OpenAiCompatible.ToString(),
                         StringComparison.OrdinalIgnoreCase))
            {
                return ValidateFreshNativeProvider(configuration, route);
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

    private static string? ValidateFreshNativeProvider(
        UnifiedGatewayConfiguration configuration,
        UnifiedGatewayRoute route)
    {
        if (!string.Equals(
                route.SecretName,
                UnifiedGatewayKeys.NativeEngineAdmissionRouteSecretName,
                StringComparison.OrdinalIgnoreCase)
            || !route.SourceId.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            return "第三方来源没有绑定总管家本机引擎准入，已停止请求。";

        var providerId = route.SourceId["custom:".Length..];
        if (string.IsNullOrWhiteSpace(providerId)
            || !route.RoutePrefix.Equals(providerId + "/", StringComparison.OrdinalIgnoreCase)
            || !route.UpstreamModel.StartsWith(providerId + "/", StringComparison.OrdinalIgnoreCase))
            return "第三方来源的名称空间与来源身份不一致，已停止请求。";

        var nativeRoot = Path.Combine(configuration.DataDirectory, "native-proxy");
        var native = new NativeProxyConfigStore(nativeRoot).Load();
        var matches = native.Providers.Where(provider =>
            provider.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Disabled)
            return "第三方来源已停用、移除或重复，已停止请求。";

        var provider = matches[0];
        var modelId = route.UpstreamModel[(providerId.Length + 1)..];
        if (!route.Adapter.Equals(provider.Adapter, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(modelId)
            || !(provider.Models.Contains(modelId, StringComparer.OrdinalIgnoreCase)
                 || string.Equals(provider.DefaultModel, modelId, StringComparison.OrdinalIgnoreCase)))
            return "第三方来源的接口类型或模型名单已经变化，已停止请求并要求重新同步。";

        var expectedEndpoint = $"http://127.0.0.1:{native.ListenPort}/v1";
        if (!SubagentSourceIdentity.NormalizeEndpoint(route.BaseUrl).Equals(
                SubagentSourceIdentity.NormalizeEndpoint(expectedEndpoint),
                StringComparison.OrdinalIgnoreCase))
            return "第三方来源不再指向当前总管家本机引擎，已停止请求。";

        var currentFingerprint = SubagentSourceIdentity.Compute(
            route.SourceId,
            SubagentSourceKind.OpenAiCompatible.ToString(),
            expectedEndpoint,
            provider.Adapter,
            UnifiedGatewayKeys.NativeEngineAdmissionRouteSecretName,
            route.RoutePrefix);
        return SubagentSourceIdentity.FixedTimeEquals(route.SourceFingerprint, currentFingerprint)
            ? null
            : "第三方来源身份已经变化，已停止请求并要求重新授权。";
    }

    private static string ReadNativeEngineAdmission(
        UnifiedGatewayConfiguration configuration,
        UnifiedGatewayRoute route)
    {
        var native = new NativeProxyConfigStore(
            Path.Combine(configuration.DataDirectory, "native-proxy")).Load();
        if (string.IsNullOrWhiteSpace(native.AdmissionToken))
            throw new InvalidOperationException("总管家本机引擎缺少准入令牌。");
        var expectedEndpoint = $"http://127.0.0.1:{native.ListenPort}/v1";
        if (!SubagentSourceIdentity.NormalizeEndpoint(route.BaseUrl).Equals(
                SubagentSourceIdentity.NormalizeEndpoint(expectedEndpoint),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("第三方路由没有指向当前总管家本机引擎，拒绝附加准入令牌。");
        return native.AdmissionToken;
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
        if (configuration.RotationGroups is null)
            throw new InvalidOperationException("统一网关轮换组目录缺失。");
        if (ValidateRotationGroups(configuration) is { Length: > 0 } groupError)
            throw new InvalidOperationException(groupError);
        return configuration;
    }

    /// <summary>轮换组静态校验：命名唯一、不与精确路由冲突、候选存在且上游模型一致。失败关闭。</summary>
    internal static string? ValidateRotationGroups(UnifiedGatewayConfiguration configuration)
    {
        var routeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in configuration.Routes) routeNames.Add(route.GatewayModel);
        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in configuration.RotationGroups)
        {
            if (string.IsNullOrWhiteSpace(group.GatewayModel) || ContainsControlCharacters(group.GatewayModel))
                return "轮换组缺少稳定模型名，或名称包含不允许的控制字符。";
            if (!seenGroups.Add(group.GatewayModel))
                return $"轮换组名称重复：{group.GatewayModel}。";
            if (routeNames.Contains(group.GatewayModel))
                return $"轮换组名称与精确路由冲突：{group.GatewayModel}。";
            if (string.IsNullOrWhiteSpace(group.UpstreamModel))
                return $"轮换组 {group.GatewayModel} 缺少上游模型。";
            if (group.Candidates is null || group.Candidates.Count == 0)
                return $"轮换组 {group.GatewayModel} 没有候选路由。";
            var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in group.Candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)
                    || !seenCandidates.Add(candidate)
                    || ContainsControlCharacters(candidate))
                    return $"轮换组 {group.GatewayModel} 存在空、含控制字符或重复的候选。";
                var route = configuration.Routes.FirstOrDefault(item =>
                    item.GatewayModel.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (route is null)
                    return $"轮换组 {group.GatewayModel} 的候选 {candidate} 不在路由目录中。";
                if (!route.UpstreamModel.Equals(group.UpstreamModel, StringComparison.OrdinalIgnoreCase))
                    return $"轮换组 {group.GatewayModel} 的候选 {candidate} 上游模型与组声明不一致。";
            }
        }
        return null;
    }

    private static bool ContainsControlCharacters(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character)) return true;
        }
        return false;
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
