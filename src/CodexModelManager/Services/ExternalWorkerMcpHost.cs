using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class ExternalWorkerMcpHost
{
    public const string ToolName = "delegate_to_worker";
    public const int MaximumMessageCharacters = 1_048_576;
    private const string DefaultProtocolVersion = "2024-11-05";
    private readonly ExternalWorkerService _worker;
    private readonly WorkerBroker? _broker;
    private readonly IExternalWorkerRuntimeStateSink? _runtimeState;
    private readonly Func<IReadOnlyList<ExternalWorkerDiscoverableRole>> _readDiscoverableRoles;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveRequest> _activeRequests = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ExternalWorkerMcpHost(
        ExternalWorkerService worker,
        IExternalWorkerRuntimeStateSink? runtimeState = null,
        WorkerBroker? broker = null,
        Func<IReadOnlyList<ExternalWorkerDiscoverableRole>>? readDiscoverableRoles = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _broker = broker;
        _runtimeState = runtimeState;
        _readDiscoverableRoles = readDiscoverableRoles ?? ReadRolesFromWorker;
    }

    public async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await input.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > MaximumMessageCharacters)
                {
                    await WriteAsync(output, Error(null, -32600, "JSON-RPC 消息超过安全上限。"));
                    continue;
                }

                JsonObject? request = null;
                try
                {
                    request = JsonNode.Parse(line) as JsonObject;
                    if (request is null)
                        throw new JsonException("request must be an object");
                }
                catch (JsonException)
                {
                    await WriteAsync(output, Error(null, -32700, "JSON 解析失败。"));
                    continue;
                }

                var hasId = request.TryGetPropertyValue("id", out var idValue);
                var id = idValue?.DeepClone();
                if (!TryReadString(request, "jsonrpc", out var jsonRpc)
                    || jsonRpc != "2.0"
                    || !TryReadString(request, "method", out var method)
                    || string.IsNullOrWhiteSpace(method))
                {
                    if (hasId)
                        await WriteAsync(output, Error(id, -32600, "JSON-RPC 请求格式不正确。"));
                    continue;
                }

                if (!hasId)
                {
                    if (method.Equals("notifications/cancelled", StringComparison.Ordinal))
                        CancelRequest(request["params"] as JsonObject);
                    continue;
                }

                if (method.Equals("tools/call", StringComparison.Ordinal))
                {
                    await StartToolCallAsync(output, id, request["params"] as JsonObject, cancellationToken);
                    // Give the detached invocation a scheduling turn. The reader never awaits
                    // the invocation itself, so cancellation notifications remain responsive.
                    await Task.Yield();
                    continue;
                }

                JsonObject response;
                try
                {
                    response = method switch
                    {
                        "initialize" => await InitializeAsync(id, request["params"] as JsonObject, cancellationToken),
                        "ping" => Success(id, new JsonObject()),
                        "tools/list" => Success(id, BuildToolsList()),
                        _ => Error(id, -32601, "不支持的 JSON-RPC 方法。")
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    response = Error(id, -32603, "外部工人 MCP发生内部错误。");
                }
                await WriteAsync(output, response);
            }
        }
        finally
        {
            await CancelAndDrainActiveRequestsAsync();
        }
        return 0;
    }

    private Task StartToolCallAsync(
        TextWriter output,
        JsonNode? id,
        JsonObject? parameters,
        CancellationToken serverCancellationToken)
    {
        var requestKey = RequestKey(id);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        var active = new ActiveRequest(linked);
        if (!_activeRequests.TryAdd(requestKey, active))
        {
            linked.Dispose();
            return WriteAsync(
                output,
                Error(id, -32600, "同一个 JSON-RPC id 已有正在处理的请求。"));
        }

        active.Completion = ExecuteToolCallAsync(output, requestKey, active, id, parameters);
        return Task.CompletedTask;
    }

    private async Task ExecuteToolCallAsync(
        TextWriter output,
        string requestKey,
        ActiveRequest active,
        JsonNode? id,
        JsonObject? parameters)
    {
        await Task.Yield();

        JsonObject response;
        try
        {
            response = await CallToolAsync(id, parameters, active.Cancellation.Token);
        }
        catch (ExternalWorkerException ex)
        {
            response = Success(id, ToolFailure(ex.Code, ex.SafeMessage));
        }
        catch (OperationCanceledException)
        {
            response = Success(id, ToolFailure("request_canceled", "外部工人调用已取消。"));
        }
        catch
        {
            response = Error(id, -32603, "外部工人 MCP发生内部错误。");
        }

        try
        {
            await WriteAsync(output, response);
        }
        finally
        {
            if (_activeRequests.TryGetValue(requestKey, out var current)
                && ReferenceEquals(current, active))
                _activeRequests.TryRemove(requestKey, out _);
            active.Cancellation.Dispose();
        }
    }

    private void CancelRequest(JsonObject? parameters)
    {
        if (parameters is null || !parameters.TryGetPropertyValue("requestId", out var requestId)) return;
        if (_activeRequests.TryGetValue(RequestKey(requestId), out var active))
        {
            try { active.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task CancelAndDrainActiveRequestsAsync()
    {
        var active = _activeRequests.Values.ToArray();
        foreach (var request in active)
        {
            try { request.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        if (active.Length == 0) return;
        try { await Task.WhenAll(active.Select(request => request.Completion)); }
        catch { }
    }

    private async Task<JsonObject> InitializeAsync(
        JsonNode? id,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        string? clientName = null;
        string? clientVersion = null;
        if (parameters?["clientInfo"] is JsonObject clientInfo)
        {
            if (TryReadString(clientInfo, "name", out var name)) clientName = name;
            if (TryReadString(clientInfo, "version", out var version)) clientVersion = version;
        }
        if (_runtimeState is not null)
            await _runtimeState.RecordHandshakeAsync(clientName, clientVersion, cancellationToken);
        return Success(id, BuildInitializeResult(parameters));
    }

    private async Task<JsonObject> CallToolAsync(
        JsonNode? id,
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        if (parameters is null
            || !TryReadString(parameters, "name", out var name)
            || !name.Equals(ToolName, StringComparison.Ordinal))
            return Error(id, -32602, "tools/call 只允许 delegate_to_worker。");
        if (parameters["arguments"] is not JsonObject arguments)
            return Error(id, -32602, "arguments 必须是对象。");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "role_id", "task", "context", "max_output_tokens"
        };
        if (arguments.Any(item => !allowed.Contains(item.Key)))
            return Error(id, -32602, "arguments 包含未允许字段；模型、路径和命令不能由调用方传入。");
        if (!TryReadRequiredString(arguments, "role_id", out var roleId)
            || !TryReadRequiredString(arguments, "task", out var task))
            return Error(id, -32602, "role_id 和 task 必须是非空字符串。");
        if (!TryReadOptionalString(arguments, "context", out var context))
            return Error(id, -32602, "context 必须是字符串。");
        if (!TryReadOptionalInt32(arguments, "max_output_tokens", out var maxTokens))
            return Error(id, -32602, "max_output_tokens 必须是整数。");

        ExternalWorkerCompletion completion;
        if (_broker is not null)
        {
            // 走 WorkerBroker：价格/币种/预算/超时校验 + 委托后扣减预算（P0 失败关闭）。
            completion = await _broker.DelegateAsync(
                roleId,
                task,
                context,
                maxTokens ?? ExternalWorkerService.DefaultOutputTokens,
                cancellationToken);
        }
        else
        {
            completion = await _worker.DelegateAsync(new ExternalWorkerInvocation(
                roleId,
                task,
                context,
                maxTokens ?? ExternalWorkerService.DefaultOutputTokens), cancellationToken);
        }
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = completion.Content
                }
            },
            ["structuredContent"] = new JsonObject
            {
                ["request_id"] = completion.RequestId,
                ["role_id"] = completion.RoleId,
                ["configured_model"] = completion.ConfiguredModel,
                ["resolved_model"] = completion.ResolvedModel,
                ["account_source"] = completion.AccountSource,
                ["http_status"] = completion.HttpStatusCode,
                ["finish_reason"] = completion.FinishReason,
                ["elapsed_ms"] = completion.ElapsedMilliseconds,
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = completion.Usage.PromptTokens,
                    ["completion_tokens"] = completion.Usage.CompletionTokens,
                    ["total_tokens"] = completion.Usage.TotalTokens
                }
            },
            ["isError"] = false
        };
        return Success(id, result);
    }

    private JsonObject BuildInitializeResult(JsonObject? parameters)
    {
        var requested = string.Empty;
        if (parameters is not null) _ = TryReadString(parameters, "protocolVersion", out requested);
        var protocolVersion = string.IsNullOrWhiteSpace(requested) ? DefaultProtocolVersion : requested;
        var roles = ReadDiscoverableRoles();
        var roleSummary = roles.Count == 0
            ? "当前没有发现已选择且来源已授权的外部纯文本角色；请先在总管家中保存并应用角色配置。"
            : "当前已配置的外部纯文本角色：" + FormatRoleSummary(roles)
              + "；每次调用仍会重新检查来源授权、身份指纹、模型目录和运行状态。";
        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "codex-total-manager-external-worker",
                ["version"] = "1.0.0"
            },
            ["instructions"] = "本服务无法验证调用者身份，包括无法确认调用者是否为 Codex、Sol 或总监督；"
                               + "客户端必须在每次真实 tools/call 前单独向用户请求审批。"
                               + "仅提供纯文本 delegate_to_worker；模型由总管家角色配置决定，无文件、命令或模型覆盖参数。"
                               + roleSummary
        };
    }

    private JsonObject BuildToolsList()
    {
        var roles = ReadDiscoverableRoles();
        var roleIdSchema = new JsonObject
        {
            ["type"] = "string",
            ["description"] = roles.Count == 0
                ? "当前没有来源已授权且模型可用的外部纯文本角色。"
                : "总管家中当前可用的外部纯文本角色。" + FormatRoleSummary(roles)
        };
        var enabledRoleIds = new JsonArray();
        foreach (var role in roles) enabledRoleIds.Add(role.RoleId);
        roleIdSchema["enum"] = enabledRoleIds;

        return new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = ToolName,
                    ["description"] = "把只读分析或文本草稿交给总管家中该角色已选择且明确授权的外部模型。不能访问或修改文件，不能执行命令，也不能由调用方覆盖模型或来源。"
                                      + (roles.Count == 0
                                          ? "当前没有可调用角色。"
                                          : "当前角色：" + FormatRoleSummary(roles) + "。"),
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["role_id"] = roleIdSchema,
                            ["task"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["minLength"] = 1,
                                ["maxLength"] = ExternalWorkerService.MaximumTaskCharacters
                            },
                            ["context"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = ExternalWorkerService.MaximumContextCharacters,
                                ["description"] = "由监督模型显式提供的只读文本上下文；不是文件路径。"
                            },
                            ["max_output_tokens"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["minimum"] = 1,
                                ["maximum"] = ExternalWorkerService.MaximumOutputTokens,
                                ["default"] = ExternalWorkerService.DefaultOutputTokens
                            }
                        },
                        ["required"] = new JsonArray("role_id", "task")
                    },
                    ["annotations"] = new JsonObject
                    {
                        ["readOnlyHint"] = true,
                        ["destructiveHint"] = false,
                        ["idempotentHint"] = false,
                        ["openWorldHint"] = true
                    }
                }
            }
        };
    }

    private IReadOnlyList<ExternalWorkerDiscoverableRole> ReadDiscoverableRoles()
    {
        try
        {
            return (_readDiscoverableRoles() ?? Array.Empty<ExternalWorkerDiscoverableRole>())
                .Where(IsSafeDiscoverableRole)
                .GroupBy(role => role.RoleId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(role => role.RoleId, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<ExternalWorkerDiscoverableRole>();
        }
    }

    private IReadOnlyList<ExternalWorkerDiscoverableRole> ReadRolesFromWorker() =>
        _worker.ReadEnabledRoleOptions()
            .Select(role => new ExternalWorkerDiscoverableRole(
                role.RoleId,
                role.DisplayName,
                role.Purpose,
                role.ConfiguredModel,
                role.SourceId))
            .ToArray();

    private static bool IsSafeDiscoverableRole(ExternalWorkerDiscoverableRole role)
    {
        if (role is null
            || string.IsNullOrWhiteSpace(role.RoleId)
            || role.RoleId.Length is < 5 or > 67
            || !role.RoleId.StartsWith("cmm_", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(role.DisplayName)
            || string.IsNullOrWhiteSpace(role.Purpose)
            || string.IsNullOrWhiteSpace(role.ModelId)
            || role.DisplayName.Any(char.IsControl)
            || role.Purpose.Any(char.IsControl)
            || !IsSafeExternalModel(role.ModelId)
            || !IsSafeSourceId(role.SourceId))
            return false;

        return role.RoleId.AsSpan(4).ToString().All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }

    private static bool IsSafeExternalModel(string modelId)
    {
        if (modelId.Length is < 3 or > 320) return false;
        var slash = modelId.IndexOf('/');
        if (slash is < 1 or > 128 || slash == modelId.Length - 1
            || !char.IsAsciiLetterOrDigit(modelId[0])
            || !char.IsAsciiLetterOrDigit(modelId[slash + 1]))
            return false;
        return modelId[..slash].All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
               && modelId[(slash + 1)..].All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '/' or '-');
    }

    private static bool IsSafeSourceId(string? sourceId) =>
        !string.IsNullOrWhiteSpace(sourceId)
        && sourceId.Length <= 128
        && char.IsAsciiLetterOrDigit(sourceId[0])
        && sourceId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static string FormatRoleSummary(IEnumerable<ExternalWorkerDiscoverableRole> roles) =>
        string.Join("；", roles.Select(role =>
            $"{role.RoleId}（{role.DisplayName}：{role.Purpose}）→ {role.ModelId} [{role.SourceId}]"));

    private static JsonObject ToolFailure(string code, string message) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"{message}（{code}）"
            }
        },
        ["structuredContent"] = new JsonObject { ["error_code"] = code },
        ["isError"] = true
    };

    private static bool TryReadRequiredString(JsonObject source, string name, out string value)
    {
        value = string.Empty;
        if (source[name] is not JsonValue node || !node.TryGetValue<string>(out var parsed)
                                                    || string.IsNullOrWhiteSpace(parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryReadString(JsonObject source, string name, out string value)
    {
        value = string.Empty;
        if (source[name] is not JsonValue node || !node.TryGetValue<string>(out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryReadOptionalString(JsonObject source, string name, out string? value)
    {
        value = null;
        if (!source.ContainsKey(name) || source[name] is null) return true;
        if (source[name] is not JsonValue node || !node.TryGetValue<string>(out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryReadOptionalInt32(JsonObject source, string name, out int? value)
    {
        value = null;
        if (!source.ContainsKey(name) || source[name] is null) return true;
        if (source[name] is not JsonValue node || !node.TryGetValue<int>(out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static JsonObject Success(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };

    private string RequestKey(JsonNode? id) => id?.ToJsonString(_jsonOptions) ?? "null";

    private async Task WriteAsync(TextWriter output, JsonObject message)
    {
        await _writeGate.WaitAsync();
        try
        {
            var line = message.ToJsonString(_jsonOptions);
            await output.WriteLineAsync(line);
            await output.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed class ActiveRequest(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
    }
}

public sealed record ExternalWorkerDiscoverableRole(
    string RoleId,
    string DisplayName,
    string Purpose,
    string ModelId,
    string SourceId = "");
