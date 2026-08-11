using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public interface IExternalWorkerConfigurationSource
{
    IReadOnlyList<SubagentRoleDefinition> Roles { get; }
    string? LoadWarning { get; }
    SubagentConfigurationDocument LoadDraft();
}

public interface IExternalWorkerBackend
{
    IReadOnlyList<string> ReadConfiguredModels();
    Task<ExternalWorkerBackendResponse> CompleteAsync(
        ExternalWorkerBackendRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SubagentExternalWorkerConfigurationSource : IExternalWorkerConfigurationSource
{
    private readonly SubagentConfigurationService _service;

    public SubagentExternalWorkerConfigurationSource(SubagentConfigurationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public IReadOnlyList<SubagentRoleDefinition> Roles => _service.Roles;
    public string? LoadWarning => _service.LoadWarning;
    public SubagentConfigurationDocument LoadDraft() => _service.LoadDraft();
}

public sealed class UnifiedGatewayExternalWorkerBackend : IExternalWorkerBackend
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly UnifiedGatewayService _gateway;
    private readonly HttpClient _httpClient;

    public UnifiedGatewayExternalWorkerBackend(UnifiedGatewayService gateway, HttpClient? httpClient = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public IReadOnlyList<string> ReadConfiguredModels() =>
        _gateway.ReadConfiguredModelCatalog();

    public async Task<ExternalWorkerBackendResponse> CompleteAsync(
        ExternalWorkerBackendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gatewayStatus = await _gateway.EnsureExternalWorkerReadyAsync(
            request.SourceId,
            request.ExpectedSourceFingerprint,
            request.Model,
            cancellationToken);
        if (!gatewayStatus.Running)
            throw new ExternalWorkerException("gateway_not_ready", "本机统一网关尚未就绪。");
        if (!gatewayStatus.Models.Contains(request.Model, StringComparer.OrdinalIgnoreCase))
            throw new ExternalWorkerException("model_not_ready", "所选外部模型不在当前统一网关目录中。");
        var endpoint = new Uri(_gateway.Url.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
        if (!endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != _gateway.Port
            || !IPAddress.TryParse(endpoint.Host, out var address)
            || !IPAddress.IsLoopback(address))
            throw new ExternalWorkerException("unsafe_gateway_endpoint", "外部纯文本工人只允许调用本机统一网关。");

        var payload = new
        {
            model = request.Model,
            stream = false,
            max_tokens = request.MaxOutputTokens,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是 Codex 总管家的纯文本子工人。你没有文件系统、命令、网络或其他工具权限；"
                              + "不得声称已经修改、运行或验证任何外部对象。只返回分析、草稿、检查清单或建议。\n\n"
                              + $"角色：{request.RoleId}\n角色说明：{request.RoleInstructions}"
                },
                new
                {
                    role = "user",
                    content = string.IsNullOrWhiteSpace(request.Context)
                        ? $"任务：\n{request.Task}"
                        : $"任务：\n{request.Task}\n\n只读上下文：\n{request.Context}"
                }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _gateway.GetClientKey());
        message.Headers.TryAddWithoutValidation(
            UnifiedGatewayHost.SourceFingerprintHeader,
            request.ExpectedSourceFingerprint);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalWorkerException("upstream_timeout", "外部工人请求超过 5 分钟，已取消。", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalWorkerException("gateway_unreachable", "本机统一网关当前无法连接。", null, ex);
        }

        using (response)
        {
            var body = await ReadLimitedBodyAsync(response.Content, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new ExternalWorkerException(
                    $"gateway_http_{(int)response.StatusCode}",
                    $"外部模型路由返回 HTTP {(int)response.StatusCode}。",
                    (int)response.StatusCode);

            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0
                    || !choices[0].TryGetProperty("message", out var responseMessage)
                    || !responseMessage.TryGetProperty("content", out var contentNode))
                    throw new JsonException("choices[0].message.content missing");

                var content = ReadContent(contentNode);
                if (string.IsNullOrWhiteSpace(content))
                    throw new JsonException("empty content");
                var finishReason = choices[0].TryGetProperty("finish_reason", out var finishNode)
                                   && finishNode.ValueKind == JsonValueKind.String
                    ? finishNode.GetString()
                    : null;
                var usage = ReadUsage(root);
                var resolvedModel = root.TryGetProperty("model", out var modelNode)
                                    && modelNode.ValueKind == JsonValueKind.String
                    ? modelNode.GetString() ?? request.Model
                    : request.Model;
                return new ExternalWorkerBackendResponse(
                    content, finishReason, usage, (int)response.StatusCode, resolvedModel);
            }
            catch (JsonException ex)
            {
                throw new ExternalWorkerException(
                    "invalid_gateway_response",
                    "外部模型路由返回了无法识别的响应。",
                    (int)response.StatusCode,
                    ex);
            }
        }
    }

    private static async Task<byte[]> ReadLimitedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new ExternalWorkerException("gateway_response_too_large", "外部模型路由响应超过安全上限。");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > MaximumResponseBytes)
                throw new ExternalWorkerException("gateway_response_too_large", "外部模型路由响应超过安全上限。");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                parts.Add(item.GetString() ?? string.Empty);
            else if (item.ValueKind == JsonValueKind.Object
                     && item.TryGetProperty("text", out var text)
                     && text.ValueKind == JsonValueKind.String)
                parts.Add(text.GetString() ?? string.Empty);
        }
        return string.Join(string.Empty, parts);
    }

    private static ExternalWorkerTokenUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return new ExternalWorkerTokenUsage(null, null, null);
        return new ExternalWorkerTokenUsage(
            ReadOptionalInt32(usage, "prompt_tokens"),
            ReadOptionalInt32(usage, "completion_tokens"),
            ReadOptionalInt32(usage, "total_tokens"));
    }

    private static int? ReadOptionalInt32(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number) ? number : null;
}

public sealed class ExternalWorkerService : IDisposable
{
    public const int MaximumOutputTokens = 2048;
    public const int DefaultOutputTokens = 1024;
    public const int MaximumTaskCharacters = 16_000;
    public const int MaximumContextCharacters = 64_000;
    private static readonly Regex SafeRoleId = new("^cmm_[a-z0-9_]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex SafeExternalModel = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}(?:/[A-Za-z0-9][A-Za-z0-9._:/-]{0,191})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SafeSourceId = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IExternalWorkerConfigurationSource _configuration;
    private readonly IExternalWorkerBackend _backend;
    private readonly IExternalWorkerAuditSink _audit;
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    public ExternalWorkerService(
        SubagentConfigurationService configuration,
        UnifiedGatewayService gateway,
        IExternalWorkerAuditSink? audit = null,
        HttpClient? httpClient = null,
        string? dataDirectory = null)
        : this(
            new SubagentExternalWorkerConfigurationSource(configuration),
            new UnifiedGatewayExternalWorkerBackend(gateway, httpClient),
            audit ?? new ExternalWorkerAuditStore(),
            dataDirectory)
    {
    }

    public ExternalWorkerService(
        IExternalWorkerConfigurationSource configuration,
        IExternalWorkerBackend backend,
        IExternalWorkerAuditSink audit,
        string? dataDirectory = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _dataDirectory = Path.GetFullPath(dataDirectory ?? AppSettingsService.ResolveDefaultDataDirectory());
    }

    public async Task<ExternalWorkerCompletion> DelegateAsync(
        ExternalWorkerInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ValidateInvocation(invocation);
        // Reject a corrupt/ambiguous persisted role before creating locks or audit files.
        // The role is resolved again after both gates are held to close the change race.
        _ = ResolveRole(invocation.RoleId);
        var requestId = Guid.NewGuid().ToString("N");
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _singleFlight.WaitAsync(0, cancellationToken))
            throw new ExternalWorkerException("worker_busy", "已有一个 外部工人请求正在执行；为避免并发误耗额度，本次没有排队或发送。");
        FileStream? crossProcessGate = null;
        try
        {
            try
            {
                crossProcessGate = TryAcquireCrossProcessGate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ExternalWorkerException(
                    "worker_gate_unavailable",
                    "无法建立 外部工人跨进程额度闸门；本次没有发送请求。",
                    null,
                    ex);
            }
            if (crossProcessGate is null)
                throw new ExternalWorkerException("worker_busy", "另一个 Codex/MCP 进程正在使用 外部工人；本次没有排队或发送。");

            // Resolve only after both gates are held so a request cannot use a
            // role/model selection that changed during another invocation.
            var resolved = ResolveRole(invocation.RoleId);
            var startedAt = DateTimeOffset.UtcNow;
            await AppendAuditRequiredAsync(new ExternalWorkerAuditEntry(
                startedAt,
                "started",
                requestId,
                resolved.Role.Id,
                resolved.Model,
                null,
                resolved.SourceId,
                "started",
                null,
                null,
                null,
                null,
                null,
                null));

            try
            {
                var backendResult = await _backend.CompleteAsync(new ExternalWorkerBackendRequest(
                    resolved.Model,
                    resolved.SourceId,
                    resolved.ExpectedFingerprint,
                    resolved.Role.Id,
                    resolved.Role.DeveloperInstructions,
                    invocation.Task.Trim(),
                    string.IsNullOrWhiteSpace(invocation.Context) ? null : invocation.Context,
                    invocation.MaxOutputTokens), cancellationToken);
                var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
                await AppendAuditRequiredAsync(new ExternalWorkerAuditEntry(
                    DateTimeOffset.UtcNow,
                    "completed",
                    requestId,
                    resolved.Role.Id,
                    resolved.Model,
                    backendResult.ResolvedModel,
                    resolved.SourceId,
                    "success",
                    backendResult.HttpStatusCode,
                    backendResult.Usage.PromptTokens,
                    backendResult.Usage.CompletionTokens,
                    backendResult.Usage.TotalTokens,
                    elapsed,
                    null));
                return new ExternalWorkerCompletion(
                    requestId,
                    resolved.Role.Id,
                    resolved.Model,
                    backendResult.ResolvedModel,
                    resolved.SourceId,
                    backendResult.Content,
                    backendResult.FinishReason,
                    backendResult.Usage,
                    backendResult.HttpStatusCode,
                    elapsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var workerError = ex as ExternalWorkerException
                                  ?? new ExternalWorkerException("worker_failed", "外部工人调用失败。", null, ex);
                await AppendFailureAuditAsync(requestId, resolved, startedAt, workerError, CancellationToken.None);
                throw workerError;
            }
            catch (OperationCanceledException)
            {
                var canceled = new ExternalWorkerException("request_canceled", "外部工人调用已取消。");
                await AppendFailureAuditAsync(requestId, resolved, startedAt, canceled, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            crossProcessGate?.Dispose();
            _singleFlight.Release();
        }
    }

    private FileStream? TryAcquireCrossProcessGate()
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, "external-worker-single-flight.lock");
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }

    public IReadOnlyList<ExternalWorkerRoleOption> ReadEnabledRoleOptions()
    {
        var draft = _configuration.LoadDraft();
        if (!string.IsNullOrWhiteSpace(_configuration.LoadWarning))
            return Array.Empty<ExternalWorkerRoleOption>();
        return _configuration.Roles
            .Where(role => role.AllowsExternalWorker)
            .Select(role => new
            {
                Role = role,
                Matches = draft.Roles.Where(selection =>
                    selection.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase)).ToArray(),
                Grants = draft.SourceAuthorizations
            })
            .Where(item => item.Matches.Length == 1
                           && item.Matches[0].WorkerKind == SubagentWorkerKind.External
                           && SafeExternalModel.IsMatch(item.Matches[0].ModelId)
                           && !string.IsNullOrWhiteSpace(item.Matches[0].SourceId)
                               && item.Grants.Count(grant => grant.Enabled
                               && grant.SourceId.Equals(item.Matches[0].SourceId, StringComparison.OrdinalIgnoreCase)
                               && IsSha256(grant.ExpectedFingerprint)) == 1)
            .Select(item => new ExternalWorkerRoleOption(
                item.Role.Id,
                item.Role.DisplayName,
                item.Role.Purpose,
                item.Matches[0].ModelId,
                item.Matches[0].SourceId!))
            .OrderBy(item => item.RoleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private ResolvedWorkerRole ResolveRole(string roleId)
    {
        var role = _configuration.Roles.FirstOrDefault(item =>
                       item.Id.Equals(roleId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ExternalWorkerException("role_not_found", "没有找到指定的子代理角色。");
        if (!role.AllowsExternalWorker)
            throw new ExternalWorkerException("role_external_forbidden", "该角色不允许使用外部纯文本工人。");

        var draft = _configuration.LoadDraft();
        if (!string.IsNullOrWhiteSpace(_configuration.LoadWarning))
            throw new ExternalWorkerException(
                "role_configuration_invalid",
                "子代理草稿损坏或包含冲突角色，已按失败关闭且不会访问任何模型来源。");
        var matches = draft.Roles.Where(item =>
            item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new ExternalWorkerException("role_configuration_invalid", "该角色的已保存配置不唯一或不存在。");
        var selected = matches[0];
        if (selected.WorkerKind != SubagentWorkerKind.External)
            throw new ExternalWorkerException("role_not_external", "该角色当前没有选择外部纯文本工人。");
        if (!SafeExternalModel.IsMatch(selected.ModelId))
            throw new ExternalWorkerException("model_route_invalid", "该角色保存的外部模型路由格式不安全。");
        if (string.IsNullOrWhiteSpace(selected.SourceId) || !SafeSourceId.IsMatch(selected.SourceId))
            throw new ExternalWorkerException("source_id_invalid", "该角色没有保存安全的来源 ID。");
        var grants = draft.SourceAuthorizations.Where(grant => grant.Enabled
            && grant.SourceId.Equals(selected.SourceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (grants.Length != 1 || !IsSha256(grants[0].ExpectedFingerprint))
            throw new ExternalWorkerException("source_not_authorized", "该角色的来源未获授权、授权重复或身份指纹无效。");

        return new ResolvedWorkerRole(role, selected.ModelId, selected.SourceId, grants[0].ExpectedFingerprint);
    }

    private static void ValidateInvocation(ExternalWorkerInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.RoleId) || !SafeRoleId.IsMatch(invocation.RoleId))
            throw new ExternalWorkerException("invalid_role_id", "role_id 格式不正确。");
        if (string.IsNullOrWhiteSpace(invocation.Task))
            throw new ExternalWorkerException("task_required", "task 不能为空。");
        if (invocation.Task.Length > MaximumTaskCharacters)
            throw new ExternalWorkerException("task_too_large", $"task 不能超过 {MaximumTaskCharacters} 个字符。");
        if (invocation.Context?.Length > MaximumContextCharacters)
            throw new ExternalWorkerException("context_too_large", $"context 不能超过 {MaximumContextCharacters} 个字符。");
        if (invocation.MaxOutputTokens is < 1 or > MaximumOutputTokens)
            throw new ExternalWorkerException("max_output_tokens_invalid", $"max_output_tokens 必须在 1 到 {MaximumOutputTokens} 之间。");
    }

    private async Task AppendFailureAuditAsync(
        string requestId,
        ResolvedWorkerRole resolved,
        DateTimeOffset startedAt,
        ExternalWorkerException error,
        CancellationToken cancellationToken)
    {
        var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        await AppendAuditRequiredAsync(new ExternalWorkerAuditEntry(
            DateTimeOffset.UtcNow,
            "completed",
            requestId,
            resolved.Role.Id,
            resolved.Model,
            null,
            resolved.SourceId,
            "failed",
            error.HttpStatusCode,
            null,
            null,
            null,
            elapsed,
            error.Code), cancellationToken);
    }

    private async Task AppendAuditRequiredAsync(
        ExternalWorkerAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _audit.AppendAsync(entry, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ExternalWorkerException(
                "audit_write_failed",
                "外部工人审计不可写，本次调用已停止。",
                null,
                ex);
        }
    }

    public void Dispose()
    {
        _singleFlight.Dispose();
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record ResolvedWorkerRole(
        SubagentRoleDefinition Role,
        string Model,
        string SourceId,
        string ExpectedFingerprint);
}
