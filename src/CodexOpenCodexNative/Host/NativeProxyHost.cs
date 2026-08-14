using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RequestDecompression;
using Microsoft.Extensions.DependencyInjection;
using CodexOpenCodexNative.Adapters;
using CodexOpenCodexNative.Config;
using CodexOpenCodexNative.Logging;
using CodexOpenCodexNative.Models;
using CodexOpenCodexNative.Providers;
using CodexOpenCodexNative.Responses;

namespace CodexOpenCodexNative.Host;

public sealed class NativeProxyHost
{
    private readonly NativeProxyConfigStore _store;
    private readonly string? _admissionTokenOverride;
    private readonly WebApplication _app;
    private readonly HttpClient _upstream;
    private readonly RequestLogService _requestLog;

    public NativeProxyHost(
        NativeProxyConfigStore? store = null,
        string? admissionTokenOverride = null,
        HttpClient? upstream = null,
        string[]? args = null,
        string? dataRootOverride = null)
    {
        _store = store ?? new NativeProxyConfigStore(dataRootOverride);
        _admissionTokenOverride = admissionTokenOverride;
        _upstream = upstream ?? new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var config = LoadConfig();
        if (string.IsNullOrWhiteSpace(config.AdmissionToken))
            throw new InvalidOperationException("Native Proxy 缺少 Admission Token，已拒绝启动。");
        var dataRoot = dataRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTotalManager", "runtime-v3", "native-proxy");
        _requestLog = new RequestLogService(dataRoot);

        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        builder.WebHost.UseUrls($"http://127.0.0.1:{config.ListenPort}");
        builder.Services.AddRequestDecompression(options =>
        {
            options.DecompressionProviders["zstd"] = new ZstdSharpDecompressionProvider();
        });
        _app = builder.Build();
        _app.UseRequestDecompression();
        var traceRoot = dataRootOverride ?? Path.GetDirectoryName(_store.ConfigPath) ?? string.Empty;
        if (!string.IsNullOrEmpty(traceRoot) && File.Exists(Path.Combine(traceRoot, "trace-requests.flag")))
        {
            _app.Use(next => async (HttpContext context) =>
            {
                File.AppendAllText(Path.Combine(traceRoot, "request-trace.log"),
                    $"{DateTime.Now:O} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} CE={context.Request.Headers.ContentEncoding} CT={context.Request.ContentType} LEN={context.Request.ContentLength}\n");
                await next(context);
            });
        }
        MapRoutes(config);
    }

    public WebApplication Application => _app;

    public string? ListenUrl => $"http://127.0.0.1:{LoadConfig().ListenPort}";

    private NativeProxyConfig LoadConfig()
    {
        var config = _store.Load();
        if (_admissionTokenOverride is not null)
            config.AdmissionToken = _admissionTokenOverride;
        return config;
    }

    private void MapRoutes(NativeProxyConfig config)
    {
        var token = config.AdmissionToken;
        var registry = new ProviderRegistry(config);
        var continuationStore = new ResponseContinuationStore();
        var codexSessionAdmission = new CodexSessionAdmissionRegistry();
        var startedAt = DateTimeOffset.Now;

        _app.MapGet("/healthz", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { status = "unauthorized" }, statusCode: 401);
            return Results.Json(new
            {
                status = "ok",
                pid = Environment.ProcessId,
                port = config.ListenPort,
                uptime = (DateTimeOffset.Now - startedAt).TotalSeconds,
                engine = "CodexOpenCodexNative"
            });
        });

        _app.MapGet("/readyz", (HttpContext context) =>
        {
            if (!Admitted(context, token))
                return Results.Json(new { status = "unauthorized" }, statusCode: 401);
            var enabledProviders = registry.All.Count(provider => !provider.Disabled);
            var catalogModels = registry.ListModels().Count;
            var ready = enabledProviders > 0 && catalogModels > 0;
            return Results.Json(new
            {
                service = "codex-total-manager-native",
                status = ready ? "ready" : "pending",
                pid = Environment.ProcessId,
                port = config.ListenPort,
                catalogModels,
                enabledProviders
            }, statusCode: ready ? 200 : 503);
        });

        _app.MapGet("/v1/models", (HttpContext context) =>
        {
            // Codex Desktop keeps using its real ChatGPT Bearer token after
            // openai_base_url is pointed at this loopback engine. That token is
            // acceptable only for the built-in official pass-through surface.
            // It must never authorize namespaced third-party models or any
            // management API.
            if (!AdmittedInference(context, token) && !HasOfficialBearer(context))
                return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            return Results.Json(new { @object = "list", data = registry.ListModels() });
        });

        // Compatibility management surface used by Codex Total Manager. The
        // native engine owns one pass-through official account; additional
        // native accounts must not be synthesized from unrelated credentials.
        var managementGate = new object();
        var activeAccountId = "__main__";
        var autoSwitchThreshold = config.AutoSwitchThreshold;
        var failoverThreshold = config.FailoverThreshold;
        var accountMode = "direct";

        _app.MapGet("/api/models", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            var models = registry.All.Where(provider => !provider.Disabled).SelectMany(provider =>
            {
                var ids = provider.Models
                    .Concat(string.IsNullOrWhiteSpace(provider.DefaultModel)
                        ? Array.Empty<string>()
                        : new[] { provider.DefaultModel! })
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                return ids.Select(id => new
                {
                    provider = provider.Id,
                    id,
                    namespaced = $"{provider.Id}/{id}",
                    displayName = id,
                    disabled = false,
                    native = provider.Id.Equals("openai", StringComparison.OrdinalIgnoreCase),
                    contextWindow = provider.ContextWindow
                });
            }).ToArray();
            return Results.Json(models);
        });

        _app.MapGet("/api/providers", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            string mode;
            lock (managementGate) mode = accountMode;
            return Results.Json(registry.All.Select(provider => new
            {
                name = provider.Id,
                displayName = provider.Name,
                baseUrl = provider.BaseUrl,
                adapter = provider.Adapter,
                hasApiKey = !string.IsNullOrWhiteSpace(provider.ApiKey),
                disabled = provider.Disabled,
                codexAccountMode = provider.Id.Equals("openai", StringComparison.OrdinalIgnoreCase) ? mode : null
            }).ToArray());
        });

        _app.MapPost("/api/providers", async (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!TryReadProviderDefinition(body.RootElement, out var definition, out var error))
                return Results.Json(new { error = new { message = error } }, statusCode: 400);
            if (definition.Id.Equals("openai", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = new { message = "the built-in openai provider cannot be replaced" } }, statusCode: 400);
            lock (managementGate)
            {
                config.Providers.RemoveAll(provider => provider.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
                config.Providers.Add(definition);
                _store.Save(config);
                registry.UpsertProvider(definition);
            }
            return Results.Json(new { ok = true, name = definition.Id });
        });

        _app.MapMethods("/api/providers", new[] { "PATCH" }, async (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            var name = context.Request.Query["name"].ToString();
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (name.Equals("openai", StringComparison.OrdinalIgnoreCase))
            {
                var requestedMode = body.RootElement.TryGetProperty("codexAccountMode", out var modeValue)
                    ? modeValue.GetString()
                    : null;
                if (requestedMode is not "direct" and not "pool")
                    return Results.Json(new { error = new { message = "codexAccountMode must be direct or pool" } }, statusCode: 400);
                lock (managementGate) accountMode = requestedMode;
                return Results.Json(new { ok = true, codexAccountMode = requestedMode });
            }

            lock (managementGate)
            {
                var definition = config.Providers.FirstOrDefault(provider =>
                    provider.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (definition is null)
                    return Results.Json(new { error = new { message = "provider not found" } }, statusCode: 404);
                if (body.RootElement.TryGetProperty("disabled", out var disabled)
                    && disabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    definition.Disabled = disabled.GetBoolean();
                if (body.RootElement.TryGetProperty("baseUrl", out var baseUrlNode)
                    && baseUrlNode.ValueKind == JsonValueKind.String)
                {
                    var baseUrl = baseUrlNode.GetString() ?? string.Empty;
                    if (!IsAllowedProviderEndpoint(baseUrl, definition.AllowPrivateNetwork))
                        return Results.Json(new { error = new { message = "provider baseUrl is not allowed" } }, statusCode: 400);
                    definition.BaseUrl = baseUrl;
                }
                if (body.RootElement.TryGetProperty("adapter", out var adapterNode)
                    && adapterNode.ValueKind == JsonValueKind.String)
                {
                    var adapter = adapterNode.GetString() ?? string.Empty;
                    if (!IsSupportedAdapter(adapter))
                        return Results.Json(new { error = new { message = "provider adapter is not supported" } }, statusCode: 400);
                    definition.Adapter = adapter;
                }
                _store.Save(config);
                registry.UpsertProvider(definition);
                return Results.Json(new { ok = true, name = definition.Id, disabled = definition.Disabled });
            }
        });

        _app.MapDelete("/api/providers", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            var name = context.Request.Query["name"].ToString();
            if (name.Equals("openai", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = new { message = "the built-in openai provider cannot be deleted" } }, statusCode: 400);
            lock (managementGate)
            {
                var removed = config.Providers.RemoveAll(provider =>
                    provider.Id.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
                if (!removed) return Results.Json(new { error = new { message = "provider not found" } }, statusCode: 404);
                config.Combos.RemoveAll(combo => combo.Targets.Any(target =>
                    target.Provider.Equals(name, StringComparison.OrdinalIgnoreCase)));
                _store.Save(config);
                registry.RemoveProvider(name);
            }
            return Results.Json(new { ok = true });
        });

        _app.MapPost("/api/providers/test", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            var name = context.Request.Query["name"].ToString();
            var provider = registry.Find(name);
            if (provider is null || provider.Disabled)
                return Results.Json(new { ok = false, message = "provider is missing or disabled" }, statusCode: 404);
            if (string.IsNullOrWhiteSpace(provider.ApiKey))
                return Results.Json(new { ok = false, message = "provider credential environment variable is unavailable" }, statusCode: 400);
            return Results.Json(new { ok = true, message = "provider configuration and credential are loaded" });
        });

        _app.MapGet("/api/codex-auth/accounts", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            string active;
            lock (managementGate) active = activeAccountId;
            return Results.Json(new
            {
                accounts = new[]
                {
                    new
                    {
                        id = "__main__",
                        email = "Codex official pass-through",
                        plan = "pro",
                        isMain = true,
                        hasCredential = true,
                        needsReauth = false,
                        health = new { status = "healthy" },
                        isActive = active == "__main__"
                    }
                }
            });
        });

        _app.MapPost("/api/codex-auth/login", (HttpContext context) =>
            Admitted(context, token)
                ? Results.Json(new
                {
                    error = new
                    {
                        message = "additional native Codex accounts are not supported by this engine",
                        additional_accounts_supported = false
                    }
                }, statusCode: StatusCodes.Status501NotImplemented)
                : Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401));

        _app.MapGet("/api/codex-auth/login-status", (HttpContext context) =>
            Admitted(context, token)
                ? Results.Json(new
                {
                    error = new
                    {
                        message = "native Codex account login is unavailable",
                        additional_accounts_supported = false
                    }
                }, statusCode: StatusCodes.Status501NotImplemented)
                : Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401));

        _app.MapDelete("/api/codex-auth/accounts", (HttpContext context) =>
            Admitted(context, token)
                ? Results.Json(new
                {
                    error = new
                    {
                        message = "additional native Codex accounts are not supported by this engine",
                        additional_accounts_supported = false
                    }
                }, statusCode: StatusCodes.Status501NotImplemented)
                : Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401));

        _app.MapGet("/api/codex-auth/active", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            lock (managementGate)
            {
                return Results.Json(new
                {
                    activeCodexAccountId = activeAccountId,
                    autoSwitchThreshold,
                    upstreamFailoverThreshold = failoverThreshold
                });
            }
        });

        _app.MapPut("/api/codex-auth/active", async (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var requested = body.RootElement.TryGetProperty("accountId", out var value) ? value.GetString() : null;
            if (!string.Equals(requested, "__main__", StringComparison.Ordinal))
                return Results.Json(new { error = new { message = "only the official pass-through account is available" } }, statusCode: 400);
            lock (managementGate) activeAccountId = "__main__";
            return Results.Json(new { ok = true, activeCodexAccountId = "__main__" });
        });

        _app.MapPut("/api/codex-auth/auto-switch", async (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!body.RootElement.TryGetProperty("threshold", out var value) || !value.TryGetInt32(out var threshold) || threshold is < 0 or > 100)
                return Results.Json(new { error = new { message = "invalid threshold" } }, statusCode: 400);
            lock (managementGate)
            {
                autoSwitchThreshold = threshold;
                config.AutoSwitchThreshold = threshold;
                _store.Save(config);
            }
            return Results.Json(new { ok = true, threshold });
        });

        _app.MapPut("/api/codex-auth/failover", async (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!body.RootElement.TryGetProperty("threshold", out var value) || !value.TryGetInt32(out var threshold) || threshold is < 0 or > 20)
                return Results.Json(new { error = new { message = "invalid threshold" } }, statusCode: 400);
            lock (managementGate)
            {
                failoverThreshold = threshold;
                config.FailoverThreshold = threshold;
                _store.Save(config);
            }
            return Results.Json(new { ok = true, threshold });
        });

        _app.MapGet("/api/provider-quotas", (HttpContext context) =>
            Admitted(context, token)
                ? Results.Json(new { reports = Array.Empty<object>() })
                : Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401));

        _app.MapGet("/api/combos", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            lock (managementGate)
            {
                return Results.Json(new
                {
                    combos = config.Combos.Select(combo => new
                    {
                        id = combo.Id,
                        alias = combo.Alias,
                        strategy = combo.Strategy,
                        stickyLimit = combo.StickyLimit,
                        targets = combo.Targets.Select(target => new
                        {
                            provider = target.Provider,
                            model = target.Model,
                            weight = target.Weight
                        }).ToArray()
                    }).ToArray()
                });
            }
        });

        _app.MapPut("/api/combos", async (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (!TryReadCombo(body.RootElement, registry, out var combo, out var error))
                return Results.Json(new { error = new { message = error } }, statusCode: 400);
            lock (managementGate)
            {
                config.Combos.RemoveAll(existing => existing.Id.Equals(combo.Id, StringComparison.OrdinalIgnoreCase)
                                                    || existing.Alias.Equals(combo.Alias, StringComparison.OrdinalIgnoreCase));
                config.Combos.Add(combo);
                _store.Save(config);
                registry.UpsertCombo(combo);
            }
            return Results.Json(new { ok = true, id = combo.Id, alias = combo.Alias });
        });

        _app.MapGet("/api/system/memory", (HttpContext context) =>
            Admitted(context, token)
                ? Results.Json(new { rss = Environment.WorkingSet })
                : Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401));

        _app.MapGet("/api/status", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            var active = registry.Default;
            return Results.Json(new
            {
                pid = Environment.ProcessId,
                port = config.ListenPort,
                uptime_seconds = (DateTimeOffset.Now - startedAt).TotalSeconds,
                default_provider = active.Id,
                providers = registry.All.Select(p => new { p.Id, p.Name, p.Adapter, p.BaseUrl }),
                oauth = new
                {
                    chatgpt_logged_in = false,
                    additional_accounts_supported = false
                }
            });
        });

        _app.MapGet("/api/logs", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            var limit = context.Request.Query.TryGetValue("limit", out var raw)
                && int.TryParse(raw.FirstOrDefault(), out var parsed)
                ? Math.Clamp(parsed, 1, 500)
                : 50;
            return Results.Json(_requestLog.Recent(limit).Select(entry => new
            {
                id = entry.Id,
                requestId = entry.Id,
                timestamp = entry.StartedAt,
                status = entry.HttpStatus ?? (entry.Status == "completed" ? 200 : 500),
                durationMs = entry.ElapsedMs,
                requestedModel = entry.RequestedModel ?? entry.Model,
                provider = entry.Provider,
                model = entry.Model,
                resolvedModel = entry.Model,
                route = new { provider = entry.Provider, model = entry.Model, resolvedModel = entry.Model },
                usage = new
                {
                    inputTokens = entry.PromptTokens ?? 0,
                    outputTokens = entry.CompletionTokens ?? 0,
                    totalTokens = entry.TotalTokens ?? 0
                },
                totalTokens = entry.TotalTokens ?? 0,
                error = entry.Error,
                errorMessage = entry.Error,
                attempts = new[]
                {
                    new
                    {
                        selected = true,
                        status = entry.HttpStatus ?? (entry.Status == "completed" ? 200 : 500),
                        provider = entry.Provider,
                        model = entry.Model,
                        resolvedModel = entry.Model,
                        durationMs = entry.ElapsedMs,
                        error = entry.Error,
                        errorMessage = entry.Error,
                        usage = new
                        {
                            inputTokens = entry.PromptTokens ?? 0,
                            outputTokens = entry.CompletionTokens ?? 0,
                            totalTokens = entry.TotalTokens ?? 0
                        }
                    }
                }
            }).ToArray());
        });

        _app.MapGet("/api/usage", (HttpContext context) =>
        {
            if (!Admitted(context, token)) return Results.Json(new { error = new { message = "unauthorized" } }, statusCode: 401);
            return Results.Json(_requestLog.Summarize());
        });

        _app.MapPost("/v1/chat/completions", async (HttpContext context, CancellationToken ct) =>
        {
            var locallyAdmitted = AdmittedInference(context, token);
            var hasOfficialBearer = HasOfficialBearer(context);
            if (!locallyAdmitted && !hasOfficialBearer)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "unauthorized" } });
                return;
            }
            var sw = Stopwatch.StartNew();
            OcxParsedRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<OcxParsedRequest>(
                    context.Request.Body, cancellationToken: ct);
            }
            catch
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "请求体不是合法 JSON" } });
                return;
            }
            if (request is null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "请求体为空" } });
                return;
            }

            RouteResult route;
            try
            {
                route = RouteResolver.Resolve(registry, request.Model);
            }
            catch (ModelNotFoundException ex)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { error = new { message = ex.Message, type = "model_not_found", model = ex.Model } });
                return;
            }
            if (!locallyAdmitted
                && !IsOfficialPassThrough(route)
                && !codexSessionAdmission.Contains(context))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { message = "the Codex session credential may use only the built-in official provider" }
                });
                return;
            }
            var adapter = CreateAdapter(route.Provider);
            await using var result = await adapter.FetchAsync(route.Provider, request, route.ModelId, ct);
            if (IsOfficialPassThrough(route) && result.StatusCode is >= 200 and < 300)
                codexSessionAdmission.Remember(context);

            if (!result.Streaming)
            {
                context.Response.StatusCode = result.StatusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                var upstream = result.Message is null
                    ? ParseChatCompletion(result.JsonBody)
                    : new ChatCompletionResponse
                    {
                        Id = $"chatcmpl-{Guid.NewGuid():N}"[..24],
                        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Model = route.ModelId,
                        Choices =
                        [
                            new ChatChoice
                            {
                                Index = 0,
                                Message = result.Message,
                                FinishReason = result.FinishReason
                            }
                        ],
                        Usage = result.Usage
                    };
                await context.Response.WriteAsync(
                    upstream is null ? result.JsonBody ?? "{}" : JsonSerializer.Serialize(upstream),
                    ct);
                sw.Stop();
                _requestLog.Record(new RequestLogEntry
                {
                    Path = "/v1/chat/completions",
                    RequestedModel = request.Model,
                    Model = route.ModelId,
                    Provider = route.ProviderId,
                    Status = upstream is null
                        ? "error"
                        : upstream.Choices.FirstOrDefault()?.FinishReason is "length" or "max_tokens"
                            ? "incomplete"
                            : "completed",
                    HttpStatus = result.StatusCode,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    PromptTokens = upstream?.Usage?.PromptTokens,
                    CompletionTokens = upstream?.Usage?.CompletionTokens,
                    TotalTokens = upstream?.Usage?.TotalTokens,
                    Error = result.StatusCode is >= 200 and < 300
                        ? null
                        : ExtractErrorMessage(result.JsonBody)
                });
                return;
            }

            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sessionId = $"chatcmpl-{Guid.NewGuid():N}"[..24];
            var status = "completed";
            string? streamError = null;
            OcxUsage? usage = null;

            await foreach (var adapterEvent in result.Events!)
            {
                if (ct.IsCancellationRequested) break;
                if (adapterEvent.Type == "usage") usage = adapterEvent.Usage;
                if (adapterEvent.Type == "error")
                {
                    status = "error";
                    streamError ??= adapterEvent.Text;
                }
                if (adapterEvent.Type == "incomplete") status = "incomplete";
                if (adapterEvent.Type is "finish" or "done"
                    && adapterEvent.FinishReason is "length" or "max_tokens")
                    status = "incomplete";
                var toolCalls = adapterEvent.Type == "function_call"
                    ? new List<ChatToolCallDelta>
                    {
                        new()
                        {
                            Index = adapterEvent.ToolCallIndex,
                            Id = adapterEvent.CallId,
                            Type = adapterEvent.CallId is null ? null : "function",
                            Function = new ChatToolCallFunctionDelta
                            {
                                Name = adapterEvent.FunctionName,
                                Arguments = adapterEvent.Arguments
                            }
                        }
                    }
                    : null;
                var chunk = new ChatCompletionChunk
                {
                    Id = sessionId,
                    Created = created,
                    Model = route.ModelId,
                    Choices =
                    [
                        new ChunkChoice
                        {
                            Index = 0,
                            Delta = new ChunkDelta
                            {
                                Role = adapterEvent.Role,
                                Content = adapterEvent.Text,
                                ToolCalls = toolCalls
                            },
                            FinishReason = adapterEvent.FinishReason
                        }
                    ]
                };
                if (adapterEvent.Type == "usage")
                    chunk.Usage = adapterEvent.Usage;
                var line = $"data: {JsonSerializer.Serialize(chunk)}\n\n";
                await context.Response.WriteAsync(line, ct);
                if (adapterEvent.Type is "done" or "finish") break;
            }
            await context.Response.WriteAsync("data: [DONE]\n\n", ct);
            sw.Stop();
            _requestLog.Record(new RequestLogEntry
            {
                Path = "/v1/chat/completions",
                RequestedModel = request.Model,
                Model = route.ModelId,
                Provider = route.ProviderId,
                Status = status,
                HttpStatus = 200,
                ElapsedMs = sw.ElapsedMilliseconds,
                PromptTokens = usage?.PromptTokens,
                CompletionTokens = usage?.CompletionTokens,
                TotalTokens = usage?.TotalTokens,
                Error = streamError
            });
        });

        _app.MapPost("/v1/responses", async (HttpContext context, CancellationToken ct) =>
        {
            var locallyAdmitted = AdmittedInference(context, token);
            var hasOfficialBearer = HasOfficialBearer(context);
            if (!locallyAdmitted && !hasOfficialBearer)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "unauthorized" } });
                return;
            }
            var sw = Stopwatch.StartNew();
            var rawBody = await ReadRawBodyAsync(context);
            ResponsesRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ResponsesRequest>(rawBody);
            }
            catch
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = ResponsesJson.Error("invalid_request_error", "请求体不是合法 JSON")
                });
                return;
            }
            if (request is null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = ResponsesJson.Error("invalid_request_error", "请求体为空")
                });
                return;
            }

            var parsed = ResponsesParser.Parse(request);
            parsed.IsResponsesRequest = true;
            parsed.RawBody = rawBody;
            parsed.ForwardHeaders = CollectForwardHeaders(context);
            RouteResult route;
            try
            {
                route = RouteResolver.Resolve(registry, parsed.Model);
            }
            catch (ModelNotFoundException ex)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = ResponsesJson.Error("model_not_found", ex.Message)
                });
                return;
            }
            var officialPassThrough = route.Provider.Adapter == "openai-responses"
                                      && OpenAiResponsesAdapter.IsOfficialCodexProvider(route.Provider);
            if (!locallyAdmitted
                && !officialPassThrough
                && !codexSessionAdmission.Contains(context))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = ResponsesJson.Error(
                        "forbidden_provider",
                        "Codex 自己的登录令牌只能原样访问 OpenAI 官方模型；第三方模型和账号池必须使用总管家本机准入令牌。")
                }, cancellationToken: ct);
                return;
            }
            if (!officialPassThrough && !string.IsNullOrWhiteSpace(parsed.PreviousResponseId))
            {
                if (!continuationStore.TryExpand(
                        parsed.PreviousResponseId,
                        parsed.Messages,
                        out var expandedMessages))
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = ResponsesJson.Error(
                            "previous_response_not_found",
                            "总管家找不到上一轮回复记录；请让客户端用完整历史重建对话后再试。")
                    }, cancellationToken: ct);
                    return;
                }
                parsed.Messages = expandedMessages;
                parsed.PreviousResponseId = null;
                parsed.RawBody = null;
            }
            else if (!officialPassThrough)
            {
                // Total Manager's provider/model prefix is only a local routing name.
                // Rebuild the request with the provider's bare upstream model id.
                parsed.RawBody = null;
            }
            var adapter = CreateAdapter(route.Provider);
            await using var result = await adapter.FetchAsync(route.Provider, parsed, route.ModelId, ct);
            if (officialPassThrough && result.StatusCode is >= 200 and < 300)
                codexSessionAdmission.Remember(context);
            var status = "completed";
            OcxUsage? usage = null;

            if (!result.Streaming)
            {
                status = result.StatusCode is >= 200 and < 300 ? "completed" : "error";
                if (route.Provider.Adapter == "openai-responses")
                {
                    context.Response.StatusCode = result.StatusCode;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(result.JsonBody ?? "{}", ct);
                    if (result.StatusCode is >= 200 and < 300 && !officialPassThrough)
                        continuationStore.SaveFromResponseJson(result.JsonBody, parsed.Messages);
                }
                else
                {
                    if (result.StatusCode != 200)
                    {
                        context.Response.StatusCode = result.StatusCode;
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync(result.JsonBody ?? "{}", ct);
                    }
                    else
                    {
                        var upstream = result.Message is null ? ParseChatCompletion(result.JsonBody) : null;
                        var message = result.Message ?? upstream?.Choices.FirstOrDefault()?.Message;
                        var upstreamUsage = result.Usage ?? upstream?.Usage;
                        var finishReason = result.FinishReason ?? upstream?.Choices.FirstOrDefault()?.FinishReason;
                        var responseJson = BuildResponsesNonStreamingResponse(
                            route.ModelId,
                            message,
                            upstreamUsage,
                            finishReason);
                        context.Response.ContentType = "application/json; charset=utf-8";
                        await context.Response.WriteAsync(responseJson.ToJsonString(), ct);
                        continuationStore.SaveFromResponseJson(responseJson.ToJsonString(), parsed.Messages);
                        usage = upstreamUsage;
                        status = finishReason is "length" or "max_tokens" ? "incomplete" : "completed";
                    }
                }
                sw.Stop();
                _requestLog.Record(new RequestLogEntry
                {
                    Path = "/v1/responses",
                    RequestedModel = request.Model,
                    Model = route.ModelId,
                    Provider = route.ProviderId,
                    Status = status,
                    HttpStatus = result.StatusCode,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    PromptTokens = usage?.PromptTokens,
                    CompletionTokens = usage?.CompletionTokens,
                    TotalTokens = usage?.TotalTokens,
                    Error = result.StatusCode is >= 200 and < 300
                        ? null
                        : ExtractErrorMessage(result.JsonBody)
                });
                return;
            }

            if (officialPassThrough)
            {
                if (result.RawStream is null)
                    throw new InvalidOperationException("官方 Codex 流式响应缺少原始响应流，已停止以避免伪造事件。");
                context.Response.StatusCode = result.StatusCode;
                context.Response.ContentType = result.ContentType;
                context.Response.Headers.CacheControl = "no-cache";
                await result.RawStream.CopyToAsync(context.Response.Body, ct);
                await context.Response.Body.FlushAsync(ct);
                sw.Stop();
                _requestLog.Record(new RequestLogEntry
                {
                    Path = "/v1/responses",
                    RequestedModel = request.Model,
                    Model = route.ModelId,
                    Provider = route.ProviderId,
                    Status = "passed-through",
                    HttpStatus = result.StatusCode,
                    ElapsedMs = sw.ElapsedMilliseconds
                });
                return;
            }

            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            var bridgeStream = new ResponsesBridge(route.ModelId);
            await foreach (var frame in bridgeStream.StreamAsync(result.Events!, ct))
            {
                await context.Response.WriteAsync(frame, ct);
            }
            continuationStore.Save(
                bridgeStream.ResponseId,
                parsed.Messages,
                bridgeStream.GetContinuationMessages());
            sw.Stop();
            _requestLog.Record(new RequestLogEntry
            {
                Path = "/v1/responses",
                RequestedModel = request.Model,
                Model = route.ModelId,
                Provider = route.ProviderId,
                Status = bridgeStream.Status,
                HttpStatus = bridgeStream.Status == "completed" ? 200 : 500,
                ElapsedMs = sw.ElapsedMilliseconds,
                PromptTokens = bridgeStream.Usage?.PromptTokens,
                CompletionTokens = bridgeStream.Usage?.CompletionTokens,
                TotalTokens = bridgeStream.Usage?.TotalTokens,
                Error = bridgeStream.ErrorMessage
            });
        });

        _app.MapPost("/v1/messages", async (HttpContext context, CancellationToken ct) =>
        {
            var locallyAdmitted = AdmittedInference(context, token);
            var hasOfficialBearer = HasOfficialBearer(context);
            if (!locallyAdmitted && !hasOfficialBearer)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "unauthorized" } });
                return;
            }
            var sw = Stopwatch.StartNew();
            AnthropicMessagesRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<AnthropicMessagesRequest>(
                    context.Request.Body, cancellationToken: ct);
            }
            catch
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "请求体不是合法 JSON" } });
                return;
            }
            if (request is null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "请求体为空" } });
                return;
            }

            OcxParsedRequest parsed;
            try
            {
                parsed = AnthropicParser.Parse(request);
            }
            catch (InvalidOperationException ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = new { message = ex.Message, type = "invalid_request_error" } });
                return;
            }
            RouteResult route;
            try
            {
                route = RouteResolver.Resolve(registry, parsed.Model);
            }
            catch (ModelNotFoundException ex)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { error = new { message = ex.Message, type = "model_not_found" } });
                return;
            }
            if (!locallyAdmitted
                && !IsOfficialPassThrough(route)
                && !codexSessionAdmission.Contains(context))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new { message = "the Codex session credential may use only the built-in official provider" }
                });
                return;
            }
            var adapter = CreateAdapter(route.Provider);
            await using var result = await adapter.FetchAsync(route.Provider, parsed, route.ModelId, ct);
            if (IsOfficialPassThrough(route) && result.StatusCode is >= 200 and < 300)
                codexSessionAdmission.Remember(context);
            if (!result.Streaming)
            {
                if (result.StatusCode is < 200 or >= 300)
                {
                    context.Response.StatusCode = result.StatusCode;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(result.JsonBody ?? "{}", ct);
                    sw.Stop();
                    _requestLog.Record(new RequestLogEntry
                    {
                        Path = "/v1/messages",
                        RequestedModel = request.Model,
                        Model = route.ModelId,
                        Provider = route.ProviderId,
                        Status = "error",
                        HttpStatus = result.StatusCode,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Error = ExtractErrorMessage(result.JsonBody)
                    });
                    return;
                }
                var message = result.Message ?? new OcxMessage
                {
                    Role = "assistant",
                    Content = ExtractUpstreamText(result.JsonBody, route.Provider.Adapter)
                };
                var promptTokens = result.Usage?.PromptTokens
                                   ?? ExtractUsageToken(result.JsonBody, "prompt_tokens")
                                   ?? 0;
                var completionTokens = result.Usage?.CompletionTokens
                                       ?? ExtractUsageToken(result.JsonBody, "completion_tokens")
                                       ?? 0;
                var responseJson = BuildAnthropicNonStreamingResponse(
                    route.ModelId,
                    message,
                    result.FinishReason,
                    promptTokens,
                    completionTokens);
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(responseJson.ToJsonString(), ct);
                sw.Stop();
                _requestLog.Record(new RequestLogEntry
                {
                    Path = "/v1/messages",
                    RequestedModel = request.Model,
                    Model = route.ModelId,
                    Provider = route.ProviderId,
                    Status = result.FinishReason is "length" or "max_tokens" ? "incomplete" : "completed",
                    HttpStatus = result.StatusCode,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = promptTokens + completionTokens
                });
                return;
            }

            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            var outbound = new AnthropicOutboundBridge();
            await foreach (var frame in outbound.StreamAsync(result.Events!, route.ModelId, ct))
            {
                await context.Response.WriteAsync(frame, ct);
            }
            sw.Stop();
            _requestLog.Record(new RequestLogEntry
            {
                Path = "/v1/messages",
                RequestedModel = request.Model,
                Model = route.ModelId,
                Provider = route.ProviderId,
                Status = outbound.Status,
                HttpStatus = outbound.Status == "completed" ? 200 : 500,
                ElapsedMs = sw.ElapsedMilliseconds,
                PromptTokens = outbound.Usage?.PromptTokens,
                CompletionTokens = outbound.Usage?.CompletionTokens,
                TotalTokens = outbound.Usage?.TotalTokens,
                Error = outbound.ErrorMessage
            });
        });

    }

    private static readonly string[] ForwardHeaderNames =
    [
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
    ];

    private static bool TryReadProviderDefinition(
        JsonElement root,
        out ProviderDefinition definition,
        out string error)
    {
        definition = new ProviderDefinition();
        error = string.Empty;
        var id = root.TryGetProperty("name", out var nameNode) ? nameNode.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(id)
            || id.Length > 64
            || !char.IsAsciiLetterOrDigit(id[0])
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            error = "provider name is invalid";
            return false;
        }
        if (!root.TryGetProperty("provider", out var providerNode)
            || providerNode.ValueKind != JsonValueKind.Object)
        {
            error = "provider definition is missing";
            return false;
        }
        var adapter = providerNode.TryGetProperty("adapter", out var adapterNode)
            ? adapterNode.GetString()?.Trim()
            : null;
        var baseUrl = providerNode.TryGetProperty("baseUrl", out var baseUrlNode)
            ? baseUrlNode.GetString()?.Trim()
            : null;
        var apiKey = providerNode.TryGetProperty("apiKey", out var apiKeyNode)
            ? apiKeyNode.GetString()?.Trim()
            : null;
        var allowPrivate = providerNode.TryGetProperty("allowPrivateNetwork", out var privateNode)
                           && privateNode.ValueKind is JsonValueKind.True or JsonValueKind.False
                           && privateNode.GetBoolean();
        if (string.IsNullOrWhiteSpace(adapter) || !IsSupportedAdapter(adapter))
        {
            error = "provider adapter is not supported";
            return false;
        }
        if (string.IsNullOrWhiteSpace(baseUrl) || !IsAllowedProviderEndpoint(baseUrl, allowPrivate))
        {
            error = "provider baseUrl is not allowed";
            return false;
        }
        if (string.IsNullOrWhiteSpace(apiKey)
            || !apiKey.StartsWith("${CMM_", StringComparison.Ordinal)
            || !apiKey.EndsWith("_API_KEY}", StringComparison.Ordinal)
            || apiKey.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '$' or '{' or '}' or '_')))
        {
            error = "provider apiKey must be a CMM environment reference";
            return false;
        }
        var modelsNode = providerNode.TryGetProperty("selectedModels", out var selectedModels)
                         && selectedModels.ValueKind == JsonValueKind.Array
            ? selectedModels
            : providerNode.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                ? models
                : default;
        var modelIds = modelsNode.ValueKind == JsonValueKind.Array
            ? modelsNode.EnumerateArray()
                .Where(model => model.ValueKind == JsonValueKind.String)
                .Select(model => model.GetString()?.Trim())
                .Where(model => !string.IsNullOrWhiteSpace(model) && model!.Length <= 200)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
        if (modelIds.Count == 0)
        {
            error = "provider model list is empty";
            return false;
        }
        var contextWindow = providerNode.TryGetProperty("contextWindow", out var contextNode)
                            && contextNode.TryGetInt32(out var parsedContext)
            ? Math.Clamp(parsedContext, 1024, 4_000_000)
            : 128000;
        definition = new ProviderDefinition
        {
            Id = id,
            Name = id,
            Adapter = adapter,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            DefaultModel = modelIds[0],
            Models = modelIds,
            ContextWindow = contextWindow,
            AllowPrivateNetwork = allowPrivate
        };
        return true;
    }

    private static bool TryReadCombo(
        JsonElement root,
        ProviderRegistry registry,
        out ComboDefinition combo,
        out string error)
    {
        combo = new ComboDefinition();
        error = string.Empty;
        var id = root.TryGetProperty("id", out var idNode) ? idNode.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80)
        {
            error = "combo id is invalid";
            return false;
        }
        if (!root.TryGetProperty("combo", out var comboNode) || comboNode.ValueKind != JsonValueKind.Object)
        {
            error = "combo definition is missing";
            return false;
        }
        var alias = comboNode.TryGetProperty("alias", out var aliasNode) ? aliasNode.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(alias)
            || !InternalRouteNames.IsAlias(alias)
            || alias.Length > 128)
        {
            error = "combo alias is invalid";
            return false;
        }
        if (!comboNode.TryGetProperty("targets", out var targetsNode)
            || targetsNode.ValueKind != JsonValueKind.Array)
        {
            error = "combo targets are missing";
            return false;
        }
        var targets = new List<ComboTargetDefinition>();
        foreach (var targetNode in targetsNode.EnumerateArray())
        {
            var providerId = targetNode.TryGetProperty("provider", out var providerNode)
                ? providerNode.GetString()?.Trim()
                : null;
            var model = targetNode.TryGetProperty("model", out var modelNode)
                ? modelNode.GetString()?.Trim()
                : null;
            var provider = string.IsNullOrWhiteSpace(providerId) ? null : registry.Find(providerId);
            if (provider is null || provider.Disabled || string.IsNullOrWhiteSpace(model)
                || !(provider.Models.Contains(model, StringComparer.OrdinalIgnoreCase)
                     || string.Equals(provider.DefaultModel, model, StringComparison.OrdinalIgnoreCase)))
            {
                error = "combo target does not match an enabled provider model";
                return false;
            }
            targets.Add(new ComboTargetDefinition { Provider = provider.Id, Model = model, Weight = 1 });
        }
        if (targets.Count == 0
            || targets.Select(target => target.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            error = "combo must contain targets from exactly one provider";
            return false;
        }
        combo = new ComboDefinition
        {
            Id = id,
            Alias = alias,
            Strategy = "failover",
            StickyLimit = 1,
            Targets = targets
        };
        return true;
    }

    private static bool IsSupportedAdapter(string adapter) =>
        adapter is "openai-chat" or "openai-responses" or "anthropic" or "google";

    private static bool IsAllowedProviderEndpoint(string value, bool allowPrivateNetwork)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
        return allowPrivateNetwork
               && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && uri.IsLoopback;
    }

    private static Dictionary<string, string> CollectForwardHeaders(HttpContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ForwardHeaderNames)
        {
            if (context.Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                headers[name] = value.ToString();
        }
        return headers;
    }

    private static async Task<string> ReadRawBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private IProviderAdapter CreateAdapter(ProviderDefinition provider) =>
        provider.Adapter switch
        {
            "openai-chat" => new OpenAiChatAdapter(_upstream),
            "openai-responses" => new OpenAiResponsesAdapter(_upstream),
            "anthropic" => new AnthropicAdapter(_upstream),
            "google" => new GoogleAdapter(_upstream),
            _ => new OpenAiChatAdapter(_upstream)
        };

    private static ChatCompletionResponse? ParseChatCompletion(string? jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody)) return null;
        try
        {
            return JsonSerializer.Deserialize<ChatCompletionResponse>(jsonBody);
        }
        catch
        {
            return null;
        }
    }

    public static JsonObject BuildResponsesNonStreamingResponse(
        string modelId,
        OcxMessage? message,
        OcxUsage? usage,
        string? finishReason)
    {
        var output = new JsonArray();
        var text = message?.Content?.ToString();
        if (!string.IsNullOrEmpty(text))
        {
            output.Add(new JsonObject
            {
                ["type"] = "message",
                ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..24],
                ["status"] = "completed",
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = text,
                        ["annotations"] = new JsonArray()
                    }
                }
            });
        }
        foreach (var call in message?.ToolCalls ?? Enumerable.Empty<OcxToolCall>())
        {
            output.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["id"] = "fc_" + Guid.NewGuid().ToString("N")[..24],
                ["call_id"] = call.Id ?? "call_" + Guid.NewGuid().ToString("N")[..24],
                ["name"] = call.Function?.Name ?? string.Empty,
                ["arguments"] = call.Function?.Arguments ?? "{}"
            });
        }
        var incomplete = finishReason is "length" or "max_tokens";
        var response = new JsonObject
        {
            ["id"] = "resp_" + Guid.NewGuid().ToString("N")[..24],
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["status"] = incomplete ? "incomplete" : "completed",
            ["model"] = modelId,
            ["output"] = output,
            ["usage"] = ResponsesUsage.Build(
                usage is null ? null : JsonSerializer.SerializeToElement(usage)),
            ["end_turn"] = !incomplete && (message?.ToolCalls?.Count ?? 0) == 0
        };
        if (incomplete)
        {
            response["incomplete_details"] = new JsonObject
            {
                ["reason"] = "max_output_tokens"
            };
        }
        return response;
    }

    public static JsonObject BuildAnthropicNonStreamingResponse(
        string modelId,
        OcxMessage message,
        string? finishReason,
        long promptTokens,
        long completionTokens)
    {
        var content = new JsonArray();
        var text = message.Content?.ToString();
        if (!string.IsNullOrEmpty(text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var call in message.ToolCalls ?? Enumerable.Empty<OcxToolCall>())
        {
            JsonNode input;
            try { input = JsonNode.Parse(call.Function?.Arguments ?? "{}") ?? new JsonObject(); }
            catch (JsonException) { input = new JsonObject { ["input"] = call.Function?.Arguments ?? string.Empty }; }
            content.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id ?? "toolu_" + Guid.NewGuid().ToString("N")[..24],
                ["name"] = call.Function?.Name ?? string.Empty,
                ["input"] = input
            });
        }
        var stopReason = finishReason switch
        {
            "tool_calls" => "tool_use",
            "length" or "max_tokens" => "max_tokens",
            _ => "end_turn"
        };
        return new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..24],
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = modelId,
            ["content"] = content,
            ["stop_reason"] = stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = promptTokens,
                ["output_tokens"] = completionTokens
            }
        };
    }

    private static string ExtractUpstreamText(string? jsonBody, string adapter)
    {
        if (string.IsNullOrWhiteSpace(jsonBody)) return string.Empty;
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(jsonBody).RootElement;
        }
        catch
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        switch (adapter)
        {
            case "openai-chat":
            {
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var content))
                    AppendAnyText(builder, content);
                break;
            }
            case "anthropic":
            {
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.TryGetProperty("text", out var text))
                            builder.Append(text.GetString());
                    }
                }
                break;
            }
            case "google":
            {
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                {
                    foreach (var candidate in candidates.EnumerateArray())
                    {
                        if (candidate.TryGetProperty("content", out var content)
                            && content.TryGetProperty("parts", out var parts))
                        {
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.TryGetProperty("text", out var text))
                                    builder.Append(text.GetString());
                            }
                        }
                    }
                }
                break;
            }
            default:
            {
                if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in output.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var type) && type.GetString() == "output_text"
                            && item.TryGetProperty("text", out var text))
                            builder.Append(text.GetString());
                    }
                }
                break;
            }
        }
        return builder.ToString();
    }

    private static string? ExtractErrorMessage(string? jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody)) return null;
        try
        {
            using var json = JsonDocument.Parse(jsonBody);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                        return message.GetString();
                    if (error.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        return detail.GetString();
                }
            }
            return root.TryGetProperty("message", out var direct) && direct.ValueKind == JsonValueKind.String
                ? direct.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendAnyText(StringBuilder builder, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            builder.Append(content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    builder.Append(text.GetString());
            }
        }
    }

    private static long? ExtractUsageToken(string? jsonBody, string preferredName)
    {
        if (string.IsNullOrWhiteSpace(jsonBody)) return null;
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(jsonBody).RootElement;
        }
        catch
        {
            return null;
        }
        foreach (var container in new[] { "usage", "usageMetadata" })
        {
            if (!root.TryGetProperty(container, out var usage) || usage.ValueKind != JsonValueKind.Object)
                continue;
            if (TryReadLong(usage, preferredName, out var value))
                return value;
        }
        if (root.TryGetProperty("usage", out var usage2) && usage2.ValueKind == JsonValueKind.Object)
        {
            if (preferredName == "prompt_tokens" && TryReadLong(usage2, "input_tokens", out var input))
                return input;
            if (preferredName == "completion_tokens" && TryReadLong(usage2, "output_tokens", out var output))
                return output;
        }
        if (root.TryGetProperty("usageMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (preferredName == "prompt_tokens" && TryReadLong(metadata, "promptTokenCount", out var prompt))
                return prompt;
            if (preferredName == "completion_tokens" && TryReadLong(metadata, "candidatesTokenCount", out var candidates))
                return candidates;
        }
        return null;
    }

    private static long? ExtractUsageTotal(string? jsonBody)
    {
        var prompt = ExtractUsageToken(jsonBody, "prompt_tokens");
        var completion = ExtractUsageToken(jsonBody, "completion_tokens");
        if (prompt is null && completion is null) return null;
        return (prompt ?? 0) + (completion ?? 0);
    }

    private static bool TryReadLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }

    private static bool Admitted(HttpContext context, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var header = context.Request.Headers["X-CMM-Admission"].ToString();
        return string.Equals(header, $"Bearer {token}", StringComparison.Ordinal);
    }

    private static bool AdmittedInference(HttpContext context, string? token)
    {
        if (Admitted(context, token)) return true;
        if (string.IsNullOrWhiteSpace(token)) return false;
        // Inference clients that cannot set X-CMM-Admission may use the same
        // per-installation admission secret as their Bearer credential. Never
        // accept an arbitrary non-empty Bearer merely because it is loopback:
        // every process running as the user can reach a loopback listener.
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = authorization["Bearer ".Length..].Trim();
        var expectedBytes = Encoding.UTF8.GetBytes(token);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   expectedBytes,
                   suppliedBytes);
    }

    private static bool HasOfficialBearer(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(authorization["Bearer ".Length..]);
    }

    private static bool IsOfficialPassThrough(RouteResult route) =>
        route.Provider.Adapter == "openai-responses"
        && OpenAiResponsesAdapter.IsOfficialCodexProvider(route.Provider);

    private sealed class CodexSessionAdmissionRegistry
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
        private const int MaximumEntries = 8;
        private readonly object _gate = new();
        private readonly Dictionary<string, DateTimeOffset> _validated = new(StringComparer.Ordinal);

        public bool Contains(HttpContext context)
        {
            var digest = DigestBearer(context);
            if (digest is null) return false;
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                Prune(now);
                return _validated.TryGetValue(digest, out var validatedAt)
                       && now - validatedAt <= Lifetime;
            }
        }

        public void Remember(HttpContext context)
        {
            var digest = DigestBearer(context);
            if (digest is null) return;
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                Prune(now);
                if (_validated.Count >= MaximumEntries && !_validated.ContainsKey(digest))
                {
                    var oldest = _validated.OrderBy(pair => pair.Value).First().Key;
                    _validated.Remove(oldest);
                }
                _validated[digest] = now;
            }
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (var key in _validated
                         .Where(pair => now - pair.Value > Lifetime)
                         .Select(pair => pair.Key)
                         .ToArray())
                _validated.Remove(key);
        }

        private static string? DigestBearer(HttpContext context)
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
            var supplied = authorization["Bearer ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(supplied)) return null;
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(supplied)));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _app.StartAsync(cancellationToken);
        try
        {
            // 等待取消令牌触发；取消后走 finally 显式停止 Kestrel。
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常取消路径
        }
        finally
        {
            await _app.StopAsync(CancellationToken.None);
        }
    }

    public Task StartAsync() => _app.StartAsync();
    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _app.StopAsync(cancellationToken);

    public Task StopAsync() => _app.StopAsync();
}
