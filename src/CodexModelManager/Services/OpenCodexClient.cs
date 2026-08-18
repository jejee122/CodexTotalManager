using System.Net.Http;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexModelManager.Models;
using CodexOpenCodexNative.Config;
using CodexOpenCodexNative.Providers;

namespace CodexModelManager.Services;

public sealed class OpenCodexClient
{
    public const string InternalRoutePrefix = InternalRouteNames.Prefix;
    public const string SwitchComboId = InternalRouteNames.SwitchComboId;
    public const string SwitchAlias = InternalRouteNames.MainAlias;
    public const string DefaultManagementUrl = "http://127.0.0.1:10100/";

    public static bool IsInternalRouteAlias(string? value) =>
        InternalRouteNames.IsAlias(value);

    public static string ToUserVisibleModelName(
        string? value,
        string? resolvedModel = null,
        string fallback = "当前号池入口")
    {
        if (IsInternalRouteAlias(value))
            return string.IsNullOrWhiteSpace(resolvedModel) ? fallback : resolvedModel.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private readonly HttpClient _http;
    private readonly int _expectedPort;
    private readonly string? _requestLogPath;
    public string ManagementUrl => $"http://127.0.0.1:{_expectedPort}/";

    private static HttpClient CreateDefaultClient(
        string? nativeEngineDataRoot = null,
        int nativeEnginePort = LocalPortPolicy.DefaultNativeEnginePort)
    {
        HttpMessageHandler handler = new SocketsHttpHandler();
        if (!string.IsNullOrWhiteSpace(nativeEngineDataRoot))
        {
            handler = new NativeAdmissionHandler(nativeEngineDataRoot)
            {
                InnerHandler = handler
            };
        }

        return new HttpClient(handler)
        {
            BaseAddress = ResolveBaseAddress(nativeEnginePort),
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    private static Uri ResolveBaseAddress(int nativeEnginePort)
    {
        var sandboxUrl = Environment.GetEnvironmentVariable("CMM_SANDBOX_OCX_URL");
        if (!string.IsNullOrWhiteSpace(sandboxUrl) && Uri.TryCreate(sandboxUrl, UriKind.Absolute, out var sandboxUri))
            return sandboxUri;
        if (!LocalPortPolicy.IsUserPort(nativeEnginePort))
            throw new ArgumentOutOfRangeException(nameof(nativeEnginePort));
        return new Uri($"http://127.0.0.1:{nativeEnginePort}");
    }
    private readonly Dictionary<string, ProviderHealthState> _providerHealth =
        new(StringComparer.OrdinalIgnoreCase);

    public OpenCodexClient() : this(CreateDefaultClient(), null) { }

    public OpenCodexClient(string nativeEngineDataRoot)
        : this(CreateDefaultClient(nativeEngineDataRoot), nativeEngineDataRoot) { }

    public OpenCodexClient(string nativeEngineDataRoot, int nativeEnginePort)
        : this(CreateDefaultClient(nativeEngineDataRoot, nativeEnginePort), nativeEngineDataRoot) { }

    public OpenCodexClient(HttpClient http, string? nativeEngineDataRoot = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.BaseAddress ??= new Uri("http://127.0.0.1:10100");
        _expectedPort = _http.BaseAddress.Port;
        _requestLogPath = string.IsNullOrWhiteSpace(nativeEngineDataRoot)
            ? null
            : Path.Combine(Path.GetFullPath(nativeEngineDataRoot), "request-log.jsonl");
    }

    private sealed class NativeAdmissionHandler(string dataRoot) : DelegatingHandler
    {
        private readonly NativeProxyConfigStore _store = new(dataRoot);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                var token = _store.Load().AdmissionToken;
                if (!string.IsNullOrWhiteSpace(token)
                    && request.RequestUri is { IsAbsoluteUri: true } uri
                    && uri.IsLoopback)
                {
                    request.Headers.Remove("X-CMM-Admission");
                    request.Headers.TryAddWithoutValidation("X-CMM-Admission", $"Bearer {token}");
                }
            }
            catch
            {
                // The legacy OpenCodex service does not use this token. If the
                // native store is absent or unreadable, let the request produce
                // the authoritative health/API error instead of masking it here.
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("/healthz", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.TryGetProperty("status", out var status)
                   && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(ReadString(json.RootElement, "engine"), "CodexOpenCodexNative", StringComparison.Ordinal)
                   && ReadInt(json.RootElement, "port") == _expectedPort
                   && ReadInt(json.RootElement, "pid") is > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("/readyz", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return string.Equals(ReadString(json.RootElement, "status"), "ready", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(ReadString(json.RootElement, "service"), "codex-total-manager-native", StringComparison.Ordinal)
                   && ReadInt(json.RootElement, "port") == _expectedPort
                   && ReadInt(json.RootElement, "pid") is > 0
                   && ReadInt(json.RootElement, "catalogModels") is > 0
                   && ReadInt(json.RootElement, "enabledProviders") is > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<OpenCodexRuntimeStatus> GetRuntimeStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var healthResponse = await _http.GetAsync("/healthz", cancellationToken);
            await EnsureSuccessAsync(healthResponse, cancellationToken);
            using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync(cancellationToken));
            var ok = string.Equals(ReadString(health.RootElement, "status"), "ok", StringComparison.OrdinalIgnoreCase);
            var pid = ReadInt(health.RootElement, "pid");
            var port = ReadInt(health.RootElement, "port") ?? _expectedPort;
            var uptimeSeconds = ReadDouble(health.RootElement, "uptime");
            long? rss = null;
            try
            {
                using var memoryResponse = await _http.GetAsync("/api/system/memory", cancellationToken);
                if (memoryResponse.IsSuccessStatusCode)
                {
                    using var memory = JsonDocument.Parse(await memoryResponse.Content.ReadAsStringAsync(cancellationToken));
                    rss = ReadLong(memory.RootElement, "rss");
                }
            }
            catch
            {
                // 运行状态仍然可信；内存指标只是补充信息。
            }
            return new OpenCodexRuntimeStatus(
                ok,
                pid,
                port,
                uptimeSeconds is null ? null : TimeSpan.FromSeconds(uptimeSeconds.Value),
                rss,
                ok ? string.Empty : "健康端点没有返回 ok");
        }
        catch (Exception ex)
        {
            return new OpenCodexRuntimeStatus(false, null, _expectedPort, null, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ModelOption>> GetModelsAsync(
        AppSettingsService settings,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/models", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var rows = UnwrapArray(json.RootElement);
        var result = new List<ModelOption>();
        foreach (var item in rows.EnumerateArray())
        {
            var provider = ReadString(item, "provider");
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id)) continue;
            var namespaced = ReadString(item, "namespaced") ?? $"{provider}/{id}";
            // cmm/* values are Manager-owned routes, not user-selectable models.
            // Keep them available to Codex routing while preventing them from resurfacing in the model catalog.
            if (IsInternalRouteAlias(namespaced)) continue;
            var option = new ModelOption
            {
                Provider = provider,
                Id = id,
                Namespaced = namespaced,
                DisplayName = ReadString(item, "displayName"),
                Disabled = ReadBool(item, "disabled"),
                IsOfficial = provider == "openai" && ReadBool(item, "native"),
                ContextWindow = ReadLong(item, "contextWindow")
            };
            option.ProviderLabel = settings.GetProviderName(provider);
            result.Add(option);
        }
        return result;
    }

    public async Task<IReadOnlyList<ProviderView>> GetProvidersAsync(
        AppSettingsService settings,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/providers", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var rows = UnwrapArray(json.RootElement);
        var result = new List<ProviderView>();
        foreach (var item in rows.EnumerateArray())
        {
            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new ProviderView
            {
                Id = name,
                DisplayName = settings.GetProviderName(name),
                BaseUrl = ReadString(item, "baseUrl") ?? string.Empty,
                Adapter = ReadString(item, "adapter") ?? "openai-chat",
                HasApiKey = ReadBool(item, "hasApiKey"),
                Disabled = ReadBool(item, "disabled")
            });
        }
        foreach (var provider in result)
        {
            if (!_providerHealth.TryGetValue(provider.Id, out var health)) continue;
            provider.ConnectionState = health.Success ? "连接正常" : "连接失败";
            provider.RecentError = health.Success ? health.Message : $"最近错误：{health.Message}";
            provider.LatencyMs = health.LatencyMs;
            provider.CheckedAt = health.CheckedAt;
        }
        return result;
    }

    public async Task<(IReadOnlyList<CodexAccountView> Accounts, CodexPoolSettings Settings, AccountRosterCompleteness RosterCompleteness)> GetCodexAccountsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var accountsPath = forceRefresh
            ? "/api/codex-auth/accounts?refresh=1"
            : "/api/codex-auth/accounts";
        JsonDocument accountsJson;
        try
        {
            using var accountsResponse = await _http.GetAsync(accountsPath, cancellationToken);
            await EnsureSuccessAsync(accountsResponse, cancellationToken);
            accountsJson = JsonDocument.Parse(await accountsResponse.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or IOException)
        {
            return (Array.Empty<CodexAccountView>(),
                new CodexPoolSettings("__main__", 80, 3, "pool"),
                AccountRosterCompleteness.ReadFailed);
        }
        using var accountsJsonScope = accountsJson;
        var activeRequest = _http.GetAsync("/api/codex-auth/active", cancellationToken);
        var providersRequest = _http.GetAsync("/api/providers", cancellationToken);
        var activeId = "__main__";
        var autoSwitch = 80;
        var failover = 3;
        var mode = "pool";
        try
        {
            using var activeResponse = await activeRequest;
            await EnsureSuccessAsync(activeResponse, cancellationToken);
            using var activeJson = JsonDocument.Parse(await activeResponse.Content.ReadAsStringAsync(cancellationToken));
            activeId = ReadString(activeJson.RootElement, "activeCodexAccountId") ?? "__main__";
            autoSwitch = ReadInt(activeJson.RootElement, "autoSwitchThreshold") ?? 80;
            failover = ReadInt(activeJson.RootElement, "upstreamFailoverThreshold") ?? 3;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        try
        {
            using var providersResponse = await providersRequest;
            await EnsureSuccessAsync(providersResponse, cancellationToken);
            using var providersJson = JsonDocument.Parse(await providersResponse.Content.ReadAsStringAsync(cancellationToken));
            foreach (var provider in UnwrapArray(providersJson.RootElement).EnumerateArray())
            {
                if (!string.Equals(ReadString(provider, "name"), "openai", StringComparison.OrdinalIgnoreCase)) continue;
                mode = ReadString(provider, "codexAccountMode") ?? "pool";
                break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        var rows = accountsJson.RootElement.TryGetProperty("accounts", out var accounts)
                   && accounts.ValueKind == JsonValueKind.Array
            ? accounts
            : default;
        var result = new List<CodexAccountView>();
        var stableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rosterCompleteness = rows.ValueKind == JsonValueKind.Array
            ? AccountRosterCompleteness.Complete : AccountRosterCompleteness.Partial;
        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rows.EnumerateArray())
            {
                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    rosterCompleteness = AccountRosterCompleteness.Partial;
                    continue;
                }
                if (!stableIds.Add(id))
                {
                    rosterCompleteness = AccountRosterCompleteness.Partial;
                    continue;
                }
                var health = item.TryGetProperty("health", out var healthObject)
                    ? ReadString(healthObject, "status")
                    : null;
                double? weeklyPercent = null;
                double? monthlyPercent = null;
                long? weeklyResetAt = null;
                long? monthlyResetAt = null;
                long? quotaUpdatedAt = null;
                int? resetCredits = null;
                if (item.TryGetProperty("quota", out var quota) && quota.ValueKind == JsonValueKind.Object)
                {
                    weeklyPercent = ReadDouble(quota, "weeklyPercent");
                    monthlyPercent = ReadDouble(quota, "monthlyPercent");
                    weeklyResetAt = ReadLong(quota, "weeklyResetAt");
                    monthlyResetAt = ReadLong(quota, "monthlyResetAt");
                    quotaUpdatedAt = ReadLong(quota, "updatedAt");
                    resetCredits = ReadInt(quota, "resetCredits");
                }
                result.Add(new CodexAccountView
                {
                    Id = id,
                    Email = ReadString(item, "email") ?? "账号信息已隐藏",
                    Plan = ReadString(item, "plan"),
                    IsMain = ReadBool(item, "isMain"),
                    HasCredential = ReadBool(item, "hasCredential"),
                    NeedsReauth = ReadBool(item, "needsReauth"),
                    HealthStatus = health ?? "unknown",
                    WeeklyPercent = weeklyPercent,
                    WeeklyResetAt = weeklyResetAt,
                    MonthlyPercent = monthlyPercent,
                    MonthlyResetAt = monthlyResetAt,
                    QuotaUpdatedAt = quotaUpdatedAt,
                    ResetCredits = resetCredits,
                    IsActive = string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        return (result, new CodexPoolSettings(activeId, autoSwitch, failover, mode), rosterCompleteness);
    }

    public async Task<IReadOnlyList<ProviderQuotaReportView>> GetProviderQuotaReportsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = forceRefresh ? "/api/provider-quotas?refresh=1" : "/api/provider-quotas";
            using var response = await _http.GetAsync(path, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("reports", out var reports)
                || reports.ValueKind != JsonValueKind.Array) return Array.Empty<ProviderQuotaReportView>();
            var result = new List<ProviderQuotaReportView>();
            foreach (var report in reports.EnumerateArray())
            {
                var provider = ReadString(report, "provider");
                if (string.IsNullOrWhiteSpace(provider)) continue;
                var windows = new List<UsageWindowView>();
                var quota = report.TryGetProperty("quota", out var quotaElement)
                            && quotaElement.ValueKind == JsonValueKind.Object
                    ? quotaElement
                    : default;
                if (quota.ValueKind == JsonValueKind.Object)
                {
                    AddQuotaWindow(windows, quota, "five_hour", "5 小时额度", "fiveHourPercent", "fiveHourResetAt");
                    AddQuotaWindow(windows, quota, "weekly", "每周额度", "weeklyPercent", "weeklyResetAt");
                    AddQuotaWindow(windows, quota, "monthly", "每月额度", "monthlyPercent", "monthlyResetAt");
                    if (quota.TryGetProperty("customWindows", out var customWindows)
                        && customWindows.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var window in customWindows.EnumerateArray())
                        {
                            var percent = ReadDouble(window, "percent");
                            var periodKey = ReadString(window, "periodKey") ?? ReadString(window, "id");
                            if (percent is null || string.IsNullOrWhiteSpace(periodKey)) continue;
                            windows.Add(new UsageWindowView
                            {
                                PeriodKey = periodKey,
                                Label = ReadString(window, "label") ?? "自定义额度",
                                UsedPercent = percent.Value,
                                ResetAtUtc = UsageFormatting.FromUnix(ReadLong(window, "resetAt")),
                                ResetState = UsageFormatting.FromUnix(ReadLong(window, "resetAt")) is null ? QuotaResetState.NotProvided : QuotaResetState.Parsed,
                                ResetText = UsageFormatting.Reset(UsageFormatting.FromUnix(ReadLong(window, "resetAt")))
                            });
                        }
                    }
                }
                result.Add(new ProviderQuotaReportView(
                    provider,
                    ReadString(report, "source") ?? "服务端额度接口",
                    windows,
                    UsageFormatting.FromUnix(ReadLong(report, "updatedAt") ?? ReadLong(quota, "updatedAt")),
                    ReadBool(report, "reverseEngineered") || ReadBool(quota, "reverseEngineered")));
            }
            return result;
        }
        catch
        {
            return Array.Empty<ProviderQuotaReportView>();
        }
    }

    public async Task<LocalUsageSnapshot> GetRecentUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/logs?limit=200", cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.ValueKind != JsonValueKind.Array) return LocalUsageSnapshot.Empty;
            var providers = new Dictionary<string, UsageAccumulator>(StringComparer.OrdinalIgnoreCase);
            var models = new Dictionary<string, UsageAccumulator>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                count++;
                var route = ResolveUsageRoute(item);
                var provider = ReadString(route, "provider") ?? ReadString(item, "provider") ?? "unknown";
                var model = ReadString(route, "resolvedModel")
                            ?? ReadString(route, "model")
                            ?? ReadString(item, "resolvedModel")
                            ?? ReadString(item, "model")
                            ?? "unknown";
                var status = ReadHttpStatus(item);
                var usage = TryGetPropertyIgnoreCase(item, "usage", out var usageElement)
                            && usageElement.ValueKind == JsonValueKind.Object
                    ? usageElement
                    : default;
                var input = ReadLongAny(usage, "inputTokens", "promptTokens", "input_tokens", "prompt_tokens")
                            ?? ReadLongAny(item, "inputTokens", "promptTokens", "input_tokens", "prompt_tokens") ?? 0;
                var output = ReadLongAny(usage, "outputTokens", "completionTokens", "output_tokens", "completion_tokens")
                             ?? ReadLongAny(item, "outputTokens", "completionTokens", "output_tokens", "completion_tokens") ?? 0;
                var total = ReadLong(usage, "totalTokens") ?? ReadLong(item, "totalTokens") ?? input + output;
                var cost = ReadEstimatedCost(item);
                var timestamp = ReadLogTimestamp(item);
                AddUsage(providers, provider, null, status, input, output, total, cost, timestamp);
                AddUsage(models, LocalUsageSnapshot.Key(provider, model), model, status, input, output, total, cost, timestamp, provider);
            }
            return new LocalUsageSnapshot(
                providers.ToDictionary(pair => pair.Key, pair => pair.Value.ToSummary(), StringComparer.OrdinalIgnoreCase),
                models.ToDictionary(pair => pair.Key, pair => pair.Value.ToSummary(), StringComparer.OrdinalIgnoreCase),
                count);
        }
        catch
        {
            return LocalUsageSnapshot.Empty;
        }
    }

    public async Task<NativeRoutingAudit> GetNativeRoutingAuditAsync(
        IReadOnlyList<CodexAccountView> accounts,
        DateTimeOffset switchedAt,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ResolveRequestLogPath();
        if (!File.Exists(sourcePath))
            return NativeRoutingAudit.Unavailable(switchedAt, "本机还没有总管家请求日志。 ");

        try
        {
            var audit = new NativeRoutingAuditAccumulator(
                switchedAt,
                ReadNativeProviderLabels(accounts),
                sourcePath);
            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                audit.Accept(line);
            }

            return audit.Build();
        }
        catch (Exception ex)
        {
            return NativeRoutingAudit.Unavailable(switchedAt, $"读取本机路由日志失败：{ex.Message}");
        }
    }

    public async Task<IReadOnlyDictionary<string, LiveTokenUsageView>> GetNativeAccountUsageAsync(
        IReadOnlyList<CodexAccountView> accounts,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ResolveRequestLogPath();
        var empty = new Dictionary<string, LiveTokenUsageView>(StringComparer.OrdinalIgnoreCase)
        {
            ["pro"] = LiveTokenUsageView.Empty("pro", "Codex Pro", "总管家本机完整日志"),
            ["plus"] = LiveTokenUsageView.Empty("plus", "Codex Plus", "总管家本机完整日志")
        };
        if (!File.Exists(sourcePath)) return empty;

        var labels = ReadNativeProviderLabels(accounts);
        var accumulators = new Dictionary<string, LiveUsageAccumulator>(StringComparer.OrdinalIgnoreCase)
        {
            ["pro"] = new LiveUsageAccumulator("pro", "Codex Pro"),
            ["plus"] = new LiveUsageAccumulator("plus", "Codex Plus")
        };
        try
        {
            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var item = json.RootElement;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var route = ResolveUsageRoute(item);
                    var provider = ReadString(route, "provider") ?? ReadString(item, "provider");
                    if (string.IsNullOrWhiteSpace(provider)
                        || provider.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                        || provider.Equals("combo", StringComparison.OrdinalIgnoreCase)) continue;
                    var status = ReadHttpStatus(route) ?? ReadHttpStatus(item);
                    if (status is not (>= 200 and < 300)) continue;

                    string key;
                    string displayName;
                    if (TryResolveNativeAccountLabel(provider, labels, out var accountLabel))
                    {
                        key = accountLabel.Equals("Pro", StringComparison.OrdinalIgnoreCase) ? "pro"
                            : accountLabel.Equals("Plus", StringComparison.OrdinalIgnoreCase) ? "plus"
                            : $"provider:{provider}";
                        displayName = key == "pro" ? "Codex Pro" : key == "plus" ? "Codex Plus" : accountLabel;
                    }
                    else
                    {
                        key = $"provider:{provider}";
                        displayName = provider;
                    }
                    if (!accumulators.TryGetValue(key, out var accumulator))
                    {
                        accumulator = new LiveUsageAccumulator(key, displayName);
                        accumulators[key] = accumulator;
                    }
                    var usage = TryGetPropertyIgnoreCase(item, "usage", out var usageElement)
                                && usageElement.ValueKind == JsonValueKind.Object
                        ? usageElement
                        : default;
                    var input = ReadLongAny(usage, "inputTokens", "promptTokens", "input_tokens", "prompt_tokens")
                                ?? ReadLongAny(item, "inputTokens", "promptTokens", "input_tokens", "prompt_tokens") ?? 0;
                    var output = ReadLongAny(usage, "outputTokens", "completionTokens", "output_tokens", "completion_tokens")
                                 ?? ReadLongAny(item, "outputTokens", "completionTokens", "output_tokens", "completion_tokens") ?? 0;
                    var total = ReadLong(usage, "totalTokens") ?? ReadLong(item, "totalTokens") ?? input + output;
                    accumulator.Add(input, output, total, ReadLogTimestamp(item));
                }
                catch (JsonException)
                {
                    // OpenCodex 可能正在写最后一行，下一次刷新会自动补上。
                }
            }

            return accumulators.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToView("总管家本机完整日志", true),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return empty;
        }
    }

    public static NativeRoutingAudit AnalyzeNativeRoutingAudit(
        IEnumerable<string> lines,
        DateTimeOffset switchedAt,
        IReadOnlyDictionary<string, string> providerLabels,
        string sourcePath = "usage.jsonl")
    {
        var audit = new NativeRoutingAuditAccumulator(switchedAt, providerLabels, sourcePath);
        foreach (var line in lines) audit.Accept(line);
        return audit.Build();
    }

    private sealed class NativeRoutingAuditAccumulator
    {
        private readonly DateTimeOffset _switchedAt;
        private readonly IReadOnlyDictionary<string, string> _providerLabels;
        private readonly string _sourcePath;
        private string? _lastBillingAccount;
        private string? _lastBillingProvider;
        private DateTimeOffset? _lastBillingAt;
        private DateTimeOffset? _proLastRequestAt;
        private int _proSuccessfulRequestsSinceSwitch;
        private int _nativeSuccessCount;

        public NativeRoutingAuditAccumulator(
            DateTimeOffset switchedAt,
            IReadOnlyDictionary<string, string> providerLabels,
            string sourcePath)
        {
            _switchedAt = switchedAt;
            _providerLabels = providerLabels;
            _sourcePath = sourcePath;
        }

        public void Accept(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                using var json = JsonDocument.Parse(line);
                var item = json.RootElement;
                if (item.ValueKind != JsonValueKind.Object) return;
                var route = ResolveUsageRoute(item);
                var provider = ReadString(route, "provider") ?? ReadString(item, "provider");
                if (string.IsNullOrWhiteSpace(provider)
                    || !TryResolveNativeAccountLabel(provider, _providerLabels, out var accountLabel)) return;
                var status = ReadHttpStatus(route) ?? ReadHttpStatus(item);
                if (status is not (>= 200 and < 300)) return;
                var model = ReadString(route, "resolvedModel")
                            ?? ReadString(route, "model")
                            ?? ReadString(item, "resolvedModel")
                            ?? ReadString(item, "model");
                if (string.IsNullOrWhiteSpace(model) || model.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    return;
                var timestamp = ReadLogTimestamp(item);
                if (timestamp is null) return;

                _nativeSuccessCount++;
                if (_lastBillingAt is null || timestamp > _lastBillingAt)
                {
                    _lastBillingAt = timestamp;
                    _lastBillingAccount = accountLabel;
                    _lastBillingProvider = provider;
                }

                if (!provider.Equals("openai", StringComparison.OrdinalIgnoreCase)) return;
                if (_proLastRequestAt is null || timestamp > _proLastRequestAt) _proLastRequestAt = timestamp;
                if (timestamp >= _switchedAt) _proSuccessfulRequestsSinceSwitch++;
            }
            catch (JsonException)
            {
                // OpenCodex may be appending the final JSONL line while it is being read.
            }
        }

        public NativeRoutingAudit Build() => new(
            _lastBillingAccount,
            _lastBillingProvider,
            _lastBillingAt,
            _proLastRequestAt,
            _proSuccessfulRequestsSinceSwitch,
            _switchedAt,
            _nativeSuccessCount,
            true,
            _nativeSuccessCount == 0
                ? "日志中还没有可确认扣费账号的成功模型请求。"
                : $"已从 {Path.GetFileName(_sourcePath)} 核对 {_nativeSuccessCount:N0} 条成功原生请求。 ");
    }

    private static IReadOnlyDictionary<string, string> ReadNativeProviderLabels(
        IReadOnlyList<CodexAccountView> accounts)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var main = accounts.FirstOrDefault(account => account.IsMain);
        labels["openai"] = AccountAuditLabel(main, "Pro");

        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".opencodex",
                "config.json");
            if (File.Exists(configPath))
            {
                using var config = JsonDocument.Parse(File.ReadAllText(configPath, Encoding.UTF8));
                if (config.RootElement.TryGetProperty("codexAccounts", out var rows)
                    && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        var id = ReadString(row, "id");
                        var logLabel = ReadString(row, "logLabel");
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(logLabel)) continue;
                        var account = accounts.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                        if (account is null) continue;
                        labels[$"openai-{logLabel}"] = AccountAuditLabel(account, "Codex 次账号");
                    }
                }
            }
        }
        catch
        {
            // The active API state remains usable even if the local config is temporarily locked.
        }

        var secondary = accounts.Where(account => !account.IsMain).ToArray();
        if (secondary.Length == 1)
            labels["openai-*"] = AccountAuditLabel(secondary[0], "Codex 次账号");
        return labels;
    }

    private static bool TryResolveNativeAccountLabel(
        string provider,
        IReadOnlyDictionary<string, string> providerLabels,
        out string label)
    {
        if (providerLabels.TryGetValue(provider, out label!)) return true;
        if (provider.StartsWith("openai-", StringComparison.OrdinalIgnoreCase)
            && providerLabels.TryGetValue("openai-*", out label!)) return true;
        label = string.Empty;
        return false;
    }

    private static string AccountAuditLabel(CodexAccountView? account, string fallback)
    {
        if (account is null || string.IsNullOrWhiteSpace(account.Plan)) return fallback;
        if (account.Plan.Equals("plus", StringComparison.OrdinalIgnoreCase)) return "Plus";
        if (account.Plan.Equals("pro", StringComparison.OrdinalIgnoreCase)) return "Pro";
        return account.Plan.ToUpperInvariant();
    }

    public async Task<UsageTimelineSnapshot> GetUsageTimelineAsync(
        int days = 365,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ResolveRequestLogPath();
        if (!File.Exists(sourcePath))
            return UsageTimelineSnapshot.Empty(sourcePath, "本机还没有总管家历史日志；产生请求后会自动出现。 ");

        try
        {
            var cutoff = DateOnly.FromDateTime(DateTime.Today.AddDays(-Math.Max(1, days) + 1));
            var daily = new Dictionary<DateOnly, UsageTimelineAccumulator>();
            var logCount = 0;
            long inputTotal = 0;
            long outputTotal = 0;
            long tokenTotal = 0;
            double costTotal = 0;
            DateTimeOffset? firstSeen = null;
            DateTimeOffset? lastSeen = null;

            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var item = json.RootElement;
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var timestamp = ReadLogTimestamp(item);
                    if (timestamp is null) continue;

                    var usage = TryGetPropertyIgnoreCase(item, "usage", out var usageElement)
                                && usageElement.ValueKind == JsonValueKind.Object
                        ? usageElement
                        : default;
                    var input = ReadLongAny(usage, "inputTokens", "promptTokens", "input_tokens", "prompt_tokens")
                                ?? ReadLongAny(item, "inputTokens", "promptTokens", "input_tokens", "prompt_tokens") ?? 0;
                    var output = ReadLongAny(usage, "outputTokens", "completionTokens", "output_tokens", "completion_tokens")
                                 ?? ReadLongAny(item, "outputTokens", "completionTokens", "output_tokens", "completion_tokens") ?? 0;
                    var total = ReadLong(usage, "totalTokens") ?? ReadLong(item, "totalTokens") ?? input + output;
                    var cost = ReadEstimatedCost(item);
                    var status = ReadHttpStatus(item);
                    logCount++;
                    inputTotal += input;
                    outputTotal += output;
                    tokenTotal += total;
                    costTotal += cost;
                    if (firstSeen is null || timestamp < firstSeen) firstSeen = timestamp;
                    if (lastSeen is null || timestamp > lastSeen) lastSeen = timestamp;

                    var date = DateOnly.FromDateTime(timestamp.Value.LocalDateTime);
                    if (date < cutoff) continue;
                    if (!daily.TryGetValue(date, out var accumulator))
                    {
                        accumulator = new UsageTimelineAccumulator(date);
                        daily[date] = accumulator;
                    }
                    accumulator.Add(status, input, output, total, cost);
                }
                catch (JsonException)
                {
                    // OpenCodex may be appending the final line while it is being read.
                }
            }

            return new UsageTimelineSnapshot(
                daily.Values.OrderBy(day => day.Date).Select(day => day.ToPoint()).ToArray(),
                logCount,
                inputTotal,
                outputTotal,
                tokenTotal,
                costTotal,
                firstSeen,
                lastSeen,
                sourcePath,
                true,
                logCount == 0 ? "日志文件存在，但还没有可统计的请求。" : "已读取总管家本机完整历史日志。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UsageTimelineSnapshot.Empty(sourcePath, $"历史日志读取失败：{ex.Message}");
        }
    }

    private static void AddQuotaWindow(
        ICollection<UsageWindowView> windows,
        JsonElement quota,
        string periodKey,
        string label,
        string percentName,
        string resetName)
    {
        var percent = ReadDouble(quota, percentName);
        if (percent is null) return;
        windows.Add(new UsageWindowView
        {
            PeriodKey = periodKey,
            Label = label,
            UsedPercent = percent.Value,
            ResetAtUtc = UsageFormatting.FromUnix(ReadLong(quota, resetName)),
            ResetState = UsageFormatting.FromUnix(ReadLong(quota, resetName)) is null ? QuotaResetState.NotProvided : QuotaResetState.Parsed,
            ResetText = UsageFormatting.Reset(UsageFormatting.FromUnix(ReadLong(quota, resetName)))
        });
    }

    private static JsonElement ResolveUsageRoute(JsonElement item)
    {
        if (!TryGetPropertyIgnoreCase(item, "attempts", out var attempts) || attempts.ValueKind != JsonValueKind.Array)
            return item;
        var rows = attempts.EnumerateArray().ToArray();
        for (var index = rows.Length - 1; index >= 0; index--)
            if (ReadHttpStatus(rows[index]) is >= 200 and < 300) return rows[index];
        return rows.Length > 0 ? rows[^1] : item;
    }

    private static double ReadEstimatedCost(JsonElement item)
    {
        if (!item.TryGetProperty("displayMetrics", out var metrics)
            || !metrics.TryGetProperty("cost", out var cost)
            || !cost.TryGetProperty("estimate", out var estimate)
            || !estimate.TryGetProperty("cost", out var costDetail)) return 0;
        return ReadDouble(costDetail, "total") ?? 0;
    }

    private static void AddUsage(
        IDictionary<string, UsageAccumulator> target,
        string key,
        string? model,
        int? status,
        long input,
        long output,
        long total,
        double cost,
        DateTimeOffset? timestamp,
        string? providerOverride = null)
    {
        if (!target.TryGetValue(key, out var accumulator))
        {
            accumulator = new UsageAccumulator(providerOverride ?? key, model);
            target[key] = accumulator;
        }
        accumulator.Add(status, input, output, total, cost, timestamp);
    }

    public async Task SetPreferredCodexAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(
            "/api/codex-auth/active",
            new { accountId },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetCodexAutoSwitchThresholdAsync(
        int threshold,
        CancellationToken cancellationToken = default)
    {
        if (threshold is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(threshold));
        using var response = await _http.PutAsJsonAsync(
            "/api/codex-auth/auto-switch",
            new { threshold },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetCodexFailoverThresholdAsync(
        int threshold,
        CancellationToken cancellationToken = default)
    {
        if (threshold is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(threshold));
        using var response = await _http.PutAsJsonAsync(
            "/api/codex-auth/failover",
            new { threshold },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<CodexAccountLoginStartResult> StartCodexAccountLoginAsync(
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/api/codex-auth/login", new { }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenCodexAccountApiUnavailableException("总管家本机引擎的账号登录接口无法连接。", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OpenCodexAccountApiUnavailableException("总管家本机引擎的账号登录接口响应超时。", ex);
        }

        using (response)
        {
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.MethodNotAllowed
                or System.Net.HttpStatusCode.NotImplemented)
            throw new OpenCodexAccountApiUnavailableException("当前总管家本机引擎没有提供账号登录接口。");
            await EnsureSuccessAsync(response, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var flowId = ReadString(json.RootElement, "flowId");
            if (string.IsNullOrWhiteSpace(flowId))
            throw new InvalidOperationException("总管家本机引擎没有返回登录流程编号。");
            return new CodexAccountLoginStartResult(flowId, ReadString(json.RootElement, "url"));
        }
    }

    public async Task<CodexAccountLoginStatusResult> GetCodexAccountLoginStatusAsync(
        string flowId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(flowId)) throw new ArgumentException("登录流程编号不能为空。", nameof(flowId));
        using var response = await _http.GetAsync(
            $"/api/codex-auth/login-status?flowId={Uri.EscapeDataString(flowId)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new CodexAccountLoginStatusResult(
            ReadString(json.RootElement, "status") ?? "unknown",
            ReadString(json.RootElement, "accountId"),
            ReadString(json.RootElement, "email"),
            ReadString(json.RootElement, "error"));
    }

    public async Task DeleteCodexAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("账号编号不能为空。", nameof(accountId));
        using var response = await _http.DeleteAsync(
            $"/api/codex-auth/accounts?id={Uri.EscapeDataString(accountId)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<(string Provider, string Model)?> GetActiveTargetAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/combos", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("combos", out var combos) || combos.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var combo in combos.EnumerateArray())
        {
            if (!string.Equals(ReadString(combo, "id"), SwitchComboId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!combo.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
                return null;
            var target = targets.EnumerateArray().FirstOrDefault();
            if (target.ValueKind != JsonValueKind.Object) return null;
            var provider = ReadString(target, "provider");
            var model = ReadString(target, "model");
            return string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)
                ? null
                : (provider, model);
        }
        return null;
    }

    public async Task<ActiveRoute?> GetActiveRouteAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/combos", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("combos", out var combos) || combos.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var combo in combos.EnumerateArray())
        {
            if (!string.Equals(ReadString(combo, "id"), SwitchComboId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!combo.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
                return null;
            var parsed = targets.EnumerateArray()
                .Select(target => (Provider: ReadString(target, "provider") ?? string.Empty,
                    Model: ReadString(target, "model") ?? string.Empty))
                .Where(target => !string.IsNullOrWhiteSpace(target.Provider) && !string.IsNullOrWhiteSpace(target.Model))
                .ToArray();
            return parsed.Length == 0 ? null : new ActiveRoute(parsed[0].Provider, parsed[0].Model, parsed);
        }
        return null;
    }

    public async Task<PoolRouteSnapshot?> GetPoolRouteAsync(
        string comboId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/combos", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("combos", out var combos) || combos.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var combo in combos.EnumerateArray())
        {
            var id = ReadString(combo, "id");
            if (!string.Equals(id, comboId, StringComparison.OrdinalIgnoreCase)) continue;
            var alias = ReadString(combo, "alias") ?? string.Empty;
            if (!combo.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
                return new PoolRouteSnapshot(id ?? comboId, alias, Array.Empty<(string Provider, string Model)>());
            var parsed = targets.EnumerateArray()
                .Select(target => (Provider: ReadString(target, "provider") ?? string.Empty,
                    Model: ReadString(target, "model") ?? string.Empty))
                .Where(target => !string.IsNullOrWhiteSpace(target.Provider) && !string.IsNullOrWhiteSpace(target.Model))
                .ToArray();
            return new PoolRouteSnapshot(id ?? comboId, alias, parsed);
        }
        return null;
    }

    public async Task SetActiveTargetAsync(
        string provider,
        string model,
        CancellationToken cancellationToken = default)
    {
        await UpsertPoolRouteAsync(
            SwitchComboId,
            SwitchAlias,
            new[] { (Provider: provider, Model: model) },
            cancellationToken);
    }

    public async Task UpsertPoolRouteAsync(
        string comboId,
        string alias,
        IReadOnlyList<(string Provider, string Model)> targets,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comboId) || string.IsNullOrWhiteSpace(alias) || targets.Count == 0)
            throw new InvalidOperationException("号池路由缺少 ID、别名或目标。");
        if (!IsInternalRouteAlias(alias))
            throw new InvalidOperationException("号池路由别名必须以 cmm/ 开头。");
        var providers = targets.Select(target => target.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (providers.Length != 1)
            throw new InvalidOperationException("一个号池的路由只能包含同一 provider，禁止跨池备用。");
        var payload = new
        {
            id = comboId,
            combo = new
            {
                alias,
                strategy = "failover",
                stickyLimit = 1,
                targets = targets.Select(target => new { provider = target.Provider, model = target.Model, weight = 1 }).ToArray()
            }
        };
        using var response = await _http.PutAsJsonAsync("/api/combos", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetCodexAccountModeAsync(
        string mode,
        CancellationToken cancellationToken = default)
    {
        if (mode is not "pool" and not "direct")
            throw new ArgumentOutOfRangeException(nameof(mode));
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            "/api/providers?name=openai")
        {
            Content = JsonContent.Create(new { codexAccountMode = mode })
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task AddProviderAsync(
        string provider,
        string baseUrl,
        string apiKeyReference,
        IReadOnlyList<string> models,
        string adapter,
        int contextWindow,
        bool allowPrivateNetwork,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            name = provider,
            setDefault = false,
            provider = new
            {
                adapter,
                baseUrl,
                authMode = "key",
                apiKey = apiKeyReference,
                models,
                liveModels = true,
                selectedModels = models,
                contextWindow,
                allowPrivateNetwork
            }
        };
        using var response = await _http.PostAsJsonAsync("/api/providers", payload, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<OperationResult> TestProviderAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await _http.PostAsync(
                $"/api/providers/test?name={Uri.EscapeDataString(provider)}",
                new StringContent("{}", Encoding.UTF8, "application/json"),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = ReadApiError(body);
                RememberProviderHealth(provider, false, error, clock.ElapsedMilliseconds);
                return OperationResult.Fail(error);
            }
            using var json = JsonDocument.Parse(body);
            var ok = ReadBool(json.RootElement, "ok");
            var message = ReadString(json.RootElement, "message")
                          ?? ReadString(json.RootElement, "error")
                          ?? (ok ? "连接成功" : "连接失败");
            RememberProviderHealth(provider, ok, message, clock.ElapsedMilliseconds);
            return new OperationResult(ok, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RememberProviderHealth(provider, false, ex.Message, clock.ElapsedMilliseconds);
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task SetProviderEnabledAsync(
        string provider,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/providers?name={Uri.EscapeDataString(provider)}")
        {
            Content = JsonContent.Create(new { disabled = !enabled })
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task PatchProviderAsync(
        string provider,
        string baseUrl,
        string adapter,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/providers?name={Uri.EscapeDataString(provider)}")
        {
            Content = JsonContent.Create(new { baseUrl, adapter, disabled, liveModels = true })
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<RuntimeRouteExecution?> GetLatestRouteExecutionAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/api/logs?limit=40", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return ParseLatestRouteExecution(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public static RuntimeRouteExecution? ParseLatestRouteExecution(string jsonText)
    {
        using var json = JsonDocument.Parse(jsonText);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return null;
        var rows = json.RootElement.EnumerateArray().ToArray();
        var objectRows = rows
            .Select((row, index) => (Row: row, Index: index, Timestamp: ReadLogTimestamp(row)))
            .Where(candidate => candidate.Row.ValueKind == JsonValueKind.Object)
            .ToArray();
        if (objectRows.Length == 0) return null;
        var allRowsHaveTimestamps = objectRows.All(candidate => candidate.Timestamp is not null);
        var selectedRow = allRowsHaveTimestamps
            ? objectRows
                .OrderBy(candidate => candidate.Timestamp)
                .ThenBy(candidate => candidate.Index)
                .Last()
            : objectRows.OrderBy(candidate => candidate.Index).Last();
        var selectionBasis = allRowsHaveTimestamps
            ? RuntimeLogSelectionBasis.Timestamp
            : RuntimeLogSelectionBasis.ArrayLastFallback;
        var item = selectedRow.Row;

        var requested = RuntimeTruthSanitizer.Redact(
                            ReadString(item, "requestedModel") ?? ReadString(item, "model"))
                        ?? "未知";
        var rawAttempts = TryGetPropertyIgnoreCase(item, "attempts", out var attempts)
                          && attempts.ValueKind == JsonValueKind.Array
            ? attempts.EnumerateArray().Where(attempt => attempt.ValueKind == JsonValueKind.Object).ToArray()
            : Array.Empty<JsonElement>();
        if (rawAttempts.Length == 0) rawAttempts = new[] { item };

        var selectedIndex = FindExplicitSelectedAttempt(rawAttempts);
        var selectionEvidence = selectedIndex >= 0
            ? RuntimeAttemptSelectionEvidence.ExplicitFlag
            : RuntimeAttemptSelectionEvidence.None;
        for (var index = rawAttempts.Length - 1; selectedIndex < 0 && index >= 0; index--)
        {
            if (ReadHttpStatus(rawAttempts[index]) is >= 200 and < 300)
            {
                selectedIndex = index;
                selectionEvidence = rawAttempts.Length == 1
                    ? RuntimeAttemptSelectionEvidence.SingleAttempt
                    : RuntimeAttemptSelectionEvidence.Http2xxFallback;
                break;
            }
        }
        if (selectedIndex < 0)
        {
            selectedIndex = rawAttempts.Length - 1;
            selectionEvidence = rawAttempts.Length == 1
                ? RuntimeAttemptSelectionEvidence.SingleAttempt
                : RuntimeAttemptSelectionEvidence.ArrayLastFallback;
        }

        var requestLevelUsage = rawAttempts.Length > 1
            ? ReadTokenUsage(item, "execution.usage")
            : null;

        var parsedAttempts = new List<RuntimeRouteAttempt>(rawAttempts.Length);
        for (var index = 0; index < rawAttempts.Length; index++)
        {
            var attempt = rawAttempts[index];
            var provider = RuntimeTruthSanitizer.Redact(
                               ReadString(attempt, "provider") ?? ReadString(item, "provider"))
                           ?? "unknown";
            var explicitAccountId = ReadString(attempt, "accountId")
                                    ?? ReadString(attempt, "account_id");
            if (rawAttempts.Length == 1)
                explicitAccountId ??= ReadString(item, "accountId") ?? ReadString(item, "account_id");
            var accountIdentity = ResolveAccountIdentity(
                provider,
                explicitAccountId,
                allowProviderRouteFallback: rawAttempts.Length == 1);
            var model = RuntimeTruthSanitizer.Redact(
                            ReadString(attempt, "resolvedModel")
                            ?? ReadString(attempt, "model")
                            ?? ReadString(item, "resolvedModel")
                            ?? ReadString(item, "model"))
                        ?? "未知模型";
            var status = ReadHttpStatus(attempt)
                         ?? (rawAttempts.Length == 1 ? ReadHttpStatus(item) : null);
            var errorCode = RuntimeTruthSanitizer.Redact(ReadErrorCode(attempt) ?? ReadErrorCode(item));
            var errorMessage = RuntimeTruthSanitizer.Redact(ReadErrorMessage(attempt) ?? ReadErrorMessage(item));
            var tokenUsage = ReadTokenUsage(attempt, "attempt.usage");
            if (tokenUsage is null && rawAttempts.Length == 1 && index == selectedIndex)
                tokenUsage = ReadTokenUsage(item, "execution.usage");
            parsedAttempts.Add(new RuntimeRouteAttempt(
                index + 1,
                provider,
                ToProviderDisplayName(provider),
                accountIdentity.AccountId,
                accountIdentity.DisplayName,
                accountIdentity.Source,
                model,
                status,
                ReadLong(attempt, "durationMs"),
                errorCode,
                errorMessage,
                ClassifyFailoverReason(status, errorCode, errorMessage),
                index == selectedIndex,
                index == selectedIndex ? selectionEvidence : RuntimeAttemptSelectionEvidence.None,
                tokenUsage,
                accountIdentity.IdentityMaterial));
        }

        var actual = parsedAttempts[selectedIndex];
        var topStatus = ReadHttpStatus(item) ?? actual.HttpStatus;
        var outcomeStatus = topStatus ?? actual.HttpStatus;
        var topErrorCode = RuntimeTruthSanitizer.Redact(ReadErrorCode(item) ?? actual.ErrorCode);
        var topErrorMessage = RuntimeTruthSanitizer.Redact(ReadErrorMessage(item) ?? actual.ErrorMessage);
        var topReason = ClassifyFailoverReason(outcomeStatus, topErrorCode, topErrorMessage);
        var outcome = outcomeStatus is >= 200 and < 300
            ? RuntimeExecutionOutcome.Succeeded
            : topReason == RuntimeFailoverReason.Cancelled
                ? RuntimeExecutionOutcome.Cancelled
                : outcomeStatus is null && string.IsNullOrWhiteSpace(topErrorCode) && string.IsNullOrWhiteSpace(topErrorMessage)
                    ? RuntimeExecutionOutcome.Unknown
                    : RuntimeExecutionOutcome.Failed;
        var rawRequestIdentity = ReadIdentifier(item, "requestId") ?? ReadIdentifier(item, "id");
        return new RuntimeRouteExecution(
            RuntimeTruthSanitizer.Redact(rawRequestIdentity),
            requested,
            topStatus,
            ReadLong(item, "durationMs") ?? actual.DurationMs,
            ReadLogTimestamp(item),
            outcome,
            topErrorCode,
            topErrorMessage,
            selectionBasis,
            selectedRow.Index,
            parsedAttempts,
            requestLevelUsage,
            rawRequestIdentity);
    }

    public static string ToProviderDisplayName(string provider) =>
        provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? "OpenAI 主账号"
            : provider.StartsWith("openai-", StringComparison.OrdinalIgnoreCase)
                ? "OpenAI 次账号"
                : provider.Equals("combo", StringComparison.OrdinalIgnoreCase) ? "组合路由" : provider;

    public async Task<RecentRouteResult> GetRecentRouteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var execution = await GetLatestRouteExecutionAsync(cancellationToken);
            if (execution is null)
                return new RecentRouteResult(false, string.Empty, string.Empty, string.Empty, string.Empty, null, null, null, "还没有模型请求记录");
            var actual = execution.ActualAttempt;
            if (actual is null)
                return new RecentRouteResult(false, execution.RequestedModel, string.Empty, string.Empty, string.Empty,
                    execution.HttpStatus, execution.DurationMs, execution.Timestamp, "最近记录没有可识别的实际尝试");
            var status = execution.HttpStatus ?? actual.HttpStatus;
            var ok = status is >= 200 and < 300;
            var upstreamError = execution.ErrorMessage ?? actual.ErrorMessage;
            var capacityError = !string.IsNullOrWhiteSpace(upstreamError)
                                && (upstreamError.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
                                    || upstreamError.Contains("capacity", StringComparison.OrdinalIgnoreCase));
            var message = ok
                ? $"实际入口：{actual.ProviderDisplayName}/{actual.Model} · HTTP {status}"
                : capacityError
                    ? $"最近请求异常：{actual.ProviderDisplayName}/{actual.Model} 当前容量已满 · HTTP {status?.ToString() ?? "未知"}"
                    : $"最近请求异常：{actual.ProviderDisplayName}/{actual.Model} · HTTP {status?.ToString() ?? "未知"}";
            return new RecentRouteResult(
                true,
                execution.RequestedModel,
                actual.ProviderDisplayName,
                actual.ProviderId,
                actual.Model,
                status,
                execution.DurationMs,
                execution.Timestamp,
                message);
        }
        catch (Exception ex)
        {
            return new RecentRouteResult(false, string.Empty, string.Empty, string.Empty, string.Empty, null, null, null,
                RuntimeTruthSanitizer.Redact(ex.Message) ?? "读取最近实际执行失败");
        }
    }

    private static (string? AccountId, string DisplayName, RuntimeAccountIdentitySource Source, string? IdentityMaterial) ResolveAccountIdentity(
        string provider,
        string? explicitAccountId,
        bool allowProviderRouteFallback)
    {
        if (!string.IsNullOrWhiteSpace(explicitAccountId))
        {
            var safeId = RuntimeTruthSanitizer.Redact(explicitAccountId) ?? "账号未知";
            return (safeId, safeId, RuntimeAccountIdentitySource.ExplicitAccountId, explicitAccountId);
        }
        if (allowProviderRouteFallback
            && !string.IsNullOrWhiteSpace(provider)
            && !provider.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            && !provider.Equals("combo", StringComparison.OrdinalIgnoreCase))
            return (provider, ToProviderDisplayName(provider), RuntimeAccountIdentitySource.ProviderRoute, null);
        return (null, "账号未知", RuntimeAccountIdentitySource.Unknown, null);
    }

    private static int FindExplicitSelectedAttempt(IReadOnlyList<JsonElement> attempts)
    {
        foreach (var name in new[] { "selected", "winner", "used" })
        {
            var selectedIndex = -1;
            for (var index = 0; index < attempts.Count; index++)
                if (ReadBool(attempts[index], name)) selectedIndex = index;
            if (selectedIndex >= 0) return selectedIndex;
        }
        return -1;
    }

    private static RuntimeFailoverReason ClassifyFailoverReason(
        int? status,
        string? errorCode,
        string? errorMessage)
    {
        if (status is >= 200 and < 300) return RuntimeFailoverReason.None;
        if (status == 499) return RuntimeFailoverReason.Cancelled;
        if (status == 401) return RuntimeFailoverReason.Authentication;
        if (status == 403) return RuntimeFailoverReason.Permission;
        if (status == 429) return RuntimeFailoverReason.RateLimit;

        var detail = $"{errorCode} {errorMessage}";
        if (detail.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return RuntimeFailoverReason.RateLimit;
        if (detail.Contains("overload", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("capacity", StringComparison.OrdinalIgnoreCase)) return RuntimeFailoverReason.Capacity;
        if (detail.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("invalid credential", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)) return RuntimeFailoverReason.Authentication;
        if (detail.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("authorization_denied", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("access_denied", StringComparison.OrdinalIgnoreCase)) return RuntimeFailoverReason.Permission;
        if (detail.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("context", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("window", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("length", StringComparison.OrdinalIgnoreCase))) return RuntimeFailoverReason.ContextWindow;
        if (detail.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("operation_canceled", StringComparison.OrdinalIgnoreCase)) return RuntimeFailoverReason.Cancelled;
        if (detail.Contains("connect", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return RuntimeFailoverReason.Connectivity;
        return status is null ? RuntimeFailoverReason.Unknown : RuntimeFailoverReason.HttpFailure;
    }

    private static string? ReadErrorCode(JsonElement item)
    {
        var direct = ReadString(item, "errorCode") ?? ReadString(item, "error_code");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        foreach (var name in new[] { "upstreamError", "error" })
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(name, out var error)
                && error.ValueKind == JsonValueKind.Object)
                return ReadString(error, "code") ?? ReadString(error, "type");
        return null;
    }

    private static string? ReadErrorMessage(JsonElement item)
    {
        var direct = ReadString(item, "upstreamError")
                     ?? ReadString(item, "errorMessage")
                     ?? ReadString(item, "message")
                     ?? ReadString(item, "error");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        foreach (var name in new[] { "upstreamError", "error" })
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(name, out var error)
                && error.ValueKind == JsonValueKind.Object)
                return ReadString(error, "message") ?? ReadString(error, "detail");
        return null;
    }

    public async Task DeleteProviderAsync(string provider, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(
            $"/api/providers?name={Uri.EscapeDataString(provider)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private void RememberProviderHealth(string provider, bool success, string message, long latencyMs) =>
        _providerHealth[provider] = new ProviderHealthState(
            success,
            message,
            latencyMs,
            DateTimeOffset.Now);

    private static JsonElement UnwrapArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.Array) return value;
            throw new InvalidOperationException("总管家本机引擎返回了无法识别的数据。");
    }

    private static async Task ObserveAndDisposeAsync(Task<HttpResponseMessage> task)
    {
        try { using var response = await task.ConfigureAwait(false); }
        catch { }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ReadApiError(body));
    }

    private static string ReadApiError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var direct = ReadString(root, "message") ?? ReadString(root, "error");
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var message = ReadString(error, "message") ?? ReadString(error, "detail");
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
            return $"请求失败：{body}";
        }
        catch
        {
        return string.IsNullOrWhiteSpace(body) ? "请求失败，请检查总管家本机引擎。" : body;
        }
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && TryGetPropertyIgnoreCase(item, name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadIdentifier(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(item, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static AttemptTokenUsageFact? ReadTokenUsage(JsonElement item, string sourcePath)
    {
        var usage = item.ValueKind == JsonValueKind.Object
                    && TryGetPropertyIgnoreCase(item, "usage", out var usageElement)
                    && usageElement.ValueKind == JsonValueKind.Object
            ? usageElement
            : item;
        if (usage.ValueKind != JsonValueKind.Object) return null;

        var input = ReadLongAny(usage, "inputTokens", "input_tokens", "promptTokens", "prompt_tokens");
        var cached = ReadLongAny(usage, "cachedInputTokens", "cached_input_tokens", "cachedTokens", "cached_tokens");
        var cacheRead = ReadLongAny(usage, "cacheReadInputTokens", "cache_read_input_tokens");
        var cacheCreation = ReadLongAny(usage, "cacheCreationInputTokens", "cache_creation_input_tokens");
        var output = ReadLongAny(usage, "outputTokens", "output_tokens", "completionTokens", "completion_tokens");
        var reasoning = ReadLongAny(usage, "reasoningOutputTokens", "reasoning_output_tokens", "reasoningTokens", "reasoning_tokens");
        var upstreamTotal = ReadLongAny(usage, "totalTokens", "total_tokens")
                            ?? ReadLongAny(item, "totalTokens", "total_tokens");

        if (usage.TryGetProperty("input_tokens_details", out var inputDetails)
            && inputDetails.ValueKind == JsonValueKind.Object)
            cached ??= ReadLongAny(inputDetails, "cached_tokens", "cachedTokens");
        if (usage.TryGetProperty("output_tokens_details", out var outputDetails)
            && outputDetails.ValueKind == JsonValueKind.Object)
            reasoning ??= ReadLongAny(outputDetails, "reasoning_tokens", "reasoningTokens");

        if (input is null && cached is null && cacheRead is null && cacheCreation is null
            && output is null && reasoning is null && upstreamTotal is null) return null;

        var total = upstreamTotal;
        var totalSource = upstreamTotal is not null ? TokenTotalSource.Upstream : TokenTotalSource.Unknown;
        if (total is null && input is not null && output is not null)
        {
            try
            {
                total = checked(input.Value + output.Value);
                totalSource = TokenTotalSource.DerivedInputOutput;
            }
            catch (OverflowException)
            {
                total = null;
            }
        }

        var values = new[] { input, cached, cacheRead, cacheCreation, output, reasoning, total };
        TokenTotalValidationState validation;
        string validationMessage;
        if (values.Any(value => value is < 0))
        {
            validation = TokenTotalValidationState.InvalidValue;
            validationMessage = "上游 Token 字段包含负值；原值保留但不进入汇总";
        }
        else if (upstreamTotal is not null && input is not null && output is not null)
        {
            validation = (decimal)upstreamTotal.Value == (decimal)input.Value + output.Value
                ? TokenTotalValidationState.Valid
                : TokenTotalValidationState.Mismatch;
            validationMessage = validation == TokenTotalValidationState.Valid
                ? "upstream total 与 input+output 一致"
                : "upstream total 与 input+output 不一致；保留上游 total";
        }
        else if (totalSource == TokenTotalSource.DerivedInputOutput)
        {
            validation = TokenTotalValidationState.Valid;
            validationMessage = "upstream total 缺失；由 input+output 派生，cached/reasoning 未重复相加";
        }
        else
        {
            validation = TokenTotalValidationState.Unknown;
            validationMessage = "缺少足够字段，无法校验 total";
        }

        return new AttemptTokenUsageFact(
            input,
            cached,
            cacheRead,
            cacheCreation,
            output,
            reasoning,
            total,
            totalSource,
            validation,
            validationMessage,
            sourcePath);
    }

    private static long? ReadLongAny(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadLong(item, name);
            if (value is not null) return value;
        }
        return null;
    }

    private static bool ReadBool(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && TryGetPropertyIgnoreCase(item, name, out var value)
        && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        && value.GetBoolean();

    private static long? ReadLong(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && TryGetPropertyIgnoreCase(item, name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? ReadInt(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && TryGetPropertyIgnoreCase(item, name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && TryGetPropertyIgnoreCase(item, name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(item, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            return parsed.ToUniversalTime();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(number); }
            catch { return null; }
        }
        return null;
    }

    private string ResolveRequestLogPath() => _requestLogPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexTotalManager", "runtime-v3", "native-proxy", "request-log.jsonl");

    private static int? ReadHttpStatus(JsonElement item) =>
        ReadInt(item, "status") ?? ReadInt(item, "httpStatus");

    private static DateTimeOffset? ReadLogTimestamp(JsonElement item) =>
        ReadDateTimeOffset(item, "timestamp") ?? ReadDateTimeOffset(item, "startedAt");

    private static bool TryGetPropertyIgnoreCase(JsonElement item, string name, out JsonElement value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty(name, out value)) return true;
            foreach (var property in item.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed record ProviderHealthState(
        bool Success,
        string Message,
        long LatencyMs,
        DateTimeOffset CheckedAt);

    private sealed class UsageAccumulator
    {
        private readonly string _provider;
        private readonly string? _model;
        private int _requests;
        private int _successes;
        private long _input;
        private long _output;
        private long _total;
        private double _cost;
        private DateTimeOffset? _lastSeen;

        public UsageAccumulator(string provider, string? model)
        {
            _provider = provider;
            _model = model;
        }

        public void Add(int? status, long input, long output, long total, double cost, DateTimeOffset? timestamp)
        {
            _requests++;
            if (status is >= 200 and < 300) _successes++;
            _input += input;
            _output += output;
            _total += total;
            _cost += cost;
            if (timestamp is not null && (_lastSeen is null || timestamp > _lastSeen)) _lastSeen = timestamp;
        }

        public LocalUsageSummary ToSummary() =>
            new(_provider, _model, _requests, _successes, _input, _output, _total, _cost, _lastSeen);
    }

    private sealed class UsageTimelineAccumulator
    {
        private int _requests;
        private int _successes;
        private long _input;
        private long _output;
        private long _total;
        private double _cost;

        public UsageTimelineAccumulator(DateOnly date) => Date = date;

        public DateOnly Date { get; }

        public void Add(int? status, long input, long output, long total, double cost)
        {
            _requests++;
            if (status is >= 200 and < 300) _successes++;
            _input += input;
            _output += output;
            _total += total;
            _cost += cost;
        }

        public DailyUsagePoint ToPoint() =>
            new(Date, _requests, _successes, _input, _output, _total, _cost);
    }

    private sealed class LiveUsageAccumulator
    {
        private readonly string _key;
        private readonly string _displayName;
        private long _todayInput;
        private long _todayOutput;
        private long _todayTotal;
        private long _weekInput;
        private long _weekOutput;
        private long _weekTotal;
        private long _input;
        private long _output;
        private long _total;
        private int _requests;
        private DateTimeOffset? _lastSeen;

        public LiveUsageAccumulator(string key, string displayName)
        {
            _key = key;
            _displayName = displayName;
        }

        public void Add(long input, long output, long total, DateTimeOffset? timestamp)
        {
            _requests++;
            _input += input;
            _output += output;
            _total += total;
            if (timestamp?.LocalDateTime.Date == DateTime.Today)
            {
                _todayInput += input;
                _todayOutput += output;
                _todayTotal += total;
            }
            if (timestamp is not null && timestamp.Value.LocalDateTime >= DateTime.Now.AddDays(-7))
            {
                _weekInput += input;
                _weekOutput += output;
                _weekTotal += total;
            }
            if (timestamp is not null && (_lastSeen is null || timestamp > _lastSeen)) _lastSeen = timestamp;
        }

        public LiveTokenUsageView ToView(string source, bool available) => new(
            _key,
            _displayName,
            _todayInput,
            _todayOutput,
            _todayTotal,
            _weekInput,
            _weekOutput,
            _weekTotal,
            _input,
            _output,
            _total,
            _requests,
            _requests,
            _lastSeen,
            source,
            available);
    }
}
