using CodexModelManager.Services;
using CodexModelManager.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodexOpenCodexNative.Models;
using CodexOpenCodexNative.Responses;

try
{
var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "unit";
if (mode == "unit")
{
    await RunUnitTestsAsync();
    return;
}

if (mode == "ledger-perf-generate")
{
    var ledgerRoot = Path.GetFullPath(args[1]);
    var count = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    Directory.CreateDirectory(ledgerRoot);
    using var ledger = new AccountUsageLedgerService(ledgerRoot, Path.Combine(ledgerRoot, "disabled"),
        () => DateTimeOffset.Parse("2026-08-01T08:00:00Z"), sourceDisabled: true);
    for (var offset = 0; offset < count; offset += 2000)
    {
        var batch = Enumerable.Range(offset, Math.Min(2000, count - offset))
            .Select(index => LedgerPerformanceExecution(index)).ToArray();
        await ledger.IngestExecutionsAsync(batch);
    }
    var snapshot = await ledger.ReadAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        marker = "LEDGER_PERF_GENERATE_OK",
        snapshot.StoredAttemptCount,
        snapshot.Accounts.Single().RequestCount,
        ledger.Diagnostics.InMemoryFactObjectCount,
        ledger.Diagnostics.CompactIdempotencyEntryCount,
        ledger.Diagnostics.CheckpointPublishCount
    }));
    return;
}

if (mode == "ledger-perf-read")
{
    var ledgerRoot = Path.GetFullPath(args[1]);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var managedBefore = GC.GetTotalMemory(true);
    var workingBefore = Process.GetCurrentProcess().WorkingSet64;
    var timer = Stopwatch.StartNew();
    using var ledger = new AccountUsageLedgerService(ledgerRoot, Path.Combine(ledgerRoot, "disabled"),
        () => DateTimeOffset.Parse("2026-08-01T08:00:00Z"), sourceDisabled: true);
    var snapshot = await ledger.ReadAsync();
    timer.Stop();
    var managedAfter = GC.GetTotalMemory(true);
    var workingAfter = Process.GetCurrentProcess().WorkingSet64;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        marker = "LEDGER_PERF_READ_OK",
        snapshot.StoredAttemptCount,
        snapshot.Accounts.Single().RequestCount,
        elapsedMs = timer.ElapsedMilliseconds,
        managedDelta = Math.Max(0, managedAfter - managedBefore),
        workingSetDelta = Math.Max(0, workingAfter - workingBefore),
        diagnostics = ledger.Diagnostics
    }));
    return;
}

if (mode == "ledger-perf-append")
{
    var ledgerRoot = Path.GetFullPath(args[1]);
    var count = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    using var ledger = new AccountUsageLedgerService(ledgerRoot, Path.Combine(ledgerRoot, "disabled"),
        () => DateTimeOffset.Parse("2026-08-01T08:00:00Z"), sourceDisabled: true);
    var beforeSnapshot = await ledger.ReadAsync();
    var before = ledger.Diagnostics;
    var timer = Stopwatch.StartNew();
    for (var index = 0; index < count; index++)
        await ledger.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(beforeSnapshot.StoredAttemptCount + index) });
    var snapshot = await ledger.ReadAsync();
    timer.Stop();
    var after = ledger.Diagnostics;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        marker = "LEDGER_PERF_APPEND_OK",
        snapshot.StoredAttemptCount,
        elapsedMs = timer.ElapsedMilliseconds,
        derivedBytesWritten = after.DerivedIndexBytesWritten - before.DerivedIndexBytesWritten,
        derivedReplacements = after.DerivedIndexReplacementCount - before.DerivedIndexReplacementCount,
        projectionRows = after.AttemptProjectionRowsProcessed - before.AttemptProjectionRowsProcessed,
        retainedFacts = after.InMemoryFactObjectCount
    }));
    return;
}

if (mode == "ledger-checkpoint-content")
{
    await AssertProjectionCheckpointContentCommitmentAsync();
    Console.WriteLine("LEDGER_CHECKPOINT_CONTENT_OK same_inode_same_length cross_ledger_copy truncate append seal_tamper_sha_rewrite identity_key_change no_change");
    return;
}

if (mode == "ledger-checkpoint-location-publication")
{
    await AssertProjectionCheckpointSameKeyRelocationFailsClosedAsync();
    await AssertProjectionCheckpointIsNotPublishedBeforeCurrentLedgerRefreshAsync();
    Console.WriteLine("LEDGER_CHECKPOINT_LOCATION_PUBLICATION_OK same_key_relocation current_refresh_before_publish");
    return;
}

if (mode == "ledger-recovery-regression")
{
    await AssertLedgerRecoveryRegressionsAsync();
    Console.WriteLine("LEDGER_RECOVERY_REGRESSION_OK projection_retry tail_newline tail_truncate direct_reorder quota_read_recovery quota_atomic_rebuild");
    return;
}

if (mode == "catalog-startup-isolation")
{
    var isolationRoot = CreateOwnedTestRoot("cmm-catalog-startup-isolation");
    try
    {
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "null-pools",
            """{"SchemaVersion":2,"Pools":null,"Active":{"PoolId":"official-pro","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "null-active",
            """{"SchemaVersion":2,"Pools":[],"Active":null}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "null-active-pool-id",
            """{"SchemaVersion":2,"Pools":[{"Id":"official-pro","DisplayName":"官方保底","Transport":"OfficialCodex"}],"Active":{"PoolId":null,"Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "duplicate-pool-ids",
            """{"SchemaVersion":2,"Pools":[{"Id":"duplicate","DisplayName":"One","Transport":"OfficialCodex"},{"Id":"DUPLICATE","DisplayName":"Two","Transport":"NativeCodexAccount"}],"Active":{"PoolId":"duplicate","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "unsafe-cli-id",
            """{"SchemaVersion":2,"Pools":[{"Id":"..","DisplayName":"Unsafe CLI","Transport":"CliProxyApi","ProviderId":"unsafe","BaseUrl":"http://127.0.0.1:8400/v1","LocalPort":8400}],"Active":{"PoolId":"..","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "alternate-loopback",
            """{"SchemaVersion":2,"Pools":[{"Id":"alt-cli","DisplayName":"Alt CLI","Transport":"CliProxyApi","ProviderId":"cmm-alt-cli","BaseUrl":"http://localhost:8401/v1","LocalPort":8401}],"Active":{"PoolId":"alt-cli","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "reserved-provider-slot",
            """{"SchemaVersion":2,"Pools":[{"Id":"slot-cli","DisplayName":"Slot CLI","Transport":"CliProxyApi","ProviderId":"cmm-test-worker","BaseUrl":"http://127.0.0.1:8402/v1","LocalPort":8402}],"Active":{"PoolId":"slot-cli","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "duplicate-provider-slot",
            """{"SchemaVersion":2,"Pools":[{"Id":"cli-a","DisplayName":"CLI A","Transport":"CliProxyApi","ProviderId":"cmm-cli-a","BaseUrl":"http://127.0.0.1:8403/v1","LocalPort":8403},{"Id":"custom-a","DisplayName":"Custom A","Transport":"OpenAiCompatible","ProviderId":"cmm-cli-a","BaseUrl":"https://example.invalid/v1"}],"Active":{"PoolId":"cli-a","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            isolationRoot,
            "duplicate-cli-port",
            """{"SchemaVersion":2,"Pools":[{"Id":"port-a","DisplayName":"Port A","Transport":"CliProxyApi","ProviderId":"cmm-port-a","BaseUrl":"http://127.0.0.1:8404/v1","LocalPort":8404},{"Id":"port-b","DisplayName":"Port B","Transport":"CliProxyApi","ProviderId":"cmm-port-b","BaseUrl":"http://127.0.0.1:8404/v1","LocalPort":8404}],"Active":{"PoolId":"port-a","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogCaseInsensitiveJsonAsync(isolationRoot);
        Console.WriteLine("CATALOG_STARTUP_ISOLATION_OK cases=9 case_insensitive=1 writes=0 network_calls=0 model_calls=0");
    }
    finally
    {
        DeleteOwnedTestRoot(isolationRoot);
    }
    return;
}

if (mode == "security-boundaries")
{
    await RunSecurityBoundaryTestsAsync();
    return;
}

if (mode == "config-safety")
{
    var configPath = args.ElementAtOrDefault(1)
                     ?? throw new ArgumentException("config-safety 需要一个 config.toml 路径。");
    var bytes = await File.ReadAllBytesAsync(configPath);
    var strictUtf8 = new UTF8Encoding(false, true);
    string configText;
    try
    {
        configText = strictUtf8.GetString(bytes);
    }
    catch (DecoderFallbackException)
    {
        Console.WriteLine($"CONFIG_SAFETY utf8=false length={bytes.Length} sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}");
        Environment.ExitCode = 2;
        return;
    }

    var inspection = ManagedTomlBlockEditor.InspectCodexSafetySettings(configText);
    Console.WriteLine(
        $"CONFIG_SAFETY utf8=true syntax={inspection.SyntaxValid.ToString().ToLowerInvariant()} "
        + $"custom={inspection.UnsafeCustomProvider.ToString().ToLowerInvariant()} "
        + $"agents_disabled={inspection.AgentsExplicitlyDisabled.ToString().ToLowerInvariant()} "
        + $"managed_mcp={configText.Contains(ManagedTomlBlockEditor.BeginMarker, StringComparison.Ordinal).ToString().ToLowerInvariant()} "
        + $"length={bytes.Length} sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}");
    Environment.ExitCode = inspection.SyntaxValid && !inspection.UnsafeCustomProvider ? 0 : 2;
    return;
}

if (mode == "codex-validator")
{
    var configPath = args.ElementAtOrDefault(1)
                     ?? throw new ArgumentException("codex-validator 需要一个 config.toml 路径。");
    var validationDataDirectory = args.ElementAtOrDefault(2)
                                  ?? Path.Combine(Path.GetTempPath(), "cmm-codex-validator-cli");
    var agentDirectory = args.ElementAtOrDefault(3);
    IReadOnlyDictionary<string, byte[]>? agentFiles = null;
    if (!string.IsNullOrWhiteSpace(agentDirectory))
    {
        agentFiles = Directory
            .EnumerateFiles(agentDirectory, "*.toml", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                path => Path.GetFileName(path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
    }
    var validator = new CodexCliConfigurationValidator(validationDataDirectory);
    var result = await validator.ValidateAsync(await File.ReadAllBytesAsync(configPath), agentFiles);
    Console.WriteLine(
        $"CODEX_VALIDATOR available={result.ValidatorAvailable.ToString().ToLowerInvariant()} "
        + $"valid={result.IsValid.ToString().ToLowerInvariant()} status={result.StatusText}");
    Environment.ExitCode = result.ValidatorAvailable && result.IsValid ? 0 : 2;
    return;
}

if (mode == "mcp-deployment-selftest")
{
    var executablePath = args.ElementAtOrDefault(1)
                         ?? throw new ArgumentException("mcp-deployment-selftest 需要总管家可执行文件路径。");
    var selfTestDataDirectory = CreateOwnedTestRoot("cmm-mcp-deployment-selftest");
    try
    {
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("--external-worker-mcp");
        start.ArgumentList.Add("--self-test-data-directory");
        start.ArgumentList.Add(selfTestDataDirectory);
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("无法启动已部署的 MCP 进程。");
        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"deployment-zero-quota-check\",\"version\":\"1.0\"}}}");
        await process.StandardInput.FlushAsync();
        var initializeLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var initializeJson = JsonDocument.Parse(
            initializeLine ?? throw new InvalidDataException("MCP initialize 没有返回结果。"));
        if (!initializeJson.RootElement.TryGetProperty("result", out var initializeResult))
            throw new InvalidDataException($"MCP initialize 返回错误：{initializeLine}");
        var serverName = initializeResult
            .GetProperty("serverInfo")
            .GetProperty("name")
            .GetString();

        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
        await process.StandardInput.FlushAsync();
        var toolsLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var toolsJson = JsonDocument.Parse(
            toolsLine ?? throw new InvalidDataException("MCP tools/list 没有返回结果。"));
        var tools = toolsJson.RootElement.GetProperty("result").GetProperty("tools");

        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(process.ExitCode == 0
               && serverName == "codex-total-manager-external-worker"
               && tools.GetArrayLength() == 1
               && tools[0].GetProperty("name").GetString() == ExternalWorkerMcpHost.ToolName,
            $"已部署 MCP 自检不匹配：exit={process.ExitCode}, server={serverName}, tools={tools.GetArrayLength()}。");
        Console.WriteLine(
            $"MCP_DEPLOYED_SELFTEST_OK exit={process.ExitCode} server={serverName} "
            + $"tool={tools[0].GetProperty("name").GetString()} isolated_data=true model_calls=0");
    }
    finally
    {
        DeleteOwnedTestRoot(selfTestDataDirectory);
    }
    return;
}


if (mode is "plus-sol-chat" or "plus-terra-chat")
{
    var plusServices = AppServices.Create();
    var pool = CreateLegacyPlusApiTool();
    var key = plusServices.Secrets.Read(pool.ProviderId!)
              ?? throw new InvalidOperationException("没有找到 Plus API 客户端密钥。");
    using var client = new HttpClient
    {
        BaseAddress = new Uri(pool.BaseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(120)
    };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    key = string.Empty;
    var requestedModel = mode == "plus-terra-chat" ? "gpt-5.6-terra" : "gpt-5.6-sol";
    using var response = await client.PostAsJsonAsync("responses", new
    {
        model = requestedModel,
        input = "Reply with exactly OK.",
        max_output_tokens = 16,
        stream = false
    });
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"Plus Sol 真实请求失败：HTTP {(int)response.StatusCode}。");
    using var json = JsonDocument.Parse(body);
    var resolvedModel = json.RootElement.TryGetProperty("model", out var modelValue)
        ? modelValue.GetString()
        : null;
    var id = json.RootElement.TryGetProperty("id", out var idValue)
        ? idValue.GetString()
        : null;
    Console.WriteLine($"PLUS_MODEL_CHAT_OK requested={requestedModel} resolved={resolvedModel ?? "(not-returned)"} response_id={(!string.IsNullOrWhiteSpace(id))} http={(int)response.StatusCode}");
    if (string.IsNullOrWhiteSpace(id)) Environment.ExitCode = 1;
    return;
}


if (mode == "native-account-state")
{
    var nativeServices = AppServices.Create();
    var views = await nativeServices.AccountPools.ReadViewsAsync();
    var accountData = await nativeServices.OpenCodex.GetCodexAccountsAsync();
    var nativePools = views.Where(view => view.SectionOrder == 0).ToArray();
    var official = nativeServices.PoolCatalog.Find(CodexModelManager.Models.PoolCatalogDefaults.OfficialPoolId)
                   ?? throw new InvalidOperationException("没有找到官方 Pro 原生账号卡片。");
    var plus = nativeServices.PoolCatalog.Find(CodexModelManager.Models.PoolCatalogDefaults.PlusPoolId)
               ?? throw new InvalidOperationException("没有找到 Plus 原生账号卡片。");
    if (accountData.Accounts.Count == 1 && accountData.Accounts[0].IsMain)
    {
        if (plus.Enabled || !string.IsNullOrWhiteSpace(plus.NativeAccountId))
            throw new InvalidOperationException("单账号原生引擎伪造了 Plus 绑定或启用状态。");
        Console.WriteLine("NATIVE_ACCOUNT_STATE_OK accounts=1 plus=disabled-unbound mode=direct");
        return;
    }
    if (accountData.Accounts.Count < 2 || nativePools.Length < 2)
        throw new InvalidOperationException("原生 Codex 账号没有完整同步。");
    if (official.Transport != CodexModelManager.Models.PoolTransport.OfficialCodex
        || plus.Transport != CodexModelManager.Models.PoolTransport.NativeCodexAccount
        || string.IsNullOrWhiteSpace(official.NativeAccountId)
        || string.IsNullOrWhiteSpace(plus.NativeAccountId)
        || plus.RouteAlias is not null)
        throw new InvalidOperationException("Pro / Plus 仍没有按原生账号方式隔离。");
    if (!nativePools.All(pool => pool.Models.Contains("gpt-5.6-sol", StringComparer.OrdinalIgnoreCase))
        || !nativePools.Any(pool => pool.Models.Contains("gpt-5.6-terra", StringComparer.OrdinalIgnoreCase)))
        throw new InvalidOperationException("原生账号模型列表不完整。");
    if (nativePools.Any(pool => pool.NewTasksOnly))
        throw new InvalidOperationException("原生账号仍被错误标记为只影响新任务。");
    var effectiveActive = nativePools.SingleOrDefault(pool => pool.IsActive)
                          ?? throw new InvalidOperationException("界面没有标出真实生效的原生账号。");
    if (accountData.Settings.Mode.Equals("direct", StringComparison.OrdinalIgnoreCase)
        && effectiveActive.Id != CodexModelManager.Models.PoolCatalogDefaults.OfficialPoolId)
        throw new InvalidOperationException("OpenCodex 为 direct 时，界面仍错误地把 Plus 标为当前线路。");
    Console.WriteLine($"NATIVE_ACCOUNT_STATE_OK accounts={accountData.Accounts.Count} pools={nativePools.Length} mode={accountData.Settings.Mode} stored={(accountData.Accounts.FirstOrDefault(account => account.IsActive)?.IsMain == true ? "main" : "secondary")} effective={effectiveActive.Id} aliases=false models={nativePools[0].Models.Count}");
    return;
}

const string providerId = "cmm-integration-test";
const string providerName = "本机临时测试";
const string baseUrl = "http://127.0.0.1:18888/v1";
const string apiKey = "test-key-only";
const string modelId = "k3-test";

var forceRollback = mode == "rollback";
var services = AppServices.Create();


if (mode == "desktop")
{
    var state = await services.CodexDesktop.ReadStateAsync();
    Console.WriteLine($"DESKTOP connected={state.Connected} running={state.IsTurnRunning} model={state.CurrentModel ?? "(none)"}");
    if (!state.Connected)
        throw new InvalidOperationException("没有读到普通 Codex 当前任务。");
    if (state.IsTurnRunning)
    {
        var guarded = await services.CodexDesktop.EnsureCurrentChatUsesAliasAsync(OpenCodexClient.SwitchAlias);
        if (guarded.Status != CodexAliasSwitchStatus.Busy)
            throw new InvalidOperationException("Codex 正在回答时，模型切换没有被安全拦住。");
        Console.WriteLine("DESKTOP_BUSY_GUARD_OK");
    }
    return;
}

if (mode == "desktop-attach")
{
    var before = await services.CodexDesktop.ReadStateAsync();
    Console.WriteLine($"DESKTOP_BEFORE connected={before.Connected} running={before.IsTurnRunning} model={before.CurrentModel ?? "(none)"}");
    if (!before.Connected)
        throw new InvalidOperationException("没有读到普通 Codex 当前任务。");
    if (before.IsTurnRunning)
        throw new InvalidOperationException("Codex 正在回答，不能执行接入测试。");
    var result = await services.CodexDesktop.EnsureCurrentChatUsesAliasAsync(OpenCodexClient.SwitchAlias);
    Console.WriteLine($"DESKTOP_ATTACH status={result.Status} message={result.Message}");
    if (result.Status != CodexAliasSwitchStatus.Success)
        throw new InvalidOperationException($"当前任务接入失败：{result.Message}");
    var after = await services.CodexDesktop.ReadStateAsync();
    Console.WriteLine($"DESKTOP_AFTER connected={after.Connected} running={after.IsTurnRunning} model={after.CurrentModel ?? "(none)"}");
    if (!after.Connected
        || after.IsTurnRunning
        || !string.Equals(after.CurrentModel, OpenCodexClient.SwitchAlias, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("接入操作返回成功，但当前任务没有真正显示 cmm/main。");
    return;
}

if (mode == "server")
{
    var result = await services.Dashboard.RunServerHealthAsync();
    Console.WriteLine($"SERVER_CHECK success={result.Success}");
    foreach (var server in result.Servers)
        Console.WriteLine($"{server.Role} online={server.Online} alerts={server.Alerts.Count}");
    Console.WriteLine($"PUBLIC {result.PublicEntryStatus}");
    Console.WriteLine(result.Message);
    Environment.ExitCode = result.Success ? 0 : 1;
    return;
}

if (mode == "theme-status")
{
    var snapshot = await services.DreamSkin.DiscoverAsync();
    Console.WriteLine($"THEME_STATUS engine={snapshot.EngineReady} trusted={snapshot.ManagerScriptTrusted} live={snapshot.LiveSessionConnected} paused={snapshot.IsPaused} version={snapshot.EngineVersion}");
    Console.WriteLine($"ACTIVE_THEME id={snapshot.ActiveThemeId} name={snapshot.ActiveThemeName}");
    foreach (var theme in snapshot.Themes)
        Console.WriteLine($"THEME id={theme.Id} name={theme.Name} active={theme.IsActive} dynamic={theme.IsDynamic} appearance={theme.AppearanceText}");
    Environment.ExitCode = snapshot.EngineReady && snapshot.Themes.Count > 0 ? 0 : 1;
    return;
}

if (mode == "pool-bootstrap")
{
    var pool = CreateLegacyPlusApiTool();
    var running = await services.CliProxyPools.EnsureRunningAsync(pool);
    var snapshot = await services.CliProxyPools.ReadAsync(pool);
    Console.WriteLine($"POOL_BOOTSTRAP running={running} status={snapshot.StatusTitle} accounts={snapshot.Accounts.Count} models={snapshot.Models.Count} endpoint={snapshot.Endpoint}");
    Environment.ExitCode = running ? 0 : 1;
    return;
}

if (mode == "pool-models")
{
    var pool = CreateLegacyPlusApiTool();
    var models = await services.CliProxyPools.ReadModelsAsync(pool);
    Console.WriteLine($"PLUS_MODELS count={models.Count}");
    foreach (var model in models) Console.WriteLine(model);
    return;
}

if (mode != "setup" && mode != "rollback" && mode != "cleanup")
    throw new ArgumentException($"未知测试模式：{mode}。请使用 unit、config-safety、codex-validator、setup、rollback、cleanup 或其他已定义模式。");

if (!await services.Process.EnsureOpenCodexAsync())
    throw new InvalidOperationException("OpenCodex 没有运行。");

if (mode == "cleanup")
{
    var providers = await services.OpenCodex.GetProvidersAsync(services.Settings);
    var active = await services.OpenCodex.GetActiveTargetAsync();
    if (active is not null && active.Value.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase))
        await services.OpenCodex.SetActiveTargetAsync("openai", "gpt-5.6-sol");
    if (providers.Any(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
        await services.OpenCodex.DeleteProviderAsync(providerId);
    services.Secrets.Remove(providerId);
    services.Settings.RemoveProviderName(providerId);
    services.CodexConfig.SetDefaultModel(OpenCodexClient.SwitchAlias);
    if (!await services.Process.RestartOpenCodexAsync())
        throw new InvalidOperationException("清理后 OpenCodex 重启失败。");
    var verified = await services.OpenCodex.GetActiveTargetAsync();
    if (verified is null
        || verified.Value.Provider != "openai"
        || verified.Value.Model != "gpt-5.6-sol")
        throw new InvalidOperationException("清理后没有回到官方 Sol。");
    Console.WriteLine("INTEGRATION_CLEANUP_OK");
    return;
}

var existingProviders = await services.OpenCodex.GetProvidersAsync(services.Settings);
if (existingProviders.Any(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("上一次临时测试没有清理，请先运行 cleanup。");

var backup = services.Backups.Create();
try
{
    var probe = await services.Probe.ProbeAsync(baseUrl, apiKey);
    if (!probe.Models.Contains(modelId, StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("没有自动发现测试模型。");

    services.Secrets.Save(providerId, apiKey);
    var envName = services.Secrets.GetEnvironmentName(providerId);
    await services.OpenCodex.AddProviderAsync(
        providerId,
        probe.BaseUrl,
        $"${{{envName}}}",
        probe.Models,
        adapter: "openai-chat",
        contextWindow: 128000,
        allowPrivateNetwork: true);
    if (forceRollback)
        throw new InvalidOperationException("FORCED_ROLLBACK_TEST");
    if (!await services.Process.RestartOpenCodexAsync())
        throw new InvalidOperationException("添加后 OpenCodex 重启失败。");

    var providerTest = await services.OpenCodex.TestProviderAsync(providerId);
    if (!providerTest.Success)
        throw new InvalidOperationException($"OpenCodex 复查失败：{providerTest.Message}");

    services.Settings.SetProviderName(providerId, providerName);
    var models = await services.OpenCodex.GetModelsAsync(services.Settings);
    if (!models.Any(item => item.Provider == providerId && item.Id == modelId))
        throw new InvalidOperationException("模型没有出现在 Codex 模型目录里。");

    await services.OpenCodex.SetActiveTargetAsync(providerId, modelId);
    services.CodexConfig.SetDefaultModel(OpenCodexClient.SwitchAlias);
    var active = await services.OpenCodex.GetActiveTargetAsync();
    if (active is null || active.Value.Provider != providerId || active.Value.Model != modelId)
        throw new InvalidOperationException("临时模型没有成为当前目标。");

    Console.WriteLine("INTEGRATION_SETUP_OK");
}
catch
{
    try
    {
        if (!await services.Process.StopOpenCodexAsync())
            throw new InvalidOperationException("无法停止 OpenCodex 以恢复备份。");
        services.Backups.Restore(backup);
        services.Secrets.Remove(providerId);
        services.Settings.RemoveProviderName(providerId);
        if (!await services.Process.StartOpenCodexAsync())
            throw new InvalidOperationException("恢复备份后无法启动 OpenCodex。");
    }
    catch { }
    throw;
}
}
catch (Exception ex)
{
    var failedMode = args.FirstOrDefault()?.ToLowerInvariant() ?? "unit";
    Console.Error.WriteLine($"INTEGRATION_TEST_FAILED mode={failedMode} type={ex.GetType().Name}");
    Console.Error.WriteLine(ex.ToString());
    Environment.ExitCode = 1;
}

static CodexModelManager.Models.PoolDefinition CreateLegacyPlusApiTool() => new()
{
    Id = "plus-api-1",
    DisplayName = "Plus API 工具",
    Transport = CodexModelManager.Models.PoolTransport.CliProxyApi,
    Product = CodexModelManager.Models.AccountProduct.CodexPlus,
    ProviderId = "cmm-plus-api-1",
    RouteAlias = "cmm/plus-api-tool",
    LocalPort = 8317,
    BaseUrl = "http://127.0.0.1:8317/v1",
    DefaultModel = "gpt-5.6-sol"
};

static void PrintJsonContract(JsonElement element, string path, int depth)
{
    if (depth > 8) return;
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
            PrintJsonContract(property.Value, $"{path}.{property.Name}", depth + 1);
        return;
    }
    if (element.ValueKind == JsonValueKind.Array)
    {
        Console.WriteLine($"CONTRACT path={path} kind=array count={element.GetArrayLength()}");
        var sampleCount = path.EndsWith(".keys", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(element.GetArrayLength(), 5)
            : Math.Min(element.GetArrayLength(), 1);
        for (var index = 0; index < sampleCount; index++)
            PrintJsonContract(element[index], $"{path}[{index}]", depth + 1);
        return;
    }
    var safeString = element.ValueKind == JsonValueKind.String
                     && (path.Contains("plan", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("period", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("status", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("reset", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("scope", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("source", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("model", StringComparison.OrdinalIgnoreCase)
                         || path.Contains("error_code", StringComparison.OrdinalIgnoreCase));
    var safeValue = element.ValueKind switch
    {
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        JsonValueKind.Null => "null",
        JsonValueKind.String when safeString => element.GetString() ?? string.Empty,
        _ => "[string]"
    };
    Console.WriteLine($"CONTRACT path={path} kind={element.ValueKind} value={safeValue}");
}

static async Task RunUnitTestsAsync()
{
    var root = CreateOwnedTestRoot("CodexModelManagerTests");
    try
    {
        var settingsDirectory = Path.Combine(root, "settings");
        Directory.CreateDirectory(settingsDirectory);
        var settingsPath = Path.Combine(settingsDirectory, "settings.json");
        const string badSettings = "{这不是有效的json";
        await File.WriteAllTextAsync(settingsPath, badSettings);
        var settings = new AppSettingsService(settingsDirectory);
        Ensure(settings.LoadWarning is not null, "损坏的设置文件没有触发保护。");
        var settingsBlocked = false;
        try { settings.SetProviderName("test", "测试"); }
        catch (InvalidOperationException) { settingsBlocked = true; }
        Ensure(settingsBlocked, "损坏设置仍然允许写入。");
        Ensure(await File.ReadAllTextAsync(settingsPath) == badSettings, "损坏设置原件被覆盖了。");
        Ensure(Directory.GetFiles(settingsDirectory, "settings.corrupt-*.json").Length == 1,
            "损坏设置没有保留副本。");

        var secretDirectory = Path.Combine(root, "secrets");
        Directory.CreateDirectory(secretDirectory);
        var secretPath = Path.Combine(secretDirectory, "secrets.json");
        const string badSecrets = "[错误的密钥文件";
        await File.WriteAllTextAsync(secretPath, badSecrets);
        var secrets = new SecretStore(secretDirectory);
        Ensure(secrets.LoadWarning is not null, "损坏的密钥文件没有触发保护。");
        var secretsBlocked = false;
        try { secrets.Save("test", "secret"); }
        catch (InvalidOperationException) { secretsBlocked = true; }
        Ensure(secretsBlocked, "损坏密钥仍然允许写入。");
        Ensure(await File.ReadAllTextAsync(secretPath) == badSecrets, "损坏密钥原件被覆盖了。");
        Ensure(Directory.GetFiles(secretDirectory, "secrets.corrupt-*.json").Length == 1,
            "损坏密钥没有保留副本。");

        var apiErrorClient = new HttpClient(new ScriptedHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"端口被占用，请更换端口"}}""", Encoding.UTF8, "application/json")
            })))
        {
            BaseAddress = new Uri("http://127.0.0.1:10100")
        };
        string? apiErrorMessage = null;
        try
        {
            await new OpenCodexClient(apiErrorClient).SetCodexAutoSwitchThresholdAsync(50);
        }
        catch (InvalidOperationException ex)
        {
            apiErrorMessage = ex.Message;
        }
        Ensure(apiErrorMessage == "端口被占用，请更换端口",
            "结构化 API 错误没有提取 message，仍向用户显示整段 JSON。");

        var nativeHistoryRoot = Path.Combine(root, "native-history-source");
        Directory.CreateDirectory(nativeHistoryRoot);
        var nativeHistoryPath = Path.Combine(nativeHistoryRoot, "request-log.jsonl");
        await File.WriteAllTextAsync(nativeHistoryPath,
            """{"id":"history-1","startedAt":"2026-08-13T08:00:00Z","elapsedMs":12,"path":"/v1/responses","requestedModel":"openai/gpt-5.6-sol","model":"gpt-5.6-sol","provider":"openai","status":"completed","httpStatus":200,"promptTokens":11,"completionTokens":7,"totalTokens":18,"error":null}""" + Environment.NewLine);
        var nativeHistoryHttp = new HttpClient(new ScriptedHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            })))
        {
            BaseAddress = new Uri("http://127.0.0.1:10100")
        };
        var nativeHistoryClient = new OpenCodexClient(nativeHistoryHttp, nativeHistoryRoot);
        var nativeHistoryTimeline = await nativeHistoryClient.GetUsageTimelineAsync();
        var nativeHistoryUsage = await nativeHistoryClient.GetNativeAccountUsageAsync(
            [new CodexAccountView { Id = "__main__", IsMain = true, Plan = "pro" }]);
        var nativeHistoryAudit = await nativeHistoryClient.GetNativeRoutingAuditAsync(
            [new CodexAccountView { Id = "__main__", IsMain = true, Plan = "pro" }],
            DateTimeOffset.Parse("2026-08-13T07:00:00Z"));
        Ensure(nativeHistoryTimeline.LogCount == 1
               && nativeHistoryTimeline.TotalTokens == 18
               && nativeHistoryUsage["pro"].TotalTokens == 18
               && nativeHistoryAudit.NativeSuccessCount == 1,
            $"历史时间线、账号用量或路由审计仍未读取总管家 request-log.jsonl：path={nativeHistoryTimeline.SourcePath} available={nativeHistoryTimeline.SourceAvailable} message={nativeHistoryTimeline.Message} timeline={nativeHistoryTimeline.LogCount}/{nativeHistoryTimeline.TotalTokens} account={nativeHistoryUsage["pro"].TotalTokens}/{nativeHistoryUsage["pro"].Available} audit={nativeHistoryAudit.NativeSuccessCount}/{nativeHistoryAudit.Message}。");

        var isolatedSecretDirectory = Path.Combine(root, "isolated-secrets");
        var isolatedSecrets = new SecretStore(isolatedSecretDirectory);
        isolatedSecrets.Save("provider-one", "provider-secret");
        isolatedSecrets.SaveInternal("cliproxy:test:management", "management-secret");
        var providerEnvironment = isolatedSecrets.GetProviderProcessEnvironment();
        Ensure(providerEnvironment.Count == 1
               && providerEnvironment.Values.Single() == "provider-secret"
               && !providerEnvironment.Values.Contains("management-secret", StringComparer.Ordinal),
            "CLIProxy 管理密钥被注入 OpenCodex 子进程。");
        var internalNamespaceReadBlocked = false;
        var internalNamespaceWriteBlocked = false;
        try { _ = isolatedSecrets.Read("internal:cliproxy:test:management"); }
        catch (InvalidOperationException) { internalNamespaceReadBlocked = true; }
        try { isolatedSecrets.Save("internal:cliproxy:test:management", "wrong-api"); }
        catch (InvalidOperationException) { internalNamespaceWriteBlocked = true; }
        Ensure(internalNamespaceReadBlocked && internalNamespaceWriteBlocked,
            "外部 provider API 仍可跨域读取或覆盖总管家内部凭据。");

        var poolDirectory = Path.Combine(root, "pool-catalog");
        var poolCatalog = new PoolCatalogService(poolDirectory);
        var defaults = poolCatalog.GetPools();
        Ensure(defaults.Count >= 2, "默认官方 Pro、原生 Plus 两个保留入口没有完整建立。");
        Ensure(defaults.Single(pool => pool.Id == CodexModelManager.Models.PoolCatalogDefaults.OfficialPoolId).IsProtected,
            "官方 Pro 号池没有受保护。");
        var nativePlus = defaults.Single(pool => pool.Id == CodexModelManager.Models.PoolCatalogDefaults.PlusPoolId);
        Ensure(nativePlus.Transport == CodexModelManager.Models.PoolTransport.NativeCodexAccount
               && nativePlus.RouteAlias is null
               && nativePlus.ProviderId is null
               && nativePlus.NativeAccountId is null
               && !nativePlus.Enabled,
            "默认 Plus 没有保持未绑定且停用，或仍在使用 CLIProxy 别名。");
        poolCatalog.SyncNativeCodexAccounts(new[]
        {
            new CodexModelManager.Models.CodexAccountView
            {
                Id = "unit-main-pro", Plan = "pro", IsMain = true, HasCredential = true, HealthStatus = "healthy"
            },
            new CodexModelManager.Models.CodexAccountView
            {
                Id = "unit-plus", Plan = "plus", HasCredential = true, HealthStatus = "healthy"
            },
            new CodexModelManager.Models.CodexAccountView
            {
                Id = "unit-extra-pro", Plan = "pro", HasCredential = true, HealthStatus = "healthy"
            }
        });
        var syncedPools = poolCatalog.GetPools();
        Ensure(syncedPools.Single(pool => pool.Id == CodexModelManager.Models.PoolCatalogDefaults.OfficialPoolId).NativeAccountId == "unit-main-pro"
               && syncedPools.Single(pool => pool.Id == CodexModelManager.Models.PoolCatalogDefaults.PlusPoolId).NativeAccountId == "unit-plus"
               && syncedPools.Single(pool => pool.Id == CodexModelManager.Models.PoolCatalogDefaults.PlusPoolId).Enabled
               && syncedPools.Any(pool => pool.NativeAccountId == "unit-extra-pro"),
            "新增 Pro / Plus 原生账号没有同步成独立卡片。");
        poolCatalog.SyncNativeCodexAccounts(new[]
        {
            new CodexModelManager.Models.CodexAccountView
            {
                Id = "unit-main-pro", Plan = "pro", IsMain = true, HasCredential = true, HealthStatus = "healthy"
            },
            new CodexModelManager.Models.CodexAccountView
            {
                Id = "unit-plus", Plan = "plus", HasCredential = true, HealthStatus = "healthy"
            },
            new CodexModelManager.Models.CodexAccountView
            {
                Id = "unit-not-imported", Plan = "plus", HasCredential = true, HealthStatus = "healthy"
            }
        }, addMissing: false);
        Ensure(poolCatalog.GetPools().All(pool => pool.NativeAccountId != "unit-not-imported"),
            "同步已有账号时错误新增了账号卡片。");
        var mainDeleteBlocked = false;
        try
        {
            poolCatalog.RemoveNativeCodexAccountPool(
                CodexModelManager.Models.PoolCatalogDefaults.OfficialPoolId,
                "unit-main-pro");
        }
        catch (InvalidOperationException) { mainDeleteBlocked = true; }
        Ensure(mainDeleteBlocked, "Pro 主账号没有受到删除保护。");

        var extraNative = poolCatalog.GetPools().Single(pool => pool.NativeAccountId == "unit-extra-pro");
        poolCatalog.RemoveNativeCodexAccountPool(extraNative.Id, "unit-extra-pro");
        Ensure(poolCatalog.Find(extraNative.Id) is null, "附加 Codex 账号删除后仍保留卡片。");

        var backupSourceDirectory = Path.Combine(root, "backup-source");
        Directory.CreateDirectory(backupSourceDirectory);
        var backupConfig = Path.Combine(backupSourceDirectory, "config.json");
        await File.WriteAllTextAsync(backupConfig, "{}");
        await File.WriteAllBytesAsync(Path.Combine(backupSourceDirectory, "oauth-tokens.json"), new byte[] { 1, 2, 3, 4 });
        await File.WriteAllTextAsync(Path.Combine(backupSourceDirectory, "codex-accounts.json"), "{}");
        var deletionBackups = new ConfigBackupService(backupConfig, Path.Combine(root, "deletion-backups"));
        var deletionBackup = deletionBackups.CreateAccountDeletionBackup(poolCatalog.FilePath);
        Ensure(File.Exists(Path.Combine(deletionBackup, "config.json"))
               && File.Exists(Path.Combine(deletionBackup, "oauth-tokens.json"))
               && File.Exists(Path.Combine(deletionBackup, "codex-accounts.json"))
               && File.Exists(Path.Combine(deletionBackup, "pools.json"))
               && File.Exists(Path.Combine(deletionBackup, "backup-manifest.json")),
            "删除 Codex 账号前没有完整备份配置、OAuth token、号池和备份清单。");
        var officialDisableBlocked = false;
        try { poolCatalog.SetEnabled(CodexModelManager.Models.PoolCatalogDefaults.OfficialPoolId, false); }
        catch (InvalidOperationException) { officialDisableBlocked = true; }
        Ensure(officialDisableBlocked, "官方 Pro 保底号池仍可被停用。");
        var extraPlus = poolCatalog.AddCliProxyPool(CodexModelManager.Models.AccountProduct.CodexPlus);
        var extraPro = poolCatalog.AddCliProxyPool(CodexModelManager.Models.AccountProduct.CodexPro);
        Ensure(extraPlus.LocalPort != extraPro.LocalPort
               && extraPlus.LocalPort is not 8317 and not 8318
               && extraPro.LocalPort is not 8317 and not 8318
               && !string.Equals(extraPlus.RouteAlias, extraPro.RouteAlias, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(extraPlus.ProviderId, extraPro.ProviderId, StringComparison.OrdinalIgnoreCase),
            "新增 Plus/Pro 号池没有获得独立端口、别名和 provider。");
        var legacyPortRoot = Path.Combine(root, "legacy-reserved-cli-port");
        Directory.CreateDirectory(legacyPortRoot);
        await File.WriteAllTextAsync(Path.Combine(legacyPortRoot, "pools.json"),
            JsonSerializer.Serialize(new PoolCatalogDocument
            {
                SchemaVersion = 2,
                Pools = new List<PoolDefinition>
                {
                    new()
                    {
                        Id = "legacy-plus-pool",
                        DisplayName = "Legacy Plus Pool",
                        Transport = PoolTransport.CliProxyApi,
                        Product = AccountProduct.CodexPlus,
                        Enabled = true,
                        ProviderId = "cmm-legacy-plus-pool",
                        BaseUrl = "http://127.0.0.1:8317/v1",
                        LocalPort = 8317
                    }
                },
                Active = new ActivePoolState { PoolId = "legacy-plus-pool", Model = "gpt-5.6-sol" }
            }));
        var migratedPortCatalog = new PoolCatalogService(legacyPortRoot);
        var migratedLegacyPool = migratedPortCatalog.Find("legacy-plus-pool");
        Ensure(migratedLegacyPool is not null
               && migratedLegacyPool.LocalPort is not 8317 and not 8318
               && migratedLegacyPool.BaseUrl == $"http://127.0.0.1:{migratedLegacyPool.LocalPort}/v1",
            "升级时没有迁移占用内建 Agent API 端口的历史动态号池。 ");
        var malformedPoolRoot = Path.Combine(root, "malformed-cli-source-discovery");
        Directory.CreateDirectory(malformedPoolRoot);
        var malformedPoolPath = Path.Combine(malformedPoolRoot, "pools.json");
        await File.WriteAllTextAsync(malformedPoolPath,
            JsonSerializer.Serialize(new PoolCatalogDocument
            {
                SchemaVersion = 2,
                Pools = new List<PoolDefinition>
                {
                    new()
                    {
                        Id = PoolCatalogDefaults.OfficialPoolId,
                        DisplayName = "Official",
                        Transport = PoolTransport.OfficialCodex,
                        Product = AccountProduct.CodexPro,
                        Enabled = true,
                        IsProtected = true,
                        BaseUrl = "OpenAI 原生账号"
                    },
                    new()
                    {
                        Id = "..",
                        DisplayName = "Unsafe Legacy CLI",
                        Transport = PoolTransport.CliProxyApi,
                        Product = AccountProduct.CodexPlus,
                        Enabled = true,
                        ProviderId = "unsafe-provider",
                        BaseUrl = "http://127.0.0.1:8400/v1",
                        LocalPort = 8400
                    }
                },
                Active = new ActivePoolState { PoolId = PoolCatalogDefaults.OfficialPoolId, Model = "gpt-5.6-sol" }
            }));
        var malformedOriginalBytes = await File.ReadAllBytesAsync(malformedPoolPath);
        var malformedOriginalMtime = File.GetLastWriteTimeUtc(malformedPoolPath);
        var malformedAppServices = AppServices.Create(malformedPoolRoot);
        var malformedCatalog = malformedAppServices.PoolCatalog;
        var malformedRecoveryViews = await malformedAppServices.AccountPools.ReadViewsAsync();
        var malformedRecoveryUsage = await malformedAppServices.AccountPools.ReadLiveTokenUsageAsync();
        var malformedRecoveryAudit = await malformedAppServices.AccountPools.ReadNativeRoutingAuditAsync();
        var malformedSources = await malformedAppServices.SubagentSources.DiscoverAsync();
        var malformedWriteRejected = false;
        try { malformedCatalog.SetEnabled(PoolCatalogDefaults.PlusPoolId, false); }
        catch (InvalidOperationException) { malformedWriteRejected = true; }
        Ensure(malformedCatalog.LoadWarning is not null
               && malformedCatalog.GetPools().All(pool => pool.Id != "..")
               && malformedRecoveryViews.Count == 2
               && malformedRecoveryViews.All(view => !view.CanSwitch && !view.CanAddAccount && !view.CanConfigure)
               && !malformedRecoveryUsage.Pro.Available
               && !malformedRecoveryUsage.Plus.Available
               && !malformedRecoveryAudit.SourceAvailable
               && malformedSources.Any(source => source.SourceId.StartsWith("invalid-cli:", StringComparison.Ordinal)
                                                 && !source.SupportsTextWorker)
               && malformedSources.Any(source => source.Kind == SubagentSourceKind.NativeCodexAccount)
               && CliCredentialIdentity.Read(malformedPoolRoot, "..") is null,
            "单个非法 CLI ID 让整个来源发现失败，或路径越界 ID 仍可读取凭据。 ");
        var malformedFinalBytes = await File.ReadAllBytesAsync(malformedPoolPath);
        Ensure(malformedWriteRejected
               && malformedOriginalBytes.SequenceEqual(malformedFinalBytes)
               && malformedOriginalMtime == File.GetLastWriteTimeUtc(malformedPoolPath),
            "Unsafe catalog fallback modified the original file or allowed a write.");
        await AssertCatalogStartupIsolationAsync(
            root,
            "null-pools",
            """{"SchemaVersion":2,"Pools":null,"Active":{"PoolId":"official-pro","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            root,
            "null-active",
            """{"SchemaVersion":2,"Pools":[],"Active":null}""");
        await AssertCatalogStartupIsolationAsync(
            root,
            "null-active-pool-id",
            """{"SchemaVersion":4,"Pools":[{"Id":"official-pro","DisplayName":"Official","Transport":"OfficialCodex"}],"Active":{"PoolId":null,"Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            root,
            "null-pool-id",
            """{"SchemaVersion":4,"Pools":[{"Id":null,"DisplayName":"Broken","Transport":"OfficialCodex"}],"Active":{"PoolId":"official-pro","Model":"gpt-5.6-sol"}}""");
        await AssertCatalogStartupIsolationAsync(
            root,
            "duplicate-pool-ids",
            """{"SchemaVersion":2,"Pools":[{"Id":"duplicate","DisplayName":"One","Transport":"OfficialCodex"},{"Id":"DUPLICATE","DisplayName":"Two","Transport":"NativeCodexAccount"}],"Active":{"PoolId":"duplicate","Model":"gpt-5.6-sol"}}""");
        var dynamicGateway = new UnifiedGatewayService(
            settings,
            secrets,
            new CliProxyPoolService(settings, secrets),
            new OpenCodexClient(),
            poolCatalog);
        var discoveredCliCandidates = dynamicGateway.GetCliWorkerPools();
        Ensure(discoveredCliCandidates.Any(pool => pool.Id == extraPlus.Id)
               && discoveredCliCandidates.Any(pool => pool.Id == extraPro.Id)
               && UnifiedGatewayService.GetCliRoutePrefix(extraPlus) == $"cli/{extraPlus.Id}/",
            "中转站新增的 CLIProxy 号池没有自动进入子代理来源候选目录。 ");
        var identityTestRoot = Path.Combine(root, "cli-credential-identity");
        var identityAuthDirectory = Path.Combine(
            identityTestRoot, "cli-proxy", "pools", extraPlus.Id, "auth");
        Directory.CreateDirectory(identityAuthDirectory);
        var identityAuthPath = Path.Combine(identityAuthDirectory, "same-file.json");
        await File.WriteAllTextAsync(identityAuthPath,
            "{\"account_id\":\"account-a\",\"email\":\"a@example.test\",\"access_token\":\"secret-a\",\"type\":\"codex\"}");
        var credentialIdentityA = CliCredentialIdentity.Read(identityTestRoot, extraPlus.Id);
        await File.WriteAllTextAsync(identityAuthPath,
            "{\"account_id\":\"account-b\",\"email\":\"b@example.test\",\"access_token\":\"secret-b\",\"type\":\"codex\"}");
        var credentialIdentityB = CliCredentialIdentity.Read(identityTestRoot, extraPlus.Id);
        Ensure(credentialIdentityA is not null
               && credentialIdentityB is not null
               && !credentialIdentityA.Equals(credentialIdentityB, StringComparison.Ordinal)
               && !credentialIdentityA.Contains("account-a", StringComparison.OrdinalIgnoreCase)
               && !credentialIdentityA.Contains("a@example", StringComparison.OrdinalIgnoreCase),
            "替换唯一 CLIProxy 账号后身份没有变化，或身份摘要泄露了账号/email。 ");
        poolCatalog.SetActive(extraPlus.Id, "gpt-5.6-terra", "unit-model-selection");
        Ensure(poolCatalog.Find(extraPlus.Id)?.DefaultModel == "gpt-5.6-terra"
               && poolCatalog.GetActive().Model == "gpt-5.6-terra",
            "号池切换后没有记住所选模型。");
        var activeBeforeRollback = poolCatalog.GetActive();
        poolCatalog.SetActive(extraPro.Id, "gpt-5.6-sol", "unit-temporary-switch");
        poolCatalog.RestoreActive(activeBeforeRollback);
        var activeAfterRollback = poolCatalog.GetActive();
        Ensure(activeAfterRollback.PoolId == activeBeforeRollback.PoolId
               && activeAfterRollback.Model == activeBeforeRollback.Model
               && activeAfterRollback.Verification == activeBeforeRollback.Verification
               && activeAfterRollback.SwitchedAt == activeBeforeRollback.SwitchedAt,
            "切换失败时没有原样恢复号池状态。");

        Ensure(CodexModelManager.Models.UsageFormatting.Number(2_505_117) == "2.51M",
            "Token 数量没有按可读单位格式化。 ");
        Ensure(CodexModelManager.Models.UsageFormatting.CleanResetText(
                   "4 days. Notify me when I'm close to hitting my usage limits") == "约 4 天后重置",
            "额度重置文字没有去掉网页噪声。 ");
        var quotaAccount = new CodexModelManager.Models.CodexAccountView
        {
            WeeklyPercent = 12.5,
            WeeklyResetAt = DateTimeOffset.Now.AddDays(3).ToUnixTimeSeconds(),
            MonthlyPercent = 40
        };
        Ensure(quotaAccount.QuotaWindows.Count == 2
               && quotaAccount.QuotaWindows[0].RemainingPercent == 87.5,
            "Codex 周/月额度窗口或剩余比例计算错误。 ");
        var auditSwitch = new DateTimeOffset(2026, 7, 29, 20, 33, 2, TimeSpan.FromHours(8));
        var auditLines = new[]
        {
            JsonSerializer.Serialize(new
            {
                timestamp = auditSwitch.AddMinutes(-38).ToUnixTimeMilliseconds(),
                provider = "openai",
                status = 200,
                resolvedModel = "gpt-5.6-sol",
                totalTokens = 190_000
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = auditSwitch.AddMinutes(1).ToUnixTimeMilliseconds(),
                provider = "openai",
                status = 502,
                resolvedModel = "gpt-5.6-sol",
                totalTokens = 0
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = auditSwitch.AddMinutes(2).ToUnixTimeMilliseconds(),
                provider = "openai-punit",
                status = 200,
                resolvedModel = "gpt-5.6-sol",
                totalTokens = 42_000
            })
        };
        var routingAudit = OpenCodexClient.AnalyzeNativeRoutingAudit(
            auditLines,
            auditSwitch,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = "Pro",
                ["openai-punit"] = "Plus"
            });
        Ensure(routingAudit.LastBillingAccount == "Plus"
               && routingAudit.ProLastRequestAt == auditSwitch.AddMinutes(-38)
               && routingAudit.ProSuccessfulRequestsSinceSwitch == 0,
            "本机路由审计没有正确区分最后扣费账号、Pro 最后请求和切换后的 Pro 请求。 ");

        var cliProxyPort = LocalPortPolicy.FindAvailableCliProxyPort(
            "unit-plus",
            Array.Empty<int>());
        var cliSettings = new AppSettingsService(Path.Combine(root, "cliproxy-settings"));
        var cliSecrets = new SecretStore(cliSettings.DataDirectory);
        var cliPool = new CodexModelManager.Models.PoolDefinition
        {
            Id = "unit-plus",
            DisplayName = "Unit Plus",
            Transport = CodexModelManager.Models.PoolTransport.CliProxyApi,
            Product = CodexModelManager.Models.AccountProduct.CodexPlus,
            ProviderId = "cmm-unit-plus",
            RouteAlias = "cmm/unit-plus",
            LocalPort = cliProxyPort,
            BaseUrl = $"http://127.0.0.1:{cliProxyPort}/v1"
        };
        var cliProxy = new CliProxyPoolService(
            cliSettings,
            cliSecrets,
            binaryPath: RequireCliProxyTestArtifact());
        try
        {
            var concurrentCliStarts = await Task.WhenAll(
                cliProxy.EnsureRunningAsync(cliPool),
                cliProxy.EnsureRunningAsync(cliPool));
            Ensure(concurrentCliStarts.All(result => result),
                "随程序发布的 CLIProxyAPI 没有以同池单实例方式启动。");
            var cliSnapshot = await cliProxy.ReadAsync(cliPool);
            Ensure(cliSnapshot.StatusTitle == "待授权" && cliSnapshot.Accounts.Count == 0,
                "未授权 CLIProxyAPI 的状态没有被正确识别。");
            var cliConfig = await File.ReadAllTextAsync(Path.Combine(cliSettings.DataDirectory, "cli-proxy", "pools", cliPool.Id, "config.yaml"));
            Ensure(cliConfig.Contains("host: \"127.0.0.1\"", StringComparison.Ordinal)
                   && cliConfig.Contains("allow-remote: false", StringComparison.Ordinal)
                   && cliConfig.Contains("disable-control-panel: true", StringComparison.Ordinal),
                "CLIProxyAPI 没有应用本机限定和管理面安全配置。");
        }
        finally
        {
            await cliProxy.StopOwnedAsync(cliPool.Id);
        }

        await AssertCliProxyOwnedInstanceReconciliationAsync(root);

        var codexPath = Path.Combine(root, ".codex", "config.toml");
        var backupDirectory = Path.Combine(root, "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
        const string originalCodex = "model = \"gpt-old\"\nmodel_reasoning_effort = \"high\"\n";
        await File.WriteAllTextAsync(codexPath, originalCodex);
        var codex = new CodexConfigService(codexPath, backupDirectory);
        var snapshot = codex.CreateSnapshot();
        codex.SetDefaultModel("cmm/main");
        Ensure(codex.ReadDefaultModel() == "cmm/main", "Codex 模型没有改成固定入口。");
        codex.RestoreSnapshot(snapshot);
        Ensure(await File.ReadAllTextAsync(codexPath) == originalCodex, "Codex 配置快照没有完整恢复。");

        const string originalProviderConfig =
            "model = \"gpt-old\"\n" +
            "model_provider = \"openai\"\n" +
            "model_reasoning_effort = \"high\"\n\n" +
            "[history]\n" +
            "keep = \"user-owned\"\n";
        await File.WriteAllTextAsync(codexPath, originalProviderConfig);
        var disconnectedGateway = codex.ReadGatewaySnapshot();
        Ensure(disconnectedGateway.CanToggle
               && !disconnectedGateway.IsManagedConnected
               && disconnectedGateway.SelectedProviderId == "openai"
               && disconnectedGateway.CurrentGateway.Contains("Codex 内置官方网关", StringComparison.Ordinal)
               && disconnectedGateway.ManagedGateway == CodexConfigService.DefaultManagedNativeBaseUrl,
            "默认关闭状态没有正确显示当前 Codex 网关和连接后的总管家网关。");
        var backupCountBeforeToggle = Directory.Exists(backupDirectory)
            ? Directory.GetFiles(backupDirectory).Length
            : 0;
        Ensure(codex.EnsureManagedNativeProvider(createSnapshot: false), "首次应用 native-routing v2 没有报告变更。");
        var managedProviderConfig = await File.ReadAllTextAsync(codexPath);
        var connectedGateway = codex.ReadGatewaySnapshot();
        Ensure(codex.IsManagedNativeProviderSelected()
               && codex.ReadModelProvider() == "openai"
               && connectedGateway.CanToggle
               && connectedGateway.IsManagedConnected
               && connectedGateway.CurrentGateway == CodexConfigService.DefaultManagedNativeBaseUrl
               && connectedGateway.SelectedProviderId == "openai",
            "native-routing v2 没有保持 Codex 的 openai 原生身份。");
        Ensure(managedProviderConfig.Contains("openai_base_url = \"http://127.0.0.1:10100/v1\"", StringComparison.Ordinal)
               && managedProviderConfig.Contains("model_catalog_json = ", StringComparison.Ordinal)
               && managedProviderConfig.Contains("native-routing v2", StringComparison.Ordinal)
               && !managedProviderConfig.Contains("model_provider = \"cmm_native\"", StringComparison.Ordinal)
               && !managedProviderConfig.Contains("Bearer ", StringComparison.Ordinal)
               && managedProviderConfig.Contains("[history]", StringComparison.Ordinal)
               && managedProviderConfig.Contains("model_reasoning_effort = \"high\"", StringComparison.Ordinal),
            "native-routing v2 写入了旧 Provider、疑似 Token，或破坏了用户原配置。");
        Ensure(!codex.EnsureManagedNativeProvider(createSnapshot: false), "重复应用 native-routing v2 不是幂等操作。");
        Ensure(codex.RemoveManagedNativeProvider(createSnapshot: false)
               && await File.ReadAllTextAsync(codexPath) == originalProviderConfig,
            "移除 native-routing v2 后没有逐字恢复原配置。");
        Ensure((Directory.Exists(backupDirectory) ? Directory.GetFiles(backupDirectory).Length : 0)
               == backupCountBeforeToggle,
            "界面连接/取消逻辑不应创建额外配置备份。");

        const string noProviderConfig = "model = \"gpt-old\"\nmodel_reasoning_effort = \"high\"\n";
        await File.WriteAllTextAsync(codexPath, noProviderConfig);
        Ensure(codex.EnsureManagedNativeProvider(createSnapshot: false)
               && codex.RemoveManagedNativeProvider(createSnapshot: false)
               && await File.ReadAllTextAsync(codexPath) == noProviderConfig,
            "原配置没有 model_provider 时，native-routing v2 的安装/卸载没有精确往返。");

        const string userOwnedBaseUrl =
            "openai_base_url = \"https://user-owned.example/v1\"\n" +
            "model = \"gpt-user\"\n";
        await File.WriteAllTextAsync(codexPath, userOwnedBaseUrl);
        var userOwnedBlocked = false;
        try { codex.EnsureManagedNativeProvider(createSnapshot: false); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("openai_base_url", StringComparison.Ordinal))
        {
            userOwnedBlocked = true;
        }
        Ensure(userOwnedBlocked && await File.ReadAllTextAsync(codexPath) == userOwnedBaseUrl,
            "用户自己的 openai_base_url 被覆盖，或冲突没有失败关闭。");

        var legacyPrevious = "model_provider = \"openai\"";
        var legacyPreviousBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(legacyPrevious));
        var legacyConfig =
            "# BEGIN CODEX TOTAL MANAGER: cmm_native_provider v1\n" +
            $"# previous-model-provider-line-base64: {legacyPreviousBase64}\n" +
            "model_provider = \"cmm_native\"\n" +
            "model_providers.cmm_native.name = \"Codex Total Manager Native\"\n" +
            "model_providers.cmm_native.base_url = \"http://127.0.0.1:10100/v1\"\n" +
            "model_providers.cmm_native.wire_api = \"responses\"\n" +
            "model_providers.cmm_native.requires_openai_auth = true\n" +
            "model_providers.cmm_native.supports_websockets = false\n" +
            "model_providers.cmm_native.env_http_headers = { \"X-CMM-Admission\" = \"CMM_NATIVE_ADMISSION_TOKEN\" }\n" +
            "# END CODEX TOTAL MANAGER: cmm_native_provider\n" +
            "model = \"gpt-old\"\n";
        await File.WriteAllTextAsync(codexPath, legacyConfig);
        Ensure(codex.EnsureManagedNativeProvider(createSnapshot: false), "合法旧 cmm_native 块没有迁移到 v2。 ");
        var migrated = await File.ReadAllTextAsync(codexPath);
        Ensure(migrated.Contains("native-routing v2", StringComparison.Ordinal)
               && migrated.Contains(legacyPrevious, StringComparison.Ordinal)
               && !migrated.Contains("cmm_native_provider", StringComparison.Ordinal),
            "旧 cmm_native 迁移后没有保留 openai 身份或仍残留旧标记。");
        Ensure(codex.RemoveManagedNativeProvider(createSnapshot: false)
               && await File.ReadAllTextAsync(codexPath) == legacyPrevious + "\nmodel = \"gpt-old\"\n",
            "迁移后的 v2 块没有精确恢复旧块保存的原 Provider。");

        await File.WriteAllTextAsync(codexPath, noProviderConfig);
        var catalog = new CodexModelCatalogService(codex);
        const string existingOfficialCache =
            "{\"fetched_at\":\"2026-08-11T00:00:00Z\",\"client_version\":\"current\",\"models\":[" +
            "{\"slug\":\"gpt-cached-current\",\"display_name\":\"Cached current Codex model\",\"shell_type\":\"shell_command\",\"visibility\":\"list\",\"supported_in_api\":true}]}";
        await File.WriteAllTextAsync(catalog.CachePath, existingOfficialCache);
        var catalogCount = catalog.WriteCatalog(new[]
        {
            new ModelOption { Provider = "openai", Id = "gpt-official", Namespaced = "openai/gpt-official", IsOfficial = true },
            new ModelOption { Provider = "deepseek", Id = "deepseek-coder", Namespaced = "deepseek/deepseek-coder", ProviderLabel = "DeepSeek" }
        });
        Ensure(catalogCount == 3, "模型目录没有合并 Codex 缓存、官方兜底和第三方模型。");
        using (var catalogJson = JsonDocument.Parse(await File.ReadAllTextAsync(catalog.CatalogPath)))
        {
            var slugs = catalogJson.RootElement.GetProperty("models").EnumerateArray()
                .Select(item => item.GetProperty("slug").GetString()).ToArray();
            Ensure(slugs.Contains("gpt-cached-current")
                   && slugs.Contains("gpt-official")
                   && slugs.Contains("deepseek/deepseek-coder"),
                "Codex 当前官方模型没有被保留，或第三方模型没有使用 provider/model 进入原生目录。");
        }
        catalog.RemoveOwnedArtifacts();
        Ensure(!File.Exists(catalog.CatalogPath)
               && await File.ReadAllTextAsync(catalog.CachePath) == existingOfficialCache,
            "断开时删除或改写了 Codex 自己的官方模型缓存。");
        File.Delete(catalog.CachePath);
        _ = catalog.WriteCatalog(new[]
        {
            new ModelOption { Provider = "openai", Id = "gpt-fallback", IsOfficial = true }
        });
        Ensure((await File.ReadAllTextAsync(catalog.CachePath)).Contains("codex_total_manager_catalog", StringComparison.Ordinal),
            "仅在缓存不存在时创建的失效文件没有所有权标记。");
        catalog.RemoveOwnedArtifacts();
        Ensure(!File.Exists(catalog.CatalogPath) && !File.Exists(catalog.CachePath),
            "总管家自己拥有的目录或缓存失效文件没有被清理。");

        var continuationStore = new ResponseContinuationStore();
        continuationStore.Save(
            "resp_cmm_first",
            new[] { new OcxMessage { Role = "user", Content = "第一轮问题" } },
            new[] { new OcxMessage { Role = "assistant", Content = "第一轮回答" } });
        Ensure(continuationStore.TryExpand(
                   "resp_cmm_first",
                   new[] { new OcxMessage { Role = "user", Content = "第二轮问题" } },
                   out var expandedConversation)
               && expandedConversation.Count == 3
               && expandedConversation[0].Content?.ToString() == "第一轮问题"
               && expandedConversation[1].Content?.ToString() == "第一轮回答"
               && expandedConversation[2].Content?.ToString() == "第二轮问题",
            "previous_response_id 没有展开上一轮完整对话，切第三方模型仍会失忆。");

        const string credentialLikeUserInfo = "user:password@";
        var credentialLikeGateway =
            "model_provider = \"private\"\n" +
            "[model_providers.private]\n" +
            $"base_url = \"https://{credentialLikeUserInfo}example.test/v1?api_key=do-not-display\"\n";
        await File.WriteAllTextAsync(codexPath, credentialLikeGateway);
        var sanitizedGateway = codex.ReadGatewaySnapshot();
        Ensure(sanitizedGateway.CurrentGateway == "https://example.test/v1"
               && !sanitizedGateway.CurrentGateway.Contains("password", StringComparison.OrdinalIgnoreCase)
               && !sanitizedGateway.CurrentGateway.Contains("api_key", StringComparison.OrdinalIgnoreCase),
            "网关摘要泄露了 URL 中的认证信息或查询参数。");

        const string damagedManagedMarkers =
            "model_provider = \"openai\"\n" +
            "# BEGIN CODEX TOTAL MANAGER: native-routing v2\n";
        await File.WriteAllTextAsync(codexPath, damagedManagedMarkers);
        var damagedBefore = await File.ReadAllTextAsync(codexPath);
        var damagedGateway = codex.ReadGatewaySnapshot();
        Ensure(!damagedGateway.CanToggle
               && await File.ReadAllTextAsync(codexPath) == damagedBefore,
            "损坏的托管标记没有失败关闭，或只读网关检查改写了配置。");

        const string unmanagedNativeProvider =
            "model_provider = \"cmm_native\"\n" +
            "[model_providers.cmm_native]\n" +
            "base_url = \"http://127.0.0.1:9999/v1\"\n";
        await File.WriteAllTextAsync(codexPath, unmanagedNativeProvider);
        var unmanagedNativeBlocked = false;
        try { codex.EnsureManagedNativeProvider(createSnapshot: false); }
        catch (InvalidOperationException)
        {
            unmanagedNativeBlocked = true;
        }
        Ensure(unmanagedNativeBlocked
               && await File.ReadAllTextAsync(codexPath) == unmanagedNativeProvider,
            "同名非托管 Provider 没有失败关闭，或冲突配置被改写。");
        await File.WriteAllTextAsync(codexPath, originalCodex);

        var subagentDirectory = Path.Combine(root, ".codex", "agents");
        var subagentDataPath = Path.Combine(root, "manager-data", "subagents.json");
        var subagentBackupRoot = Path.Combine(root, "subagent-backups");
        var bridgeExecutable = Path.Combine(root, "CodexModelManager-test.exe");
        Directory.CreateDirectory(subagentDirectory);
        await File.WriteAllBytesAsync(bridgeExecutable, new byte[] { 0x4D, 0x5A });
        var unrelatedAgentPath = Path.Combine(subagentDirectory, "user-owned.toml");
        const string unrelatedAgent = "name = \"user_owned\"\ndescription = \"keep\"\ndeveloper_instructions = \"keep\"\n";
        await File.WriteAllTextAsync(unrelatedAgentPath, unrelatedAgent);
        var unicodeAgentPath = Path.Combine(subagentDirectory, "代码 审查.toml");
        const string unicodeAgent = "name = \"code_review\"\ndescription = \"keep unicode filename\"\ndeveloper_instructions = \"keep unicode filename\"\n";
        await File.WriteAllTextAsync(unicodeAgentPath, unicodeAgent);
        var subagentCodexValidator = new TestCodexConfigValidator(
            new CodexConfigValidationResult(true, true, "test validator accepted"));
        var subagents = new SubagentConfigurationService(
            codexPath,
            subagentDirectory,
            subagentDataPath,
            subagentBackupRoot,
            bridgeExecutablePath: bridgeExecutable,
            bridgeStatePath: Path.Combine(root, "manager-data", "external-worker-state.json"),
            codexConfigValidator: subagentCodexValidator);
        var cliSourceId = SubagentSourceIdentity.CliSourceId(extraPlus.Id);
        var cliRoutePrefix = UnifiedGatewayService.GetCliRoutePrefix(extraPlus);
        var cliSourceFingerprint = SubagentSourceIdentity.ComputeForPool(
            extraPlus,
            cliSourceId,
            SubagentSourceKind.CliProxyPool,
            cliRoutePrefix,
            SubagentSourceIdentity.OpenAiChatAdapter,
            extraPlus.ProviderId,
            credentialIdentityA);
        var replacedAccountFingerprint = SubagentSourceIdentity.ComputeForPool(
            extraPlus,
            cliSourceId,
            SubagentSourceKind.CliProxyPool,
            cliRoutePrefix,
            SubagentSourceIdentity.OpenAiChatAdapter,
            extraPlus.ProviderId,
            credentialIdentityB);
        Ensure(!cliSourceFingerprint.Equals(replacedAccountFingerprint, StringComparison.Ordinal),
            "同端点/同 provider 槽替换唯一账号后仍复用了旧来源授权指纹。 ");
        var cliSourceModel = cliRoutePrefix + "gpt-5.6-terra";
        var cliSource = new SubagentSourceDescriptor(
            cliSourceId,
            extraPlus.DisplayName,
            SubagentSourceKind.CliProxyPool,
            cliRoutePrefix,
            extraPlus.BaseUrl,
            $"独立凭据槽 {extraPlus.ProviderId}",
            $"只消耗 {extraPlus.DisplayName}",
            SubagentSourceIdentity.OpenAiChatAdapter,
            cliSourceFingerprint,
            true,
            true,
            true,
            new[] { cliSourceModel },
            "可用",
            null,
            DateTimeOffset.UtcNow);
        var sourcePlanSelections = subagents.Roles.Select(role => new SubagentRoleSelection
        {
            RoleId = role.Id,
            WorkerKind = SubagentWorkerKind.CodexNative,
            ModelId = role.DefaultModel
        }).ToList();
        var pendingSourcePlan = subagents.CreatePlan(
            sourcePlanSelections,
            Array.Empty<SubagentSourceAuthorization>(),
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { cliSource });
        Ensure(pendingSourcePlan.CanApply
               && sourcePlanSelections.All(selection => selection.WorkerKind == SubagentWorkerKind.CodexNative),
            "自动发现新来源时改变了角色，或未授权候选错误阻止纯原生草稿。 ");
        var sourceExplorer = sourcePlanSelections.Single(selection => selection.RoleId == "cmm_explorer");
        sourceExplorer.WorkerKind = SubagentWorkerKind.External;
        sourceExplorer.ModelId = cliSourceModel;
        sourceExplorer.SourceId = cliSourceId;
        var untrustedSourcePlan = subagents.CreatePlan(
            sourcePlanSelections,
            Array.Empty<SubagentSourceAuthorization>(),
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { cliSource });
        Ensure(!untrustedSourcePlan.CanApply
               && untrustedSourcePlan.Issues.Any(issue => issue.Contains("尚未明确授权", StringComparison.Ordinal)),
            "未授权的新号池已经可以进入外部角色执行计划。 ");
        var cliGrant = new SubagentSourceAuthorization
        {
            SourceId = cliSourceId,
            ExpectedFingerprint = cliSourceFingerprint,
            Enabled = true,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedDisplayName = extraPlus.DisplayName
        };
        var trustedSourcePlan = subagents.CreatePlan(
            sourcePlanSelections,
            new[] { cliGrant },
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { cliSource });
        Ensure(trustedSourcePlan.CanApply && trustedSourcePlan.ExternalPendingCount == 1,
            "来源明确授权后仍无法生成精确外部角色计划。 ");
        var expandedCatalogPlan = subagents.CreatePlan(
            sourcePlanSelections,
            new[] { cliGrant },
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { cliSource with { Models = new[] { cliSourceModel, cliRoutePrefix + "gpt-5.6-sol" } } });
        Ensure(expandedCatalogPlan.CanApply,
            "同一来源只增加模型目录时错误撤销了既有授权。 ");
        var changedFingerprint = SubagentSourceIdentity.Compute(
            cliSourceId,
            SubagentSourceKind.CliProxyPool.ToString(),
            $"http://127.0.0.1:{extraPlus.LocalPort!.Value + 100}/v1",
            SubagentSourceIdentity.OpenAiChatAdapter,
            extraPlus.ProviderId!,
            cliRoutePrefix,
            credentialIdentityA!);
        var changedSourcePlan = subagents.CreatePlan(
            sourcePlanSelections,
            new[] { cliGrant },
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { cliSource with { Fingerprint = changedFingerprint, EndpointDisplay = "http://127.0.0.1:changed/v1" } });
        Ensure(!changedSourcePlan.CanApply
               && changedSourcePlan.Issues.Any(issue => issue.Contains("必须重新授权", StringComparison.Ordinal)
                                                       || issue.Contains("身份已变化", StringComparison.Ordinal)),
            "来源端点身份变化后旧授权仍可应用。 ");
        var disabledSourcePlan = subagents.CreatePlan(
            sourcePlanSelections,
            new[] { cliGrant },
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { cliSource with { Enabled = false, Ready = false, StatusText = "号池已停用" } });
        Ensure(!disabledSourcePlan.CanApply, "已停用来源仍可应用或执行。 ");
        var removedSourcePlan = subagents.CreatePlan(
            sourcePlanSelections,
            new[] { cliGrant },
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            Array.Empty<SubagentSourceDescriptor>());
        Ensure(!removedSourcePlan.CanApply, "已移除来源的旧授权仍可复活。 ");

        var guardedRoute = new UnifiedGatewayRoute
        {
            GatewayModel = cliSourceModel,
            UpstreamModel = "gpt-5.6-terra",
            BaseUrl = extraPlus.BaseUrl,
            SecretName = extraPlus.ProviderId,
            PoolId = extraPlus.Id,
            PoolLabel = extraPlus.DisplayName,
            SourceId = cliSourceId,
            SourceKind = SubagentSourceKind.CliProxyPool.ToString(),
            RoutePrefix = cliRoutePrefix,
            Adapter = SubagentSourceIdentity.OpenAiChatAdapter,
            CredentialIdentity = credentialIdentityA!,
            SourceFingerprint = cliSourceFingerprint
        };
        Ensure(SubagentSourceIdentity.IsRouteIdentityValid(guardedRoute),
            "合法来源路由没有通过身份指纹复核。 ");
        var hostPortProbe = new TcpListener(IPAddress.Loopback, 0);
        hostPortProbe.Start();
        var hostGuardPort = ((IPEndPoint)hostPortProbe.LocalEndpoint).Port;
        hostPortProbe.Stop();
        const string hostAdmissionKey = "unit-source-guard-admission-key";
        new SecretStore(identityTestRoot).SaveInternal("unified-gateway:client", hostAdmissionKey);
        await File.WriteAllTextAsync(
            Path.Combine(identityTestRoot, "pools.json"),
            JsonSerializer.Serialize(new PoolCatalogDocument
            {
                SchemaVersion = 2,
                Pools = new List<PoolDefinition> { extraPlus },
                Active = new ActivePoolState { PoolId = extraPlus.Id, Model = "gpt-5.6-terra" }
            }));
        var hostGuardConfigPath = Path.Combine(identityTestRoot, "guarded-gateway.json");
        var hostGuardConfiguration = new UnifiedGatewayConfiguration
        {
            SchemaVersion = 4,
            Port = hostGuardPort,
            DataDirectory = identityTestRoot,
            Routes = new List<UnifiedGatewayRoute> { guardedRoute }
        };
        hostGuardConfiguration.ConfigurationFingerprint =
            UnifiedGatewayConfigurationIdentity.Compute(hostGuardConfiguration);
        await File.WriteAllTextAsync(hostGuardConfigPath, JsonSerializer.Serialize(hostGuardConfiguration));
        using (var hostGuardCancellation = new CancellationTokenSource())
        {
            var hostRun = UnifiedGatewayHost.RunAsync(hostGuardConfigPath, hostGuardCancellation.Token);
            using var hostClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var hostReady = false;
            var expectedGatewayProductVersion = typeof(UnifiedGatewayHost).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Single().InformationalVersion;
            for (var attempt = 0; attempt < 30 && !hostReady; attempt++)
            {
                try
                {
                    using var health = await hostClient.GetAsync($"http://127.0.0.1:{hostGuardPort}/health");
                    using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStreamAsync());
                    var rootElement = healthJson.RootElement;
                    hostReady = health.IsSuccessStatusCode
                                && rootElement.GetProperty("product").GetString() == "CodexTotalManager"
                                && rootElement.GetProperty("productVersion").GetString() == expectedGatewayProductVersion
                                && rootElement.GetProperty("pid").GetInt32() == Environment.ProcessId
                                && rootElement.GetProperty("routeCount").GetInt32() == 1;
                }
                catch (Exception ex) when (ex is HttpRequestException
                                           or JsonException
                                           or KeyNotFoundException
                                           or InvalidOperationException)
                {
                    await Task.Delay(50);
                }
            }
            Ensure(hostReady, "来源身份网关测试没有启动。 ");
            using var guardedRequest = new HttpRequestMessage(
                HttpMethod.Post, $"http://127.0.0.1:{hostGuardPort}/v1/chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { model = cliSourceModel, messages = Array.Empty<object>() }),
                    Encoding.UTF8,
                    "application/json")
            };
            guardedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostAdmissionKey);
            guardedRequest.Headers.TryAddWithoutValidation(
                UnifiedGatewayHost.SourceFingerprintHeader, cliSourceFingerprint);
            using var guardedResponse = await hostClient.SendAsync(guardedRequest);
            using var guardedError = JsonDocument.Parse(await guardedResponse.Content.ReadAsStringAsync());
            Ensure((int)guardedResponse.StatusCode == 409
                   && guardedError.RootElement.GetProperty("error").GetProperty("type").GetString()
                   == "credential_identity_changed",
                "唯一 CLIProxy 账号被替换后，网关没有在上游请求前以 409 失败关闭。 ");

            var disabledRoute = new UnifiedGatewayRoute
            {
                GatewayModel = cliSourceModel,
                UpstreamModel = "gpt-5.6-terra",
                BaseUrl = extraPlus.BaseUrl,
                SecretName = extraPlus.ProviderId,
                PoolId = extraPlus.Id,
                PoolLabel = extraPlus.DisplayName,
                SourceId = cliSourceId,
                SourceKind = SubagentSourceKind.CliProxyPool.ToString(),
                RoutePrefix = cliRoutePrefix,
                Adapter = SubagentSourceIdentity.OpenAiChatAdapter,
                CredentialIdentity = credentialIdentityB!,
                SourceFingerprint = replacedAccountFingerprint
            };
            var disabledConfiguration = new UnifiedGatewayConfiguration
            {
                SchemaVersion = 4,
                Port = hostGuardPort,
                DataDirectory = identityTestRoot,
                Routes = new List<UnifiedGatewayRoute> { disabledRoute }
            };
            disabledConfiguration.ConfigurationFingerprint =
                UnifiedGatewayConfigurationIdentity.Compute(disabledConfiguration);
            await File.WriteAllTextAsync(hostGuardConfigPath, JsonSerializer.Serialize(disabledConfiguration));
            extraPlus.Enabled = false;
            await File.WriteAllTextAsync(
                Path.Combine(identityTestRoot, "pools.json"),
                JsonSerializer.Serialize(new PoolCatalogDocument
                {
                    SchemaVersion = 2,
                    Pools = new List<PoolDefinition> { extraPlus },
                    Active = new ActivePoolState { PoolId = extraPlus.Id, Model = "gpt-5.6-terra" }
                }));
            using var disabledRequest = new HttpRequestMessage(
                HttpMethod.Post, $"http://127.0.0.1:{hostGuardPort}/v1/chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { model = cliSourceModel, messages = Array.Empty<object>() }),
                    Encoding.UTF8,
                    "application/json")
            };
            disabledRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostAdmissionKey);
            disabledRequest.Headers.TryAddWithoutValidation(
                UnifiedGatewayHost.SourceFingerprintHeader, replacedAccountFingerprint);
            using var disabledResponse = await hostClient.SendAsync(disabledRequest);
            using var disabledError = JsonDocument.Parse(await disabledResponse.Content.ReadAsStringAsync());
            Ensure((int)disabledResponse.StatusCode == 409
                   && disabledError.RootElement.GetProperty("error").GetProperty("type").GetString()
                   == "source_state_changed",
                "route 准备后由另一实例停用号池，网关仍继续发送上游请求。 ");
            extraPlus.Enabled = true;
            hostGuardCancellation.Cancel();
            Ensure(await hostRun.WaitAsync(TimeSpan.FromSeconds(5)) == 0,
                "来源身份网关测试没有正常退出。 ");
        }
        guardedRoute.BaseUrl = $"http://127.0.0.1:{extraPlus.LocalPort!.Value + 101}/v1";
        Ensure(!SubagentSourceIdentity.IsRouteIdentityValid(guardedRoute),
            "网关端点被替换后仍可复用旧来源指纹。 ");

        var subagentDraft = subagents.LoadDraft();
        var implementer = subagentDraft.Roles.Single(item => item.RoleId == "cmm_implementer");
        implementer.WorkerKind = SubagentWorkerKind.External;
        implementer.ModelId = "cli/test-worker/model-a";
        var legacyExternalBypassPlan = subagents.CreatePlan(
            subagentDraft.Roles,
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { "cli/test-worker/model-a" });
        Ensure(!legacyExternalBypassPlan.CanApply
               && legacyExternalBypassPlan.Issues.Count > 0,
            "旧版无授权 API 仍可凭模型目录自动启用外部来源。 ");
        var subagentPlan = CreateCanonicalExternalPlan(subagents, subagentDraft.Roles);
        Ensure(subagentPlan.CanApply && subagentPlan.NativeRoleCount == 5 && subagentPlan.ExternalPendingCount == 1,
            "子代理计划没有正确区分 Codex 原生角色与待桥接外部 CLI 工人。");
        var firstSubagentBaseline = subagents.Inspect().BaselineRevision;
        var subagentResult = ApplyCanonicalExternal(
            subagents, subagentDraft.Roles, firstSubagentBaseline);
        var appliedCodexConfig = await File.ReadAllTextAsync(codexPath);
        var savedSubagentDraft = subagents.LoadDraft();
        var migratedImplementer = savedSubagentDraft.Roles.Single(item => item.RoleId == "cmm_implementer");
        Ensure(savedSubagentDraft.SchemaVersion == 3
               && migratedImplementer.SourceId == "gateway-cli:test-worker"
               && savedSubagentDraft.SourceAuthorizations.Count(item => item.Enabled
                    && item.SourceId == "gateway-cli:test-worker") == 1,
            "显式外部 CLI 角色没有安全保存为 schema v3 来源授权，或产生了重复授权。 ");
        var removeManagedMcp = ManagedTomlBlockEditor.Remove(
            appliedCodexConfig, savedSubagentDraft.ManagedMcpBlockHash);
        Ensure(appliedCodexConfig.Contains(ManagedTomlBlockEditor.TargetTableHeader, StringComparison.Ordinal)
               && removeManagedMcp.CanWrite
               && removeManagedMcp.CandidateText == originalCodex,
            "外部 CLI 角色没有写入精确 MCP 区块，或修改了区块外的 Codex 配置。");
        Ensure(File.Exists(Path.Combine(subagentDirectory, "cmm_supervisor.toml"))
               && (await File.ReadAllTextAsync(Path.Combine(subagentDirectory, "cmm_supervisor.toml"))).Contains("model = \"gpt-5.6-sol\"", StringComparison.Ordinal),
            "总监督角色没有写入预期的 Sol 模型。");
        Ensure(!File.Exists(Path.Combine(subagentDirectory, "cmm_implementer.toml")),
            "待桥接外部 CLI 工人被错误写成了 Codex 原生代理。");
        Ensure(await File.ReadAllTextAsync(unrelatedAgentPath) == unrelatedAgent,
            "应用过程中修改了非总管家拥有的代理文件。");
        Ensure(await File.ReadAllTextAsync(unicodeAgentPath) == unicodeAgent,
            "应用过程中修改了带中文或空格文件名的用户自有代理文件。");
        Ensure(File.Exists(Path.Combine(subagentResult.BackupDirectory, "config.toml")),
            "子代理应用前没有生成 Codex 主配置只读备份。");
        Ensure(subagentCodexValidator.Requests.Count == 1,
            "应用候选没有且仅有一次交给 Codex 自身解析器验证。");
        var firstCodexValidation = subagentCodexValidator.Requests.Single();
        var appliedManagedAgentBytes = Directory
            .EnumerateFiles(subagentDirectory, "cmm_*.toml", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                path => Path.GetFileName(path)!,
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
        var appliedCodexConfigBytes = await File.ReadAllBytesAsync(codexPath);
        var appliedUserOwnedAgentBytes = await File.ReadAllBytesAsync(unrelatedAgentPath);
        var appliedUnicodeAgentBytes = await File.ReadAllBytesAsync(unicodeAgentPath);
        Ensure(firstCodexValidation.ConfigBytes.SequenceEqual(appliedCodexConfigBytes)
               && firstCodexValidation.AgentFiles.Count == appliedManagedAgentBytes.Count + 2
               && appliedManagedAgentBytes.All(pair =>
                   firstCodexValidation.AgentFiles.TryGetValue(pair.Key, out var validatedBytes)
                   && validatedBytes.SequenceEqual(pair.Value))
               && firstCodexValidation.AgentFiles.TryGetValue("user-owned.toml", out var validatedUserOwned)
               && validatedUserOwned.SequenceEqual(appliedUserOwnedAgentBytes)
               && firstCodexValidation.AgentFiles.TryGetValue("代码 审查.toml", out var validatedUnicodeAgent)
               && validatedUnicodeAgent.SequenceEqual(appliedUnicodeAgentBytes)
               && !firstCodexValidation.AgentFiles.ContainsKey("cmm_implementer.toml"),
            "Codex 自身解析器收到的不是最终精确候选 config/全部保留 Agent，或包含了外部角色文件。");
        var appliedSubagents = subagents.Inspect();
        Ensure(appliedSubagents.AppliedRoles.Values.Count(item => item.ExactMatch) == 6
               && appliedSubagents.Draft.Roles.Count(item => item.WorkerKind == SubagentWorkerKind.External) == 1
               && appliedSubagents.Bridge.ConfigurationExact,
            "子代理落盘后的重新读取验证不正确。");

        var configBeforeIdempotentApply = await File.ReadAllBytesAsync(codexPath);
        ApplyCanonicalExternal(subagents, subagentDraft.Roles, subagents.Inspect().BaselineRevision);
        Ensure((await File.ReadAllBytesAsync(codexPath)).AsSpan().SequenceEqual(configBeforeIdempotentApply),
            "相同外部 CLI 配置重复应用改变了 Codex 主配置字节。");

        var supervisorPath = Path.Combine(subagentDirectory, "cmm_supervisor.toml");
        var supervisorManagedBytes = await File.ReadAllBytesAsync(supervisorPath);
        var configBeforeAgentConflict = await File.ReadAllBytesAsync(codexPath);
        await File.AppendAllTextAsync(supervisorPath, "# user edit\n");
        var agentConflictBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, subagents.Inspect().BaselineRevision);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("手工修改", StringComparison.Ordinal))
        {
            agentConflictBlocked = true;
        }
        Ensure(agentConflictBlocked
               && (await File.ReadAllBytesAsync(codexPath)).AsSpan().SequenceEqual(configBeforeAgentConflict),
            "被手工修改的托管 Agent 没有阻止应用，或冲突时改变了主配置。");
        await File.WriteAllBytesAsync(supervisorPath, supervisorManagedBytes);

        var exactMcpConfig = await File.ReadAllTextAsync(codexPath);
        await File.WriteAllTextAsync(codexPath,
            exactMcpConfig.Replace("tool_timeout_sec = 300", "tool_timeout_sec = 301", StringComparison.Ordinal));
        var mcpConflictBytes = await File.ReadAllBytesAsync(codexPath);
        var mcpConflictBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, subagents.Inspect().BaselineRevision);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("MCP", StringComparison.OrdinalIgnoreCase))
        {
            mcpConflictBlocked = true;
        }
        Ensure(mcpConflictBlocked
               && (await File.ReadAllBytesAsync(codexPath)).AsSpan().SequenceEqual(mcpConflictBytes),
            "被手工修改的 MCP 区块没有触发冲突保护，或冲突时被覆盖。");
        await File.WriteAllTextAsync(codexPath, exactMcpConfig);

        var staleBaseline = subagents.Inspect().BaselineRevision;
        await File.AppendAllTextAsync(codexPath, "\n# concurrent user edit\n");
        var concurrentConfigBytes = await File.ReadAllBytesAsync(codexPath);
        var staleBaselineBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, staleBaseline);
        }
        catch (IOException ex) when (ex.Message.Contains("发生变化", StringComparison.Ordinal))
        {
            staleBaselineBlocked = true;
        }
        Ensure(staleBaselineBlocked
               && (await File.ReadAllBytesAsync(codexPath)).AsSpan().SequenceEqual(concurrentConfigBytes),
            "过期基线没有阻止应用，或覆盖了并发用户修改。");
        await File.WriteAllTextAsync(codexPath, exactMcpConfig);

        var staleUserAgentBaseline = subagents.Inspect().BaselineRevision;
        await File.AppendAllTextAsync(unrelatedAgentPath, "# concurrent user agent edit\n");
        var concurrentUserAgentBytes = await File.ReadAllBytesAsync(unrelatedAgentPath);
        var staleUserAgentBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, staleUserAgentBaseline);
        }
        catch (IOException ex) when (ex.Message.Contains("发生变化", StringComparison.Ordinal))
        {
            staleUserAgentBlocked = true;
        }
        Ensure(staleUserAgentBlocked
               && (await File.ReadAllBytesAsync(unrelatedAgentPath)).SequenceEqual(concurrentUserAgentBytes),
            "用户自有 Agent 的并发变化没有进入基线保护，或被总管家覆盖。");
        await File.WriteAllTextAsync(unrelatedAgentPath, unrelatedAgent);

        var healthyDraftBytes = await File.ReadAllBytesAsync(subagentDataPath);
        await File.WriteAllTextAsync(subagentDataPath, "{ broken draft");
        var corruptDraftSnapshot = subagents.Inspect();
        var corruptDraftBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, corruptDraftSnapshot.BaselineRevision);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("损坏草稿", StringComparison.Ordinal))
        {
            corruptDraftBlocked = true;
        }
        Ensure(corruptDraftBlocked && (await File.ReadAllTextAsync(subagentDataPath)) == "{ broken draft",
            "损坏的子代理草稿没有进入只读保护，或被默认值覆盖。");
        await File.WriteAllBytesAsync(subagentDataPath, healthyDraftBytes);

        var futureDraftJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        futureDraftJsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var futureDraft = JsonSerializer.Deserialize<SubagentConfigurationDocument>(
                              healthyDraftBytes, futureDraftJsonOptions)
                          ?? throw new InvalidOperationException("测试草稿无法反序列化。");
        futureDraft.SchemaVersion = 99;
        var futureDraftBytes = JsonSerializer.SerializeToUtf8Bytes(futureDraft, futureDraftJsonOptions);
        await File.WriteAllBytesAsync(subagentDataPath, futureDraftBytes);
        var futureSnapshot = subagents.Inspect();
        var futureSchemaBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, futureSnapshot.BaselineRevision);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("版本 99", StringComparison.Ordinal))
        {
            futureSchemaBlocked = true;
        }
        Ensure(futureSchemaBlocked
               && (await File.ReadAllBytesAsync(subagentDataPath)).SequenceEqual(futureDraftBytes),
            "未来版本子代理草稿被降级覆盖，或没有进入只读保护。 ");
        await File.WriteAllBytesAsync(subagentDataPath, healthyDraftBytes);

        var nativeOnlyDraft = subagents.LoadDraft();
        var nativeImplementer = nativeOnlyDraft.Roles.Single(item => item.RoleId == "cmm_implementer");
        nativeImplementer.WorkerKind = SubagentWorkerKind.CodexNative;
        nativeImplementer.ModelId = "gpt-5.6-terra";
        subagents.Apply(
            nativeOnlyDraft.Roles,
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { "cli/test-worker/model-a" },
            subagents.Inspect().BaselineRevision);
        Ensure(await File.ReadAllTextAsync(codexPath) == originalCodex
               && File.Exists(Path.Combine(subagentDirectory, "cmm_implementer.toml")),
            "全部切回 Codex 原生角色时没有精确移除 MCP 区块或恢复原生 Agent。");

        var externalAgainDraft = subagents.LoadDraft();
        var externalAgainImplementer = externalAgainDraft.Roles.Single(item => item.RoleId == "cmm_implementer");
        externalAgainImplementer.WorkerKind = SubagentWorkerKind.External;
        externalAgainImplementer.ModelId = "cli/test-worker/model-a";
        ApplyCanonicalExternal(subagents, externalAgainDraft.Roles, subagents.Inspect().BaselineRevision);

        var validatorGuardRoot = Path.Combine(root, "subagent-validator-guard");
        var validatorGuardConfig = Path.Combine(validatorGuardRoot, ".codex", "config.toml");
        var validatorGuardAgents = Path.Combine(validatorGuardRoot, ".codex", "agents");
        var validatorGuardDraft = Path.Combine(validatorGuardRoot, "manager-data", "subagents.json");
        var validatorGuardBackups = Path.Combine(validatorGuardRoot, "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(validatorGuardConfig)!);
        await File.WriteAllTextAsync(validatorGuardConfig, originalCodex);
        var validatorBootstrap = new SubagentConfigurationService(
            validatorGuardConfig,
            validatorGuardAgents,
            validatorGuardDraft,
            validatorGuardBackups,
            bridgeExecutablePath: bridgeExecutable,
            bridgeStatePath: Path.Combine(validatorGuardRoot, "manager-data", "worker-state.json"),
            codexConfigValidator: new TestCodexConfigValidator(
                new CodexConfigValidationResult(true, true, "test validator accepted")));
        var validatorGuardSelection = validatorBootstrap.LoadDraft();
        validatorBootstrap.Apply(
            validatorGuardSelection.Roles,
            new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
            new[] { "cli/test-worker/model-a" },
            validatorBootstrap.Inspect().BaselineRevision);
        var guardedBytesBeforeFailure = CaptureDirectoryFileBytes(validatorGuardRoot);

        var rejectingValidator = new TestCodexConfigValidator(
            new CodexConfigValidationResult(true, false, "test validator rejected"));
        var rejectingService = new SubagentConfigurationService(
            validatorGuardConfig,
            validatorGuardAgents,
            validatorGuardDraft,
            validatorGuardBackups,
            bridgeExecutablePath: bridgeExecutable,
            bridgeStatePath: Path.Combine(validatorGuardRoot, "manager-data", "worker-state.json"),
            codexConfigValidator: rejectingValidator);
        var rejectedSelection = rejectingService.LoadDraft();
        rejectedSelection.Roles.Single(item => item.RoleId == "cmm_documenter").ModelId = "gpt-5.6-sol";
        Exception? rejectedValidationError = null;
        try
        {
            rejectingService.Apply(
                rejectedSelection.Roles,
                new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
                new[] { "cli/test-worker/model-a" },
                rejectingService.Inspect().BaselineRevision);
        }
        catch (Exception ex)
        {
            rejectedValidationError = ex;
        }
        Ensure(rejectedValidationError is InvalidOperationException
               && rejectingValidator.Requests.Count == 1
               && DirectoryFileBytesEqual(guardedBytesBeforeFailure, CaptureDirectoryFileBytes(validatorGuardRoot)),
            "Codex 自身解析器拒绝候选后，Apply 没有失败，或真实 config/draft/agents/backup 字节发生了变化。");

        var unavailableValidator = new TestCodexConfigValidator(
            new CodexConfigValidationResult(false, false, "test validator unavailable"));
        var unavailableService = new SubagentConfigurationService(
            validatorGuardConfig,
            validatorGuardAgents,
            validatorGuardDraft,
            validatorGuardBackups,
            bridgeExecutablePath: bridgeExecutable,
            bridgeStatePath: Path.Combine(validatorGuardRoot, "manager-data", "worker-state.json"),
            codexConfigValidator: unavailableValidator);
        var unavailableSelection = unavailableService.LoadDraft();
        unavailableSelection.Roles.Single(item => item.RoleId == "cmm_documenter").ModelId = "gpt-5.6-sol";
        Exception? unavailableValidationError = null;
        try
        {
            unavailableService.Apply(
                unavailableSelection.Roles,
                new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
                new[] { "cli/test-worker/model-a" },
                unavailableService.Inspect().BaselineRevision);
        }
        catch (Exception ex)
        {
            unavailableValidationError = ex;
        }
        Ensure(unavailableValidationError is InvalidOperationException
               && unavailableValidator.Requests.Count == 1
               && DirectoryFileBytesEqual(guardedBytesBeforeFailure, CaptureDirectoryFileBytes(validatorGuardRoot)),
            "Codex 自身解析器不可用时没有 fail closed，或真实 config/draft/agents/backup 字节发生了变化。");

        var unmanagedRoot = Path.Combine(root, "unmanaged-subagents");
        var unmanagedConfig = Path.Combine(unmanagedRoot, ".codex", "config.toml");
        var unmanagedAgents = Path.Combine(unmanagedRoot, ".codex", "agents");
        Directory.CreateDirectory(unmanagedAgents);
        await File.WriteAllTextAsync(unmanagedConfig, originalCodex);
        var unmanagedSupervisor = Path.Combine(unmanagedAgents, "cmm_supervisor.toml");
        const string userOwnedSameName = "name = \"cmm_supervisor\"\ndescription = \"user file\"\ndeveloper_instructions = \"keep\"\n";
        await File.WriteAllTextAsync(unmanagedSupervisor, userOwnedSameName);
        var unmanagedService = new SubagentConfigurationService(
            unmanagedConfig,
            unmanagedAgents,
            Path.Combine(unmanagedRoot, "data", "subagents.json"),
            Path.Combine(unmanagedRoot, "backups"),
            bridgeExecutablePath: bridgeExecutable,
            bridgeStatePath: Path.Combine(unmanagedRoot, "data", "worker-state.json"),
            codexConfigValidator: subagentCodexValidator);
        var unmanagedDraft = unmanagedService.LoadDraft();
        var unmanagedBlocked = false;
        try
        {
            unmanagedService.Apply(
                unmanagedDraft.Roles,
                new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
                new[] { "cli/test-worker/model-a" },
                unmanagedService.Inspect().BaselineRevision);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("不是总管家拥有", StringComparison.Ordinal))
        {
            unmanagedBlocked = true;
        }
        Ensure(unmanagedBlocked && await File.ReadAllTextAsync(unmanagedSupervisor) == userOwnedSameName,
            "用户自建的同名 cmm_*.toml 被总管家覆盖。");

        var unmanagedMcpConfig = Path.Combine(unmanagedRoot, ".codex", "unmanaged-mcp.toml");
        const string userMcp = "model = \"gpt-old\"\n[mcp_servers.codex_total_manager_external]\ncommand = \"user-tool.exe\"\n";
        await File.WriteAllTextAsync(unmanagedMcpConfig, userMcp);
        var unmanagedMcpService = new SubagentConfigurationService(
            unmanagedMcpConfig,
            Path.Combine(unmanagedRoot, ".codex", "agents-mcp"),
            Path.Combine(unmanagedRoot, "data", "mcp-subagents.json"),
            Path.Combine(unmanagedRoot, "mcp-backups"),
            bridgeExecutablePath: bridgeExecutable,
            bridgeStatePath: Path.Combine(unmanagedRoot, "data", "mcp-state.json"),
            codexConfigValidator: subagentCodexValidator);
        var unmanagedMcpDraft = unmanagedMcpService.LoadDraft();
        var unmanagedMcpImplementer = unmanagedMcpDraft.Roles.Single(item => item.RoleId == "cmm_implementer");
        unmanagedMcpImplementer.WorkerKind = SubagentWorkerKind.External;
        unmanagedMcpImplementer.ModelId = "cli/test-worker/model-a";
        var unmanagedMcpBlocked = false;
        try
        {
            ApplyCanonicalExternal(
                unmanagedMcpService,
                unmanagedMcpDraft.Roles,
                unmanagedMcpService.Inspect().BaselineRevision);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("MCP", StringComparison.OrdinalIgnoreCase))
        {
            unmanagedMcpBlocked = true;
        }
        Ensure(unmanagedMcpBlocked && await File.ReadAllTextAsync(unmanagedMcpConfig) == userMcp,
            "用户自建的同名 MCP 表没有触发冲突保护，或被总管家改写。");

        var inlineMcpConflicts = new[]
        {
            "[mcp_servers]\ncodex_total_manager_external = { command = \"user-tool.exe\" }\n",
            "mcp_servers.codex_total_manager_external = { command = \"user-tool.exe\" }\n",
            "mcp_servers = { codex_total_manager_external = { command = \"user-tool.exe\" } }\n",
            "[\"mcp_servers\"]\n'codex_total_manager_external' = { command = \"user-tool.exe\" }\n"
        };
        foreach (var inlineConflict in inlineMcpConflicts)
        {
            var inspection = ManagedTomlBlockEditor.Inspect(inlineConflict);
            Ensure(inspection.Conflict
                   && inspection.Status == ManagedTomlEditStatus.ConflictUnmanagedTargetTable,
                "合法的同名 MCP inline table/key 没有触发所有权冲突保护。");
        }
        var inlineStringDecoy = "note = \"mcp_servers.codex_total_manager_external = { command = 'decoy' }\"\n";
        Ensure(!ManagedTomlBlockEditor.Inspect(inlineStringDecoy).Conflict,
            "普通字符串中的 MCP 伪定义被误判为真实 TOML 冲突。");

        var supervisorBeforeUnsafeAttempt = await File.ReadAllTextAsync(supervisorPath);
        await File.WriteAllTextAsync(codexPath, originalCodex + "model_provider = \"custom\"\n");
        var unsafeBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, subagents.Inspect().BaselineRevision);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("custom provider", StringComparison.OrdinalIgnoreCase))
        {
            unsafeBlocked = true;
        }
        Ensure(unsafeBlocked && await File.ReadAllTextAsync(supervisorPath) == supervisorBeforeUnsafeAttempt,
            "旧 custom provider 没有阻止子代理写入，或阻止后仍改变了托管文件。");

        foreach (var unsafeCustom in new[]
                 {
                     "model_provider = \"custom\" # legacy\n",
                     "\"model_provider\" = 'custom' # quoted\n",
                     "\"model\\u005fprovider\" = \"cus\\u0074om\" # unicode escape\n",
                     "model_provider = \"\"\"custom\"\"\" # multiline basic form\n",
                     "model_provider = '''custom''' # multiline literal form\n",
                     "model_provider = \"\"\"cus\\   \ntom\"\"\" # spaced continuation\n"
                 })
        {
            await File.WriteAllTextAsync(codexPath, originalCodex + unsafeCustom);
            Ensure(!subagents.Inspect().ConfigSafe,
                "带注释或 quoted key 的 custom provider 绕过了安全检查。");
        }
        foreach (var disabledAgents in new[]
                 {
                     "agents.enabled = false # dotted\n",
                     "\"agents\".'enabled' = false # quoted dotted\n",
                     "[agents] # comment\nenabled = false\n",
                     "['agents'] # quoted table\n\"enabled\" = false # quoted key\n",
                     "agents = { enabled = false } # inline\n",
                     "\"\\u0061gents\".\"\\u0065nabled\" = false # unicode dotted\n",
                     "[\"\\u0061gents\"]\n\"\\u0065nabled\" = false\n",
                     "agents = { \"\\u0065nabled\" = false } # unicode inline\n"
                 })
        {
            await File.WriteAllTextAsync(codexPath, originalCodex + disabledAgents);
            Ensure(!subagents.Inspect().AgentsEnabled,
                "合法 TOML 写法的 agents.enabled=false 绕过了安全检查。");
        }
        await File.WriteAllTextAsync(codexPath,
            originalCodex + "agents = { \"\\u0065nabled\" = true }\n\"\\u006eote\" = \"custom\"\n");
        Ensure(subagents.Inspect().ConfigSafe && subagents.Inspect().AgentsEnabled,
            "安全的 quoted/unicode TOML 被错误阻止。");

        foreach (var validCase in new[]
                 {
                     (Name: "compound", Toml: "arr = [\n  1,\n  2,\n]\nmeta = { a = 1, b = \"ok\" }\nnote = \"\"\"hello\nworld\"\"\"\n"),
                     (Name: "four-quotes", Toml: "x = \"\"\"\"a\"\"\"\"\n"),
                     (Name: "spaced-continuation", Toml: "x = \"\"\"a\\   \nb\"\"\"\n")
                 })
        {
            Ensure(ManagedTomlBlockEditor.InspectCodexSafetySettings(validCase.Toml).SyntaxValid,
                $"合法 TOML 用例 {validCase.Name} 被错误判为损坏。");
        }

        foreach (var malformedCase in new[]
                 {
                     (Name: "unclosed-array", Toml: "arr = [1, 2\n"),
                     (Name: "unclosed-inline-table", Toml: "meta = { a = 1\n"),
                     (Name: "unclosed-multiline-string", Toml: "note = \"\"\"unterminated\n"),
                     (Name: "unclosed-string", Toml: "note = \"unterminated\n"),
                     (Name: "not-an-assignment", Toml: "this is not a TOML assignment\n"),
                     (Name: "unknown-value", Toml: "x = ?\n"),
                     (Name: "trailing-garbage", Toml: "x = 1 garbage\n"),
                     (Name: "double-comma", Toml: "x = [1,,2]\n"),
                     (Name: "duplicate-key", Toml: "x = 0\nx = 1\n"),
                     (Name: "single-line-spaced-continuation", Toml: "x = \"a\\   \nb\"\n"),
                     (Name: "quoted-key-spaced-continuation", Toml: "\"a\\   \nb\" = 1\n"),
                     (Name: "table-key-spaced-continuation", Toml: "[\"a\\   \nb\"]\nx = 1\n"),
                     (Name: "toml-1.1-escape", Toml: "note = \"\\e\"\n"),
                     (Name: "toml-1.1-multiline-inline-table", Toml: "x = {\n  a = 1,\n}\n")
                 })
        {
            Ensure(!ManagedTomlBlockEditor.InspectCodexSafetySettings(malformedCase.Toml).SyntaxValid,
                $"损坏 TOML 用例 {malformedCase.Name} 绕过了失败关闭检查。");
        }

        var malformedConfigBytes = Encoding.UTF8.GetBytes(originalCodex + "arr = [1, 2\n");
        await File.WriteAllBytesAsync(codexPath, malformedConfigBytes);
        var malformedSnapshot = subagents.Inspect();
        var malformedApplyBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, malformedSnapshot.BaselineRevision);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("TOML", StringComparison.OrdinalIgnoreCase))
        {
            malformedApplyBlocked = true;
        }
        Ensure(!malformedSnapshot.ConfigSafe
               && malformedApplyBlocked
               && (await File.ReadAllBytesAsync(codexPath)).AsSpan().SequenceEqual(malformedConfigBytes),
            "损坏 TOML 没有进入只读保护，或拒绝应用时改变了原字节。");

        var invalidUtf8ConfigBytes = Encoding.UTF8.GetBytes(originalCodex).Concat(new byte[] { 0xFF }).ToArray();
        await File.WriteAllBytesAsync(codexPath, invalidUtf8ConfigBytes);
        var invalidUtf8Snapshot = subagents.Inspect();
        var invalidUtf8ApplyBlocked = false;
        try
        {
            ApplyCanonicalExternal(subagents, subagentDraft.Roles, invalidUtf8Snapshot.BaselineRevision);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            invalidUtf8ApplyBlocked = true;
        }
        Ensure(!invalidUtf8Snapshot.ConfigSafe
               && invalidUtf8ApplyBlocked
               && (await File.ReadAllBytesAsync(codexPath)).AsSpan().SequenceEqual(invalidUtf8ConfigBytes),
            "非法 UTF-8 没有进入只读保护，或拒绝应用时改变了原字节。");
        await File.WriteAllTextAsync(codexPath, originalCodex);

        var workerDraft = new SubagentConfigurationDocument
        {
            Roles = subagents.Roles.Select(role => new SubagentRoleSelection
            {
                RoleId = role.Id,
                WorkerKind = SubagentWorkerKind.CodexNative,
                ModelId = role.DefaultModel
            }).ToList()
        };
        var workerExplorer = workerDraft.Roles.Single(item => item.RoleId == "cmm_explorer");
        workerExplorer.WorkerKind = SubagentWorkerKind.External;
        workerExplorer.ModelId = "cli/test-worker/model-a";
        workerExplorer.SourceId = "gateway-cli:test-worker";
        var workerExternalPool = CreateTestExternalPool();
        var workerSourceFingerprint = SubagentSourceIdentity.ComputeForPool(
            workerExternalPool,
            "gateway-cli:test-worker",
            SubagentSourceKind.CliProxyPool,
            "cli/test-worker/",
            SubagentSourceIdentity.OpenAiChatAdapter,
            workerExternalPool.ProviderId);
        workerDraft.SourceAuthorizations.Add(new SubagentSourceAuthorization
        {
            SourceId = "gateway-cli:test-worker",
            ExpectedFingerprint = workerSourceFingerprint,
            Enabled = true,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedDisplayName = "测试外部 CLI 号池"
        });
        var workerConfig = new TestWorkerConfiguration(subagents.Roles, workerDraft);
        var workerBackend = new TestWorkerBackend(new[] { "cli/test-worker/model-a" });
        var workerAudit = new TestWorkerAuditSink();
        using var worker = new ExternalWorkerService(workerConfig, workerBackend, workerAudit);
        const string privateTaskMarker = "SECRET_TASK_MUST_NOT_ENTER_AUDIT";
        var workerCompletion = await worker.DelegateAsync(new ExternalWorkerInvocation(
            "cmm_explorer",
            privateTaskMarker,
            "SECRET_CONTEXT_MUST_NOT_ENTER_AUDIT",
            128));
        Ensure(workerBackend.Requests.Count == 1
               && workerBackend.Requests[0].Model == "cli/test-worker/model-a"
               && workerBackend.Requests[0].MaxOutputTokens == 128
               && workerCompletion.ConfiguredModel == "cli/test-worker/model-a"
               && workerCompletion.ResolvedModel == "upstream/model-a"
               && workerCompletion.AccountSource == "gateway-cli:test-worker"
               && workerBackend.Requests[0].SourceId == "gateway-cli:test-worker"
               && workerBackend.Requests[0].ExpectedSourceFingerprint == workerSourceFingerprint,
            "外部 CLI 工人没有强制使用角色保存的精确路由，或没有如实返回路由证据。");
        var auditJson = JsonSerializer.Serialize(workerAudit.Entries);
        Ensure(!auditJson.Contains(privateTaskMarker, StringComparison.Ordinal)
               && !auditJson.Contains("SECRET_CONTEXT", StringComparison.Ordinal)
               && workerAudit.Entries.Count == 2,
            "外部 CLI 工人审计泄露了任务/上下文，或没有记录开始和完成事件。");
        var enabledWorkerRoles = worker.ReadEnabledRoleOptions();
        Ensure(enabledWorkerRoles.Count == 1
               && enabledWorkerRoles[0].RoleId == "cmm_explorer"
               && enabledWorkerRoles[0].ConfiguredModel == "cli/test-worker/model-a"
               && enabledWorkerRoles[0].SourceId == "gateway-cli:test-worker",
            "外部工人 MCP 无法从已保存配置发现当前可委派角色和精确模型。");

        var savedWorkerGrant = workerDraft.SourceAuthorizations.Single();
        workerDraft.SourceAuthorizations.Clear();
        var requestsBeforeMissingGrant = workerBackend.Requests.Count;
        var missingGrantBlocked = false;
        try
        {
            await worker.DelegateAsync(new ExternalWorkerInvocation("cmm_explorer", "must not reach backend"));
        }
        catch (ExternalWorkerException ex) when (ex.Code == "source_not_authorized")
        {
            missingGrantBlocked = true;
        }
        Ensure(missingGrantBlocked && workerBackend.Requests.Count == requestsBeforeMissingGrant,
            "来源授权缺失时仍进入了外部模型后端。 ");
        workerDraft.SourceAuthorizations.Add(savedWorkerGrant);
        workerDraft.SourceAuthorizations.Add(new SubagentSourceAuthorization
        {
            SourceId = savedWorkerGrant.SourceId,
            ExpectedFingerprint = savedWorkerGrant.ExpectedFingerprint,
            Enabled = true,
            AuthorizedAt = savedWorkerGrant.AuthorizedAt,
            AuthorizedDisplayName = savedWorkerGrant.AuthorizedDisplayName
        });
        var duplicateGrantBlocked = false;
        try
        {
            await worker.DelegateAsync(new ExternalWorkerInvocation("cmm_explorer", "must not reach backend"));
        }
        catch (ExternalWorkerException ex) when (ex.Code == "source_not_authorized")
        {
            duplicateGrantBlocked = true;
        }
        Ensure(duplicateGrantBlocked && workerBackend.Requests.Count == requestsBeforeMissingGrant,
            "重复来源授权仍进入了外部模型后端。 ");
        workerDraft.SourceAuthorizations.RemoveAt(1);

        var mcpInput = new StringBuilder()
            .AppendLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"clientInfo\":{\"name\":\"unit\",\"version\":\"1\"}}}")
            .AppendLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}")
            .AppendLine("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"delegate_to_worker\",\"arguments\":{\"role_id\":\"cmm_explorer\",\"task\":\"unit task\",\"max_output_tokens\":64}}}")
            .AppendLine("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"delegate_to_worker\",\"arguments\":{\"role_id\":\"cmm_explorer\",\"task\":\"must reject\",\"model\":\"other/model\"}}}")
            .ToString();
        using var mcpReader = new ControlledTextReader();
        using var mcpWriter = new SignalingTextWriter(expectedLineCount: 4);
        foreach (var line in mcpInput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            mcpReader.AddLine(line);
        var mcpHost = new ExternalWorkerMcpHost(
            worker,
            readDiscoverableRoles: () => new[]
            {
                new ExternalWorkerDiscoverableRole(
                    "cmm_explorer",
                    "资料探索",
                    subagents.Roles.Single(role => role.Id == "cmm_explorer").Purpose,
                    "cli/test-worker/model-a",
                    "gateway-cli:test-worker")
            });
        var mcpRun = mcpHost.RunAsync(mcpReader, mcpWriter);
        await mcpWriter.ExpectedLinesReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        mcpReader.Complete();
        Ensure(await mcpRun == 0, "外部工人 MCP 内存握手没有正常结束。");
        var mcpLines = mcpWriter.ReadLines();
        Ensure(mcpLines.Length == 4, "外部工人 MCP 没有为四个带 id 的请求逐一返回。 ");
        var mcpResponses = mcpLines.Select(line => JsonDocument.Parse(line)).ToArray();
        var toolsResponse = mcpResponses.Single(document =>
            document.RootElement.GetProperty("id").GetInt32() == 2);
        var workerTools = toolsResponse.RootElement.GetProperty("result").GetProperty("tools");
        var roleIdSchema = workerTools[0].GetProperty("inputSchema").GetProperty("properties").GetProperty("role_id");
        Ensure(workerTools.GetArrayLength() == 1
               && workerTools[0].GetProperty("name").GetString() == ExternalWorkerMcpHost.ToolName
               && workerTools[0].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()
               && roleIdSchema.GetProperty("enum").GetArrayLength() == 1
               && roleIdSchema.GetProperty("enum")[0].GetString() == "cmm_explorer",
            $"外部工人 MCP 暴露了多余工具，或没有枚举当前角色、用途和只读边界。 tools={workerTools.GetRawText()}");
        var successfulToolResponse = mcpResponses.Single(document =>
            document.RootElement.GetProperty("id").GetInt32() == 3);
        Ensure(!successfulToolResponse.RootElement.GetProperty("result").GetProperty("isError").GetBoolean()
               && successfulToolResponse.RootElement.GetProperty("result").GetProperty("structuredContent")
                     .GetProperty("configured_model").GetString() == "cli/test-worker/model-a",
            "外部工人 MCP 成功结果没有包含精确配置模型证据。");
        var rejectedToolResponse = mcpResponses.Single(document =>
            document.RootElement.GetProperty("id").GetInt32() == 4);
        Ensure(rejectedToolResponse.RootElement.GetProperty("error").GetProperty("code").GetInt32() == -32602
               && workerBackend.Requests.Count == 2,
            "外部工人 MCP 没有拒绝调用方注入 model 字段，或拒绝后仍发起模型请求。");
        foreach (var response in mcpResponses) response.Dispose();

        var blockingBackend = new BlockingWorkerBackend(new[] { "cli/test-worker/model-a" });
        using (var firstWorker = new ExternalWorkerService(workerConfig, blockingBackend, new TestWorkerAuditSink()))
        using (var secondWorker = new ExternalWorkerService(workerConfig, blockingBackend, new TestWorkerAuditSink()))
        {
            var firstCall = firstWorker.DelegateAsync(new ExternalWorkerInvocation("cmm_explorer", "first"));
            await blockingBackend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var concurrentBlocked = false;
            try
            {
                await secondWorker.DelegateAsync(new ExternalWorkerInvocation("cmm_explorer", "second"));
            }
            catch (ExternalWorkerException ex) when (ex.Code == "worker_busy")
            {
                concurrentBlocked = true;
            }
            Ensure(concurrentBlocked && blockingBackend.RequestCount == 1,
                "两个外部工人服务实例仍可并发发送，跨进程额度闸门未生效。");
            blockingBackend.Release();
            await firstCall;
        }

        var cancelBackend = new BlockingWorkerBackend(new[] { "cli/test-worker/model-a" });
        using (var cancelWorker = new ExternalWorkerService(workerConfig, cancelBackend, new TestWorkerAuditSink()))
        {
            using var cancelReader = new ControlledTextReader();
            using var cancelWriter = new SignalingTextWriter(expectedLineCount: 1);
            var cancelHost = new ExternalWorkerMcpHost(
                cancelWorker,
                readDiscoverableRoles: () => new[]
                {
                    new ExternalWorkerDiscoverableRole(
                        "cmm_explorer", "资料探索", "只读探索",
                        "cli/test-worker/model-a", "gateway-cli:test-worker")
                });
            var cancelRun = cancelHost.RunAsync(cancelReader, cancelWriter);
            cancelReader.AddLine("{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"tools/call\",\"params\":{\"name\":\"delegate_to_worker\",\"arguments\":{\"role_id\":\"cmm_explorer\",\"task\":\"slow\"}}}");
            await cancelBackend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelReader.AddLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":31,\"reason\":\"unit\"}}");
            await cancelWriter.ExpectedLinesReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelReader.Complete();
            Ensure(await cancelRun == 0,
                "MCP 取消测试没有正常退出。");
            var cancelLines = cancelWriter.ReadLines();
            Ensure(cancelBackend.RequestCount == 1 && cancelBackend.CancellationObserved,
                "notifications/cancelled 没有终止进行中的外部后端请求。");
            using var cancelResponse = JsonDocument.Parse(cancelLines.Single());
            Ensure(cancelResponse.RootElement.GetProperty("id").GetInt32() == 31
                   && cancelResponse.RootElement.GetProperty("result").GetProperty("isError").GetBoolean()
                   && cancelResponse.RootElement.GetProperty("result").GetProperty("structuredContent")
                       .GetProperty("error_code").GetString() == "request_canceled",
                "MCP 取消没有返回结构化 request_canceled 结果。");
        }

        var requestsBeforeAuditFailure = workerBackend.Requests.Count;
        using var auditFailureWorker = new ExternalWorkerService(
            workerConfig, workerBackend, new FailingWorkerAuditSink());
        var auditFailureBlockedNetwork = false;
        try
        {
            await auditFailureWorker.DelegateAsync(new ExternalWorkerInvocation("cmm_explorer", "must not reach network"));
        }
        catch (ExternalWorkerException ex) when (ex.Code == "audit_write_failed")
        {
            auditFailureBlockedNetwork = true;
        }
        Ensure(auditFailureBlockedNetwork && workerBackend.Requests.Count == requestsBeforeAuditFailure,
            "审计不可写时外部工人仍发起了模型请求。");

        var stateAuditRoot = Path.Combine(root, "worker-audit-state");
        using (var stateAudit = new ExternalWorkerAuditStore(
                   Path.Combine(stateAuditRoot, "audit.jsonl"),
                   Path.Combine(stateAuditRoot, "state.json")))
        {
            await stateAudit.RecordHandshakeAsync("codex-unit", "1.0");
            await stateAudit.AppendAsync(new ExternalWorkerAuditEntry(
                DateTimeOffset.Now,
                "completed",
                "unit-request",
                "cmm_explorer",
                "cli/test-worker/model-a",
                "upstream/model-a",
                "gateway-cli:test-worker",
                "success",
                200,
                12,
                3,
                15,
                25,
                null));
            var runtimeState = stateAudit.ReadState();
            Ensure(runtimeState.LastHandshakeClient == "codex-unit"
                   && runtimeState.LastCallSucceeded == true
                   && runtimeState.LastRequestedModel == "cli/test-worker/model-a"
                   && runtimeState.LastResolvedModel == "upstream/model-a"
                   && runtimeState.LastAccountSource == "gateway-cli:test-worker"
                   && runtimeState.InputTokens == 12
                   && runtimeState.OutputTokens == 3,
                 "外部工人运行状态没有保存握手、真实路由和 Token 证据。");
        }

        var concurrentAuditRoot = Path.Combine(root, "worker-audit-concurrent");
        var concurrentAuditPath = Path.Combine(concurrentAuditRoot, "audit.jsonl");
        var concurrentStatePath = Path.Combine(concurrentAuditRoot, "state.json");
        using (var firstAudit = new ExternalWorkerAuditStore(concurrentAuditPath, concurrentStatePath))
        using (var secondAudit = new ExternalWorkerAuditStore(concurrentAuditPath, concurrentStatePath))
        {
            var firstEntry = new ExternalWorkerAuditEntry(
                DateTimeOffset.UtcNow, "started", "audit-one", "cmm_explorer",
                "cli/test-worker/model-a", null, "gateway-cli:test-worker", "started",
                null, null, null, null, null, null);
            var secondEntry = firstEntry with { RequestId = "audit-two" };
            await Task.WhenAll(
                firstAudit.AppendAsync(firstEntry).AsTask(),
                secondAudit.AppendAsync(secondEntry).AsTask());
            var concurrentAuditLines = await File.ReadAllLinesAsync(concurrentAuditPath);
            Ensure(concurrentAuditLines.Length == 2 && concurrentAuditLines.All(line =>
            {
                try { using var document = JsonDocument.Parse(line); return document.RootElement.ValueKind == JsonValueKind.Object; }
                catch (JsonException) { return false; }
            }), "两个进程视角的审计写入发生覆盖或 JSONL 交错。");

            var auditLockPath = Path.Combine(concurrentAuditRoot, "external-worker-audit.lock");
            using (var heldLock = new FileStream(
                       auditLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var waitingAppend = firstAudit.AppendAsync(firstEntry with { RequestId = "audit-after-lock" }).AsTask();
                await Task.Delay(100);
                Ensure(!waitingAppend.IsCompleted, "审计跨进程锁被占用时仍然写入。");
                heldLock.Dispose();
                await waitingAppend.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        workerExplorer.WorkerKind = SubagentWorkerKind.CodexNative;
        workerExplorer.ModelId = "gpt-5.6-terra";
        var nativeRoleBlocked = false;
        try
        {
            await worker.DelegateAsync(new ExternalWorkerInvocation("cmm_explorer", "must not route"));
        }
        catch (ExternalWorkerException ex) when (ex.Code == "role_not_external")
        {
            nativeRoleBlocked = true;
        }
        Ensure(nativeRoleBlocked && workerBackend.Requests.Count == 2,
            "原生 Codex 角色被错误发送到外部工人。");

        var supervisorSelection = workerDraft.Roles.Single(item => item.RoleId == "cmm_supervisor");
        supervisorSelection.WorkerKind = SubagentWorkerKind.External;
        supervisorSelection.ModelId = "cli/test-worker/model-a";
        var supervisorExternalBlocked = false;
        try
        {
            await worker.DelegateAsync(new ExternalWorkerInvocation("cmm_supervisor", "must not route"));
        }
        catch (ExternalWorkerException ex) when (ex.Code == "role_external_forbidden")
        {
            supervisorExternalBlocked = true;
        }
        Ensure(supervisorExternalBlocked && workerBackend.Requests.Count == 2,
            "总监督角色绕过了禁止外部工人的安全策略。");

        await RunProbeFallbackAsync();
        var probe = new ProviderProbeService();
        var queryBlocked = false;
        try { await probe.ProbeAsync("http://127.0.0.1:9/v1?key=bad", "x"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("?参数", StringComparison.Ordinal))
        {
            queryBlocked = true;
        }
        Ensure(queryBlocked, "带参数的 URL 没有被阻止。");

        var resourceRoot = Path.Combine(root, "resources");
        var serverResource = Path.Combine(resourceRoot, "Server");
        var themeResource = Path.Combine(resourceRoot, "Themes");
        Directory.CreateDirectory(serverResource);
        Directory.CreateDirectory(themeResource);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Resources", "Server", "health-check.ps1"),
            Path.Combine(serverResource, "health-check.ps1"));
        var syntheticSshConfigPath = Path.Combine(serverResource, "synthetic-ssh-config");
        const string syntheticSshConfig = """
                                                Host synthetic-cn
                                                  HostName 127.0.0.1
                                                  User synthetic-test
                                                  IdentityFile C:/nonexistent/cmm-synthetic-test-key
                                                  BatchMode yes
                                                Host synthetic-us
                                                  HostName 127.0.0.1
                                                Host synthetic-jp
                                                  HostName 127.0.0.1
                                                Host synthetic-de
                                                  HostName 127.0.0.1
                                                Host synthetic-sg
                                                  HostName 127.0.0.1
                                                Host synthetic-extra
                                                  HostName 127.0.0.1
                                                """;
        string[] syntheticServerAliases = ["synthetic-cn", "synthetic-us", "synthetic-jp", "synthetic-de", "synthetic-sg"];
        await File.WriteAllTextAsync(
            syntheticSshConfigPath,
            syntheticSshConfig,
            new UTF8Encoding(false));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Resources", "Themes", "codex-bridge.ps1"),
            Path.Combine(themeResource, "codex-bridge.ps1"));
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Resources", "Themes", "dream-skin-manager.ps1"),
            Path.Combine(themeResource, "dream-skin-manager.ps1"));

        var serverHealthScriptText = await File.ReadAllTextAsync(Path.Combine(serverResource, "health-check.ps1"));
        var invokeRemoteStart = serverHealthScriptText.IndexOf("function Invoke-RemoteScript", StringComparison.Ordinal);
        var invokeSshStart = serverHealthScriptText.IndexOf("function Invoke-SshCommand", StringComparison.Ordinal);
        var telemetryStart = serverHealthScriptText.IndexOf("$telemetryScript =", StringComparison.Ordinal);
        Ensure(invokeSshStart >= 0 && invokeRemoteStart > invokeSshStart && telemetryStart > invokeRemoteStart,
            "The bounded SSH and remote health helper functions are missing or reordered.");
        var invokeRemoteBody = serverHealthScriptText[invokeRemoteStart..telemetryStart];
        var encodeIndex = invokeRemoteBody.IndexOf("$encoded =", StringComparison.Ordinal);
        var timedCallIndex = invokeRemoteBody.IndexOf(
            "$result = Invoke-SshCommand -HostAlias $HostAlias -RemoteCommand $remoteCommand",
            StringComparison.Ordinal);
        var metricIndex = invokeRemoteBody.IndexOf("Write-Output \"metric:latency_ms=", StringComparison.Ordinal);
        var resultIndex = invokeRemoteBody.IndexOf("Write-Output $result", StringComparison.Ordinal);
        Ensure(System.Text.RegularExpressions.Regex.Matches(
                   invokeRemoteBody,
                   @"\bInvoke-SshCommand\b",
                   System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count == 1
               && !invokeRemoteBody.Contains("-RemoteCommand 'true'", StringComparison.Ordinal)
               && encodeIndex >= 0
               && encodeIndex < timedCallIndex
               && timedCallIndex < metricIndex
               && metricIndex < resultIndex,
            "Each host health sample must use exactly one timed SSH command and report its result afterward.");
        Ensure(serverHealthScriptText.Contains("function Get-ConfiguredHostAliases", StringComparison.Ordinal)
               && serverHealthScriptText.Contains("[string]$ServerAliasesJson", StringComparison.Ordinal)
               && serverHealthScriptText.Contains("$serverAliases.Count -ne 5", StringComparison.Ordinal)
               && serverHealthScriptText.Contains("$configuredSet.Contains($serverAlias)", StringComparison.Ordinal)
               && serverHealthScriptText.Contains("foreach ($serverAlias in $serverAliases)", StringComparison.Ordinal)
               && !serverHealthScriptText.Contains("CMM_SERVER_", StringComparison.Ordinal),
            "Server sampling must require exactly five explicit safe aliases instead of scanning every SSH Host.");
        Ensure(serverHealthScriptText.Contains("WaitForExit(15000)", StringComparison.Ordinal)
               && serverHealthScriptText.Contains("ConnectTimeout=8", StringComparison.Ordinal),
            "Each server must have a bounded independent SSH timeout.");
        Ensure(serverHealthScriptText.Contains(
                   "curl.exe -sS --retry 1 --retry-all-errors --retry-delay 1 --max-time 12",
                   StringComparison.Ordinal),
            "Public endpoint sampling must retain one bounded retry.");
        Ensure(serverHealthScriptText.Contains("[string]$SshConfigSha256", StringComparison.Ordinal)
               && serverHealthScriptText.Contains(
                   "Get-FileHash -LiteralPath $configPath -Algorithm SHA256",
                   StringComparison.Ordinal)
               && serverHealthScriptText.Contains(
                   "SSH config SHA-256 mismatch; health check was not started.",
                   StringComparison.Ordinal),
            "The standalone health script does not require and verify the SSH config SHA-256.");

        var expectedServerScriptHash = (string?)typeof(DashboardStatusService)
            .GetField("ServerScriptHash", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetRawConstantValue();
        var actualServerScriptHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(serverResource, "health-check.ps1"))));
        var syntheticSshConfigHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(syntheticSshConfigPath)));
        Ensure(expectedServerScriptHash == actualServerScriptHash,
            "The trusted server health script hash is stale.");

        var wrongSshHash = new string(
            syntheticSshConfigHash[0] == '0' ? '1' : '0',
            syntheticSshConfigHash.Length);
        var wrongSshHashDashboard = new DashboardStatusService(
            resourceRoot,
            syntheticSshConfigPath,
            wrongSshHash,
            syntheticServerAliases);
        var wrongSshHashResult = await wrongSshHashDashboard.RunServerHealthAsync();
        Ensure(!wrongSshHashResult.Success
               && wrongSshHashResult.Message.Contains("安全锁", StringComparison.Ordinal),
            "错误的 SSH 配置哈希没有在脚本启动前被安全锁拦截。");

        await File.AppendAllTextAsync(Path.Combine(serverResource, "health-check.ps1"), "\n# tampered");
        var lockedDashboard = new DashboardStatusService(
            resourceRoot,
            syntheticSshConfigPath,
            syntheticSshConfigHash,
            syntheticServerAliases);
        Ensure(lockedDashboard.ServerCheckExists,
            "The synthetic server binding was not accepted before the tamper test.");
        var lockedServer = await lockedDashboard.RunServerHealthAsync();
        Ensure(!lockedServer.Success && lockedServer.Message.Contains("安全锁", StringComparison.Ordinal),
            "服务器脚本被改后没有被安全锁拦住。");
        await File.AppendAllTextAsync(Path.Combine(themeResource, "codex-bridge.ps1"), "\n# tampered");
        var lockedTheme = await lockedDashboard.CheckThemeSafetyAsync();
        Ensure(!lockedTheme.CodexFound && lockedTheme.Message.Contains("被改过", StringComparison.Ordinal),
            "皮肤脚本被改后没有被安全锁拦住。");

        var dreamRoot = Path.Combine(root, "dream-skin");
        var dreamEngineScripts = Path.Combine(dreamRoot, "engine", "scripts");
        var dreamThemes = Path.Combine(dreamRoot, "themes");
        var alphaTheme = Path.Combine(dreamThemes, "alpha-theme");
        var hiddenTheme = Path.Combine(dreamThemes, "hidden-theme");
        var activeTheme = Path.Combine(dreamRoot, "active-theme");
        Directory.CreateDirectory(dreamEngineScripts);
        Directory.CreateDirectory(alphaTheme);
        Directory.CreateDirectory(hiddenTheme);
        Directory.CreateDirectory(activeTheme);
        await File.WriteAllTextAsync(Path.Combine(dreamRoot, "engine", "VERSION"), "9.9.9\n");
        foreach (var scriptName in new[]
                 {
                     "common-windows.ps1", "theme-windows.ps1", "start-dream-skin.ps1",
                     "restore-dream-skin.ps1", "apply-community-theme.ps1"
                 })
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Resources", "CodexDreamSkin", "scripts", scriptName),
                Path.Combine(dreamEngineScripts, scriptName));
        const string alphaJson = """
                                 {
                                   "schemaVersion": 1,
                                   "id": "alpha-theme",
                                   "name": "Alpha 动态主题",
                                   "appearance": "dark",
                                   "tagline": "测试主题",
                                   "effects": { "ambientIncense": { "enabled": true } },
                                   "colors": { "background": "#071014", "accent": "#D69A4B" },
                                   "image": "background.png"
                                 }
                                 """;
        const string hiddenJson = """
                                  { "schemaVersion": 1, "id": "hidden-theme", "name": "隐藏主题", "appearance": "light" }
                                  """;
        await File.WriteAllTextAsync(Path.Combine(alphaTheme, "theme.json"), alphaJson);
        await File.WriteAllTextAsync(Path.Combine(hiddenTheme, "theme.json"), hiddenJson);
        await File.WriteAllTextAsync(Path.Combine(activeTheme, "theme.json"), alphaJson);
        await File.WriteAllTextAsync(
            Path.Combine(dreamRoot, "theme-library.json"),
            "{\"schemaVersion\":1,\"hiddenThemeIds\":[\"hidden-theme\"]}");

        var dreamSkin = new DreamSkinService(dreamRoot, resourceRoot);
        var dreamSnapshot = await dreamSkin.DiscoverAsync();
        Ensure(dreamSnapshot.EngineReady, "完整的测试 Dream Skin 引擎没有被识别。");
        Ensure(dreamSnapshot.Themes.Count == 1, "隐藏主题没有被过滤，或可用主题没有读到。");
        Ensure(dreamSnapshot.Themes[0].IsActive && dreamSnapshot.Themes[0].IsDynamic,
            "当前主题或动态效果标记读取错误。");
        await File.WriteAllTextAsync(Path.Combine(dreamRoot, "paused"), "official appearance\n");
        var pausedDreamSnapshot = await dreamSkin.DiscoverAsync();
        Ensure(pausedDreamSnapshot.IsPaused && pausedDreamSnapshot.StatusTitle == "当前使用官方外观",
            "暂停后的官方外观状态没有被如实显示。");
        Ensure(pausedDreamSnapshot.Themes[0].WasLastSelected
               && !pausedDreamSnapshot.Themes[0].IsActive
               && pausedDreamSnapshot.Themes[0].CanApply
               && pausedDreamSnapshot.Themes[0].ActionText == "恢复此皮肤"
               && pausedDreamSnapshot.Themes[0].StateText == "上次使用",
            "官方外观模式仍把上次主题误判为正在使用，导致无法恢复。");
        File.Delete(Path.Combine(dreamRoot, "paused"));
        var unsafeTheme = await dreamSkin.ApplyInstalledThemeAsync("..\\outside", allowRestart: false);
        Ensure(unsafeTheme.Status == CodexModelManager.Models.DreamSkinOperationStatus.Failed,
            "越界主题标识没有在启动脚本前被阻止。");
        var tamperedEngineScript = Path.Combine(dreamEngineScripts, "start-dream-skin.ps1");
        await File.AppendAllTextAsync(tamperedEngineScript, "\n# tampered");
        var lockedEngineSnapshot = await dreamSkin.DiscoverAsync();
        Ensure(lockedEngineSnapshot.ManagerScriptTrusted && !lockedEngineSnapshot.EngineReady,
            "Dream Skin 引擎脚本被改后没有触发独立安全锁。");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Resources", "CodexDreamSkin", "scripts", "start-dream-skin.ps1"),
            tamperedEngineScript,
            overwrite: true);
        await File.AppendAllTextAsync(Path.Combine(themeResource, "dream-skin-manager.ps1"), "\n# tampered");
        var lockedDreamSnapshot = await dreamSkin.DiscoverAsync();
        Ensure(!lockedDreamSnapshot.ManagerScriptTrusted && !lockedDreamSnapshot.EngineReady,
            "换肤管理脚本被改后没有触发安全锁。");

        Ensure(OpenCodexClient.IsInternalRouteAlias("cmm/main")
               && OpenCodexClient.IsInternalRouteAlias("CMM/plus-1")
               && !OpenCodexClient.IsInternalRouteAlias("gpt-5.6-sol"),
            "总管家内部入口识别不完整。");
        Ensure(OpenCodexClient.ToUserVisibleModelName("cmm/main", "glm-5.2") == "glm-5.2"
               && OpenCodexClient.ToUserVisibleModelName("cmm/plus-1", null) == "当前号池入口"
               && OpenCodexClient.ToUserVisibleModelName("gpt-5.6-sol", "glm-5.2") == "gpt-5.6-sol",
            "总管家内部入口仍可能泄露到用户界面。");

        Console.WriteLine("UNIT_PHASE runtime_truth");
        await AssertRuntimeTruthAsync();
        Console.WriteLine("UNIT_PHASE account_usage_compatibility");
        await AssertAccountUsageLedgerAsync();
        Console.WriteLine("UNIT_PHASE account_usage_hardening");
        await AssertAccountUsageLedgerHardeningAsync();

        Console.WriteLine("UNIT_TESTS_OK settings_corruption secrets_corruption secret_process_isolation secret_namespace_guard pool_defaults native_account_sync official_pool_guard pool_unique_identity catalog_startup_isolation reserved_cli_port_migration malformed_cli_source_isolation cli_endpoint_identity_guard dynamic_subagent_source_discovery subagent_source_manual_authorization subagent_source_identity_revocation gateway_route_identity_guard cli_account_identity_guard gateway_last_hop_pool_guard future_schema_guard legacy_external_api_fail_closed legacy_mcp_fail_closed_guard duplicate_role_guard usage_format quota_windows cliproxy_binary_hash cliproxy_loopback_management cliproxy_same_pool_gate cliproxy_owned_instance_reconciliation codex_snapshot subagent_safe_apply subagent_mcp_apply subagent_mcp_idempotent subagent_mcp_remove subagent_baseline_guard subagent_user_agent_validation subagent_agent_ownership subagent_mcp_ownership subagent_mcp_inline_guard subagent_corrupt_draft_guard subagent_custom_guard subagent_agents_disabled_guard subagent_malformed_toml_guard subagent_invalid_utf8_guard subagent_codex_validator_candidate subagent_codex_validator_reject_guard subagent_codex_validator_unavailable_guard external_worker_route_lock external_worker_source_grant_guard external_worker_role_enum external_worker_cross_process_gate external_worker_cancel external_worker_audit_redaction external_worker_audit_fail_closed external_worker_audit_cross_process external_worker_runtime_state external_worker_mcp_protocol external_worker_model_injection_guard probe_fallback url_guard server_health_single_ssh server_health_config_hash_required server_health_us_proxyjump public_endpoint_retry resource_hash_current dynamic_account_single dynamic_account_states dynamic_account_unique dynamic_account_empty dynamic_account_invalid resource_hash_lock dream_skin_catalog dream_skin_official_restore dream_skin_path_guard dream_skin_hash_lock runtime_truth_missing runtime_truth_stale runtime_truth_failover runtime_truth_all_failed runtime_truth_answering runtime_truth_account_identity runtime_truth_preference_identity runtime_truth_pre_switch runtime_truth_route_evidence runtime_truth_log_order runtime_truth_explicit_selection runtime_truth_unattributed_attempts runtime_truth_failure_classification runtime_truth_redaction runtime_truth_event_isolation runtime_truth_wait_cancel runtime_truth_read_cancel runtime_truth_concurrency account_identity_acl_reload_guard account_usage_attempt_idempotency account_usage_multi_attempt account_usage_unattributed account_usage_outcomes account_usage_token_truth account_usage_source_recovery account_usage_bad_line account_usage_concurrency account_usage_payload_collision account_quota_separation account_quota_provenance account_quota_stale account_usage_redaction account_usage_runtime_ingestion");
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static async Task AssertRuntimeTruthAsync()
{
    var now = new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.FromHours(8));
    var preference = new RuntimeTruthPreferenceSource(
        "test-pool",
        "测试号池",
        "account-b",
        RuntimeAccountIdentitySource.ExplicitAccountId,
        "首选账号",
        "gpt-5.6-sol",
        OpenCodexClient.SwitchAlias,
        now.AddMinutes(-30),
        "test");
    var route = new ActiveRoute(
        "same-provider",
        "gpt-5.6-sol",
        new[]
        {
            (Provider: "same-provider", Model: "gpt-5.6-sol"),
            (Provider: "same-provider", Model: "gpt-5.6-terra")
        });
    var task = new CodexDesktopState(true, false, OpenCodexClient.SwitchAlias, "测试任务可读");

    var missingService = new RuntimeTruthService(new TestRuntimeTruthSource(), () => now, TimeSpan.FromMinutes(15));
    var missing = await missingService.ReadAsync();
    Ensure(missing.Consistency.State == RuntimeTruthState.Unknown
           && missing.Evidence.Count(evidence => !evidence.Available) >= 3,
        "运行事实缺来源时没有明确返回 Unknown 和来源证据。");

    const string failoverJson = """
                                    [
                                      {
                                        "requestId": "request-failover",
                                        "requestedModel": "cmm/main",
                                        "status": 200,
                                        "durationMs": 321,
                                        "timestamp": "2026-08-01T12:59:00+08:00",
                                        "attempts": [
                                          {
                                            "provider": "same-provider",
                                            "accountId": "account-a",
                                            "model": "gpt-5.6-sol",
                                            "status": 502,
                                            "durationMs": 100,
                                            "upstreamError": "servers overloaded"
                                          },
                                          {
                                            "provider": "same-provider",
                                            "accountId": "account-b",
                                            "resolvedModel": "gpt-5.6-terra",
                                            "status": 200,
                                            "durationMs": 221
                                          }
                                        ]
                                      }
                                    ]
                                    """;
    var failover = OpenCodexClient.ParseLatestRouteExecution(failoverJson)
                   ?? throw new InvalidOperationException("502→200 尝试链没有解析。");
    Ensure(failover.Attempts.Count == 2
           && failover.Attempts[0].FailoverReason == RuntimeFailoverReason.Capacity
           && failover.Attempts[1].Selected
           && failover.Outcome == RuntimeExecutionOutcome.Succeeded,
        "502→200 尝试链、故障原因或最终成功选择错误。");
    Ensure(failover.Attempts[0].ProviderId == failover.Attempts[1].ProviderId
           && failover.Attempts[0].AccountId == "account-a"
           && failover.Attempts[1].AccountId == "account-b",
        "同 provider 多账号在运行事实中被错误合并。");

    const string mixedAscendingJson = """
                                          [
                                            { "requestId": "old-cmm", "requestedModel": "cmm/main", "status": 200, "timestamp": "2026-08-01T12:10:00+08:00", "provider": "same-provider", "accountId": "account-a", "model": "gpt-5.6-sol" },
                                            { "requestId": "latest-native", "requestedModel": "gpt-5.6-terra", "status": 200, "timestamp": "2026-08-01T12:58:00+08:00", "provider": "same-provider", "accountId": "account-b", "model": "gpt-5.6-terra" }
                                          ]
                                          """;
    const string mixedDescendingJson = """
                                           [
                                             { "requestId": "latest-native", "requestedModel": "gpt-5.6-terra", "status": 200, "timestamp": "2026-08-01T12:58:00+08:00", "provider": "same-provider", "accountId": "account-b", "model": "gpt-5.6-terra" },
                                             { "requestId": "old-cmm", "requestedModel": "cmm/main", "status": 200, "timestamp": "2026-08-01T12:10:00+08:00", "provider": "same-provider", "accountId": "account-a", "model": "gpt-5.6-sol" }
                                           ]
                                           """;
    var mixedAscending = OpenCodexClient.ParseLatestRouteExecution(mixedAscendingJson);
    var mixedDescending = OpenCodexClient.ParseLatestRouteExecution(mixedDescendingJson);
    Ensure(mixedAscending?.RequestId == "latest-native"
           && mixedAscending.SourceArrayIndex == 1
           && mixedDescending?.RequestId == "latest-native"
           && mixedDescending.SourceArrayIndex == 0
           && mixedAscending.SelectionBasis == RuntimeLogSelectionBasis.Timestamp
           && mixedDescending.SelectionBasis == RuntimeLogSelectionBasis.Timestamp,
        "混合原生模型/总管家入口日志没有按时间戳选择真正最新对象，或依赖了数组正倒序。");

    const string noTimestampJson = """
                                       [
                                         { "requestId": "array-first", "requestedModel": "cmm/main", "status": 200, "provider": "same-provider", "model": "gpt-5.6-sol" },
                                         { "requestId": "array-last", "requestedModel": "gpt-5.6-terra", "status": 200, "provider": "same-provider", "model": "gpt-5.6-terra" }
                                       ]
                                       """;
    var noTimestamp = OpenCodexClient.ParseLatestRouteExecution(noTimestampJson);
    Ensure(noTimestamp?.RequestId == "array-last"
           && noTimestamp.SelectionBasis == RuntimeLogSelectionBasis.ArrayLastFallback
           && noTimestamp.SourceArrayIndex == 1,
        "日志全部缺时间戳时没有使用明确的数组末项 fallback。");

    const string partiallyTimestampedJson = """
                                                  [
                                                    { "requestId": "timestamped-first", "requestedModel": "cmm/main", "status": 200, "timestamp": "2026-08-01T12:58:00+08:00", "provider": "same-provider", "model": "gpt-5.6-sol" },
                                                    { "requestId": "untimestamped-last", "requestedModel": "gpt-5.6-terra", "status": 200, "provider": "same-provider", "model": "gpt-5.6-terra" }
                                                  ]
                                                  """;
    var partiallyTimestamped = OpenCodexClient.ParseLatestRouteExecution(partiallyTimestampedJson);
    Ensure(partiallyTimestamped?.RequestId == "untimestamped-last"
           && partiallyTimestamped.SelectionBasis == RuntimeLogSelectionBasis.ArrayLastFallback
           && partiallyTimestamped.SourceArrayIndex == 1,
        "部分日志缺时间戳时没有明确降级为数组末项 fallback。");

    const string explicitWinnerJson = """
                                         [
                                           {
                                             "requestId": "explicit-winner",
                                             "requestedModel": "cmm/main",
                                             "status": 200,
                                             "timestamp": "2026-08-01T12:59:00+08:00",
                                             "attempts": [
                                               { "provider": "same-provider", "accountId": "account-b", "model": "gpt-5.6-sol", "status": 200, "winner": true },
                                               { "provider": "same-provider", "accountId": "account-c", "model": "gpt-5.6-terra", "status": 200 }
                                             ]
                                           }
                                         ]
                                         """;
    var explicitWinner = OpenCodexClient.ParseLatestRouteExecution(explicitWinnerJson);
    Ensure(explicitWinner?.Attempts[0].Selected == true
           && explicitWinner.Attempts[1].Selected == false
           && explicitWinner.ActualAttempt?.AccountId == "account-b",
        "显式 winner 没有优先于多个成功 attempt 的位置启发式。");

    const string explicitFlagPriorityJson = """
                                               [
                                                 {
                                                   "requestId": "explicit-flag-priority",
                                                   "requestedModel": "cmm/main",
                                                   "status": 200,
                                                   "timestamp": "2026-08-01T12:59:00+08:00",
                                                   "attempts": [
                                                     { "provider": "same-provider", "accountId": "account-used", "model": "gpt-5.6-sol", "status": 200, "used": true },
                                                     { "provider": "same-provider", "accountId": "account-winner", "model": "gpt-5.6-terra", "status": 200, "winner": true },
                                                     { "provider": "same-provider", "accountId": "account-selected", "model": "gpt-5.6-sol", "status": 200, "selected": true }
                                                   ]
                                                 }
                                               ]
                                               """;
    var explicitFlagPriority = OpenCodexClient.ParseLatestRouteExecution(explicitFlagPriorityJson);
    Ensure(explicitFlagPriority?.ActualAttempt?.AccountId == "account-selected"
           && explicitFlagPriority.Attempts.Count(attempt => attempt.Selected) == 1,
        "selected/winner/used 显式选择标志没有按确定的优先级覆盖位置启发式。");

    const string multipleSuccessJson = """
                                          [
                                            {
                                              "requestId": "multiple-success",
                                              "requestedModel": "cmm/main",
                                              "status": 200,
                                              "timestamp": "2026-08-01T12:59:00+08:00",
                                              "attempts": [
                                                { "provider": "same-provider", "accountId": "account-a", "model": "gpt-5.6-sol", "status": 200 },
                                                { "provider": "same-provider", "accountId": "account-b", "model": "gpt-5.6-terra", "status": 200 }
                                              ]
                                            }
                                          ]
                                          """;
    var multipleSuccess = OpenCodexClient.ParseLatestRouteExecution(multipleSuccessJson);
    Ensure(multipleSuccess?.Attempts[1].Selected == true
           && multipleSuccess.ActualAttempt?.AccountId == "account-b",
        "多个成功 attempt 且无显式 winner 时没有使用最后成功项。");

    const string unattributedAttemptsJson = """
                                              [
                                                {
                                                  "requestId": "unattributed",
                                                  "requestedModel": "cmm/main",
                                                  "accountId": "top-account-must-not-spread",
                                                  "status": 200,
                                                  "timestamp": "2026-08-01T12:59:00+08:00",
                                                  "attempts": [
                                                    { "provider": "same-provider", "model": "gpt-5.6-sol", "status": 502 },
                                                    { "provider": "same-provider", "model": "gpt-5.6-terra", "status": 200 }
                                                  ]
                                                }
                                              ]
                                              """;
    var unattributedAttempts = OpenCodexClient.ParseLatestRouteExecution(unattributedAttemptsJson);
    Ensure(unattributedAttempts is not null
           && unattributedAttempts.Attempts.All(attempt => attempt.AccountId is null
                                                         && attempt.AccountIdentitySource == RuntimeAccountIdentitySource.Unknown),
        "顶层 accountId 被错误回填到多个 attempt，未归属边界失效。");

    const string singleAttemptTopAccountJson = """
                                                   [
                                                     {
                                                       "requestId": "single-attempt-top-account",
                                                       "requestedModel": "cmm/main",
                                                       "accountId": "single-account",
                                                       "status": 200,
                                                       "timestamp": "2026-08-01T12:59:00+08:00",
                                                       "attempts": [
                                                         { "provider": "same-provider", "model": "gpt-5.6-sol", "status": 200 }
                                                       ]
                                                     }
                                                   ]
                                                   """;
    var singleAttemptTopAccount = OpenCodexClient.ParseLatestRouteExecution(singleAttemptTopAccountJson);
    Ensure(singleAttemptTopAccount?.ActualAttempt?.AccountId == "single-account"
           && singleAttemptTopAccount.ActualAttempt.AccountIdentitySource == RuntimeAccountIdentitySource.ExplicitAccountId,
        "单 attempt 场景没有使用顶层 accountId 作兼容回填。");

    const string classificationJson = """
                                        [
                                          {
                                            "requestId": "classification",
                                            "requestedModel": "cmm/main",
                                            "status": 400,
                                            "timestamp": "2026-08-01T12:59:00+08:00",
                                            "attempts": [
                                              { "provider": "same-provider", "model": "gpt-5.6-sol", "status": 401, "errorCode": "unauthorized" },
                                              { "provider": "same-provider", "model": "gpt-5.6-terra", "status": 403, "errorCode": "authorization_denied" },
                                              { "provider": "same-provider", "model": "gpt-5.6-sol", "status": 403, "errorMessage": "auth proxy rejected authorization" },
                                              { "provider": "same-provider", "model": "gpt-5.6-terra", "errorCode": "authentication_failed" },
                                              { "provider": "same-provider", "model": "gpt-5.6-sol", "status": 429, "errorCode": "rate_limit" },
                                              { "provider": "same-provider", "model": "gpt-5.6-terra", "status": 400, "errorCode": "context_length_exceeded" }
                                            ]
                                          }
                                        ]
                                        """;
    var classification = OpenCodexClient.ParseLatestRouteExecution(classificationJson);
    Ensure(classification?.Attempts[0].FailoverReason == RuntimeFailoverReason.Authentication
           && classification.Attempts[1].FailoverReason == RuntimeFailoverReason.Permission
           && classification.Attempts[2].FailoverReason == RuntimeFailoverReason.Permission
           && classification.Attempts[3].FailoverReason == RuntimeFailoverReason.Authentication
           && classification.Attempts[4].FailoverReason == RuntimeFailoverReason.RateLimit
           && classification.Attempts[5].FailoverReason == RuntimeFailoverReason.ContextWindow,
        "HTTP 状态优先级、收窄认证兜底、429 或 context_length_exceeded 的失败原因分类错误。");

    var freshSource = new TestRuntimeTruthSource
    {
        Preference = preference,
        CodexDefaultModel = OpenCodexClient.SwitchAlias,
        Task = task,
        Route = route,
        Execution = failover
    };
    var freshService = new RuntimeTruthService(freshSource, () => now, TimeSpan.FromMinutes(15));
    var fresh = await freshService.ReadAsync();
    Ensure(fresh.Consistency.State == RuntimeTruthState.Consistent
           && fresh.LastExecution?.ActualAttempt?.AccountId == "account-b",
        "成功故障转移没有形成一致的统一事实快照。");
    var failoverAttemptSummary = RuntimeTruthDisplay.FormatAttempts(fresh.LastExecution);
    Ensure(failoverAttemptSummary.IndexOf("#1", StringComparison.Ordinal) >= 0
           && failoverAttemptSummary.IndexOf("#2", StringComparison.Ordinal)
              > failoverAttemptSummary.IndexOf("#1", StringComparison.Ordinal)
           && failoverAttemptSummary.Contains("account-a", StringComparison.Ordinal)
           && failoverAttemptSummary.Contains("account-b", StringComparison.Ordinal)
           && failoverAttemptSummary.Contains("HTTP 502", StringComparison.Ordinal)
           && failoverAttemptSummary.Contains("HTTP 200", StringComparison.Ordinal)
           && failoverAttemptSummary.Contains("servers overloaded", StringComparison.Ordinal),
        "事实卡 attempt 摘要没有按顺序展示账号、HTTP 与脱敏后的失败原因。");

    var accountMismatch = await new RuntimeTruthService(
        new TestRuntimeTruthSource
        {
            Preference = preference with { PreferredAccountId = "account-a" },
            CodexDefaultModel = OpenCodexClient.SwitchAlias,
            Task = task,
            Route = route,
            Execution = failover
        },
        () => now).ReadAsync();
    Ensure(accountMismatch.Consistency.State == RuntimeTruthState.Diverged
           && accountMismatch.Consistency.Mismatches.Any(item => item.Contains("实际账号", StringComparison.Ordinal)),
        "可比较的首选账号与实际账号不一致时仍被放行为一致。");

    var preSwitch = await new RuntimeTruthService(
        new TestRuntimeTruthSource
        {
            Preference = preference with { SwitchedAt = now },
            CodexDefaultModel = OpenCodexClient.SwitchAlias,
            Task = task,
            Route = route,
            Execution = failover
        },
        () => now).ReadAsync();
    Ensure(preSwitch.Consistency.State == RuntimeTruthState.Stale
           && preSwitch.LastExecutionPredatesPreference
           && preSwitch.Consistency.Message.Contains("切换之前", StringComparison.Ordinal),
        "首选切换前的实际执行仍被用于证明当前首选一致。");

    var missingRoute = await new RuntimeTruthService(
        new TestRuntimeTruthSource
        {
            Preference = preference,
            CodexDefaultModel = OpenCodexClient.SwitchAlias,
            Task = task,
            Route = null,
            Execution = failover
        },
        () => now).ReadAsync();
    Ensure(missingRoute.Consistency.State == RuntimeTruthState.Unknown
           && missingRoute.Consistency.Message.Contains("internal route", StringComparison.Ordinal),
        "internal route 缺少配置路由证据时仍被放行为一致。");

    var staleSource = new TestRuntimeTruthSource
    {
        Preference = preference,
        CodexDefaultModel = OpenCodexClient.SwitchAlias,
        Task = task,
        Route = route,
        Execution = failover with { Timestamp = now.AddMinutes(-16) }
    };
    var stale = await new RuntimeTruthService(staleSource, () => now, TimeSpan.FromMinutes(15)).ReadAsync();
    Ensure(stale.LastExecutionIsStale && stale.Consistency.State == RuntimeTruthState.Stale,
        "超过陈旧阈值的日志仍被显示为新鲜事实。");

    const string failedJson = """
                                  [
                                    {
                                      "requestId": "request-failed",
                                      "requestedModel": "cmm/main",
                                      "status": 503,
                                      "timestamp": "2026-08-01T12:59:30+08:00",
                                      "attempts": [
                                        { "provider": "same-provider", "accountId": "account-a", "model": "gpt-5.6-sol", "status": 502 },
                                        { "provider": "same-provider", "accountId": "account-b", "model": "gpt-5.6-terra", "status": 503 }
                                      ]
                                    }
                                  ]
                                  """;
    var failedExecution = OpenCodexClient.ParseLatestRouteExecution(failedJson)
                          ?? throw new InvalidOperationException("全失败尝试链没有解析。");
    Ensure(failedExecution.Outcome == RuntimeExecutionOutcome.Failed
           && failedExecution.Attempts.Count == 2
           && failedExecution.Attempts[1].Selected,
        "全失败请求没有保留完整尝试链并选择最后一次尝试。");
    var failedSource = freshSource.Clone(execution: failedExecution);
    var failed = await new RuntimeTruthService(failedSource, () => now).ReadAsync();
    Ensure(failed.Consistency.State == RuntimeTruthState.Failed,
        "最近实际执行全失败时没有进入 Failed 状态。");

    var answeringSource = freshSource.Clone(task: task with { IsTurnRunning = true });
    var answering = await new RuntimeTruthService(answeringSource, () => now).ReadAsync();
    Ensure(answering.Consistency.State == RuntimeTruthState.Pending
           && answering.Task.IsAnswering,
        "Codex 回答中没有进入只读 Pending 状态。");

    const string opaqueSecret = "opaque-value-987654";
    const string querySecret = "query-value-987654";
    // Deliberate fake JWT-shaped fixture used only to verify redaction; it is not a credential.
    const string jwtHeaderFixture = "eyJhbGciOiJIUzI1NiJ9";
    const string jwtPayloadFixture = "eyJzdWIiOiIxMjM0NTY3ODkwIn0";
    const string jwtSignatureFixture = "c2lnbmF0dXJl";
    var jwtSecret = $"{jwtHeaderFixture}.{jwtPayloadFixture}.{jwtSignatureFixture}";
    const string basicSecret = "QWxhZGRpbjpvcGVuIHNlc2FtZQ==";
    const string bearerSecret = "bearer-value-987654";
    const string skSecret = "sk-test-supersecret";
    var rawExpectedModel = $"custom-{jwtSecret}";
    var rawExecution = new RuntimeRouteExecution(
        $"{{\"token\":\"{opaqueSecret}\"}}",
        rawExpectedModel,
        200,
        10,
        now.AddMinutes(-1),
        RuntimeExecutionOutcome.Succeeded,
        $"error?token={querySecret}",
        jwtSecret,
        RuntimeLogSelectionBasis.Timestamp,
        7,
        new[]
        {
            new RuntimeRouteAttempt(
                1,
                $"provider?token={querySecret}",
                $"{{\"token\":\"{opaqueSecret}\"}}",
                $"Basic {basicSecret}",
                $"Bearer {bearerSecret}",
                RuntimeAccountIdentitySource.ExplicitAccountId,
                rawExpectedModel,
                200,
                10,
                skSecret,
                $"{{\"authorization\":\"Bearer {bearerSecret}\"}}",
                RuntimeFailoverReason.None,
                true)
        });
    rawExecution = rawExecution with
    {
        RequestIdentityMaterial = $"Bearer {bearerSecret}",
        Attempts = rawExecution.Attempts.Select(attempt => attempt with
        {
            AccountIdentityMaterial = $"Basic {basicSecret}"
        }).ToArray()
    };
    var zeroTrustSource = new TestRuntimeTruthSource
    {
        Preference = new RuntimeTruthPreferenceSource(
            $"pool?token={querySecret}",
            $"{{\"token\":\"{opaqueSecret}\"}}",
            $"Basic {basicSecret}",
            RuntimeAccountIdentitySource.ExplicitAccountId,
            $"Bearer {bearerSecret}",
            $"model?api_key={skSecret}",
            rawExpectedModel,
            now.AddMinutes(-30),
            $"{{\"refresh_token\":\"{opaqueSecret}\"}}"),
        CodexDefaultModel = rawExpectedModel,
        Task = new CodexDesktopState(
            true,
            false,
            rawExpectedModel,
            $"Authorization: Basic {basicSecret} Bearer {bearerSecret}"),
        Route = new ActiveRoute(
            $"provider?token={querySecret}",
            rawExpectedModel,
            new[] { (Provider: $"provider?token={querySecret}", Model: rawExpectedModel) }),
        Execution = rawExecution
    };
    var secretSnapshot = await new RuntimeTruthService(zeroTrustSource, () => now).ReadAsync();
    var serializedSnapshot = JsonSerializer.Serialize(secretSnapshot);
    using var serializedDocument = JsonDocument.Parse(serializedSnapshot);
    var secretPaths = new List<string>();
    var sensitiveValues = new[] { opaqueSecret, querySecret, jwtSecret, basicSecret, bearerSecret, skSecret };
    for (var secretIndex = 0; secretIndex < sensitiveValues.Length; secretIndex++)
    {
        var paths = new List<string>();
        CollectMatchingStringPaths(serializedDocument.RootElement, "$", sensitiveValues[secretIndex], paths);
        secretPaths.AddRange(paths.Select(path => $"secret[{secretIndex}]:{path}"));
    }
    var deepFieldsMarked = secretSnapshot.Preferred.Verification.Contains("已隐藏", StringComparison.Ordinal)
                           && secretSnapshot.Task.ExpectedModelLabel.Contains("已隐藏", StringComparison.Ordinal)
                           && secretSnapshot.ConfiguredRoute?.Provider.Contains("已隐藏", StringComparison.Ordinal) == true
                           && secretSnapshot.LastExecution?.RequestId?.Contains("已隐藏", StringComparison.Ordinal) == true
                           && secretSnapshot.LastExecution.Attempts[0].ProviderDisplayName.Contains("已隐藏", StringComparison.Ordinal)
                           && secretSnapshot.Evidence.Any(item => item.Message.Contains("已隐藏", StringComparison.Ordinal));
    var sanitizedAttemptSummary = RuntimeTruthDisplay.FormatAttempts(secretSnapshot.LastExecution);
    var summarySecretAbsent = sensitiveValues.All(secret =>
        !sanitizedAttemptSummary.Contains(secret, StringComparison.Ordinal));
    var rawIdentityCleared = secretSnapshot.LastExecution?.RequestIdentityMaterial is null
                             && secretSnapshot.LastExecution?.Attempts.All(attempt => attempt.AccountIdentityMaterial is null) == true;
    Ensure(secretPaths.Count == 0 && deepFieldsMarked && summarySecretAbsent && rawIdentityCleared,
        $"统一运行事实深层脱敏不完整：secretAbsent={secretPaths.Count == 0} paths={string.Join(",", secretPaths)} deepFieldsMarked={deepFieldsMarked} summarySecretAbsent={summarySecretAbsent}。");

    var concurrentSource = freshSource.Clone(execution: failover);
    concurrentSource.ExecutionDelay = TimeSpan.FromMilliseconds(20);
    var concurrentService = new RuntimeTruthService(concurrentSource, () => now);
    var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => concurrentService.ReadAsync()));
    Ensure(concurrentSource.MaxConcurrentExecutionReads == 1
           && snapshots.Select(snapshot => snapshot.Revision).Distinct().Count() == snapshots.Length
           && concurrentService.LastSnapshot?.Revision == snapshots.Max(snapshot => snapshot.Revision),
        "并发刷新没有被串行保护，或发布了重复/倒退的快照修订号。");

    var eventSource = freshSource.Clone(execution: failover);
    var eventService = new RuntimeTruthService(eventSource, () => now);
    var survivingSubscriberCalls = 0;
    eventService.SnapshotChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
    eventService.SnapshotChanged += (_, _) => Interlocked.Increment(ref survivingSubscriberCalls);
    var eventSnapshot = await eventService.ReadAsync();
    Ensure(eventSnapshot.Revision == 1 && survivingSubscriberCalls == 1,
        "事件订阅者异常向 ReadAsync 调用方传播，或阻断了其他订阅者。");

    var waitingCancelSource = freshSource.Clone(execution: failover);
    waitingCancelSource.ExecutionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    waitingCancelSource.ExecutionRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var waitingCancelService = new RuntimeTruthService(waitingCancelSource, () => now);
    var occupyingRead = waitingCancelService.ReadAsync();
    await waitingCancelSource.ExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    using var waitingCancellation = new CancellationTokenSource();
    waitingCancellation.Cancel();
    var waitingWasCanceled = false;
    try
    {
        await waitingCancelService.ReadAsync(waitingCancellation.Token);
    }
    catch (OperationCanceledException)
    {
        waitingWasCanceled = true;
    }
    finally
    {
        waitingCancelSource.ExecutionRelease.TrySetResult(true);
    }
    await occupyingRead;
    Ensure(waitingWasCanceled && waitingCancelSource.MaxConcurrentExecutionReads == 1,
        "等待刷新闸门期间的取消没有及时生效，或破坏了串行读取边界。");

    var readingCancelSource = freshSource.Clone(execution: failover);
    readingCancelSource.ExecutionDelay = TimeSpan.FromSeconds(5);
    var readingCancelService = new RuntimeTruthService(readingCancelSource, () => now);
    using var readingCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    var readingWasCanceled = false;
    try
    {
        await readingCancelService.ReadAsync(readingCancellation.Token);
    }
    catch (OperationCanceledException)
    {
        readingWasCanceled = true;
    }
    readingCancelSource.ExecutionDelay = TimeSpan.Zero;
    var recoveredAfterCancellation = await readingCancelService.ReadAsync();
    Ensure(readingWasCanceled
           && recoveredAfterCancellation.Revision == 1
           && readingCancelSource.MaxConcurrentExecutionReads == 1,
        "来源读取期间的取消没有传播，或取消后刷新闸门/修订号没有恢复。");
}

static async Task AssertAccountUsageLedgerAsync()
{
    var root = CreateOwnedTestRoot("cmm-account-usage-ledger");
    var now = new DateTimeOffset(2026, 8, 1, 14, 30, 0, TimeSpan.FromHours(8));
    try
    {
        const string attemptMatrixJson = """
                                                 [
                                                   {
                                                     "requestId": 12345678901234,
                                                     "requestedModel": "cmm/main",
                                                     "accountId": "current-account-must-not-spread",
                                                     "status": 200,
                                                     "timestamp": "2026-08-01T14:29:00+08:00",
                                                     "attempts": [
                                                       {
                                                         "provider": "provider-a",
                                                         "accountId": "account-a",
                                                         "model": "gpt-5.6-sol",
                                                         "status": 502,
                                                         "errorCode": "server_overloaded",
                                                         "usage": {
                                                           "inputTokens": 10,
                                                           "cachedInputTokens": 4,
                                                           "cacheReadInputTokens": 3,
                                                           "cacheCreationInputTokens": 1,
                                                           "outputTokens": 3,
                                                           "reasoningOutputTokens": 2,
                                                           "totalTokens": 999
                                                         }
                                                       },
                                                       {
                                                         "provider": "provider-a",
                                                         "accountId": "account-b",
                                                         "model": "gpt-5.6-terra",
                                                         "status": 200,
                                                         "usage": {
                                                           "inputTokens": 20,
                                                           "cachedInputTokens": 8,
                                                           "outputTokens": 5,
                                                           "reasoningOutputTokens": 2
                                                         }
                                                       },
                                                       {
                                                         "provider": "provider-a",
                                                         "model": "gpt-5.6-sol",
                                                         "status": 499,
                                                         "errorCode": "operation_canceled",
                                                         "usage": { "cachedInputTokens": 7 }
                                                       },
                                                       {
                                                         "provider": "provider-b",
                                                         "accountId": "account-c",
                                                         "model": "glm-5.2",
                                                         "status": 500,
                                                         "errorCode": "upstream_error"
                                                       },
                                                       {
                                                         "provider": "provider-b",
                                                         "model": "glm-5.2",
                                                         "status": 499,
                                                         "errorCode": "canceled"
                                                       }
                                                     ]
                                                   }
                                                 ]
                                                 """;
        var execution = OpenCodexClient.ParseLatestRouteExecution(attemptMatrixJson)
                        ?? throw new InvalidOperationException("逐 attempt 测试日志未解析。");
        Ensure(execution.RequestId == "12345678901234"
               && execution.Attempts.Count == 5,
            "14 位数字 requestId 或同 request 多 attempt 没有原样保留。");
        var mismatchUsage = execution.Attempts[0].TokenUsage;
        var derivedUsage = execution.Attempts[1].TokenUsage;
        Ensure(mismatchUsage?.TotalTokens == 999
               && mismatchUsage.TotalSource == TokenTotalSource.Upstream
               && mismatchUsage.TotalValidation == TokenTotalValidationState.Mismatch
               && mismatchUsage.CacheReadInputTokens == 3
               && mismatchUsage.CacheCreationInputTokens == 1,
            "upstream total、cache read/create 或不一致校验没有保留真实提供值。");
        Ensure(derivedUsage?.TotalTokens == 25
               && derivedUsage.TotalSource == TokenTotalSource.DerivedInputOutput
               && derivedUsage.ReasoningTokens == 2,
            "total 缺失时没有只从 input+output 派生，或 reasoning 字段丢失。");
        Ensure(execution.Attempts[2].Outcome == RuntimeExecutionOutcome.Cancelled
               && execution.Attempts[2].TokenUsage?.TotalTokens is null
               && execution.Attempts[3].Outcome == RuntimeExecutionOutcome.Failed
               && execution.Attempts[3].TokenUsage is null
               && execution.Attempts[4].Outcome == RuntimeExecutionOutcome.Cancelled
               && execution.Attempts[4].TokenUsage is null,
            "成功/失败/取消且有/无 usage 的事实边界错误。");

        var ledgerRoot = Path.Combine(root, "direct");
        var ledger = new AccountUsageLedgerService(ledgerRoot, Path.Combine(root, "unused-source.jsonl"), () => now);
        var firstIngest = await ledger.IngestExecutionsAsync(new[] { execution });
        if (OperatingSystem.IsWindows())
        {
            var identityKey = new FileInfo(Path.Combine(ledgerRoot, "account-ledger-identity.key"));
            var keySecurity = identityKey.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var currentSid = WindowsIdentity.GetCurrent().User
                             ?? throw new InvalidOperationException("无法解析测试进程 Windows SID。");
            var tokenOwnerSid = WindowsIdentity.GetCurrent().Owner ?? currentSid;
            var ownerSid = keySecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var keyRules = keySecurity.GetAccessRules(includeExplicit: true, includeInherited: true,
                    targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .ToArray();
            Ensure(ownerSid is not null
                   && (currentSid.Equals(ownerSid) || tokenOwnerSid.Equals(ownerSid))
                   && keySecurity.AreAccessRulesProtected
                   && keyRules.Length == 1
                   && keyRules[0].AccessControlType == AccessControlType.Allow
                   && keyRules[0].IdentityReference is SecurityIdentifier ruleSid
                   && currentSid.Equals(ruleSid)
                   && (keyRules[0].FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl,
                "身份密钥 ACL 没有收敛到当前 Windows 用户。");

            var tamperedSecurity = identityKey.GetAccessControl(AccessControlSections.Access);
            tamperedSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadData,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            identityKey.SetAccessControl(tamperedSecurity);
            using var compromisedLedger = new AccountUsageLedgerService(
                ledgerRoot,
                Path.Combine(root, "unused-source.jsonl"),
                () => now);
            var compromisedAclRejected = false;
            try
            {
                await compromisedLedger.IngestExecutionsAsync(new[] { execution });
            }
            catch (AccountLedgerIdentityKeyUnavailableException)
            {
                compromisedAclRejected = true;
            }
            finally
            {
                var restoredSecurity = new FileSecurity();
                restoredSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                restoredSecurity.AddAccessRule(new FileSystemAccessRule(
                    currentSid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                identityKey.SetAccessControl(restoredSecurity);
            }

            var compromisedAclLatched = false;
            try
            {
                await compromisedLedger.IngestExecutionsAsync(new[] { execution });
            }
            catch (AccountLedgerIdentityKeyUnavailableException)
            {
                compromisedAclLatched = true;
            }
            Ensure(compromisedAclRejected && compromisedAclLatched,
                "已有身份密钥 ACL 被篡改后没有 fail-closed 并锁存 Unavailable。");
        }
        var replay = await ledger.IngestExecutionsAsync(new[] { execution });
        var snapshot = await ledger.ReadAsync();
        Ensure(firstIngest.AppendedCount == 5
               && replay.AppendedCount == 0
               && replay.DuplicateCount == 5
               && snapshot.StoredAttemptCount == 5,
            "同一 attempt 重放没有按稳定事件键幂等。");
        var attributedAccounts = snapshot.Accounts.Where(item => item.AccountAttributed).ToArray();
        var serializedLedgerProjection = JsonSerializer.Serialize(snapshot);
        var safeLabels = attributedAccounts.All(item => item.AccountId.StartsWith("账号 ", StringComparison.Ordinal));
        var distinctLabels = attributedAccounts.Select(item => item.AccountId).Distinct(StringComparer.Ordinal).Count();
        var unattributedAttempts = snapshot.Accounts.Where(item => !item.AccountAttributed).Sum(item => item.AttemptCount);
        var rawIdsAbsent = new[] { "account-a", "account-b", "account-c", "current-account-must-not-spread" }
            .All(raw => !serializedLedgerProjection.Contains(raw, StringComparison.Ordinal));
        Ensure(attributedAccounts.Length == 3 && safeLabels && distinctLabels == 3
               && unattributedAttempts == 2 && rawIdsAbsent,
            $"多账号/未归属/隐私分离失败：attributed={attributedAccounts.Length} safeLabels={safeLabels} distinct={distinctLabels} unattributedAttempts={unattributedAttempts} rawAbsent={rawIdsAbsent}。");
        var accountA = snapshot.Accounts.Single(item => item.Total.Value == 999);
        var accountB = snapshot.Accounts.Single(item => item.Total.Value == 25);
        Ensure(accountA.Total.Value == 999
               && accountA.FailedCount == 1
               && accountB.Total.Value == 25
               && accountB.Input.Value == 20
               && accountB.Output.Value == 5,
            "失败但有 usage 未入账，或 cached/reasoning 被再次加进 total。");

        var collisionUsage = execution.Attempts[0].TokenUsage! with { TotalTokens = 998 };
        var collisionExecution = execution with
        {
            Attempts = new[]
            {
                execution.Attempts[0] with
                {
                    Model = "resolved-gpt-5.6-sol",
                    TokenUsage = collisionUsage
                }
            }
        };
        var collision = await ledger.IngestExecutionsAsync(new[] { collisionExecution });
        var afterCollision = await ledger.ReadAsync();
        Ensure(collision.ConflictingReplayCount == 1
               && collision.AppendedCount == 0
               && afterCollision.StoredAttemptCount == 5
               && afterCollision.AnomalyCount >= 1,
            "同 attempt 的 model/usage 补正没有保持同一身份键并写 conflict anomaly，或发生了重复计费。");

        Ensure(File.Exists(ledger.SourceIntegrityStatePath),
            "Direct replay collision did not create independent sticky integrity evidence.");
        File.Delete(ledger.AnomalyPath);
        var directCollisionAfterLoss = await new AccountUsageLedgerService(
            ledgerRoot, Path.Combine(root, "unused-source.jsonl"), () => now.AddMinutes(1)).ReadAsync();
        Ensure(directCollisionAfterLoss.TokenIntegrityFailureCount > 0
               && directCollisionAfterLoss.TokenSourceStale,
            "A direct replay collision was washed healthy after anomaly JSONL deletion/restart.");

        var longPrefix = new string('x', 300);
        RuntimeRouteExecution IdentityExecution(string request, string account, int ordinal) => new(
            request, "route", 200, 1, now, RuntimeExecutionOutcome.Succeeded, null, null,
            RuntimeLogSelectionBasis.Timestamp, ordinal,
            new[]
            {
                new RuntimeRouteAttempt(1, "provider-case", "provider-case", account, "安全显示",
                    RuntimeAccountIdentitySource.ExplicitAccountId, "model", 200, 1, null, null,
                    RuntimeFailoverReason.None, true, RuntimeAttemptSelectionEvidence.SingleAttempt,
                    new AttemptTokenUsageFact(1, null, null, null, 1, null, 2,
                        TokenTotalSource.Upstream, TokenTotalValidationState.Valid, "ok", "direct"), account)
            }, null, request);
        var identityRoot = Path.Combine(root, "opaque-identity");
        var identityLedger = new AccountUsageLedgerService(identityRoot, Path.Combine(root, "identity-none"), () => now);
        await identityLedger.IngestExecutionsAsync(new[]
        {
            IdentityExecution("abc", longPrefix + "-account-a", 1),
            IdentityExecution("ABC", longPrefix + "-account-b", 2),
            IdentityExecution(longPrefix + "-request-a", longPrefix + "-account-a", 3),
            IdentityExecution(longPrefix + "-request-b", longPrefix + "-account-b", 4)
        });
        var identitySnapshot = await identityLedger.ReadAsync();
        var identityFiles = await File.ReadAllTextAsync(identityLedger.AttemptLedgerPath);
        Ensure(identitySnapshot.StoredAttemptCount == 4
               && identitySnapshot.Accounts.Count(item => item.AccountAttributed) == 2
               && identitySnapshot.Accounts.Select(item => item.AccountId).Distinct(StringComparer.Ordinal).Count() == 2
               && !identityFiles.Contains(longPrefix, StringComparison.Ordinal),
            "opaque requestId 大小写/长前缀或完整原始 account HMAC 发生碰撞，或原始身份落盘。");

        var noRequestRoot = Path.Combine(root, "no-request-replay");
        Directory.CreateDirectory(noRequestRoot);
        var noRequestSource = Path.Combine(noRequestRoot, "usage.jsonl");
        const string noRequestLine = """{"requestedModel":"cmm/main","provider":"provider-no-id","model":"gpt","status":200,"timestamp":"2026-08-01T06:20:00Z","usage":{"inputTokens":2,"outputTokens":3,"totalTokens":5}}""";
        await File.WriteAllTextAsync(noRequestSource, noRequestLine + "\n" + noRequestLine + "\n", new UTF8Encoding(false));
        var noRequestLedger = new AccountUsageLedgerService(noRequestRoot, noRequestSource, () => now);
        var noRequestFirst = await noRequestLedger.IngestSourceAsync();
        File.Delete(noRequestLedger.CursorPath);
        var noRequestReplay = await noRequestLedger.IngestSourceAsync();
        var noRequestSnapshot = await noRequestLedger.ReadAsync();
        Ensure(noRequestFirst.AppendedCount == 1 && noRequestFirst.DuplicateCount == 1
               && noRequestReplay.AppendedCount == 0 && noRequestReplay.CandidateCount == 0
               && noRequestSnapshot.StoredAttemptCount == 1 && noRequestSnapshot.AnomalyCount >= 1,
            "无 requestId 的完整语义行发生双计，或 primary cursor 删除后未从独立 generation marker 恢复。");

        var sourceUpgradeRoot = Path.Combine(root, "source-contract-upgrade");
        Directory.CreateDirectory(sourceUpgradeRoot);
        var upgradedRequestLog = Path.Combine(sourceUpgradeRoot, "request-log.jsonl");
        const string alreadyPresentNativeLine = """{"requestId":"native-before-upgrade","requestedModel":"openai/gpt","provider":"openai","model":"gpt","status":200,"timestamp":"2026-08-01T06:22:00Z","usage":{"inputTokens":10,"outputTokens":5,"totalTokens":15}}""";
        const string appendedAfterUpgradeLine = """{"requestId":"native-after-upgrade","requestedModel":"openai/gpt","provider":"openai","model":"gpt","status":200,"timestamp":"2026-08-01T06:23:00Z","usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5}}""";
        await File.WriteAllTextAsync(upgradedRequestLog, alreadyPresentNativeLine + "\n", new UTF8Encoding(false));
        using var sourceUpgradeLedger = new AccountUsageLedgerService(
            sourceUpgradeRoot, upgradedRequestLog, () => now);
        var preUpgradeExecution = OpenCodexClient.ParseLatestRouteExecution(
                                      "[" + alreadyPresentNativeLine.Replace("native-before-upgrade", "old-ledger-request", StringComparison.Ordinal) + "]")
                                  ?? throw new InvalidOperationException("旧账本升级种子无法解析。 ");
        await sourceUpgradeLedger.IngestExecutionsAsync(new[] { preUpgradeExecution });
        var legacyGeneration = new string('A', 64);
        var legacyPrefix = new string('B', 64);
        var legacyCursor = JsonSerializer.Serialize(new
        {
            schemaVersion = 4,
            offset = 0,
            anchorStart = 0,
            anchorLength = 0,
            anchorHash = string.Empty,
            sourceLength = 0,
            sourceLastWriteUtcTicks = 0,
            sourceIdentity = "legacy-opencodex-source",
            generation = legacyGeneration,
            updatedAt = now,
            generationMarkerOnly = true
        });
        var legacyMarker = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            sourceIdentity = "legacy-opencodex-source",
            sourceLength = 0,
            observedOffset = 0,
            generation = legacyGeneration,
            prefixHash = legacyPrefix,
            digestBlockBytes = 64 * 1024,
            contentBlockHashes = Array.Empty<string>(),
            initializedAt = now
        });
        await File.WriteAllTextAsync(sourceUpgradeLedger.CursorPath, legacyCursor, new UTF8Encoding(false));
        await File.WriteAllTextAsync(sourceUpgradeLedger.CursorRecoveryPath, legacyCursor, new UTF8Encoding(false));
        await File.WriteAllTextAsync(sourceUpgradeLedger.SourceInitializedPath, legacyMarker, new UTF8Encoding(false));

        var sourceUpgrade = await sourceUpgradeLedger.IngestSourceAsync();
        var sourceUpgradeSnapshot = await sourceUpgradeLedger.ReadAsync();
        Ensure(sourceUpgrade.SourceContractMigrated && sourceUpgrade.AppendedCount == 0
               && !sourceUpgrade.CoverageGapDetected && sourceUpgradeSnapshot.StoredAttemptCount == 1,
            "旧 usage.jsonl 游标切换到 request-log.jsonl 时重复导入历史请求、丢掉旧账或误报覆盖缺口。 ");
        await File.AppendAllTextAsync(upgradedRequestLog, appendedAfterUpgradeLine + "\n", new UTF8Encoding(false));
        var firstNativeAppend = await sourceUpgradeLedger.IngestSourceAsync();
        var sourceUpgradeRestart = new AccountUsageLedgerService(sourceUpgradeRoot, upgradedRequestLog, () => now.AddMinutes(1));
        var restartNativeAppend = await sourceUpgradeRestart.IngestSourceAsync();
        var sourceUpgradeAfterRestart = await sourceUpgradeRestart.ReadAsync();
        using var upgradedCursorDocument = JsonDocument.Parse(await File.ReadAllTextAsync(sourceUpgradeRestart.CursorPath));
        Ensure(firstNativeAppend.AppendedCount == 1 && !restartNativeAppend.SourceContractMigrated
               && restartNativeAppend.AppendedCount == 0 && sourceUpgradeAfterRestart.StoredAttemptCount == 2
               && upgradedCursorDocument.RootElement.GetProperty("schemaVersion").GetInt32() == 5
               && upgradedCursorDocument.RootElement.GetProperty("sourceContract").GetString()
                  == "codex-total-manager:request-log.jsonl:v1",
            "新 request-log 基线后的追加请求没有恰好归账一次，或升级游标未绑定新来源契约。 ");

        var sourceRoot = Path.Combine(root, "source-recovery");
        Directory.CreateDirectory(sourceRoot);
        var sourcePath = Path.Combine(sourceRoot, "usage.jsonl");
        const string sourceLine1 = """{"requestId":"source-1","requestedModel":"cmm/main","provider":"provider-a","accountId":"account-a","model":"gpt-5.6-sol","status":200,"timestamp":"2026-08-01T14:20:00+08:00","usage":{"inputTokens":1,"outputTokens":2,"totalTokens":3}}""";
        const string sourceLine2 = """{"requestId":"source-2","requestedModel":"cmm/main","provider":"provider-a","model":"gpt-5.6-sol","status":502,"timestamp":"2026-08-01T14:21:00+08:00","usage":{"inputTokens":2,"outputTokens":3,"totalTokens":5}}""";
        const string sourceLine3Partial = """{"requestId":"source-3","requestedModel":"cmm/main","provider":"provider-b","accountId":"account-b","model":"glm-5.2","status":200,"timestamp":"2026-08-01T14:22:00+08:00","usage":{"inputTokens":3,"outputTokens":4,"totalTokens":7}""";
        const string sourceLine4 = """{"requestId":"source-4","requestedModel":"cmm/main","provider":"provider-c","accountId":"account-c","model":"gpt-5.6-terra","status":200,"timestamp":"2026-08-01T14:23:00+08:00","usage":{"inputTokens":4,"outputTokens":5,"totalTokens":9}}""";
        const string sourceLine5 = """{"requestId":"source-5","requestedModel":"cmm/main","provider":"provider-d","accountId":"account-d","model":"gpt-5.6-sol","status":200,"timestamp":"2026-08-01T14:24:00+08:00","usage":{"inputTokens":5,"outputTokens":6,"totalTokens":11}}""";
        await File.WriteAllTextAsync(
            sourcePath,
            sourceLine1 + "\n" + "{bad-json}\n" + sourceLine2 + "\n" + sourceLine3Partial,
            new UTF8Encoding(false));
        var sourceLedger = new AccountUsageLedgerService(sourceRoot, sourcePath, () => now);
        var sourceFirst = await sourceLedger.IngestSourceAsync();
        Ensure(sourceFirst.AppendedCount == 2
               && sourceFirst.BadSourceLineCount == 1
               && (await sourceLedger.ReadAsync()).StoredAttemptCount == 2,
            "中间坏行隔离或末尾半行延后失败。");
        await File.AppendAllTextAsync(sourcePath, "}\n", new UTF8Encoding(false));
        var sourceTail = await sourceLedger.IngestSourceAsync();
        Ensure(sourceTail.AppendedCount == 1
               && (await sourceLedger.ReadAsync()).StoredAttemptCount == 3,
            "并发追加完成后的末尾半行没有在下一轮恢复。");
        var restartedSourceLedger = new AccountUsageLedgerService(sourceRoot, sourcePath, () => now);
        var restartReplay = await restartedSourceLedger.IngestSourceAsync();
        Ensure(restartReplay.AppendedCount == 0
               && (await restartedSourceLedger.ReadAsync()).StoredAttemptCount == 3,
            "重复启动重读导致 Token 双计。");
        await File.WriteAllTextAsync(sourcePath, sourceLine1 + "\n", new UTF8Encoding(false));
        var truncated = await restartedSourceLedger.IngestSourceAsync();
        Ensure(truncated.SourceResetDetected && truncated.DuplicateCount == 1 && truncated.AppendedCount == 0,
            "源日志截断后没有从 0 重扫并通过全局事件键去重。");
        await File.WriteAllTextAsync(sourcePath, sourceLine4 + "\n", new UTF8Encoding(false));
        var rotated = await restartedSourceLedger.IngestSourceAsync();
        Ensure(rotated.SourceResetDetected
               && rotated.AppendedCount == 1
               && (await restartedSourceLedger.ReadAsync()).StoredAttemptCount == 4,
            "anchor 不符的轮转文件没有从 0 重扫或遗漏新事实。");

        await File.AppendAllTextAsync(restartedSourceLedger.AttemptLedgerPath, "{\"schemaVersion\":", new UTF8Encoding(false));
        var sourceExecution5 = OpenCodexClient.ParseLatestRouteExecution($"[{sourceLine5}]")
                               ?? throw new InvalidOperationException("恢复测试日志未解析。");
        await restartedSourceLedger.IngestExecutionsAsync(new[] { sourceExecution5 });
        var recovered = await restartedSourceLedger.ReadAsync();
        var recoveredAfterRestart = await new AccountUsageLedgerService(sourceRoot, sourcePath, () => now).ReadAsync();
        Ensure(recovered.StoredAttemptCount == 5
               && recovered.BadAttemptLineCount == 0
               && recoveredAfterRestart.StoredAttemptCount == 5,
            "台账断尾没有一次性截断修复、续写完整事件并在重启后恢复，或把已修复断尾永久记成坏行。");

        var concurrentRoot = Path.Combine(root, "concurrent");
        var concurrentA = new AccountUsageLedgerService(concurrentRoot, Path.Combine(root, "none-a"), () => now);
        var concurrentB = new AccountUsageLedgerService(concurrentRoot, Path.Combine(root, "none-b"), () => now);
        var concurrentTasks = new List<Task>();
        for (var index = 0; index < 12; index++)
            concurrentTasks.Add((index % 2 == 0 ? concurrentA : concurrentB).IngestExecutionsAsync(new[] { execution }));
        for (var index = 0; index < 8; index++)
            concurrentTasks.Add((index % 2 == 0 ? concurrentA : concurrentB).ReadAsync());
        await Task.WhenAll(concurrentTasks);
        var concurrentSnapshot = await concurrentA.ReadAsync();
        var restartDuplicate = await new AccountUsageLedgerService(concurrentRoot, Path.Combine(root, "none-c"), () => now)
            .IngestExecutionsAsync(new[] { execution });
        Ensure(concurrentSnapshot.StoredAttemptCount == 5
               && restartDuplicate.AppendedCount == 0
               && restartDuplicate.DuplicateCount == 5,
            "双实例并发写入/读取或重启重放破坏了幂等台账。");

        var tokenCountBeforeQuota = concurrentSnapshot.StoredAttemptCount;
        var officialBatch = concurrentA.CreateQuotaObservationBatch("official-provider", "account-a", now, true, "official-fetch-1");
        var relayBatch = concurrentA.CreateQuotaObservationBatch("relay-provider", "account-b", now, true, "relay-fetch-1");
        var unknownBatch = concurrentA.CreateQuotaObservationBatch("relay-provider", "account-c", now, false, "relay-fetch-2");
        var officialQuota = concurrentA.CreateQuotaSnapshot(
            "official-provider", true, "account-a", "weekly", "每周额度", 42m, "percent_used",
            AccountQuotaAvailability.Provided, now, true, now, false, officialBatch, false, null,
            "verified direct provider response", AccountQuotaProvenance.Official);
        var relayQuotaWithOfficialText = concurrentA.CreateQuotaSnapshot(
            "relay-provider", true, "account-b", "weekly", "每周额度", 51m, "percent_used",
            AccountQuotaAvailability.Provided, now, true, now, false, relayBatch, false, null,
            "OpenCodex 官方额度汇总");
        var unknownQuota = concurrentA.CreateQuotaSnapshot(
            "relay-provider", true, "account-c", string.Empty, "未提供", null, "unknown",
            AccountQuotaAvailability.NotProvided, now, false, now, false, unknownBatch, false, null,
            "relay returned no quota");
        var quotaIngest = await concurrentA.IngestQuotaSnapshotsAsync(new[]
        {
            officialQuota,
            relayQuotaWithOfficialText,
            unknownQuota
        });
        var quotaSnapshot = await concurrentA.ReadAsync();
        var staleQuotaSnapshot = await concurrentA.ReadAsync(quotaSourceReadFailed: true);
        Ensure(quotaIngest.AppendedCount == 3
               && quotaSnapshot.StoredAttemptCount == tokenCountBeforeQuota
               && quotaSnapshot.LatestQuotaSnapshots.Count == 3
               && quotaSnapshot.LatestQuotaSnapshots.Single(item => item.Fact.ProviderId == "official-provider").Fact.Provenance == AccountQuotaProvenance.Official
               && quotaSnapshot.LatestQuotaSnapshots.Single(item => item.Fact.ProviderId == "relay-provider" && item.Fact.Value == 51m).Fact.Provenance == AccountQuotaProvenance.RelayReported
               && quotaSnapshot.LatestQuotaSnapshots.Single(item => item.Fact.Availability == AccountQuotaAvailability.NotProvided).Fact.Value is null
               && staleQuotaSnapshot.LatestQuotaSnapshots.All(item => item.IsStale),
            "额度与 Token 未完全分离、文案误升 Official、unknown 被估算，或读取失败未保留旧值并标 stale。");
        var misleadingView = new PoolAccountView
        {
            PoolId = "relay-provider",
            Id = "account-b",
            UsageSourceText = "文案包含官方二字",
            QuotaProvenance = AccountQuotaProvenance.RelayReported
        };
        Ensure(misleadingView.QuotaProvenance == AccountQuotaProvenance.RelayReported,
            "结构化 provenance 被显示文案覆盖为 Official。");

        const string opaqueSecret = "ledger-opaque-987654";
        const string querySecret = "ledger-query-987654";
        // Deliberate fake JWT-shaped fixture used only to verify redaction; it is not a credential.
        const string jwtHeaderFixture = "eyJhbGciOiJIUzI1NiJ9";
        const string jwtPayloadFixture = "eyJzdWIiOiJsZWRnZXIifQ";
        const string jwtSignatureFixture = "c2lnbmF0dXJl";
        var jwtSecret = $"{jwtHeaderFixture}.{jwtPayloadFixture}.{jwtSignatureFixture}";
        const string basicSecret = "QWxhZGRpbjpsZWRnZXItc2VjcmV0";
        const string bearerSecret = "ledger-bearer-987654";
        const string skSecret = "sk-ledger-supersecret";
        var secretJson = $$"""
                              [
                                {
                                  "requestId": "Bearer {{bearerSecret}}",
                                  "requestedModel": "custom-{{jwtSecret}}",
                                  "provider": "provider?token={{querySecret}}",
                                  "accountId": "Basic {{basicSecret}}",
                                  "model": "custom-{{jwtSecret}}",
                                  "status": 500,
                                  "timestamp": "2026-08-01T14:29:30+08:00",
                                  "errorMessage": "{\"token\":\"{{opaqueSecret}}\"} {{skSecret}}",
                                  "usage": { "inputTokens": 1, "outputTokens": 1, "totalTokens": 2 }
                                }
                              ]
                              """;
        var secretExecution = OpenCodexClient.ParseLatestRouteExecution(secretJson)
                              ?? throw new InvalidOperationException("脱敏台账日志未解析。");
        var secretRoot = Path.Combine(root, "secret");
        var secretLedger = new AccountUsageLedgerService(secretRoot, Path.Combine(root, "secret-source-none"), () => now);
        await secretLedger.IngestExecutionsAsync(new[] { secretExecution });
        await secretLedger.IngestQuotaSnapshotsAsync(new[]
        {
            secretLedger.CreateQuotaSnapshot(
                $"provider?token={querySecret}",
                true,
                $"Basic {basicSecret}",
                string.Empty,
                $"Bearer {bearerSecret}",
                null,
                "unknown",
                AccountQuotaAvailability.NotProvided,
                now,
                false,
                now,
                false,
                secretLedger.CreateQuotaObservationBatch($"provider?token={querySecret}", $"Basic {basicSecret}", now, false, "secret-fetch"),
                false,
                null,
                $"{{\"token\":\"{opaqueSecret}\"}} {skSecret}")
        });
        string? ledgerEventJson = null;
        secretLedger.SnapshotChanged += (_, value) => ledgerEventJson = JsonSerializer.Serialize(value);
        var secretLedgerSnapshot = await secretLedger.ReadAsync();
        var sensitiveValues = new[] { opaqueSecret, querySecret, jwtSecret, basicSecret, bearerSecret, skSecret };
        var outputDocuments = new Dictionary<string, string>
        {
            ["source-output"] = JsonSerializer.Serialize(secretExecution),
            ["snapshot"] = JsonSerializer.Serialize(secretLedgerSnapshot),
            ["event"] = ledgerEventJson ?? string.Empty,
            ["ui-projection"] = string.Join("\n", secretLedgerSnapshot.Accounts.Select(item => $"{item.ProviderId}/{item.AccountId} {item.Total.DisplayText}"))
                                + string.Join("\n", secretLedgerSnapshot.RecentAttempts.Select(item => $"{item.ProviderId}/{item.AccountId}/{item.Model} {item.ErrorClassification}"))
                                + string.Join("\n", secretLedgerSnapshot.LatestQuotaSnapshots.Select(item => $"{item.Fact.ProviderId}/{item.Fact.AccountId}/{item.Fact.PeriodKey}/{item.Fact.DisplayLabel}"))
        };
        foreach (var path in new[] { secretLedger.AttemptLedgerPath, secretLedger.QuotaLedgerPath,
                     secretLedger.QuotaPrepareLedgerPath, secretLedger.QuotaCommitLedgerPath, secretLedger.AnomalyPath })
            if (File.Exists(path)) outputDocuments["file:" + Path.GetFileName(path)] = await File.ReadAllTextAsync(path);
        var secretLeakPaths = outputDocuments
            .Where(pair => sensitiveValues.Any(secret => pair.Value.Contains(secret, StringComparison.Ordinal)))
            .Select(pair => pair.Key)
            .ToArray();
        Ensure(secretLeakPaths.Length == 0
               && secretLedgerSnapshot.RecentAttempts.All(item => item.AccountId != basicSecret)
               && secretLedgerSnapshot.RecentAttempts.All(item => !(item.ErrorMessage?.Contains(opaqueSecret, StringComparison.Ordinal) ?? false)),
            $"台账源输出/JSONL/事件/UI 投影脱敏失败：secretAbsent={secretLeakPaths.Length == 0} paths={string.Join(",", secretLeakPaths)}。");

        var runtimeRoot = Path.Combine(root, "runtime-integration");
        Directory.CreateDirectory(runtimeRoot);
        var runtimeSourcePath = Path.Combine(runtimeRoot, "usage.jsonl");
        await File.WriteAllTextAsync(runtimeSourcePath, sourceLine1 + "\n", new UTF8Encoding(false));
        var runtimeLedger = new AccountUsageLedgerService(runtimeRoot, runtimeSourcePath, () => now);
        var runtimeTruth = new RuntimeTruthService(
            new TestRuntimeTruthSource { Execution = OpenCodexClient.ParseLatestRouteExecution($"[{sourceLine1}]") },
            () => now,
            accountUsageLedger: runtimeLedger);
        var emptyLedger = await runtimeLedger.ReadAsync();
        var runtimeSnapshot = await runtimeTruth.ReadAsync();
        var afterTruthRead = await runtimeLedger.ReadAsync();
        Ensure(emptyLedger.StoredAttemptCount == 0
               && afterTruthRead.StoredAttemptCount == 0,
            "Runtime Truth 只读刷新触发了台账导入/持久化。");
        await using var runtimeImporter = new AccountUsageLedgerImporter(
            runtimeLedger, _ => Task.FromResult<IReadOnlyList<AccountPoolView>>(Array.Empty<AccountPoolView>()),
            quotaSampleInterval: TimeSpan.Zero, clock: () => now);
        await runtimeImporter.RefreshOnceAsync();
        var importedRuntimeSnapshot = await runtimeTruth.ReadAsync();
        Ensure(runtimeLedger.LastSnapshot.StoredAttemptCount == 1
               && importedRuntimeSnapshot.Evidence.Any(item => item.Source == RuntimeTruthEvidenceSource.AccountUsageLedger && item.Available),
            "独立 importer 未发布台账快照，或 Runtime Truth 未只读引用最近不可变证据。");
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static async Task AssertAccountUsageLedgerHardeningAsync()
{
    var root = CreateOwnedTestRoot("cmm-account-ledger-hardening");
    var now = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
    RuntimeRouteExecution Execution(string requestId, string account, string model = "model-x") => new(
        requestId, "route", 200, 1, now, RuntimeExecutionOutcome.Succeeded, null, null,
        RuntimeLogSelectionBasis.Timestamp, 0,
        new[]
        {
            new RuntimeRouteAttempt(1, "provider-h", "provider-h", account, "safe",
                RuntimeAccountIdentitySource.ExplicitAccountId, model, 200, 1, null, null,
                RuntimeFailoverReason.None, true, RuntimeAttemptSelectionEvidence.SingleAttempt,
                new AttemptTokenUsageFact(2, null, null, null, 3, null, 5,
                    TokenTotalSource.Upstream, TokenTotalValidationState.Valid, "ok", "direct"), account)
        }, null, requestId);
    try
    {
        var identityRoot = Path.Combine(root, "request-hmac");
        var identityLedger = new AccountUsageLedgerService(identityRoot, Path.Combine(root, "none"), () => now);
        await identityLedger.IngestExecutionsAsync(new[] { Execution("42", "person@example.com") });
        var persisted = await File.ReadAllTextAsync(identityLedger.AttemptLedgerPath);
        using var persistedJson = JsonDocument.Parse(persisted.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        var persistedIdentity = persistedJson.RootElement.GetProperty("requestIdentity").GetString() ?? string.Empty;
        var requestKeyId = persistedJson.RootElement.GetProperty("requestKeyId").GetString() ?? string.Empty;
        Ensure(persistedIdentity.StartsWith("RK1:", StringComparison.Ordinal)
               && requestKeyId.Length == 32
               && !persisted.Contains("person@example.com", StringComparison.OrdinalIgnoreCase)
               && !persisted.Contains("\"requestId\":\"42\"", StringComparison.Ordinal),
            "低熵 requestId 未使用 DPAPI 身份键的域分离 HMAC，或原始账号/requestId 落盘。");
        await File.WriteAllTextAsync(identityLedger.SourcePath,
            "{\"requestId\":\"first-source\",\"requestedModel\":\"r\",\"provider\":\"p\",\"model\":\"m\",\"status\":200,\"timestamp\":\"2026-08-01T08:00:00Z\"}\n",
            new UTF8Encoding(false));
        var firstSourceAfterDirect = await identityLedger.IngestSourceAsync();
        var firstSourceSnapshot = await identityLedger.ReadAsync();
        Ensure(!firstSourceAfterDirect.CoverageGapDetected
               && !firstSourceSnapshot.CoverageGapDetected
               && !identityLedger.CoverageGapDetected,
            "direct-only attempt segment 被误判为已经初始化过 OpenCodex source。 ");

        var rewriteRoot = Path.Combine(root, "middle-rewrite");
        var rewriteLedger = new AccountUsageLedgerService(rewriteRoot, Path.Combine(root, "none-rewrite"), () => now);
        await rewriteLedger.IngestExecutionsAsync(new[] { Execution("rw-1", "a"), Execution("rw-2", "b"), Execution("rw-3", "c") });
        await rewriteLedger.ReadAsync();
        var beforeNoChange = rewriteLedger.Diagnostics;
        await rewriteLedger.ReadAsync();
        var afterNoChange = rewriteLedger.Diagnostics;
        Ensure(afterNoChange.FullIndexRebuildCount == beforeNoChange.FullIndexRebuildCount
               && afterNoChange.ParsedLedgerLineCount == beforeNoChange.ParsedLedgerLineCount,
            "无变更 ReadAsync 仍重建/重解析历史段。 ");
        var rows = (await File.ReadAllLinesAsync(rewriteLedger.AttemptLedgerPath)).Where(line => line.Length > 0).ToArray();
        rows[1] = rows[1].Replace("model-x", "model-y", StringComparison.Ordinal);
        await File.WriteAllTextAsync(rewriteLedger.AttemptLedgerPath,
            string.Join("\n", rows) + "\n" + rows[0] + "\n", new UTF8Encoding(false));
        var rewritten = await rewriteLedger.ReadAsync();
        Ensure(rewritten.IntegrityFailureCount > 0
               && rewriteLedger.Diagnostics.FullIndexRebuildCount > afterNoChange.FullIndexRebuildCount,
            "中段改写+追加被误判为纯 append，完整性仍显示绿色。 ");

        var scopeRoot = Path.Combine(root, "quota-scope");
        var scopeLedger = new AccountUsageLedgerService(scopeRoot, Path.Combine(root, "none-scope"), () => now);
        var batchA = scopeLedger.CreateQuotaObservationBatch("provider-q", "same-account", now, true, "fetch-a", "pool:a");
        var batchB = scopeLedger.CreateQuotaObservationBatch("provider-q", "same-account", now, true, "fetch-b", "pool:b");
        var factA = scopeLedger.CreateQuotaSnapshot("provider-q", true, "same-account", "weekly", "Weekly", 10, "percent_used",
            AccountQuotaAvailability.Provided, now, true, now, false, batchA, false, null, "relay", observationScope: "pool:a");
        var factB = scopeLedger.CreateQuotaSnapshot("provider-q", true, "same-account", "weekly", "Weekly", 20, "percent_used",
            AccountQuotaAvailability.Provided, now, true, now, false, batchB, false, null, "relay", observationScope: "pool:b");
        await scopeLedger.IngestQuotaSnapshotsAsync(new[] { factA, factB });
        await scopeLedger.IngestQuotaSnapshotsAsync(new[]
        {
            scopeLedger.CreateMissingAccountTombstone(factA, now.AddMinutes(1), "pool-a-missing")
        });
        var scoped = await scopeLedger.ReadAsync();
        Ensure(scoped.LatestQuotaSnapshots.Any(view => view.Fact.ObservationScope == "pool:b"
                                                       && view.Fact.Availability == AccountQuotaAvailability.Provided
                                                       && view.Fact.Value == 20),
            "A scope tombstone 错误隐藏了同 provider 的 B scope 有效额度。 ");
        var invalidBatch = scopeLedger.CreateQuotaObservationBatch("provider-q", "invalid-account", now, true, "invalid-fetch", "pool:c");
        var invalidQuota = scopeLedger.CreateQuotaSnapshot("provider-q", true, "invalid-account", "weekly", "Weekly", 150, "percent_used",
            AccountQuotaAvailability.Provided, now, true, now, false, invalidBatch, false, null, "relay", observationScope: "pool:c");
        await scopeLedger.IngestQuotaSnapshotsAsync(new[] { invalidQuota });
        var invalidSnapshot = await scopeLedger.ReadAsync();
        var validBatch = scopeLedger.CreateQuotaObservationBatch("provider-q", "invalid-account", now.AddMinutes(2), true, "valid-fetch", "pool:c");
        await scopeLedger.IngestQuotaSnapshotsAsync(new[]
        {
            scopeLedger.CreateQuotaSnapshot("provider-q", true, "invalid-account", "weekly", "Weekly", 40, "percent_used",
                AccountQuotaAvailability.Provided, now.AddMinutes(2), true, now.AddMinutes(2), false, validBatch, false, null,
                "relay", observationScope: "pool:c")
        });
        var recoveredQuota = await scopeLedger.ReadAsync();
        Ensure(invalidSnapshot.QuotaIntegrityFailureCount == 0 && invalidSnapshot.TokenIntegrityFailureCount == 0
               && recoveredQuota.LatestQuotaSnapshots.Any(view => view.Fact.ObservationScope == "pool:c" && view.Fact.Value == 40)
               && recoveredQuota.QuotaIntegrityFailureCount == 0,
            "percent_used 超过 100 被误当作完整性损坏、污染了 Token 域，或后续正常值未更新展示。 ");
        var overageWindow = new UsageWindowView { PeriodKey = "weekly", Label = "Weekly", UsedPercent = 150 };
        Ensure(overageWindow.ValueValidation == QuotaValueValidationState.Valid
               && overageWindow.VisualUsedPercent == 100
               && overageWindow.RemainingPercent == 0
               && overageWindow.SummaryText.Contains("已超用 50%", StringComparison.Ordinal),
            "供应商返回超过 100% 的真实超用值仍被界面当作损坏数据。 ");

        var rotationRoot = Path.Combine(root, "archive-tail");
        Directory.CreateDirectory(rotationRoot);
        var rotationSource = Path.Combine(rotationRoot, "usage.jsonl");
        const string archiveValid = "{\"requestId\":\"archive-1\",\"requestedModel\":\"r\",\"provider\":\"p\",\"model\":\"m\",\"status\":200,\"timestamp\":\"2026-08-01T08:00:00Z\",\"usage\":{\"inputTokens\":1,\"outputTokens\":1,\"totalTokens\":2}}";
        const string activeValid = "{\"requestId\":\"archive-2\",\"requestedModel\":\"r\",\"provider\":\"p\",\"model\":\"m\",\"status\":200,\"timestamp\":\"2026-08-01T08:01:00Z\",\"usage\":{\"inputTokens\":1,\"outputTokens\":2,\"totalTokens\":3}}";
        await File.WriteAllTextAsync(rotationSource, archiveValid + "\n" + "{\"requestId\":\"partial\"", new UTF8Encoding(false));
        var rotationLedger = new AccountUsageLedgerService(rotationRoot, rotationSource, () => now);
        await rotationLedger.IngestSourceAsync();
        File.Move(rotationSource, rotationSource + ".1");
        await File.WriteAllTextAsync(rotationSource, activeValid + "\n", new UTF8Encoding(false));
        var archivedTail = await rotationLedger.IngestSourceAsync();
        var activeAfterTail = await rotationLedger.IngestSourceAsync();
        var rotationSnapshot = await rotationLedger.ReadAsync();
        Ensure(archivedTail.CoverageGapDetected && activeAfterTail.AppendedCount == 1
               && rotationSnapshot.StoredAttemptCount == 2 && rotationSnapshot.CoverageGapDetected,
            "finalized archive 的无换行尾部未有限推进，或 active 有效行被阻塞/覆盖缺口未持久。 ");

        var ioRoot = Path.Combine(root, "token-io-quota-ok");
        Directory.CreateDirectory(ioRoot);
        var ioSource = Path.Combine(ioRoot, "usage.jsonl");
        await File.WriteAllTextAsync(ioSource, archiveValid + "\n", new UTF8Encoding(false));
        var ioLedger = new AccountUsageLedgerService(ioRoot, ioSource, () => now);
        var pool = new AccountPoolView
        {
            Id = "pool-io", Enabled = true, RuntimeProviderId = "provider-io",
            RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
            QuotaRosterCompleteness = AccountRosterCompleteness.Complete
        };
        pool.Accounts.Add(new PoolAccountView
        {
            PoolId = "pool-io", Id = "io-account", RuntimeProviderId = "provider-io",
            RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
            QuotaRosterCompleteness = AccountRosterCompleteness.Complete,
            QuotaAvailability = AccountQuotaAvailability.Provided,
            QuotaProvenance = AccountQuotaProvenance.RelayReported,
            QuotaWindows = new[] { new UsageWindowView { PeriodKey = "weekly", Label = "Weekly", UsedPercent = 12 } }
        });
        await using var lockedSource = new FileStream(ioSource, FileMode.Open, FileAccess.Read, FileShare.None);
        await using var importer = new AccountUsageLedgerImporter(ioLedger,
            _ => Task.FromResult<IReadOnlyList<AccountPoolView>>(new[] { pool }),
            quotaSampleInterval: TimeSpan.Zero, clock: () => now);
        await importer.RefreshOnceAsync();
        var ioSnapshot = ioLedger.LastSnapshot;
        Ensure(ioSnapshot.ImporterStatus.TokenHealth == AccountUsageImporterHealth.Degraded
               && ioSnapshot.ImporterStatus.QuotaHealth == AccountUsageImporterHealth.Healthy
               && ioSnapshot.LatestQuotaSnapshots.Count == 1,
            "Token source IOException 阻塞了独立 quota refresh，或两个健康域被混为一体。 ");

        var cliDefinition = new PoolDefinition { Id = "cli-roster", ProviderId = "cli-provider" };
        var cliMissing = ParsePrivateRoster(typeof(CliProxyPoolService), cliDefinition, "{}");
        var cliMalformed = ParsePrivateRoster(typeof(CliProxyPoolService), cliDefinition,
            """{"files":[{"provider":"codex"}]}""");
        var cliDuplicateCase = ParsePrivateRoster(typeof(CliProxyPoolService), cliDefinition,
            """{"files":[{"name":"Account-A","provider":"codex"},{"name":"account-a","provider":"codex"}]}""");
        var cliEmpty = ParsePrivateRoster(typeof(CliProxyPoolService), cliDefinition, """{"files":[]}""");
        Ensure(cliMissing.Completeness == AccountRosterCompleteness.Partial
               && cliMalformed.Completeness == AccountRosterCompleteness.Partial
               && cliDuplicateCase.Completeness == AccountRosterCompleteness.Partial
               && cliDuplicateCase.Accounts.Count == 1
               && cliEmpty.Completeness == AccountRosterCompleteness.Complete,
            "CLI roster parser accepted a missing, malformed, or duplicate account roster as Complete, or rejected an authoritative empty array.");

        var cliAdapterPool = new PoolDefinition
        {
            Id = "cli-adapter-duplicate", DisplayName = "CLI duplicate", Transport = PoolTransport.CliProxyApi,
            Product = AccountProduct.CodexPlus, ProviderId = "cmm-cli-adapter-duplicate",
            BaseUrl = "http://127.0.0.1:18441/v1", LocalPort = 18441, Enabled = true
        };
        var cliAdapterService = new CliProxyPoolService(new AppSettingsService(Path.Combine(root, "cli-adapter-settings")),
            new SecretStore(Path.Combine(root, "cli-adapter-settings")),
            _ => new HttpClient(new ScriptedHttpHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/v0/management/auth-files"
                    ? ScriptedHttpHandler.Json(HttpStatusCode.OK,
                        """{"files":[{"name":"Account-A","provider":"codex"},{"name":"account-a","provider":"codex"}]}""")
                    : ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"data":[{"id":"gpt-5.6-sol"}]}"""))))
            { BaseAddress = new Uri("http://127.0.0.1:18441") });
        var cliAdapterSnapshot = await cliAdapterService.ReadAsync(cliAdapterPool);
        var cliAdapterPartialPool = new AccountPoolView
        {
            Id = cliAdapterPool.Id, Enabled = true, RuntimeProviderId = cliAdapterPool.ProviderId,
            RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
            QuotaRosterCompleteness = cliAdapterSnapshot.AccountRosterCompleteness
        };
        foreach (var account in cliAdapterSnapshot.Accounts) cliAdapterPartialPool.Accounts.Add(account);
        Ensure(cliAdapterSnapshot.AccountRosterCompleteness == AccountRosterCompleteness.Partial
               && cliAdapterSnapshot.Accounts.Count == 1
               && cliAdapterSnapshot.Accounts[0].Id == "Account-A",
            "CLI duplicate roster did not preserve the first OrdinalIgnoreCase identity as a Partial authoritative result.");

        AccountPoolView openCodexAdapterPartialPool;
        using (var duplicateClient = new HttpClient(new ScriptedHttpHandler((request, _) =>
               Task.FromResult(request.RequestUri!.AbsolutePath switch
               {
                   "/api/codex-auth/accounts" => ScriptedHttpHandler.Json(HttpStatusCode.OK,
                       """{"accounts":[{"id":"Account-A","label":"first"},{"id":"account-a","label":"second"}]}"""),
                   "/api/codex-auth/active" => ScriptedHttpHandler.Json(HttpStatusCode.OK,
                       """{"activeCodexAccountId":"account-a"}"""),
                   "/api/providers" => ScriptedHttpHandler.Json(HttpStatusCode.OK,
                       """[{"name":"openai","codexAccountMode":"pool"}]"""),
                   _ => ScriptedHttpHandler.Json(HttpStatusCode.NotFound, "{}")
               }))) { BaseAddress = new Uri("http://127.0.0.1:10100") })
        {
            var duplicateOpenCodex = await new OpenCodexClient(duplicateClient).GetCodexAccountsAsync();
            Ensure(duplicateOpenCodex.RosterCompleteness == AccountRosterCompleteness.Partial
                    && duplicateOpenCodex.Accounts.Count == 1
                    && duplicateOpenCodex.Accounts[0].Id == "Account-A"
                    && duplicateOpenCodex.Accounts.Count(account => account.IsActive) == 1,
                "OpenCodex accepted OrdinalIgnoreCase duplicate account IDs as Complete or exposed multiple active accounts.");
            openCodexAdapterPartialPool = new AccountPoolView
            {
                Id = "opencodex-adapter-duplicate", Enabled = true, RuntimeProviderId = "opencodex-adapter-provider",
                RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
                QuotaRosterCompleteness = duplicateOpenCodex.RosterCompleteness
            };
            foreach (var account in duplicateOpenCodex.Accounts)
                openCodexAdapterPartialPool.Accounts.Add(new PoolAccountView
                {
                    PoolId = openCodexAdapterPartialPool.Id,
                    RuntimeProviderId = openCodexAdapterPartialPool.RuntimeProviderId,
                    RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
                    Id = account.Id,
                    Label = "safe account",
                    Enabled = true,
                    QuotaAvailability = AccountQuotaAvailability.NotProvided
                });
        }

        var auxiliaryCalls = 0;
        using (var auxiliaryClient = new HttpClient(new ScriptedHttpHandler(async (request, token) =>
               {
                   if (request.RequestUri!.AbsolutePath == "/api/codex-auth/accounts")
                       return ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"accounts":[]}""");
                   Interlocked.Increment(ref auxiliaryCalls);
                   if (request.RequestUri.AbsolutePath == "/api/codex-auth/active")
                       await Task.Delay(Timeout.InfiniteTimeSpan, token);
                   return ScriptedHttpHandler.Json(HttpStatusCode.OK, "{not-json");
               })) { BaseAddress = new Uri("http://127.0.0.1:10100"), Timeout = TimeSpan.FromMilliseconds(120) })
        {
            var auxiliaryTimer = Stopwatch.StartNew();
            var auxiliaryOpenCodex = await new OpenCodexClient(auxiliaryClient).GetCodexAccountsAsync();
            auxiliaryTimer.Stop();
            Ensure(auxiliaryOpenCodex.RosterCompleteness == AccountRosterCompleteness.Complete
                   && auxiliaryOpenCodex.Accounts.Count == 0
                   && auxiliaryCalls == 2
                   && auxiliaryTimer.Elapsed < TimeSpan.FromSeconds(1),
                "Successful OpenCodex accounts roster was polluted by auxiliary active/providers timeout or malformed JSON.");
        }

        var failedAccountsAuxiliaryCalls = 0;
        using (var failedAccountsClient = new HttpClient(new ScriptedHttpHandler((request, _) =>
               {
                   if (request.RequestUri!.AbsolutePath != "/api/codex-auth/accounts")
                       Interlocked.Increment(ref failedAccountsAuxiliaryCalls);
                   return Task.FromResult(ScriptedHttpHandler.Json(HttpStatusCode.InternalServerError, "{}"));
               })) { BaseAddress = new Uri("http://127.0.0.1:10100"), Timeout = TimeSpan.FromMilliseconds(120) })
        {
            var failedBounded = Stopwatch.StartNew();
            var accountsFailed = await new OpenCodexClient(failedAccountsClient).GetCodexAccountsAsync();
            failedBounded.Stop();
            Ensure(accountsFailed.RosterCompleteness == AccountRosterCompleteness.ReadFailed
                   && accountsFailed.Accounts.Count == 0
                   && failedAccountsAuxiliaryCalls == 0 && failedBounded.Elapsed < TimeSpan.FromSeconds(1),
                "OpenCodex started auxiliary tasks before a failed authoritative accounts response was accepted and parsed.");
        }

        using (var malformedAccountsClient = new HttpClient(new ScriptedHttpHandler((request, _) => Task.FromResult(
                   request.RequestUri!.AbsolutePath == "/api/codex-auth/accounts"
                       ? ScriptedHttpHandler.Json(HttpStatusCode.OK, "{malformed")
                       : ScriptedHttpHandler.Json(HttpStatusCode.OK, "{}"))))
               { BaseAddress = new Uri("http://127.0.0.1:10100") })
        {
            var malformedAccounts = await new OpenCodexClient(malformedAccountsClient).GetCodexAccountsAsync();
            Ensure(malformedAccounts.RosterCompleteness == AccountRosterCompleteness.ReadFailed,
                "Malformed authoritative OpenCodex accounts JSON was not projected as ReadFailed.");
        }
        using (var auxiliaryNonSuccessClient = new HttpClient(new ScriptedHttpHandler((request, _) => Task.FromResult(
                   request.RequestUri!.AbsolutePath == "/api/codex-auth/accounts"
                       ? ScriptedHttpHandler.Json(HttpStatusCode.OK, """{"accounts":[]}""")
                       : ScriptedHttpHandler.Json(HttpStatusCode.BadGateway, "{}"))))
               { BaseAddress = new Uri("http://127.0.0.1:10100") })
        {
            var auxiliaryNonSuccess = await new OpenCodexClient(auxiliaryNonSuccessClient).GetCodexAccountsAsync();
            Ensure(auxiliaryNonSuccess.RosterCompleteness == AccountRosterCompleteness.Complete
                   && auxiliaryNonSuccess.Accounts.Count == 0,
                "OpenCodex active/providers non-success polluted a successful authoritative accounts roster.");
        }

        foreach (var unsupportedStatus in new[] { HttpStatusCode.NotImplemented, HttpStatusCode.NotFound })
        {
            using var unsupportedLoginClient = new HttpClient(new ScriptedHttpHandler((_, _) => Task.FromResult(
                ScriptedHttpHandler.Json(unsupportedStatus,
                    """{"error":{"message":"additional native accounts are unavailable"}}"""))))
            { BaseAddress = new Uri("http://127.0.0.1:10100") };
            var unavailableReported = false;
            try
            {
                await new OpenCodexClient(unsupportedLoginClient).StartCodexAccountLoginAsync();
            }
            catch (OpenCodexAccountApiUnavailableException)
            {
                unavailableReported = true;
            }
            Ensure(unavailableReported,
                $"OpenCodex {unsupportedStatus} native-account capability was not projected as an unavailable feature.");
        }

        var backendFaultRoot = Path.Combine(root, "backend-roster-faults");
        var backendFaultSettings = new AppSettingsService(backendFaultRoot);
        var backendFaultSecrets = new SecretStore(backendFaultRoot);
        var cliFaultPool = new PoolDefinition
        {
            Id = "cli-fault", DisplayName = "CLI fault", Transport = PoolTransport.CliProxyApi,
            Product = AccountProduct.CodexPlus, ProviderId = "cmm-cli-fault",
            BaseUrl = "http://127.0.0.1:18431/v1", LocalPort = 18431, Enabled = true
        };
        async Task<PoolBackendSnapshot> ReadCliFault(HttpStatusCode status, string body)
        {
            var service = new CliProxyPoolService(backendFaultSettings, backendFaultSecrets,
                _ => new HttpClient(new ScriptedHttpHandler((_, _) =>
                    Task.FromResult(ScriptedHttpHandler.Json(status, body))))
                { BaseAddress = new Uri("http://127.0.0.1:18431") });
            return await service.ReadAsync(cliFaultPool);
        }
        var cliHttpFailure = await ReadCliFault(HttpStatusCode.BadGateway, "{}");
        var cliJsonFailure = await ReadCliFault(HttpStatusCode.OK, "{broken-json");

        Ensure(cliHttpFailure.AccountRosterCompleteness == AccountRosterCompleteness.ReadFailed
               && cliJsonFailure.AccountRosterCompleteness == AccountRosterCompleteness.ReadFailed,
            "CLI HTTP or JSON failures were not projected as ReadFailed roster health.");

        var cliModelFailureService = new CliProxyPoolService(backendFaultSettings, backendFaultSecrets,
            _ => new HttpClient(new ScriptedHttpHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/v0/management/auth-files"
                    ? ScriptedHttpHandler.Json(HttpStatusCode.OK,
                        """{"files":[{"name":"cli-model-account","provider":"codex"}]}""")
                    : ScriptedHttpHandler.Json(HttpStatusCode.BadGateway, "{}"))))
            { BaseAddress = new Uri("http://127.0.0.1:18431") });
        var cliModelFailure = await cliModelFailureService.ReadAsync(cliFaultPool);
        Ensure(cliModelFailure.AccountRosterCompleteness == AccountRosterCompleteness.Complete
               && cliModelFailure.Accounts.Count == 1 && cliModelFailure.Models.Count == 0,
            "CLI model-directory failure polluted a successfully parsed authoritative account roster.");

        async Task AssertAdapterPartialRetention(AccountPoolView adapterPool, string provider, string name)
        {
            var adapterRetentionRoot = Path.Combine(root, "adapter-partial-retention-" + name);
            var adapterLedger = new AccountUsageLedgerService(adapterRetentionRoot,
                Path.Combine(adapterRetentionRoot, "disabled"), () => now, sourceDisabled: true);
            var oldBatch = adapterLedger.CreateQuotaObservationBatch(provider, "old-account", now, true,
                "old-" + name, "pool:" + adapterPool.Id);
            await adapterLedger.IngestQuotaSnapshotsAsync(new[]
            {
                adapterLedger.CreateQuotaSnapshot(provider, true, "old-account", "weekly", "Weekly", 31,
                    "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, oldBatch, false,
                    null, "adapter-test", observationScope: "pool:" + adapterPool.Id)
            });
            await using var adapterImporter = new AccountUsageLedgerImporter(adapterLedger,
                _ => Task.FromResult<IReadOnlyList<AccountPoolView>>(new[] { adapterPool }),
                quotaSampleInterval: TimeSpan.Zero, clock: () => now.AddMinutes(1));
            await adapterImporter.RefreshOnceAsync();
            var retained = await adapterLedger.ReadAsync();
            Ensure(retained.LatestQuotaSnapshots.Any(view => view.Fact.Value == 31 && view.IsStale)
                   && retained.ImporterStatus.QuotaHealth == AccountUsageImporterHealth.Degraded,
                $"{name} Partial duplicate adapter output tombstoned an omitted historical account instead of retaining stale quota.");
        }

        await AssertAdapterPartialRetention(cliAdapterPartialPool, cliAdapterPool.ProviderId!, "cli");
        await AssertAdapterPartialRetention(openCodexAdapterPartialPool, "opencodex-adapter-provider", "opencodex");

        var rosterRoot = Path.Combine(root, "roster-retention");
        var rosterLedger = new AccountUsageLedgerService(rosterRoot, Path.Combine(rosterRoot, "disabled"), () => now,
            sourceDisabled: true);
        var rosterBatch = rosterLedger.CreateQuotaObservationBatch("relay-provider", "old-account", now, true,
            "roster-old", "pool:relay-roster");
        await rosterLedger.IngestQuotaSnapshotsAsync(new[]
        {
            rosterLedger.CreateQuotaSnapshot("relay-provider", true, "old-account", "weekly", "Weekly", 27,
                "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, rosterBatch, false,
                null, "relay", observationScope: "pool:relay-roster")
        });
        var partialPool = new AccountPoolView
        {
            Id = "relay-roster", Enabled = true, RuntimeProviderId = "relay-provider",
            RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
            QuotaRosterCompleteness = cliMalformed.Completeness
        };
        await using (var partialImporter = new AccountUsageLedgerImporter(rosterLedger,
                         _ => Task.FromResult<IReadOnlyList<AccountPoolView>>(new[] { partialPool }),
                         quotaSampleInterval: TimeSpan.Zero, clock: () => now.AddMinutes(1)))
            await partialImporter.RefreshOnceAsync();
        var retainedRoster = await rosterLedger.ReadAsync();
        Ensure(retainedRoster.LatestQuotaSnapshots.Any(view => view.Fact.Value == 27 && view.IsStale)
               && retainedRoster.ImporterStatus.QuotaHealth == AccountUsageImporterHealth.Degraded,
            "Partial/malformed roster tombstoned an old quota instead of retaining it as stale.");
        var completeEmptyPool = new AccountPoolView
        {
            Id = "relay-roster", Enabled = true, RuntimeProviderId = "relay-provider",
            RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
            QuotaRosterCompleteness = cliEmpty.Completeness
        };
        await using (var completeImporter = new AccountUsageLedgerImporter(rosterLedger,
                         _ => Task.FromResult<IReadOnlyList<AccountPoolView>>(new[] { completeEmptyPool }),
                         quotaSampleInterval: TimeSpan.Zero, clock: () => now.AddMinutes(2)))
            await completeImporter.RefreshOnceAsync();
        var removedRoster = await rosterLedger.ReadAsync();
        Ensure(removedRoster.LatestQuotaSnapshots.Any(view =>
                   view.Fact.ObservationScope == "pool:relay-roster"
                   && view.Fact.Availability == AccountQuotaAvailability.NotProvided)
               && removedRoster.LatestQuotaSnapshots.All(view => view.Fact.Value != 27),
            "Authoritative Complete-empty roster did not create the scoped account-removal tombstone.");

        var windowRoot = Path.Combine(root, "quota-window-structure");
        var windowLedger = new AccountUsageLedgerService(windowRoot, Path.Combine(windowRoot, "disabled"), () => now,
            sourceDisabled: true);
        foreach (var item in new[] { (Scope: "pool:blank", Account: "blank-account"), (Scope: "pool:mixed", Account: "mixed-account") })
        {
            var priorBatch = windowLedger.CreateQuotaObservationBatch("provider-window", item.Account, now, true,
                "prior-" + item.Account, item.Scope);
            await windowLedger.IngestQuotaSnapshotsAsync(new[]
            {
                windowLedger.CreateQuotaSnapshot("provider-window", true, item.Account, "weekly", "Weekly", 33,
                    "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, priorBatch, false,
                    null, "relay", observationScope: item.Scope)
            });
        }
        AccountPoolView WindowPool(string id, string account, IReadOnlyList<UsageWindowView> windows)
        {
            var view = new AccountPoolView
            {
                Id = id, Enabled = true, RuntimeProviderId = "provider-window",
                RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
                QuotaRosterCompleteness = AccountRosterCompleteness.Complete
            };
            view.Accounts.Add(new PoolAccountView
            {
                PoolId = id, Id = account, RuntimeProviderId = "provider-window",
                RuntimeProviderIdentitySource = RuntimeProviderIdentitySource.PoolDefinitionProviderId,
                QuotaAvailability = AccountQuotaAvailability.Provided,
                QuotaRosterCompleteness = AccountRosterCompleteness.Complete,
                QuotaWindows = windows
            });
            return view;
        }
        var allBlankPool = WindowPool("blank", "blank-account",
            new[] { new UsageWindowView { PeriodKey = "", Label = "blank", UsedPercent = 10 } });
        var mixedPool = WindowPool("mixed", "mixed-account", new[]
        {
            new UsageWindowView { PeriodKey = "weekly", Label = "Weekly", UsedPercent = 11 },
            new UsageWindowView { PeriodKey = "", Label = "broken", UsedPercent = 12 }
        });
        await using (var windowImporter = new AccountUsageLedgerImporter(windowLedger,
                         _ => Task.FromResult<IReadOnlyList<AccountPoolView>>(new[] { allBlankPool, mixedPool }),
                         quotaSampleInterval: TimeSpan.Zero, clock: () => now.AddMinutes(3)))
            await windowImporter.RefreshOnceAsync();
        var windowSnapshot = await windowLedger.ReadAsync();
        Ensure(windowSnapshot.ImporterStatus.QuotaHealth == AccountUsageImporterHealth.Degraded
               && windowSnapshot.ImporterStatus.TokenHealth == AccountUsageImporterHealth.Healthy
               && windowSnapshot.QuotaIntegrityFailureCount >= 2
               && windowSnapshot.LatestQuotaSnapshots.Count(view => view.Fact.Value == 33 && view.IsStale) == 2,
            "All-blank or mixed blank PeriodKey quota windows were shown as fresh/healthy or polluted Token integrity.");

        string UsageSourceLine(string requestId, string padding = "") =>
            $$"""{"requestId":"{{requestId}}","requestedModel":"r","provider":"p","model":"m","status":200,"timestamp":"2026-08-01T08:00:00Z","usage":{"inputTokens":1,"outputTokens":1,"totalTokens":2},"padding":"{{padding}}"}""";

        var safeWalRoot = Path.Combine(root, "wal-safe-rescan");
        Directory.CreateDirectory(safeWalRoot);
        var safeWalSource = Path.Combine(safeWalRoot, "usage.jsonl");
        await File.WriteAllTextAsync(safeWalSource, UsageSourceLine("wal-safe") + "\n", new UTF8Encoding(false));
        var safeWalLedger = new AccountUsageLedgerService(safeWalRoot, safeWalSource, () => now);
        await safeWalLedger.IngestSourceAsync();
        File.Delete(safeWalLedger.CursorPath);
        File.Delete(safeWalLedger.CursorRecoveryPath);
        var safeWalRestart = new AccountUsageLedgerService(safeWalRoot, safeWalSource, () => now.AddMinutes(1));
        await safeWalRestart.IngestSourceAsync();
        Ensure(!(await safeWalRestart.ReadAsync()).CoverageGapDetected,
            "Same-generation full source protected by the latest block WAL produced a false coverage gap.");

        var walRewriteRoot = Path.Combine(root, "wal-middle-rewrite");
        Directory.CreateDirectory(walRewriteRoot);
        var walRewriteSource = Path.Combine(walRewriteRoot, "usage.jsonl");
        await File.WriteAllTextAsync(walRewriteSource,
            UsageSourceLine("wal-rw-1", new string('A', 70_000)) + "\n", new UTF8Encoding(false));
        var walRewriteLedger = new AccountUsageLedgerService(walRewriteRoot, walRewriteSource, () => now);
        await walRewriteLedger.IngestSourceAsync();
        await File.AppendAllTextAsync(walRewriteSource, UsageSourceLine("wal-rw-2") + "\n", new UTF8Encoding(false));
        await walRewriteLedger.IngestSourceAsync();
        File.Delete(walRewriteLedger.CursorPath);
        File.Delete(walRewriteLedger.CursorRecoveryPath);
        await using (var rewriteStream = new FileStream(walRewriteSource, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            rewriteStream.Seek(5_000, SeekOrigin.Begin);
            rewriteStream.WriteByte((byte)'B');
            rewriteStream.Flush(true);
        }
        var walRewriteRestart = new AccountUsageLedgerService(walRewriteRoot, walRewriteSource, () => now.AddMinutes(2));
        await walRewriteRestart.IngestSourceAsync();
        Ensure((await walRewriteRestart.ReadAsync()).CoverageGapDetected,
            "A same-inode/same-length rewrite after the first 4 KiB passed cursor-loss recovery without a sticky gap.");

        var walTruncateRoot = Path.Combine(root, "wal-latest-growth");
        Directory.CreateDirectory(walTruncateRoot);
        var walTruncateSource = Path.Combine(walTruncateRoot, "usage.jsonl");
        await File.WriteAllTextAsync(walTruncateSource, UsageSourceLine("wal-t-1") + "\n", new UTF8Encoding(false));
        var walTruncateLedger = new AccountUsageLedgerService(walTruncateRoot, walTruncateSource, () => now);
        await walTruncateLedger.IngestSourceAsync();
        var firstObservedLength = new FileInfo(walTruncateSource).Length;
        await File.AppendAllTextAsync(walTruncateSource, UsageSourceLine("wal-t-2") + "\n", new UTF8Encoding(false));
        await walTruncateLedger.IngestSourceAsync();
        File.Delete(walTruncateLedger.CursorPath);
        File.Delete(walTruncateLedger.CursorRecoveryPath);
        await using (var truncateStream = new FileStream(walTruncateSource, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            truncateStream.SetLength(firstObservedLength);
            truncateStream.Flush(true);
        }
        var walTruncateRestart = new AccountUsageLedgerService(walTruncateRoot, walTruncateSource, () => now.AddMinutes(3));
        await walTruncateRestart.IngestSourceAsync();
        var stickyGap = await walTruncateRestart.ReadAsync();
        Ensure(stickyGap.CoverageGapDetected,
            "The WAL remained pinned to the first marker and missed growth followed by dual-cursor loss and truncation.");

        var unreadTailRoot = Path.Combine(root, "wal-unread-tail-reset");
        Directory.CreateDirectory(unreadTailRoot);
        var unreadTailSource = Path.Combine(unreadTailRoot, "usage.jsonl");
        await File.WriteAllTextAsync(unreadTailSource,
            UsageSourceLine("tail-complete") + "\n" + "{\"requestId\":\"unfinished-AAAA\"", new UTF8Encoding(false));
        var unreadTailLedger = new AccountUsageLedgerService(unreadTailRoot, unreadTailSource, () => now);
        await unreadTailLedger.IngestSourceAsync();
        var unreadBytes = await File.ReadAllBytesAsync(unreadTailSource);
        var replaceAt = Array.LastIndexOf(unreadBytes, (byte)'A');
        await using (var unreadRewrite = new FileStream(unreadTailSource, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            unreadRewrite.Seek(replaceAt, SeekOrigin.Begin);
            unreadRewrite.WriteByte((byte)'B');
            unreadRewrite.Flush(true);
        }
        await unreadTailLedger.IngestSourceAsync();
        Ensure((await unreadTailLedger.ReadAsync()).CoverageGapDetected,
            "Offset<SourceLength unread tail was replaced in place and reset without full-WAL proof or a sticky gap.");

        var concurrentSourceRoot = Path.Combine(root, "source-concurrent-append");
        Directory.CreateDirectory(concurrentSourceRoot);
        var concurrentSourcePath = Path.Combine(concurrentSourceRoot, "usage.jsonl");
        await File.WriteAllTextAsync(concurrentSourcePath, UsageSourceLine("concurrent-1") + "\n", new UTF8Encoding(false));
        var concurrentSourceLedger = new AccountUsageLedgerService(concurrentSourceRoot, concurrentSourcePath, () => now);
        await concurrentSourceLedger.IngestSourceAsync();
        var appendedDuringCapture = false;
        concurrentSourceLedger.SourceLengthCaptured += (_, _) =>
        {
            if (appendedDuringCapture) return;
            appendedDuringCapture = true;
            File.AppendAllText(concurrentSourcePath,
                UsageSourceLine("concurrent-2", new string('C', 70_000)) + "\n", new UTF8Encoding(false));
        };
        await concurrentSourceLedger.IngestSourceAsync();
        await concurrentSourceLedger.IngestSourceAsync();
        var concurrentSourceSnapshot = await concurrentSourceLedger.ReadAsync();
        Ensure(appendedDuringCapture && concurrentSourceSnapshot.StoredAttemptCount == 2
               && !concurrentSourceSnapshot.CoverageGapDetected,
            "Concurrent append across a 64 KiB digest boundary produced a hash/SourceLength mismatch, false gap, or lost event.");

        await File.WriteAllTextAsync(walTruncateRestart.AnomalyPath, "{\"schemaVersion\":", new UTF8Encoding(false));
        var corruptAnomalyRestart = new AccountUsageLedgerService(walTruncateRoot, walTruncateSource, () => now.AddMinutes(4));
        Ensure((await corruptAnomalyRestart.ReadAsync()).CoverageGapDetected,
            "A half-written/corrupt anomaly ledger cleared the independent sticky coverage state on restart.");
        File.Delete(corruptAnomalyRestart.AnomalyPath);
        var missingAnomalyRestart = new AccountUsageLedgerService(walTruncateRoot, walTruncateSource, () => now.AddMinutes(5));
        Ensure((await missingAnomalyRestart.ReadAsync()).CoverageGapDetected,
            "Deleting the anomaly JSONL cleared the independent sticky coverage state on restart.");

        var quotaOnlyRoot = Path.Combine(root, "quota-anomaly-first-source");
        var quotaOnlySource = Path.Combine(quotaOnlyRoot, "usage.jsonl");
        var quotaOnlyLedger = new AccountUsageLedgerService(quotaOnlyRoot, quotaOnlySource, () => now, sourceDisabled: true);
        var quotaOnlyBatch = quotaOnlyLedger.CreateQuotaObservationBatch("quota-only", "quota-account", now, true,
            "quota-only-invalid", "pool:quota-only");
        await quotaOnlyLedger.IngestQuotaSnapshotsAsync(new[]
        {
            quotaOnlyLedger.CreateQuotaSnapshot("quota-only", true, "quota-account", "weekly", "Weekly", 150,
                "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, quotaOnlyBatch, false,
                null, "relay", observationScope: "pool:quota-only")
        });
        Directory.CreateDirectory(quotaOnlyRoot);
        await File.WriteAllTextAsync(quotaOnlySource, UsageSourceLine("quota-only-first-source") + "\n", new UTF8Encoding(false));
        var quotaOnlySourceLedger = new AccountUsageLedgerService(quotaOnlyRoot, quotaOnlySource, () => now.AddMinutes(1));
        var quotaOnlyFirstImport = await quotaOnlySourceLedger.IngestSourceAsync();
        Ensure(!quotaOnlyFirstImport.CoverageGapDetected
               && !(await quotaOnlySourceLedger.ReadAsync()).CoverageGapDetected
               && !quotaOnlySourceLedger.CoverageGapDetected,
            "A quota-only anomaly falsely established prior OpenCodex source history and created a permanent gap.");

        var foreignKeyRoot = Path.Combine(root, "foreign-key-domain");
        var foreignKeyLedger = new AccountUsageLedgerService(foreignKeyRoot, Path.Combine(foreignKeyRoot, "disabled"),
            () => now, sourceDisabled: true);
        await foreignKeyLedger.IngestExecutionsAsync(new[] { Execution("foreign-key-request", "foreign-account") });
        foreignKeyLedger.Dispose();
        foreach (var malformedRow in new[] { "{}", "[]", "{\"schemaVersion\":\"4\"}" })
        {
            var malformedRoot = Path.Combine(root, "identity-malformed-" + Guid.NewGuid().ToString("N"));
            var originalLedger = new AccountUsageLedgerService(malformedRoot, Path.Combine(malformedRoot, "disabled"),
                () => now, sourceDisabled: true);
            await originalLedger.IngestExecutionsAsync(new[] { Execution("old-request", "old-account") });
            var malformedAttemptPath = originalLedger.AttemptLedgerPath;
            var malformedIdentityPath = originalLedger.IdentityKeyPath;
            var malformedManifestPath = originalLedger.IdentityDomainPath;
            originalLedger.Dispose();
            File.Delete(malformedManifestPath);
            CopyIdentityKeyForTest(foreignKeyLedger.IdentityKeyPath, malformedIdentityPath);
            await File.WriteAllTextAsync(malformedAttemptPath, malformedRow + "\n", new UTF8Encoding(false));
            var malformedRestart = new AccountUsageLedgerService(malformedRoot, Path.Combine(malformedRoot, "disabled"),
                () => now, sourceDisabled: true);
            var refused = false;
            try { await malformedRestart.IngestExecutionsAsync(new[] { Execution("new-request", "new-account") }); }
            catch (System.Security.Cryptography.CryptographicException) { refused = true; }
            Ensure(refused,
                "Malformed non-empty ledger history allowed a replacement identity key domain: " + malformedRow);
        }

        var oldDomainRoot = Path.Combine(root, "identity-manifest-swap-old");
        var oldDomainLedger = new AccountUsageLedgerService(oldDomainRoot, Path.Combine(oldDomainRoot, "disabled"),
            () => now, sourceDisabled: true);
        await oldDomainLedger.IngestExecutionsAsync(new[] { Execution("old-domain-request", "old-domain-account") });
        var oldKeyPath = oldDomainLedger.IdentityKeyPath;
        var oldManifestPath = oldDomainLedger.IdentityDomainPath;
        oldDomainLedger.Dispose();
        CopyIdentityKeyForTest(foreignKeyLedger.IdentityKeyPath, oldKeyPath);
        File.Copy(foreignKeyLedger.IdentityDomainPath, oldManifestPath, true);
        var swappedDomainRestart = new AccountUsageLedgerService(oldDomainRoot, Path.Combine(oldDomainRoot, "disabled"),
            () => now, sourceDisabled: true);
        var swappedDomainRefused = false;
        try { await swappedDomainRestart.IngestExecutionsAsync(new[] { Execution("after-swap", "after-swap-account") }); }
        catch (System.Security.Cryptography.CryptographicException) { swappedDomainRefused = true; }
        Ensure(swappedDomainRefused,
            "Replacing both the DPAPI envelope and self-asserted identity manifest bypassed the actual ledger key-domain check.");

        var legacySchemaRoot = Path.Combine(root, "legacy-schema-preflight");
        Directory.CreateDirectory(legacySchemaRoot);
        var legacySchemaLedger = new AccountUsageLedgerService(legacySchemaRoot,
            Path.Combine(legacySchemaRoot, "disabled"), () => now, sourceDisabled: true);
        var legacyAttemptPath = legacySchemaLedger.AttemptLedgerPath;
        const string legacyV3Row = "{\"schemaVersion\":3,\"requestIdentity\":\"legacy\",\"attemptOrdinal\":1}";
        await File.WriteAllTextAsync(legacyAttemptPath, legacyV3Row + "\n", new UTF8Encoding(false));
        CopyIdentityKeyForTest(foreignKeyLedger.IdentityKeyPath, legacySchemaLedger.IdentityKeyPath);
        string foreignKeyId;
        using (var foreignManifestJson = JsonDocument.Parse(await File.ReadAllBytesAsync(foreignKeyLedger.IdentityDomainPath)))
            foreignKeyId = foreignManifestJson.RootElement.GetProperty("keyId").GetString()
                           ?? throw new InvalidOperationException("foreign identity manifest keyId is missing");
        var checkpointMethod = typeof(AccountUsageLedgerService).GetMethod("ComputeIdentityLedgerCheckpoint",
                                   System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                               ?? throw new InvalidOperationException("identity checkpoint method was not found");
        var matchingCheckpoint = checkpointMethod.Invoke(legacySchemaLedger, null) as string
                                 ?? throw new InvalidOperationException("identity checkpoint could not be computed");
        await File.WriteAllTextAsync(legacySchemaLedger.IdentityDomainPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            keyId = foreignKeyId,
            ledgerCheckpoint = matchingCheckpoint,
            updatedAt = now
        }), new UTF8Encoding(false));
        var legacyBytes = await File.ReadAllBytesAsync(legacyAttemptPath);
        AccountLedgerSchemaMigrationRequiredException? legacySchemaError = null;
        try { await legacySchemaLedger.IngestExecutionsAsync(new[] { Execution("must-not-write", "legacy-account") }); }
        catch (AccountLedgerSchemaMigrationRequiredException ex) { legacySchemaError = ex; }
        var legacyAfterBytes = await File.ReadAllBytesAsync(legacyAttemptPath);
        var legacyBackupBytes = legacySchemaError is null ? Array.Empty<byte>() : await File.ReadAllBytesAsync(
            Path.Combine(legacySchemaError.BackupDirectory, Path.GetFileName(legacyAttemptPath)));
        Ensure(legacySchemaError is not null
               && legacySchemaError.DetectedSchema == 3
               && File.Exists(legacySchemaLedger.SchemaRebuildRequiredPath)
               && File.Exists(Path.Combine(legacySchemaError.BackupDirectory, Path.GetFileName(legacyAttemptPath)))
               && legacyBytes.SequenceEqual(legacyAfterBytes)
               && legacyBytes.SequenceEqual(legacyBackupBytes)
               && Directory.GetDirectories(Path.Combine(legacySchemaRoot, "ledger-schema-upgrade-backups")).Length == 1,
            "A real v3 ledger with a matching identity manifest was mixed with v4, changed before backup, or did not produce the explicit rebuild gate.");

        var greenfieldRoot = Path.Combine(root, "schema-greenfield");
        var greenfieldLedger = new AccountUsageLedgerService(greenfieldRoot,
            Path.Combine(greenfieldRoot, "disabled"), () => now, sourceDisabled: true);
        await greenfieldLedger.IngestExecutionsAsync(new[] { Execution("greenfield-v4", "greenfield-account") });
        Ensure((await greenfieldLedger.ReadAsync()).StoredAttemptCount == 1
               && !File.Exists(greenfieldLedger.SchemaRebuildRequiredPath),
            "A genuinely empty ledger was not accepted as schema-v4 greenfield.");
        await File.AppendAllTextAsync(greenfieldLedger.AttemptLedgerPath,
            "{\"schemaVersion\":3,\"requestIdentity\":\"late-legacy\",\"attemptOrdinal\":1}\n",
            new UTF8Encoding(false));
        AccountLedgerSchemaMigrationRequiredException? lateLegacyError = null;
        try { await greenfieldLedger.IngestExecutionsAsync(new[] { Execution("after-late-v3", "greenfield-account") }); }
        catch (AccountLedgerSchemaMigrationRequiredException ex) { lateLegacyError = ex; }
        Ensure(lateLegacyError is not null && lateLegacyError.DetectedSchema == 3
               && File.Exists(Path.Combine(lateLegacyError.BackupDirectory,
                   Path.GetFileName(greenfieldLedger.AttemptLedgerPath))),
            "A v3 row injected after the first clean preflight bypassed the live schema stamp and mixed with v4.");

        async Task AssertQuotaCrashRecovery(string name, bool keepPrepare, int keepFacts, bool keepCommit)
        {
            var transactionRoot = Path.Combine(root, "quota-transaction-" + name);
            var account = "transaction-account-" + name;
            var scope = "pool:transaction-" + name;
            var fetch = "transaction-fetch-" + name;
            var seed = new AccountUsageLedgerService(transactionRoot, Path.Combine(transactionRoot, "disabled"),
                () => now, sourceDisabled: true);
            IReadOnlyList<AccountQuotaSnapshotFact> Candidates(AccountUsageLedgerService service)
            {
                var batch = service.CreateQuotaObservationBatch("provider-transaction", account, now, true, fetch, scope);
                return new[]
                {
                    service.CreateQuotaSnapshot("provider-transaction", true, account, "five_hour", "5h", 21,
                        "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, batch, false,
                        null, "relay", observationScope: scope),
                    service.CreateQuotaSnapshot("provider-transaction", true, account, "weekly", "Weekly", 34,
                        "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, batch, false,
                        null, "relay", observationScope: scope)
                };
            }
            await seed.IngestQuotaSnapshotsAsync(Candidates(seed));
            var prepareRows = await File.ReadAllLinesAsync(seed.QuotaPrepareLedgerPath);
            var factRows = await File.ReadAllLinesAsync(seed.QuotaLedgerPath);
            var commitRows = await File.ReadAllLinesAsync(seed.QuotaCommitLedgerPath);
            if (!keepPrepare) File.Delete(seed.QuotaPrepareLedgerPath);
            else await File.WriteAllTextAsync(seed.QuotaPrepareLedgerPath,
                string.Join("\n", prepareRows.Where(line => line.Length > 0)) + "\n", new UTF8Encoding(false));
            if (keepFacts == 0) File.Delete(seed.QuotaLedgerPath);
            else await File.WriteAllTextAsync(seed.QuotaLedgerPath,
                string.Join("\n", factRows.Where(line => line.Length > 0).Take(keepFacts)) + "\n", new UTF8Encoding(false));
            if (!keepCommit) File.Delete(seed.QuotaCommitLedgerPath);
            else await File.WriteAllTextAsync(seed.QuotaCommitLedgerPath,
                string.Join("\n", commitRows.Where(line => line.Length > 0)) + "\n", new UTF8Encoding(false));
            seed.Dispose();

            var restarted = new AccountUsageLedgerService(transactionRoot, Path.Combine(transactionRoot, "disabled"),
                () => now, sourceDisabled: true);
            await restarted.IngestQuotaSnapshotsAsync(Candidates(restarted));
            var recoveredSnapshot = await restarted.ReadAsync();
            Ensure(recoveredSnapshot.LatestQuotaSnapshots.Count(view => view.Fact.ObservationScope == scope
                                                                        && view.Fact.Availability == AccountQuotaAvailability.Provided) == 2
                   && recoveredSnapshot.QuotaIntegrityFailureCount == 0,
                $"Quota transaction crash point '{name}' did not recover prepare/facts/commit as one complete durable batch.");
        }

        await AssertQuotaCrashRecovery("prepare-only", keepPrepare: true, keepFacts: 0, keepCommit: false);
        await AssertQuotaCrashRecovery("prepare-partial", keepPrepare: true, keepFacts: 1, keepCommit: false);
        await AssertQuotaCrashRecovery("facts-only", keepPrepare: false, keepFacts: 2, keepCommit: false);
        await AssertQuotaCrashRecovery("committed-replay", keepPrepare: true, keepFacts: 2, keepCommit: true);

        Console.WriteLine("LEDGER_PHASE multi_instance_shared_index");
        var multiInstanceRoot = Path.Combine(root, "multi-instance-derived-index");
        using var multiA = new AccountUsageLedgerService(multiInstanceRoot,
            Path.Combine(multiInstanceRoot, "disabled"), () => now, sourceDisabled: true);
        using var multiB = new AccountUsageLedgerService(multiInstanceRoot,
            Path.Combine(multiInstanceRoot, "disabled"), () => now, sourceDisabled: true);
        _ = multiA.CreateQuotaObservationBatch("multi-provider", "multi-account", now, true,
            "multi-key-domain-bootstrap", "pool:multi");
        await Task.WhenAll(multiA.ReadAsync(), multiB.ReadAsync());
        await multiA.IngestExecutionsAsync(new[] { Execution("multi-instance-a", "multi-account") });
        await multiB.IngestExecutionsAsync(new[] { Execution("multi-instance-b", "multi-account") });
        var multiSnapshotA = await multiA.ReadAsync();
        var multiSnapshotB = await multiB.ReadAsync();
        using var multiRestart = new AccountUsageLedgerService(multiInstanceRoot,
            Path.Combine(multiInstanceRoot, "disabled"), () => now.AddMinutes(1), sourceDisabled: true);
        var multiRestartSnapshot = await multiRestart.ReadAsync();
        var multiKeysA = multiSnapshotA.RecentAttempts.Select(item => item.IdempotencyKey)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var multiKeysB = multiSnapshotB.RecentAttempts.Select(item => item.IdempotencyKey)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var multiKeysRestart = multiRestartSnapshot.RecentAttempts.Select(item => item.IdempotencyKey)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        Ensure(multiSnapshotA.StoredAttemptCount == 2 && multiSnapshotB.StoredAttemptCount == 2
               && multiRestartSnapshot.StoredAttemptCount == 2
               && multiKeysA.SequenceEqual(multiKeysB) && multiKeysA.SequenceEqual(multiKeysRestart)
               && multiSnapshotA.Accounts.Single().RequestCount == 2
               && multiSnapshotB.Accounts.Single().RequestCount == 2,
            $"Two initialized ledger instances let the shared derived index suppress a newly observed durable row or diverged after restart: A={multiSnapshotA.StoredAttemptCount}/{multiSnapshotA.Accounts.FirstOrDefault()?.RequestCount}, B={multiSnapshotB.StoredAttemptCount}/{multiSnapshotB.Accounts.FirstOrDefault()?.RequestCount}, restart={multiRestartSnapshot.StoredAttemptCount}/{multiRestartSnapshot.Accounts.FirstOrDefault()?.RequestCount}, keysA={string.Join(',', multiKeysA)}, keysB={string.Join(',', multiKeysB)}, keysR={string.Join(',', multiKeysRestart)}.");

        Console.WriteLine("LEDGER_PHASE checkpoint_content_commitment");
        await AssertProjectionCheckpointContentCommitmentAsync();
        Console.WriteLine("LEDGER_PHASE checkpoint_location_publication");
        await AssertProjectionCheckpointSameKeyRelocationFailsClosedAsync();
        await AssertProjectionCheckpointIsNotPublishedBeforeCurrentLedgerRefreshAsync();
        Console.WriteLine("LEDGER_PHASE authenticated_checkpoint");
        var authenticatedCheckpointRoot = Path.Combine(root, "authenticated-checkpoint");
        using (var authenticatedSeed = new AccountUsageLedgerService(authenticatedCheckpointRoot,
                   Path.Combine(authenticatedCheckpointRoot, "disabled"), () => now, sourceDisabled: true))
        {
            await authenticatedSeed.IngestExecutionsAsync(new[]
            {
                Execution("checkpoint-auth-a", "checkpoint-account"),
                Execution("checkpoint-auth-b", "checkpoint-account")
            });
            Ensure((await authenticatedSeed.ReadAsync()).StoredAttemptCount == 2,
                "Authenticated checkpoint seed projection was not created.");
        }
        var authenticatedCheckpointPath = Path.Combine(authenticatedCheckpointRoot, "account-usage-projection-v1.json");
        var checkpointEnvelope = JsonNode.Parse(await File.ReadAllTextAsync(authenticatedCheckpointPath))!.AsObject();
        var checkpointPayload = Convert.FromBase64String(checkpointEnvelope["payloadBase64"]!.GetValue<string>());
        var checkpointPayloadNode = JsonNode.Parse(checkpointPayload)!.AsObject();
        checkpointPayloadNode["snapshot"]!["storedAttemptCount"] = 999_999;
        var tamperedPayload = JsonSerializer.SerializeToUtf8Bytes(checkpointPayloadNode);
        checkpointEnvelope["payloadBase64"] = Convert.ToBase64String(tamperedPayload);
        checkpointEnvelope["payloadSha256"] = Convert.ToHexString(SHA256.HashData(tamperedPayload));
        await File.WriteAllTextAsync(authenticatedCheckpointPath, checkpointEnvelope.ToJsonString(), new UTF8Encoding(false));
        using (var authenticatedRestart = new AccountUsageLedgerService(authenticatedCheckpointRoot,
                   Path.Combine(authenticatedCheckpointRoot, "disabled"), () => now.AddMinutes(1), sourceDisabled: true))
        {
            var rebuilt = await authenticatedRestart.ReadAsync();
            Ensure(rebuilt.StoredAttemptCount == 2
                   && authenticatedRestart.Diagnostics.CheckpointValidationFailureCount == 1
                   && authenticatedRestart.Diagnostics.ParsedLedgerLineCount >= 2,
                "A coherently SHA-rewritten but unauthenticated checkpoint snapshot was accepted as a second fact source.");
        }
        var authenticatedAttemptIndex = Path.Combine(authenticatedCheckpointRoot, "account-token-attempts-v1.idx");
        using (var stream = new FileStream(authenticatedAttemptIndex, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(stream.Length - 8);
        using (var truncatedIndexRestart = new AccountUsageLedgerService(authenticatedCheckpointRoot,
                   Path.Combine(authenticatedCheckpointRoot, "disabled"), () => now.AddMinutes(2), sourceDisabled: true))
        {
            var rebuilt = await truncatedIndexRestart.ReadAsync();
            Ensure(rebuilt.StoredAttemptCount == 2
                   && truncatedIndexRestart.Diagnostics.CheckpointValidationFailureCount == 1
                   && truncatedIndexRestart.Diagnostics.ParsedLedgerLineCount >= 2,
                "A truncated derived idempotency index was trusted instead of rebuilding from append-only facts.");
        }

        Console.WriteLine("LEDGER_PHASE incremental_projection_performance");
        var performanceRoot = Path.Combine(root, "bounded-append-performance");
        var performanceLedger = new AccountUsageLedgerService(performanceRoot,
            Path.Combine(performanceRoot, "disabled"), () => now, sourceDisabled: true);
        var bulkExecutions = Enumerable.Range(0, 1800)
            .Select(index => Execution($"perf-request-{index:D6}", "perf-account"))
            .ToArray();
        await performanceLedger.IngestExecutionsAsync(bulkExecutions);
        await performanceLedger.ReadAsync();
        var performanceBytes = new FileInfo(performanceLedger.AttemptLedgerPath).Length;
        var performanceBefore = performanceLedger.Diagnostics;
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var appendTimer = Stopwatch.StartNew();
        await performanceLedger.IngestExecutionsAsync(new[] { Execution("perf-request-final", "perf-account") });
        var performanceSnapshot = await performanceLedger.ReadAsync();
        appendTimer.Stop();
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var performanceAfter = performanceLedger.Diagnostics;
        Ensure(performanceBytes > 1_000_000
               && performanceAfter.FullIndexRebuildCount == performanceBefore.FullIndexRebuildCount
               && performanceAfter.LedgerVerificationBytes - performanceBefore.LedgerVerificationBytes < 256 * 1024
               && performanceAfter.AttemptProjectionRowsProcessed - performanceBefore.AttemptProjectionRowsProcessed == 1
               && performanceSnapshot.RecentAttempts.Count <= 80
               && memoryAfter - memoryBefore < 32L * 1024 * 1024
               && appendTimer.Elapsed < TimeSpan.FromSeconds(5),
            $"Active append was not bounded: ledger={performanceBytes}, verifyDelta={performanceAfter.LedgerVerificationBytes - performanceBefore.LedgerVerificationBytes}, projectionDelta={performanceAfter.AttemptProjectionRowsProcessed - performanceBefore.AttemptProjectionRowsProcessed}, recent={performanceSnapshot.RecentAttempts.Count}, memoryDelta={memoryAfter - memoryBefore}, elapsedMs={appendTimer.ElapsedMilliseconds}.");

        var quotaPerformanceFacts = Enumerable.Range(0, 120).Select(index =>
        {
            var observed = now.AddMinutes(index);
            var batch = performanceLedger.CreateQuotaObservationBatch("provider-perf", "quota-perf-account", observed,
                true, $"quota-perf-fetch-{index:D4}", "pool:quota-perf");
            return performanceLedger.CreateQuotaSnapshot("provider-perf", true, "quota-perf-account", "weekly", "Weekly",
                index % 100, "percent_used", AccountQuotaAvailability.Provided, observed, true, observed, false,
                batch, false, null, "relay", observationScope: "pool:quota-perf");
        }).ToArray();
        await performanceLedger.IngestQuotaSnapshotsAsync(quotaPerformanceFacts);
        await performanceLedger.ReadAsync();
        var quotaProjectionBefore = performanceLedger.Diagnostics;
        var finalQuotaObserved = now.AddDays(1);
        var finalQuotaBatch = performanceLedger.CreateQuotaObservationBatch("provider-perf", "quota-perf-account",
            finalQuotaObserved, true, "quota-perf-fetch-final", "pool:quota-perf");
        await performanceLedger.IngestQuotaSnapshotsAsync(new[]
        {
            performanceLedger.CreateQuotaSnapshot("provider-perf", true, "quota-perf-account", "weekly", "Weekly", 55,
                "percent_used", AccountQuotaAvailability.Provided, finalQuotaObserved, true, finalQuotaObserved, false,
                finalQuotaBatch, false, null, "relay", observationScope: "pool:quota-perf")
        });
        var quotaPerformanceSnapshot = await performanceLedger.ReadAsync();
        var quotaProjectionAfter = performanceLedger.Diagnostics;
        Ensure(quotaProjectionAfter.QuotaProjectionRowsProcessed - quotaProjectionBefore.QuotaProjectionRowsProcessed == 3
               && quotaProjectionAfter.QuotaWriteIndexRowsProcessed - quotaProjectionBefore.QuotaWriteIndexRowsProcessed == 0
               && quotaPerformanceSnapshot.LatestQuotaSnapshots.Count(view => view.Fact.ObservationScope == "pool:quota-perf") == 1
               && quotaPerformanceSnapshot.LatestQuotaSnapshots.Single(view => view.Fact.ObservationScope == "pool:quota-perf").Fact.Value == 55,
            "Quota append reprocessed historical facts/prepare/commit rows or failed latest-by-scope projection.");

        var failureChainRoot = Path.Combine(root, "quota-readfailed-chain");
        var failureChainLedger = new AccountUsageLedgerService(failureChainRoot,
            Path.Combine(failureChainRoot, "disabled"), () => now, sourceDisabled: true);
        var successObserved = now.AddDays(2);
        var successBatch = failureChainLedger.CreateQuotaObservationBatch("provider-chain", "account-chain",
            successObserved, true, "chain-success", "pool:chain");
        await failureChainLedger.IngestQuotaSnapshotsAsync(new[]
        {
            failureChainLedger.CreateQuotaSnapshot("provider-chain", true, "account-chain", "weekly", "Weekly", 44,
                "percent_used", AccountQuotaAvailability.Provided, successObserved, true, successObserved, false,
                successBatch, false, null, "relay", observationScope: "pool:chain")
        });
        var failureFacts = Enumerable.Range(1, 2000).Select(index =>
        {
            var observed = successObserved.AddMinutes(index);
            var batch = failureChainLedger.CreateQuotaObservationBatch("provider-chain", "account-chain",
                observed, true, $"chain-failure-{index:D5}", "pool:chain");
            return failureChainLedger.CreateQuotaSnapshot("provider-chain", true, "account-chain", string.Empty,
                "Read failed", null, "unknown", AccountQuotaAvailability.ReadFailed, observed, true, observed, true,
                batch, false, "UpstreamReadFailed", "relay", observationScope: "pool:chain");
        }).ToArray();
        await failureChainLedger.IngestQuotaSnapshotsAsync(failureFacts);
        await failureChainLedger.ReadAsync(quotaSourceReadFailed: true);
        var failureReadBefore = failureChainLedger.Diagnostics;
        var failureReadTimer = Stopwatch.StartNew();
        AccountUsageLedgerSnapshot? failureChainSnapshot = null;
        for (var index = 0; index < 100; index++)
            failureChainSnapshot = await failureChainLedger.ReadAsync(quotaSourceReadFailed: true);
        failureReadTimer.Stop();
        var failureReadAfter = failureChainLedger.Diagnostics;
        Ensure(failureChainSnapshot!.LatestQuotaSnapshots.Single(view => view.Fact.ObservationScope == "pool:chain").Fact.Value == 44
               && failureChainSnapshot.LatestQuotaSnapshots.Single(view => view.Fact.ObservationScope == "pool:chain").IsStale
               && failureReadAfter.QuotaProjectionRowsProcessed == failureReadBefore.QuotaProjectionRowsProcessed
               && failureReadAfter.QuotaFallbackSelectionCount - failureReadBefore.QuotaFallbackSelectionCount >= 100
               && failureReadAfter.QuotaFallbackCandidateRowsExamined == 0
               && failureReadAfter.InMemoryFactObjectCount <= 200
               && failureReadTimer.Elapsed < TimeSpan.FromSeconds(5),
            $"Quota ReadFailed fallback remained linear: projectionDelta={failureReadAfter.QuotaProjectionRowsProcessed - failureReadBefore.QuotaProjectionRowsProcessed}, fallbackDelta={failureReadAfter.QuotaFallbackSelectionCount - failureReadBefore.QuotaFallbackSelectionCount}, retained={failureReadAfter.InMemoryFactObjectCount}, elapsedMs={failureReadTimer.ElapsedMilliseconds}.");

        async Task<JsonElement> RunLedgerPerfChild(string childMode, string childRoot, int? count = null)
        {
            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path is unavailable.");
            var start = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(childMode);
            start.ArgumentList.Add(childRoot);
            if (count is not null) start.ArgumentList.Add(count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ledger performance child.");
            var outputTask = child.StandardOutput.ReadToEndAsync();
            var errorTask = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            var output = await outputTask;
            var error = await errorTask;
            Ensure(child.ExitCode == 0, $"Ledger performance child failed: mode={childMode} exit={child.ExitCode} stderr={error}");
            var jsonLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Last(line => line.TrimStart().StartsWith("{", StringComparison.Ordinal));
            return JsonDocument.Parse(jsonLine).RootElement.Clone();
        }

        Console.WriteLine("LEDGER_PHASE cold_start_100k_append100");
        var coldRoot = Path.Combine(root, "checkpoint-100k");
        var generated100k = await RunLedgerPerfChild("ledger-perf-generate", coldRoot, 100_000);
        var cold100k = await RunLedgerPerfChild("ledger-perf-read", coldRoot);
        var append100AfterCold = await RunLedgerPerfChild("ledger-perf-append", coldRoot, 100);
        var coldDiagnostics = cold100k.GetProperty("diagnostics");
        Ensure(generated100k.GetProperty("marker").GetString() == "LEDGER_PERF_GENERATE_OK"
               && generated100k.GetProperty("StoredAttemptCount").GetInt32() == 100_000
               && cold100k.GetProperty("marker").GetString() == "LEDGER_PERF_READ_OK"
               && cold100k.GetProperty("StoredAttemptCount").GetInt32() == 100_000
               && cold100k.GetProperty("RequestCount").GetInt32() == 100_000
               && coldDiagnostics.GetProperty("CheckpointLoadCount").GetInt64() == 1
               && coldDiagnostics.GetProperty("CheckpointRebuildCount").GetInt64() == 0
               && coldDiagnostics.GetProperty("ParsedLedgerLineCount").GetInt64() == 0
               && coldDiagnostics.GetProperty("InMemoryFactObjectCount").GetInt64() <= 160
               && cold100k.GetProperty("managedDelta").GetInt64() < 128L * 1024 * 1024
               && cold100k.GetProperty("workingSetDelta").GetInt64() < 512L * 1024 * 1024
               && cold100k.GetProperty("elapsedMs").GetInt64() < 10_000
               && append100AfterCold.GetProperty("marker").GetString() == "LEDGER_PERF_APPEND_OK"
               && append100AfterCold.GetProperty("StoredAttemptCount").GetInt32() == 100_100
               && append100AfterCold.GetProperty("derivedBytesWritten").GetInt64() < 2L * 1024 * 1024
               && append100AfterCold.GetProperty("derivedReplacements").GetInt64() == 0
               && append100AfterCold.GetProperty("projectionRows").GetInt64() == 100
               && append100AfterCold.GetProperty("retainedFacts").GetInt64() <= 160
               && append100AfterCold.GetProperty("elapsedMs").GetInt64() < 10_000,
            $"100k checkpoint cold-start bound failed: {cold100k.GetRawText()}");

        performanceLedger.Dispose();
        await File.WriteAllTextAsync(Path.Combine(performanceRoot, "account-usage-projection-v1.json"),
            "{corrupt-checkpoint", new UTF8Encoding(false));
        using var rebuiltProjectionLedger = new AccountUsageLedgerService(performanceRoot,
            Path.Combine(performanceRoot, "disabled"), () => now, sourceDisabled: true);
        var rebuiltProjectionSnapshot = await rebuiltProjectionLedger.ReadAsync();
        Ensure(rebuiltProjectionSnapshot.StoredAttemptCount == 1801
               && rebuiltProjectionLedger.Diagnostics.CheckpointValidationFailureCount == 1
               && rebuiltProjectionLedger.Diagnostics.CheckpointRebuildCount >= 1
               && rebuiltProjectionLedger.Diagnostics.InMemoryFactObjectCount <= 160
               && rebuiltProjectionLedger.Diagnostics.ParsedLedgerLineCount >= 1801,
            "Corrupt derived checkpoint did not fail closed into a bounded streaming rebuild with an identical projection.");

        var malformedStickyRoot = Path.Combine(root, "source-malformed-sticky");
        Directory.CreateDirectory(malformedStickyRoot);
        var malformedStickySource = Path.Combine(malformedStickyRoot, "usage.jsonl");
        await File.WriteAllTextAsync(malformedStickySource,
            "{malformed-json}\n" + UsageSourceLine("sticky-valid") + "\n", new UTF8Encoding(false));
        var malformedStickyLedger = new AccountUsageLedgerService(malformedStickyRoot, malformedStickySource, () => now);
        var malformedFirst = await malformedStickyLedger.IngestSourceAsync();
        await malformedStickyLedger.IngestSourceAsync();
        var malformedStickyRestartLedger = new AccountUsageLedgerService(malformedStickyRoot, malformedStickySource, () => now.AddMinutes(1));
        var malformedRestartSnapshot = await malformedStickyRestartLedger.ReadAsync();
        if (File.Exists(malformedStickyRestartLedger.AnomalyPath)) File.Delete(malformedStickyRestartLedger.AnomalyPath);
        var malformedWithoutAnomaly = await new AccountUsageLedgerService(
            malformedStickyRoot, malformedStickySource, () => now.AddMinutes(2)).ReadAsync();
        Ensure(malformedFirst.BadSourceLineCount == 1
               && malformedRestartSnapshot.TokenIntegrityFailureCount > 0
               && malformedRestartSnapshot.TokenStatus.Contains("Token", StringComparison.Ordinal)
               && malformedWithoutAnomaly.TokenIntegrityFailureCount > 0,
            "A malformed source line was washed back to healthy on no-change/restart or after anomaly JSONL loss.");

        var ambiguousStickyRoot = Path.Combine(root, "ambiguous-anomaly-sticky");
        Directory.CreateDirectory(ambiguousStickyRoot);
        var ambiguousStickySource = Path.Combine(ambiguousStickyRoot, "usage.jsonl");
        const string ambiguousLine = "{\"requestedModel\":\"route\",\"provider\":\"p\",\"model\":\"m\",\"status\":200,\"timestamp\":\"2026-08-01T08:00:00Z\",\"usage\":{\"inputTokens\":1,\"outputTokens\":1,\"totalTokens\":2}}";
        await File.WriteAllTextAsync(ambiguousStickySource, ambiguousLine + "\n" + ambiguousLine + "\n",
            new UTF8Encoding(false));
        var ambiguousStickyLedger = new AccountUsageLedgerService(ambiguousStickyRoot, ambiguousStickySource, () => now);
        var ambiguousImport = await ambiguousStickyLedger.IngestSourceAsync();
        Ensure(ambiguousImport.DuplicateCount == 1
               && !File.Exists(ambiguousStickyLedger.SourceIntegrityStatePath),
            "A normal replay without requestId incorrectly created sticky integrity damage.");
        if (File.Exists(ambiguousStickyLedger.AnomalyPath)) File.Delete(ambiguousStickyLedger.AnomalyPath);
        var ambiguousAfterLoss = await new AccountUsageLedgerService(
            ambiguousStickyRoot, ambiguousStickySource, () => now.AddMinutes(1)).ReadAsync();
        Ensure(ambiguousAfterLoss.TokenIntegrityFailureCount == 0 && !ambiguousAfterLoss.TokenSourceStale,
            "A normal replay without requestId remained permanently degraded after restart.");

        var quotaOnlyImporterStatus = new AccountUsageImporterStatus(
            AccountUsageImporterHealth.Degraded, now, "QuotaIntegrity", "Available", null)
        {
            TokenHealth = AccountUsageImporterHealth.Healthy,
            QuotaHealth = AccountUsageImporterHealth.Degraded,
            TokenLastSuccessAt = now.AddMinutes(-2),
            QuotaLastSuccessAt = now,
            TokenErrorClass = null,
            QuotaErrorClass = "QuotaIntegrity"
        };
        var quotaOnlyUiSnapshot = performanceSnapshot with
        {
            ImporterStatus = quotaOnlyImporterStatus,
            TokenIntegrityFailureCount = 0,
            QuotaIntegrityFailureCount = 2,
            TokenSourceStale = false,
            CoverageGapDetected = false,
            CoverageGapMessage = null
        };
        var tokenUiMethod = typeof(CodexModelManager.Views.PoolManagementView).GetMethod("IsTokenLedgerDegraded",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                            ?? throw new InvalidOperationException("Token ledger UI health projection was not found");
        var quotaOnlyPollutesToken = tokenUiMethod.Invoke(null, new object[] { quotaOnlyUiSnapshot }) is true;
        var tokenIntegrityUiSnapshot = quotaOnlyUiSnapshot with { TokenIntegrityFailureCount = 1 };
        var tokenIntegrityTurnsRed = tokenUiMethod.Invoke(null, new object[] { tokenIntegrityUiSnapshot }) is true;
        Ensure(!quotaOnlyPollutesToken && tokenIntegrityTurnsRed
               && quotaOnlyUiSnapshot.ImporterStatus.TokenLastSuccessAt == now.AddMinutes(-2)
               && quotaOnlyUiSnapshot.ImporterStatus.QuotaLastSuccessAt == now,
            "Pool UI mixed quota-only degradation/time into the Token domain or ignored Token integrity.");

        var shutdownRoot = Path.Combine(root, "bounded-importer-shutdown");
        var shutdownLedger = new AccountUsageLedgerService(shutdownRoot, Path.Combine(shutdownRoot, "disabled"),
            () => now, sourceDisabled: true);
        var readPoolsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadPools = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<AccountPoolView>> BlockingPools(CancellationToken _)
        {
            readPoolsEntered.TrySetResult();
            await releaseReadPools.Task.ConfigureAwait(false);
            return Array.Empty<AccountPoolView>();
        }
        var shutdownImporter = new AccountUsageLedgerImporter(shutdownLedger, BlockingPools,
            interval: TimeSpan.FromMilliseconds(20), quotaSampleInterval: TimeSpan.Zero, clock: () => now);
        shutdownImporter.Start();
        await readPoolsEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var shutdownTimer = Stopwatch.StartNew();
        var firstStop = await shutdownImporter.StopAsync(TimeSpan.FromMilliseconds(100));
        shutdownTimer.Stop();
        var refreshAtTimeout = shutdownImporter.RefreshCount;
        shutdownImporter.Start();
        await Task.Delay(80);
        var noSecondLoop = shutdownImporter.RefreshCount == refreshAtTimeout;
        releaseReadPools.TrySetResult();
        var restartDeadline = Stopwatch.StartNew();
        while (shutdownImporter.RefreshCount < refreshAtTimeout + 2 && restartDeadline.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(20);
        var restartedAfterTimedOutStop = shutdownImporter.RefreshCount >= refreshAtTimeout + 2;
        var finalStop = await shutdownImporter.StopAsync(TimeSpan.FromSeconds(2));
        await shutdownImporter.DisposeAsync();
        Ensure(!firstStop && noSecondLoop && restartedAfterTimedOutStop && finalStop
               && shutdownTimer.Elapsed < TimeSpan.FromSeconds(1),
            $"Importer shutdown/restart boundary failed: first={firstStop}, noSecondLoop={noSecondLoop}, autoRestarted={restartedAfterTimedOutStop}, final={finalStop}, elapsedMs={shutdownTimer.ElapsedMilliseconds}.");

        Console.WriteLine("LEDGER_PHASE snapshot_revision_cas");
        var revisionRoot = Path.Combine(root, "snapshot-revision-cas");
        using var revisionLedger = new AccountUsageLedgerService(revisionRoot,
            Path.Combine(revisionRoot, "disabled"), () => now, sourceDisabled: true);
        var revisionInitial = await revisionLedger.ReadAsync();
        var observedRevisions = new ConcurrentQueue<long>();
        revisionLedger.SnapshotChanged += (_, snapshot) => observedRevisions.Enqueue(snapshot.Revision);
        using var immediateEntered = new ManualResetEventSlim(false);
        using var releaseImmediate = new ManualResetEventSlim(false);
        var barrierProperty = typeof(AccountUsageLedgerService).GetProperty("SnapshotImmediateBarrierForTests",
                                  System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                              ?? throw new InvalidOperationException("Snapshot immediate barrier test seam was not found.");
        barrierProperty.SetValue(revisionLedger, (Action)(() =>
        {
            immediateEntered.Set();
            releaseImmediate.Wait();
        }));
        var immediateMethod = typeof(AccountUsageLedgerService).GetMethod("SetImporterStatusImmediate",
                                  System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                              ?? throw new InvalidOperationException("Immediate importer status method was not found.");
        var staleImmediate = Task.Run(() => immediateMethod.Invoke(revisionLedger, new object?[]
        {
            AccountUsageImporterHealth.Degraded, now, "LifecycleTest", "Stopping", null,
            null, null, null, null, AccountUsageImporterHealth.Healthy,
            AccountUsageImporterHealth.Healthy, "LifecycleTest"
        }));
        Ensure(immediateEntered.Wait(TimeSpan.FromSeconds(2)), "Snapshot revision barrier was not entered.");
        var concurrentNewFact = revisionLedger.IngestExecutionsAsync(new[]
        {
            Execution("revision-new-fact", "revision-account")
        });
        await Task.Delay(80);
        Ensure(!concurrentNewFact.IsCompleted,
            "A newer full snapshot bypassed the atomic immediate snapshot state boundary.");
        releaseImmediate.Set();
        await staleImmediate;
        await concurrentNewFact;
        barrierProperty.SetValue(revisionLedger, null);
        var revisionFinal = await revisionLedger.ReadAsync();
        await Task.Delay(100);
        var revisionSequence = observedRevisions.ToArray();
        Ensure(revisionFinal.StoredAttemptCount == 1
               && revisionFinal.Revision > revisionInitial.Revision
               && revisionSequence.SequenceEqual(revisionSequence.OrderBy(value => value))
               && revisionSequence.Distinct().Count() == revisionSequence.Length
               && revisionSequence.All(value => value <= revisionFinal.Revision),
            "Snapshot publication regressed stored facts or emitted a non-monotonic revision after an old immediate read barrier.");

        Console.WriteLine("LEDGER_PHASE isolated_snapshot_subscribers");
        var subscriberRoot = Path.Combine(root, "isolated-snapshot-subscribers");
        using var subscriberLedger = new AccountUsageLedgerService(subscriberRoot,
            Path.Combine(subscriberRoot, "disabled"), () => now, sourceDisabled: true);
        await subscriberLedger.ReadAsync();
        using var blockedSubscriberEntered = new ManualResetEventSlim(false);
        using var releaseBlockedSubscriber = new ManualResetEventSlim(false);
        var blockedSubscriberRevisions = new ConcurrentQueue<long>();
        var survivingSubscriberRevisions = new ConcurrentQueue<long>();
        EventHandler<AccountUsageLedgerSnapshot> blockedSubscriber = (_, snapshot) =>
        {
            blockedSubscriberRevisions.Enqueue(snapshot.Revision);
            blockedSubscriberEntered.Set();
            releaseBlockedSubscriber.Wait();
        };
        EventHandler<AccountUsageLedgerSnapshot> throwingSubscriber = (_, _) => throw new InvalidOperationException("subscriber fault");
        EventHandler<AccountUsageLedgerSnapshot> survivingSubscriber = (_, snapshot) =>
            survivingSubscriberRevisions.Enqueue(snapshot.Revision);
        subscriberLedger.SnapshotChanged += blockedSubscriber;
        subscriberLedger.SnapshotChanged += throwingSubscriber;
        subscriberLedger.SnapshotChanged += survivingSubscriber;
        await subscriberLedger.SetImporterStatusAsync(AccountUsageImporterHealth.Healthy, now, null, null,
            tokenHealth: AccountUsageImporterHealth.Healthy, quotaHealth: AccountUsageImporterHealth.Healthy);
        Ensure(blockedSubscriberEntered.Wait(TimeSpan.FromSeconds(2)), "First isolated subscriber did not block as arranged.");
        for (var index = 0; index < 8; index++)
            await subscriberLedger.SetImporterStatusAsync(AccountUsageImporterHealth.Healthy, now.AddSeconds(index + 1),
                null, null, tokenHealth: AccountUsageImporterHealth.Healthy,
                quotaHealth: AccountUsageImporterHealth.Healthy);
        var subscriberLatestRevision = subscriberLedger.LastSnapshot.Revision;
        var subscriberDeadline = Stopwatch.StartNew();
        while (!survivingSubscriberRevisions.Contains(subscriberLatestRevision)
               && subscriberDeadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        var blockedDiagnostics = subscriberLedger.Diagnostics;
        Ensure(survivingSubscriberRevisions.Contains(subscriberLatestRevision)
               && blockedDiagnostics.SnapshotSubscriberCount == 3
               && blockedDiagnostics.ActiveSnapshotSubscriberWorkers <= 3
               && blockedDiagnostics.PendingSnapshotSubscriberMailboxes <= 1,
            "A blocked or throwing subscriber starved a surviving subscriber or created an unbounded mailbox/worker set.");
        releaseBlockedSubscriber.Set();
        var blockedDrainDeadline = Stopwatch.StartNew();
        while (!blockedSubscriberRevisions.Contains(subscriberLatestRevision)
               && blockedDrainDeadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
        Ensure(blockedSubscriberRevisions.Contains(subscriberLatestRevision)
               && blockedSubscriberRevisions.Count <= 2,
            "The released blocked subscriber replayed an unbounded backlog instead of receiving only the latest snapshot.");
        subscriberLedger.SnapshotChanged -= blockedSubscriber;
        subscriberLedger.SnapshotChanged -= throwingSubscriber;
        subscriberLedger.SnapshotChanged -= survivingSubscriber;

        Console.WriteLine("LEDGER_PHASE hard_stop_subscriber_anomaly_lock");
        var hardStopLedger = shutdownLedger;
        using var handlerEntered = new ManualResetEventSlim(false);
        using var releaseHandler = new ManualResetEventSlim(false);
        hardStopLedger.SnapshotChanged += (_, _) =>
        {
            handlerEntered.Set();
            releaseHandler.Wait();
        };
        await hardStopLedger.ReadAsync();
        Ensure(handlerEntered.Wait(TimeSpan.FromSeconds(2)), "Blocking snapshot subscriber was not invoked.");
        await using var heldAnomalyLock = new FileStream(hardStopLedger.AnomalyLockPath,
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using var hardStopImporter = new AccountUsageLedgerImporter(hardStopLedger,
            _ => Task.FromResult<IReadOnlyList<AccountPoolView>>(Array.Empty<AccountPoolView>()),
            interval: TimeSpan.FromMilliseconds(10), quotaSampleInterval: TimeSpan.Zero, clock: () => now);
        hardStopImporter.Start();
        await Task.Delay(80);
        var hardStopTimer = Stopwatch.StartNew();
        var hardStopResult = await hardStopImporter.StopAsync(TimeSpan.FromMilliseconds(100));
        hardStopTimer.Stop();
        var hardStopStatus = hardStopLedger.ImporterStatus;
        Ensure(!hardStopResult
               && hardStopTimer.Elapsed < TimeSpan.FromMilliseconds(500)
               && hardStopStatus.Health != AccountUsageImporterHealth.Stopped
               && hardStopStatus.StoppedReason == "ShutdownTimeout"
               && hardStopStatus.LifecycleErrorClass == "ShutdownTimeout"
               && hardStopStatus.TokenErrorClass != "ShutdownTimeout",
            $"StopAsync was not hard bounded while anomaly lock/subscriber were blocked: result={hardStopResult}, elapsedMs={hardStopTimer.ElapsedMilliseconds}, health={hardStopStatus.Health}, reason={hardStopStatus.StoppedReason}.");
        heldAnomalyLock.Dispose();
        releaseHandler.Set();
        Ensure(await hardStopImporter.StopAsync(TimeSpan.FromSeconds(2)),
            "Importer did not complete after the test-only anomaly lock and subscriber were released.");

        Console.WriteLine("LEDGER_PHASE token_failure_quota_preservation");
        var splitHealthRoot = Path.Combine(root, "token-failure-quota-preservation");
        var badSourceDirectory = Path.Combine(splitHealthRoot, "source-is-directory");
        Directory.CreateDirectory(badSourceDirectory);
        var splitHealthLedger = new AccountUsageLedgerService(splitHealthRoot, badSourceDirectory, () => now);
        await splitHealthLedger.ReadAsync();
        var priorQuotaSuccess = now.AddMinutes(-3);
        await splitHealthLedger.SetImporterStatusAsync(AccountUsageImporterHealth.Healthy, priorQuotaSuccess,
            null, null, tokenSourceStale: false, tokenLastSuccessAt: now.AddMinutes(-2),
            quotaLastSuccessAt: priorQuotaSuccess, tokenHealth: AccountUsageImporterHealth.Healthy,
            quotaHealth: AccountUsageImporterHealth.Healthy);
        var splitPoolsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSplitPools = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<AccountPoolView>> SplitPools(CancellationToken _)
        {
            splitPoolsEntered.TrySetResult();
            await releaseSplitPools.Task.ConfigureAwait(false);
            return Array.Empty<AccountPoolView>();
        }
        await using var splitHealthImporter = new AccountUsageLedgerImporter(splitHealthLedger, SplitPools,
            quotaSampleInterval: TimeSpan.Zero, clock: () => now);
        var splitRefresh = splitHealthImporter.RefreshOnceAsync();
        await splitPoolsEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var splitIntermediate = splitHealthLedger.LastSnapshot.ImporterStatus;
        Ensure(splitIntermediate.TokenHealth == AccountUsageImporterHealth.Degraded
               && splitIntermediate.QuotaHealth == AccountUsageImporterHealth.Healthy
               && splitIntermediate.QuotaLastSuccessAt == priorQuotaSuccess
               && splitIntermediate.QuotaErrorClass is null,
            "Token source failure overwrote healthy quota state before an independently blocked quota read completed.");
        releaseSplitPools.TrySetResult();
        await splitRefresh;

        Console.WriteLine($"LEDGER_PERF_100K_OK elapsedMs={cold100k.GetProperty("elapsedMs").GetInt64()} managedDelta={cold100k.GetProperty("managedDelta").GetInt64()} workingSetDelta={cold100k.GetProperty("workingSetDelta").GetInt64()} retainedFacts={coldDiagnostics.GetProperty("InMemoryFactObjectCount").GetInt64()} parsedRows={coldDiagnostics.GetProperty("ParsedLedgerLineCount").GetInt64()} checkpointLoads={coldDiagnostics.GetProperty("CheckpointLoadCount").GetInt64()}");
        Console.WriteLine($"LEDGER_APPEND_100_OK elapsedMs={append100AfterCold.GetProperty("elapsedMs").GetInt64()} derivedBytes={append100AfterCold.GetProperty("derivedBytesWritten").GetInt64()} replacements={append100AfterCold.GetProperty("derivedReplacements").GetInt64()} projectionRows={append100AfterCold.GetProperty("projectionRows").GetInt64()} retainedFacts={append100AfterCold.GetProperty("retainedFacts").GetInt64()}");
        Console.WriteLine($"QUOTA_FAILURE_CHAIN_OK batches=2000 reads=100 elapsedMs={failureReadTimer.ElapsedMilliseconds} projectionDelta={failureReadAfter.QuotaProjectionRowsProcessed - failureReadBefore.QuotaProjectionRowsProcessed} fallbackDelta={failureReadAfter.QuotaFallbackSelectionCount - failureReadBefore.QuotaFallbackSelectionCount} retainedFacts={failureReadAfter.InMemoryFactObjectCount}");
        Console.WriteLine("THIRD_ROUND_LEDGER_MATRIX_UNIT_TESTS_OK multi_instance_exact_projection authenticated_checkpoint_rebuild truncated_index_rebuild monotonic_snapshot_revision isolated_latest_only_subscribers lifecycle_health_isolation duplicate_adapter_importer_retention opencodex_authoritative_failures model_directory_roster_isolation incremental_disk_hash_index append100_bounded");
        Console.WriteLine("SECOND_ROUND_LEDGER_MATRIX_UNIT_TESTS_OK request_hmac middle_rewrite no_change_fast_path scope_isolation archived_partial_tail durable_gap token_io_quota_success health_domains roster_completeness quota_window_structure block_wal cursor_unread_tail sticky_integrity concurrent_64k_append source_history_isolation identity_domain_fail_closed schema_v3_preflight_backup live_schema_recheck greenfield_v4 quota_transaction_crash_restart active_append_bounded incremental_attempt_projection incremental_quota_projection top80_memory_bound source_malformed_restart anomaly_only_restart ui_dual_health_domains shutdown_timeout_restart hard_stop_subscriber_anomaly_lock token_error_quota_preserved duplicate_rosters backend_http_json_faults");
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static async Task AssertProjectionCheckpointSameKeyRelocationFailsClosedAsync()
{
    var root = CreateOwnedTestRoot("cmm-ledger-checkpoint-relocation");
    var sourceRoot = Path.Combine(root, "source");
    var targetRoot = Path.Combine(root, "target");
    var now = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(targetRoot);
    try
    {
        string sourceKeyPath;
        string sourceCheckpointPath;
        using (var source = new AccountUsageLedgerService(sourceRoot, Path.Combine(sourceRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            await source.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(8101) });
            Ensure((await source.ReadAsync()).StoredAttemptCount == 1,
                "Same-key relocation source checkpoint was not seeded.");
            sourceKeyPath = source.IdentityKeyPath;
            sourceCheckpointPath = source.ProjectionCheckpointPath;
        }

        CopyIdentityKeyForTest(sourceKeyPath, Path.Combine(targetRoot, "account-ledger-identity.key"));
        string targetAttemptKey;
        using (var target = new AccountUsageLedgerService(targetRoot, Path.Combine(targetRoot, "disabled"),
                   () => now.AddSeconds(1), sourceDisabled: true))
        {
            await target.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(8102) });
            var targetSnapshot = await target.ReadAsync();
            Ensure(targetSnapshot.StoredAttemptCount == 1,
                "Same-key relocation target checkpoint was not seeded.");
            targetAttemptKey = targetSnapshot.RecentAttempts.Single().IdempotencyKey;
        }

        // Align all authenticated derived caches as well as the DPAPI/HMAC identity domain. The only
        // invalid relationship left is that the copied checkpoint names sourceRoot ledger segments
        // while the current service owns targetRoot segments.
        foreach (var sourceIndex in Directory.EnumerateFiles(sourceRoot, "*.idx", SearchOption.TopDirectoryOnly))
            File.Copy(sourceIndex, Path.Combine(targetRoot, Path.GetFileName(sourceIndex)), true);
        File.Copy(sourceCheckpointPath, Path.Combine(targetRoot, "account-usage-projection-v1.json"), true);

        using var restart = new AccountUsageLedgerService(targetRoot, Path.Combine(targetRoot, "disabled"),
            () => now.AddMinutes(1), sourceDisabled: true);
        var rebuilt = await restart.ReadAsync();
        Ensure(restart.Diagnostics.CheckpointLoadCount == 0
               && restart.Diagnostics.CheckpointValidationFailureCount == 1
               && restart.Diagnostics.ParsedLedgerLineCount >= 1
               && rebuilt.StoredAttemptCount == 1
               && rebuilt.RecentAttempts.Single().IdempotencyKey == targetAttemptKey,
            "A same-HMAC-key checkpoint relocated from another data directory was read as a current-ledger cache instead of failing closed and rebuilding the target ledger.");
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static async Task AssertProjectionCheckpointIsNotPublishedBeforeCurrentLedgerRefreshAsync()
{
    var root = CreateOwnedTestRoot("cmm-ledger-checkpoint-publish");
    var now = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
    try
    {
        long checkpointRevision;
        using (var seed = new AccountUsageLedgerService(root, Path.Combine(root, "disabled"),
                   () => now, sourceDisabled: true))
        {
            await seed.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(8201) });
            var seeded = await seed.ReadAsync();
            Ensure(seeded.StoredAttemptCount == 1 && File.Exists(seed.ProjectionCheckpointPath),
                "Checkpoint publication-order seed failed.");
            checkpointRevision = seeded.Revision;
        }

        using var restart = new AccountUsageLedgerService(root, Path.Combine(root, "disabled"),
            () => now.AddMinutes(1), sourceDisabled: true);
        using var checkpointRestored = new ManualResetEventSlim(false);
        using var releaseRefresh = new ManualResetEventSlim(false);
        var barrierProperty = typeof(AccountUsageLedgerService).GetProperty(
                                  "ProjectionCheckpointRestoredBarrierForTests",
                                  System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                              ?? throw new InvalidOperationException("Projection checkpoint restore barrier test seam was not found.");
        barrierProperty.SetValue(restart, (Action)(() =>
        {
            checkpointRestored.Set();
            releaseRefresh.Wait();
        }));

        var read = Task.Run(async () => await restart.ReadAsync());
        try
        {
            Ensure(checkpointRestored.Wait(TimeSpan.FromSeconds(3)),
                "Checkpoint restore did not reach the pre-refresh publication barrier.");
            Ensure(restart.Diagnostics.CheckpointLoadCount == 1
                   && restart.LastSnapshot.StoredAttemptCount == 0
                   && restart.LastSnapshot.Revision == 0
                   && !read.IsCompleted,
                "Checkpoint-derived snapshot became concurrently visible before current-ledger refresh completed.");
        }
        finally
        {
            releaseRefresh.Set();
        }

        var refreshed = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(refreshed.StoredAttemptCount == 1
               && refreshed.Revision > checkpointRevision
               && restart.LastSnapshot.Revision == refreshed.Revision
               && restart.LastSnapshot.StoredAttemptCount == refreshed.StoredAttemptCount,
            "Final PublishSnapshot did not atomically expose the current-ledger projection after checkpoint restore.");
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static async Task AssertProjectionCheckpointContentCommitmentAsync()
{
    var root = CreateOwnedTestRoot("cmm-ledger-checkpoint-content");
    var now = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
    try
    {
        async Task<(string LedgerPath, string CheckpointPath, string AttemptKey)> SeedAsync(
            string name, params int[] indexes)
        {
            var ledgerRoot = Path.Combine(root, name);
            using var ledger = new AccountUsageLedgerService(ledgerRoot, Path.Combine(ledgerRoot, "disabled"),
                () => now, sourceDisabled: true);
            await ledger.IngestExecutionsAsync(indexes.Select(LedgerPerformanceExecution).ToArray());
            var snapshot = await ledger.ReadAsync();
            Ensure(snapshot.StoredAttemptCount == indexes.Length && File.Exists(ledger.ProjectionCheckpointPath),
                $"Checkpoint commitment seed failed for {name}.");
            return (ledger.AttemptLedgerPath, ledger.ProjectionCheckpointPath,
                snapshot.RecentAttempts.OrderBy(item => item.AttemptOrdinal).First().IdempotencyKey);
        }

        static int FindLinePayloadHash(byte[] bytes, int lineIndex)
        {
            var start = 0;
            for (var index = 0; index < lineIndex; index++)
            {
                var next = bytes.AsSpan(start).IndexOf((byte)'\n');
                if (next < 0) return -1;
                start += next + 1;
            }
            var marker = Encoding.UTF8.GetBytes("\"payloadHash\":\"");
            var within = bytes.AsSpan(start).IndexOf(marker);
            return within < 0 ? -1 : start + within + marker.Length;
        }

        async Task<AccountUsageLedgerSnapshot> RestartAsync(string ledgerRoot,
            Action<AccountUsageLedgerService>? assertBeforeRead = null)
        {
            using var restart = new AccountUsageLedgerService(ledgerRoot, Path.Combine(ledgerRoot, "disabled"),
                () => now.AddMinutes(1), sourceDisabled: true);
            assertBeforeRead?.Invoke(restart);
            return await restart.ReadAsync();
        }

        var sameLength = await SeedAsync("same-inode-same-length", Enumerable.Range(0, 100).ToArray());
        var sameLengthRoot = Path.GetDirectoryName(sameLength.LedgerPath)!;
        var originalMtime = File.GetLastWriteTimeUtc(sameLength.LedgerPath);
        var sameLengthBytes = await File.ReadAllBytesAsync(sameLength.LedgerPath);
        var payloadHashOffset = FindLinePayloadHash(sameLengthBytes, 50);
        Ensure(payloadHashOffset > 4096 && payloadHashOffset < sameLengthBytes.Length - 256,
            "Same-length checkpoint tamper was not placed outside the legacy prefix/tail metadata windows.");
        await using (var stream = new FileStream(sameLength.LedgerPath, FileMode.Open, FileAccess.ReadWrite,
                         FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Position = payloadHashOffset;
            var current = stream.ReadByte();
            stream.Position = payloadHashOffset;
            stream.WriteByte(current == (byte)'A' ? (byte)'B' : (byte)'A');
            stream.Flush(true);
        }
        File.SetLastWriteTimeUtc(sameLength.LedgerPath, originalMtime);
        using (var sameLengthRestart = new AccountUsageLedgerService(sameLengthRoot,
                   Path.Combine(sameLengthRoot, "disabled"), () => now.AddMinutes(1), sourceDisabled: true))
        {
            var rebuilt = await sameLengthRestart.ReadAsync();
            Ensure(sameLengthRestart.Diagnostics.CheckpointLoadCount == 0
                   && sameLengthRestart.Diagnostics.CheckpointValidationFailureCount == 1
                   && sameLengthRestart.Diagnostics.ParsedLedgerLineCount >= 100
                   && rebuilt.StoredAttemptCount < 100,
                "Same-inode/same-length historical rewrite with restored mtime was accepted by the projection checkpoint.");
        }

        var crossA = await SeedAsync("cross-ledger-a", 201);
        var crossB = await SeedAsync("cross-ledger-b", 202);
        File.Copy(crossA.CheckpointPath, crossB.CheckpointPath, true);
        File.Copy(crossA.LedgerPath + ".append-seal.json", crossB.LedgerPath + ".append-seal.json", true);
        var crossBRoot = Path.GetDirectoryName(crossB.LedgerPath)!;
        using (var crossRestart = new AccountUsageLedgerService(crossBRoot, Path.Combine(crossBRoot, "disabled"),
                   () => now.AddMinutes(1), sourceDisabled: true))
        {
            var rebuilt = await crossRestart.ReadAsync();
            Ensure(crossRestart.Diagnostics.CheckpointValidationFailureCount == 1
                   && crossRestart.Diagnostics.CheckpointLoadCount == 0
                   && rebuilt.StoredAttemptCount == 1
                   && rebuilt.RecentAttempts.Single().IdempotencyKey == crossB.AttemptKey,
                "A checkpoint/seal copied from a different ledger domain was accepted or changed the target facts.");
        }

        var truncated = await SeedAsync("truncated-ledger", Enumerable.Range(300, 20).ToArray());
        var truncatedBytes = await File.ReadAllBytesAsync(truncated.LedgerPath);
        var priorNewline = truncatedBytes.AsSpan(0, truncatedBytes.Length - 1).LastIndexOf((byte)'\n');
        Ensure(priorNewline >= 0, "Truncation checkpoint seed has no complete prior row.");
        await using (var stream = new FileStream(truncated.LedgerPath, FileMode.Open, FileAccess.Write,
                         FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.SetLength(priorNewline + 1L);
            stream.Flush(true);
        }
        var truncatedRoot = Path.GetDirectoryName(truncated.LedgerPath)!;
        using (var truncatedRestart = new AccountUsageLedgerService(truncatedRoot,
                   Path.Combine(truncatedRoot, "disabled"), () => now.AddMinutes(1), sourceDisabled: true))
        {
            var rebuilt = await truncatedRestart.ReadAsync();
            Ensure(truncatedRestart.Diagnostics.CheckpointValidationFailureCount == 1
                   && truncatedRestart.Diagnostics.CheckpointLoadCount == 0
                   && rebuilt.StoredAttemptCount == 19,
                "A truncated ledger was accepted by an older projection checkpoint.");
        }

        var appended = await SeedAsync("append-after-checkpoint", 401);
        var appendedRoot = Path.GetDirectoryName(appended.LedgerPath)!;
        var derivedFiles = Directory.EnumerateFiles(appendedRoot, "*.idx", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        using (var appendWriter = new AccountUsageLedgerService(appendedRoot,
                   Path.Combine(appendedRoot, "disabled"), () => now.AddSeconds(1), sourceDisabled: true))
            await appendWriter.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(402) });
        foreach (var item in derivedFiles)
            await File.WriteAllBytesAsync(Path.Combine(appendedRoot, item.Key!), item.Value);
        using (var appendRestart = new AccountUsageLedgerService(appendedRoot,
                   Path.Combine(appendedRoot, "disabled"), () => now.AddMinutes(1), sourceDisabled: true))
        {
            var incremented = await appendRestart.ReadAsync();
            Ensure(appendRestart.Diagnostics.CheckpointLoadCount == 1
                   && appendRestart.Diagnostics.CheckpointValidationFailureCount == 0
                   && appendRestart.Diagnostics.ParsedLedgerLineCount == 1
                   && incremented.StoredAttemptCount == 2,
                "A valid append after an authenticated checkpoint did not restore and increment exactly once.");
        }

        var sealTamper = await SeedAsync("seal-tamper-sha-rewrite", 501, 502);
        var sealPath = sealTamper.LedgerPath + ".append-seal.json";
        var sealNode = JsonNode.Parse(await File.ReadAllTextAsync(sealPath))!.AsObject();
        sealNode["updatedAt"] = "2026-08-01T09:30:00.0000000+00:00";
        await File.WriteAllTextAsync(sealPath, sealNode.ToJsonString(), new UTF8Encoding(false));
        var sealCanonical = new JsonObject
        {
            ["schemaVersion"] = sealNode["schemaVersion"]!.DeepClone(),
            ["fileIdentity"] = sealNode["fileIdentity"]!.DeepClone(),
            ["startLength"] = sealNode["startLength"]!.DeepClone(),
            ["length"] = sealNode["length"]!.DeepClone(),
            ["previousChainDigest"] = sealNode["previousChainDigest"]!.DeepClone(),
            ["appendedHash"] = sealNode["appendedHash"]!.DeepClone(),
            ["chainDigest"] = sealNode["chainDigest"]!.DeepClone(),
            ["updatedAt"] = sealNode["updatedAt"]!.DeepClone()
        };
        var rewrittenSealCommitment = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sealCanonical.ToJsonString())));
        var checkpointEnvelope = JsonNode.Parse(await File.ReadAllTextAsync(sealTamper.CheckpointPath))!.AsObject();
        var checkpointPayload = JsonNode.Parse(Convert.FromBase64String(
            checkpointEnvelope["payloadBase64"]!.GetValue<string>()))!.AsObject();
        var attemptSegments = checkpointPayload["attemptIndex"]!["segments"]!.AsObject();
        attemptSegments.First().Value!.AsObject()["appendSealCommitment"] = rewrittenSealCommitment;
        var rewrittenPayload = Encoding.UTF8.GetBytes(checkpointPayload.ToJsonString());
        checkpointEnvelope["payloadBase64"] = Convert.ToBase64String(rewrittenPayload);
        checkpointEnvelope["payloadSha256"] = Convert.ToHexString(SHA256.HashData(rewrittenPayload));
        await File.WriteAllTextAsync(sealTamper.CheckpointPath, checkpointEnvelope.ToJsonString(),
            new UTF8Encoding(false));
        var sealTamperRoot = Path.GetDirectoryName(sealTamper.LedgerPath)!;
        using (var sealRestart = new AccountUsageLedgerService(sealTamperRoot,
                   Path.Combine(sealTamperRoot, "disabled"), () => now.AddMinutes(1), sourceDisabled: true))
        {
            var rebuilt = await sealRestart.ReadAsync();
            Ensure(sealRestart.Diagnostics.CheckpointValidationFailureCount == 1
                   && sealRestart.Diagnostics.CheckpointLoadCount == 0
                   && rebuilt.StoredAttemptCount == 2,
                "Seal tamper plus ordinary checkpoint SHA rewrite bypassed the identity-key HMAC.");
        }

        var identityA = await SeedAsync("checkpoint-identity-a", 601);
        var identityB = await SeedAsync("checkpoint-identity-b", 602);
        var identityAKey = Path.Combine(Path.GetDirectoryName(identityA.LedgerPath)!, "account-ledger-identity.key");
        var identityBKey = Path.Combine(Path.GetDirectoryName(identityB.LedgerPath)!, "account-ledger-identity.key");
        CopyIdentityKeyForTest(identityBKey, identityAKey);
        var identityRejected = false;
        try { _ = await RestartAsync(Path.GetDirectoryName(identityA.LedgerPath)!); }
        catch (AccountLedgerIdentityKeyUnavailableException) { identityRejected = true; }
        Ensure(identityRejected, "Replacing the checkpoint identity-key envelope did not fail closed.");

        var noChange = await SeedAsync("checkpoint-no-change", Enumerable.Range(700, 50).ToArray());
        var noChangeRoot = Path.GetDirectoryName(noChange.LedgerPath)!;
        using (var noChangeRestart = new AccountUsageLedgerService(noChangeRoot,
                   Path.Combine(noChangeRoot, "disabled"), () => now.AddMinutes(1), sourceDisabled: true))
        {
            var restored = await noChangeRestart.ReadAsync();
            Ensure(noChangeRestart.Diagnostics.CheckpointLoadCount == 1
                   && noChangeRestart.Diagnostics.CheckpointValidationFailureCount == 0
                   && noChangeRestart.Diagnostics.ParsedLedgerLineCount == 0
                   && noChangeRestart.Diagnostics.LedgerVerificationBytes >= new FileInfo(noChange.LedgerPath).Length
                   && restored.StoredAttemptCount == 50,
                "A normal no-change checkpoint did not verify the full parsed prefix without reprojecting rows.");
        }
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static RuntimeRouteExecution LedgerPerformanceExecution(int index)
{
    var request = $"perf-{index:D8}";
    return new RuntimeRouteExecution(
        request, "route", 200, 1, DateTimeOffset.Parse("2026-08-01T08:00:00Z"),
        RuntimeExecutionOutcome.Succeeded, null, null, RuntimeLogSelectionBasis.Timestamp, index,
        new[]
        {
            new RuntimeRouteAttempt(1, "provider-perf", "provider-perf", "account-perf", "safe",
                RuntimeAccountIdentitySource.ExplicitAccountId, "model-perf", 200, 1, null, null,
                RuntimeFailoverReason.None, true, RuntimeAttemptSelectionEvidence.SingleAttempt,
                new AttemptTokenUsageFact(1, null, null, null, 1, null, 2,
                    TokenTotalSource.Upstream, TokenTotalValidationState.Valid, "ok", "direct"), "account-perf")
        }, null, request);
}

static async Task AssertLedgerRecoveryRegressionsAsync()
{
    var root = CreateOwnedTestRoot("cmm-ledger-recovery");
    var now = DateTimeOffset.Parse("2026-08-01T08:00:00Z");
    try
    {
        var projectionRoot = Path.Combine(root, "projection-retry");
        using (var seed = new AccountUsageLedgerService(projectionRoot, Path.Combine(projectionRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            await seed.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(1), LedgerPerformanceExecution(2) });
            if (File.Exists(seed.ProjectionCheckpointPath)) File.Delete(seed.ProjectionCheckpointPath);
        }
        using (var retry = new AccountUsageLedgerService(projectionRoot, Path.Combine(projectionRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            var barrier = typeof(AccountUsageLedgerService).GetProperty(
                              "ProjectionRowAcceptedBarrierForTests",
                              System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("Projection row test barrier was not found.");
            var interrupted = false;
            barrier.SetValue(retry, (Action)(() =>
            {
                if (interrupted) return;
                interrupted = true;
                throw new OperationCanceledException("simulated projection interruption");
            }));
            try { _ = await retry.ReadAsync(); }
            catch (OperationCanceledException) { }
            Ensure(interrupted, "Projection interruption hook was not reached.");
            barrier.SetValue(retry, null);
            var recovered = await retry.ReadAsync();
            Ensure(recovered.StoredAttemptCount == 2
                   && recovered.Accounts.Single().RequestCount == 2
                   && recovered.TokenIntegrityFailureCount == 0,
                "Interrupted attempt projection was counted twice or did not rebuild cleanly.");
        }

        var validTailRoot = Path.Combine(root, "valid-tail-no-newline");
        string validTailPath;
        using (var seed = new AccountUsageLedgerService(validTailRoot, Path.Combine(validTailRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            await seed.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(10) });
            validTailPath = seed.AttemptLedgerPath;
        }
        var validTailBytes = await File.ReadAllBytesAsync(validTailPath);
        Ensure(validTailBytes.Length > 0 && validTailBytes[^1] == (byte)'\n', "Seed ledger did not end in a newline.");
        await File.WriteAllBytesAsync(validTailPath, validTailBytes[..^1]);
        using (var repaired = new AccountUsageLedgerService(validTailRoot, Path.Combine(validTailRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            var snapshot = await repaired.ReadAsync();
            var repairedBytes = await File.ReadAllBytesAsync(validTailPath);
            Ensure(snapshot.StoredAttemptCount == 1 && repairedBytes[^1] == (byte)'\n',
                "A complete JSON tail without a newline was discarded instead of repaired.");
        }

        var truncatedTailRoot = Path.Combine(root, "truncated-tail");
        string truncatedTailPath;
        long completeLength;
        using (var seed = new AccountUsageLedgerService(truncatedTailRoot, Path.Combine(truncatedTailRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            await seed.IngestExecutionsAsync(new[] { LedgerPerformanceExecution(20) });
            truncatedTailPath = seed.AttemptLedgerPath;
            completeLength = new FileInfo(truncatedTailPath).Length;
        }
        await File.AppendAllTextAsync(truncatedTailPath, "{\"schemaVersion\":4", new UTF8Encoding(false));
        using (var repaired = new AccountUsageLedgerService(truncatedTailRoot, Path.Combine(truncatedTailRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            var snapshot = await repaired.ReadAsync();
            Ensure(snapshot.StoredAttemptCount == 1
                   && new FileInfo(truncatedTailPath).Length == completeLength,
                "A half-written JSONL tail was not truncated back to the last durable row.");
        }

        var reorderRoot = Path.Combine(root, "direct-reorder");
        using (var ledger = new AccountUsageLedgerService(reorderRoot, Path.Combine(reorderRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            var first = LedgerPerformanceExecution(31);
            var second = LedgerPerformanceExecution(32);
            var initial = await ledger.IngestExecutionsAsync(new[] { first, second }, "stable-import-batch");
            var replay = await ledger.IngestExecutionsAsync(new[] { second, first }, "stable-import-batch");
            var snapshot = await ledger.ReadAsync();
            Ensure(initial.AppendedCount == 2 && replay.AppendedCount == 0 && replay.DuplicateCount == 2
                   && snapshot.StoredAttemptCount == 2,
                "Reordering a stable direct import batch created new billable facts.");
        }

        var quotaRecoveryRoot = Path.Combine(root, "quota-read-recovery");
        string quotaCommitPath;
        using (var seed = new AccountUsageLedgerService(quotaRecoveryRoot, Path.Combine(quotaRecoveryRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            var batch = seed.CreateQuotaObservationBatch("provider-recovery", "account-recovery", now, true,
                "fetch-recovery", "pool:recovery");
            await seed.IngestQuotaSnapshotsAsync(new[]
            {
                seed.CreateQuotaSnapshot("provider-recovery", true, "account-recovery", "five_hour", "5h", 23,
                    "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, batch, false,
                    null, "test", observationScope: "pool:recovery"),
                seed.CreateQuotaSnapshot("provider-recovery", true, "account-recovery", "weekly", "Weekly", 41,
                    "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, batch, false,
                    null, "test", observationScope: "pool:recovery")
            });
            quotaCommitPath = seed.QuotaCommitLedgerPath;
        }
        File.Delete(quotaCommitPath);
        using (var recovered = new AccountUsageLedgerService(quotaRecoveryRoot, Path.Combine(quotaRecoveryRoot, "disabled"),
                   () => now.AddMinutes(1), sourceDisabled: true))
        {
            var snapshot = await recovered.ReadAsync();
            Ensure(File.Exists(quotaCommitPath)
                   && snapshot.LatestQuotaSnapshots.Count(view => view.Fact.ObservationScope == "pool:recovery") == 2
                   && snapshot.QuotaIntegrityFailureCount == 0,
                "A complete prepare+facts quota batch without commit was not recovered during read-only startup.");
        }

        var quotaAtomicRoot = Path.Combine(root, "quota-atomic-rebuild");
        using (var seed = new AccountUsageLedgerService(quotaAtomicRoot, Path.Combine(quotaAtomicRoot, "disabled"),
                   () => now, sourceDisabled: true))
        {
            var batch = seed.CreateQuotaObservationBatch("provider-atomic", "account-atomic", now, true,
                "fetch-atomic", "pool:atomic");
            await seed.IngestQuotaSnapshotsAsync(new[]
            {
                seed.CreateQuotaSnapshot("provider-atomic", true, "account-atomic", "weekly", "Weekly", 125,
                    "percent_used", AccountQuotaAvailability.Provided, now, true, now, false, batch, false,
                    null, "test", observationScope: "pool:atomic")
            });
            if (File.Exists(seed.ProjectionCheckpointPath)) File.Delete(seed.ProjectionCheckpointPath);
        }
        using (var retry = new AccountUsageLedgerService(quotaAtomicRoot, Path.Combine(quotaAtomicRoot, "disabled"),
                   () => now.AddMinutes(1), sourceDisabled: true))
        {
            var barrier = typeof(AccountUsageLedgerService).GetProperty(
                              "ProjectionRowAcceptedBarrierForTests",
                              System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("Projection row test barrier was not found.");
            var interrupted = false;
            barrier.SetValue(retry, (Action)(() =>
            {
                if (interrupted) return;
                interrupted = true;
                throw new IOException("simulated quota projection failure");
            }));
            try { _ = await retry.ReadAsync(); }
            catch (IOException) { }
            Ensure(interrupted, "Quota projection interruption hook was not reached.");
            barrier.SetValue(retry, null);
            var snapshot = await retry.ReadAsync();
            Ensure(snapshot.LatestQuotaSnapshots.Count(view => view.Fact.ObservationScope == "pool:atomic") == 1
                   && snapshot.QuotaIntegrityFailureCount == 0,
                "A failed quota stage left mixed facts/prepare/commit projection state after retry.");
        }
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static (IReadOnlyList<PoolAccountView> Accounts, AccountRosterCompleteness Completeness) ParsePrivateRoster(
    Type serviceType,
    PoolDefinition pool,
    string json)
{
    using var document = JsonDocument.Parse(json);
    var method = serviceType.GetMethod("ParseAccounts",
                     System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                 ?? throw new InvalidOperationException($"{serviceType.Name}.ParseAccounts was not found");
    var parsed = method.Invoke(null, new object[] { pool, document.RootElement })
                 ?? throw new InvalidOperationException($"{serviceType.Name}.ParseAccounts returned null");
    var accounts = parsed.GetType().GetProperty("Accounts")?.GetValue(parsed) as IReadOnlyList<PoolAccountView>
                   ?? throw new InvalidOperationException("Parsed roster accounts projection is unavailable");
    var completeness = parsed.GetType().GetProperty("Completeness")?.GetValue(parsed) is AccountRosterCompleteness value
        ? value
        : throw new InvalidOperationException("Parsed roster completeness projection is unavailable");
    return (accounts, completeness);
}

static void CollectMatchingStringPaths(
    JsonElement element,
    string path,
    string match,
    ICollection<string> paths)
{
    if (element.ValueKind == JsonValueKind.String)
    {
        if (element.GetString()?.Contains(match, StringComparison.Ordinal) == true) paths.Add(path);
        return;
    }
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
            CollectMatchingStringPaths(property.Value, $"{path}.{property.Name}", match, paths);
        return;
    }
    if (element.ValueKind != JsonValueKind.Array) return;
    var index = 0;
    foreach (var item in element.EnumerateArray())
        CollectMatchingStringPaths(item, $"{path}[{index++}]", match, paths);
}

static async Task RunSecurityBoundaryTestsAsync()
{
    var root = CreateOwnedTestRoot("cmm-security-boundaries");
    try
    {
        var cliRoot = Path.Combine(root, "cli");
        var cliSettings = new AppSettingsService(cliRoot);
        var cliSecrets = new SecretStore(cliRoot);
        var cliService = new CliProxyPoolService(cliSettings, cliSecrets);
        var validPool = new PoolDefinition
        {
            Id = "security-cli-1",
            DisplayName = "Security CLI",
            Transport = PoolTransport.CliProxyApi,
            Product = AccountProduct.CodexPlus,
            Enabled = true,
            ProviderId = "cmm-security-cli-1",
            BaseUrl = "http://127.0.0.1:18401/v1",
            LocalPort = 18401
        };
        Ensure(PoolCatalogService.IsExactCliEndpoint(validPool)
               && PoolCatalogService.IsSafeCliProviderBinding(validPool)
               && PoolCatalogService.BuildCliBaseUrl(validPool) == validPool.BaseUrl,
            "Canonical CLI endpoint/provider binding was rejected.");

        var unsafePools = new[]
        {
            ClonePoolForTest(validPool, baseUrl: "http://localhost:18401/v1"),
            ClonePoolForTest(validPool, baseUrl: "http://[::1]:18401/v1"),
            ClonePoolForTest(validPool, baseUrl: "http://127.0.0.2:18401/v1"),
            ClonePoolForTest(validPool, baseUrl: "http://2130706433:18401/v1"),
            ClonePoolForTest(validPool, baseUrl: "http://0x7f000001:18401/v1"),
            ClonePoolForTest(validPool, baseUrl: "http://127.0.0.1:18402/v1"),
            ClonePoolForTest(validPool, baseUrl: "http://127.0.0.1:18401/v1/evil"),
            ClonePoolForTest(validPool, baseUrl: "http://127.0.0.1:18401/v1?x=1"),
            ClonePoolForTest(validPool, providerId: "cmm-test-worker"),
            ClonePoolForTest(validPool, providerId: "internal:unified-gateway:client"),
            ClonePoolForTest(validPool, providerId: "cmm-another-cli"),
            ClonePoolForTest(validPool, baseUrl: "http://127.0.0.1:8317/v1", localPort: 8317),
            ClonePoolForTest(
                validPool,
                baseUrl: "http://127.0.0.1:18405/v1",
                providerId: "cmm-plus-api-1",
                id: PoolCatalogDefaults.PlusPoolId,
                localPort: 18405)
        };
        foreach (var unsafePool in unsafePools)
        {
            var rejected = false;
            try { _ = await cliService.EnsureRunningAsync(unsafePool); }
            catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected, $"Unsafe CLI pool was accepted: {unsafePool.BaseUrl} / {unsafePool.ProviderId}");
        }
        var builtInPlus = new PoolCatalogService(Path.Combine(root, "catalog-agent-defaults"))
            .Find(PoolCatalogDefaults.PlusAgentPoolId)!;
        var builtInPro = new PoolCatalogService(Path.Combine(root, "catalog-agent-defaults"))
            .Find(PoolCatalogDefaults.ProAgentPoolId)!;
        Ensure(PoolCatalogService.IsSafeCliPortBinding(builtInPlus)
               && PoolCatalogService.IsSafeCliPortBinding(builtInPro)
               && PoolCatalogService.IsExactCliEndpoint(builtInPlus)
               && PoolCatalogService.IsExactCliEndpoint(builtInPro)
               && PoolCatalogService.IsSafeCliProviderBinding(builtInPlus)
               && PoolCatalogService.IsSafeCliProviderBinding(builtInPro),
            "Exact built-in CLI identities were rejected.");
        var externalInternalReadRejected = false;
        var externalInternalSaveRejected = false;
        try { _ = cliSecrets.Read("internal:unified-gateway:client"); }
        catch (InvalidOperationException) { externalInternalReadRejected = true; }
        try { cliSecrets.Save("internal:unified-gateway:client", "must-not-write"); }
        catch (InvalidOperationException) { externalInternalSaveRejected = true; }
        Ensure(externalInternalReadRejected
               && externalInternalSaveRejected
               && !File.Exists(Path.Combine(cliRoot, "secrets.json"))
               && !Directory.Exists(Path.Combine(cliRoot, "cli-proxy")),
            "Rejected CLI identities read/wrote credentials or created an instance directory.");

        var freshCatalogRoot = Path.Combine(root, "fresh-port-guard");
        var freshCatalog = new PoolCatalogService(freshCatalogRoot);
        var freshPools = freshCatalog.GetPools().ToList();
        var firstDynamic = ClonePoolForTest(
            validPool,
            baseUrl: "http://127.0.0.1:18410/v1",
            providerId: "cmm-fresh-cli-a",
            id: "fresh-cli-a",
            localPort: 18410);
        freshPools.Add(firstDynamic);
        var freshPath = freshCatalog.FilePath;
        await File.WriteAllTextAsync(freshPath, JsonSerializer.Serialize(new PoolCatalogDocument
        {
            SchemaVersion = 3,
            Pools = freshPools,
            Active = freshCatalog.GetActive()
        }));
        Ensure(freshCatalog.GetPoolsFresh().Any(pool => pool.Id == firstDynamic.Id),
            "A valid fresh dynamic CLI pool was rejected.");
        firstDynamic.LocalPort = 8317;
        firstDynamic.BaseUrl = "http://127.0.0.1:8317/v1";
        await File.WriteAllTextAsync(freshPath, JsonSerializer.Serialize(new PoolCatalogDocument
        {
            SchemaVersion = 3,
            Pools = freshPools,
            Active = freshCatalog.GetActive()
        }));
        var reservedFreshRejected = false;
        try { _ = freshCatalog.GetPoolsFresh(); }
        catch (InvalidOperationException) { reservedFreshRejected = true; }

        firstDynamic.LocalPort = 18410;
        firstDynamic.BaseUrl = "http://127.0.0.1:18410/v1";
        freshPools.Add(ClonePoolForTest(
            validPool,
            baseUrl: "http://127.0.0.1:18410/v1",
            providerId: "cmm-fresh-cli-b",
            id: "fresh-cli-b",
            localPort: 18410));
        await File.WriteAllTextAsync(freshPath, JsonSerializer.Serialize(new PoolCatalogDocument
        {
            SchemaVersion = 3,
            Pools = freshPools,
            Active = freshCatalog.GetActive()
        }));
        var duplicateFreshRejected = false;
        try { _ = freshCatalog.GetPoolsFreshForDiscovery(); }
        catch (InvalidOperationException) { duplicateFreshRejected = true; }
        Ensure(reservedFreshRejected && duplicateFreshRejected,
            "Fresh catalog accepted a reserved or duplicate CLI port.");

        var configBase = "model = \"gpt-5.6-sol\"\n";
        var managedBody = $"""
            {ManagedTomlBlockEditor.TargetTableHeader}
            command = "test-bridge.exe"
            args = ["--external-worker-mcp"]
            """;
        var managedEdit = ManagedTomlBlockEditor.Upsert(configBase, managedBody, null);
        Ensure(managedEdit.CanWrite
               && managedEdit.CandidateText is not null
               && managedEdit.CandidateManagedBlockSha256 is not null,
            "Could not create the managed MCP fixture.");

        async Task<(SubagentConfigurationService Service, byte[] ConfigBytes, byte[] DraftBytes)> CreateLegacyCaseAsync(
            string name,
            string configText,
            string managedHash)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "config.toml");
            var dataPath = Path.Combine(directory, "subagents.json");
            await File.WriteAllTextAsync(configPath, configText, new UTF8Encoding(false));
            var service = new SubagentConfigurationService(
                configPath: configPath,
                agentsDirectory: Path.Combine(directory, "agents"),
                dataPath: dataPath,
                backupRoot: Path.Combine(directory, "backups"),
                bridgeExecutablePath: Environment.ProcessPath,
                bridgeStatePath: Path.Combine(directory, "worker-state.json"));
            var document = new SubagentConfigurationDocument
            {
                SchemaVersion = 2,
                ManagedMcpBlockHash = managedHash,
                Roles = service.Roles.Select(role => new SubagentRoleSelection
                {
                    RoleId = role.Id,
                    WorkerKind = role.Id == "cmm_implementer"
                        ? SubagentWorkerKind.External
                        : SubagentWorkerKind.CodexNative,
                    ModelId = role.Id == "cmm_implementer" ? "cli/test-worker/model-a" : role.DefaultModel
                }).ToList()
            };
            await File.WriteAllTextAsync(dataPath, JsonSerializer.Serialize(document), new UTF8Encoding(false));
            return (service, await File.ReadAllBytesAsync(configPath), await File.ReadAllBytesAsync(dataPath));
        }

        var exactCase = await CreateLegacyCaseAsync(
            "legacy-exact",
            managedEdit.CandidateText!,
            managedEdit.CandidateManagedBlockSha256!);
        var exactDraft = exactCase.Service.LoadDraft();
        Ensure(exactCase.Service.LoadWarning is null
               && exactDraft.SourceAuthorizations.All(item => !item.Enabled)
               && exactDraft.Roles.All(item => item.WorkerKind != SubagentWorkerKind.External),
            "An exact legacy managed block minted an authorization or kept an unbound external role.");

        var randomHashCase = await CreateLegacyCaseAsync(
            "legacy-random-hash",
            managedEdit.CandidateText!,
            new string('A', 64));
        var randomHashDraft = randomHashCase.Service.LoadDraft();
        Ensure(randomHashDraft.SourceAuthorizations.All(item => !item.Enabled),
            "A random legacy hash minted an authorization.");

        var missingBlockCase = await CreateLegacyCaseAsync(
            "legacy-missing-block",
            configBase,
            managedEdit.CandidateManagedBlockSha256!);
        var missingBlockDraft = missingBlockCase.Service.LoadDraft();
        Ensure(missingBlockDraft.SourceAuthorizations.All(item => !item.Enabled),
            "A missing legacy managed block minted an authorization.");
        foreach (var item in new[] { exactCase, randomHashCase, missingBlockCase })
        {
            var finalConfigBytes = await File.ReadAllBytesAsync(item.Service.ConfigPath);
            var finalDraftBytes = await File.ReadAllBytesAsync(item.Service.DataPath);
            Ensure(item.ConfigBytes.SequenceEqual(finalConfigBytes)
                   && item.DraftBytes.SequenceEqual(finalDraftBytes),
                "Legacy draft inspection modified a source file.");
        }

        var duplicateDirectory = Path.Combine(root, "duplicate-role");
        Directory.CreateDirectory(duplicateDirectory);
        var duplicateConfigPath = Path.Combine(duplicateDirectory, "config.toml");
        var duplicateDataPath = Path.Combine(duplicateDirectory, "subagents.json");
        await File.WriteAllTextAsync(duplicateConfigPath, managedEdit.CandidateText!, new UTF8Encoding(false));
        var duplicateService = new SubagentConfigurationService(
            configPath: duplicateConfigPath,
            agentsDirectory: Path.Combine(duplicateDirectory, "agents"),
            dataPath: duplicateDataPath,
            backupRoot: Path.Combine(duplicateDirectory, "backups"),
            bridgeExecutablePath: Environment.ProcessPath,
            bridgeStatePath: Path.Combine(duplicateDirectory, "worker-state.json"));
        var duplicateRoles = duplicateService.Roles.Select(role => new SubagentRoleSelection
        {
            RoleId = role.Id,
            WorkerKind = role.Id == "cmm_implementer"
                ? SubagentWorkerKind.External
                : SubagentWorkerKind.CodexNative,
            ModelId = role.Id == "cmm_implementer" ? "cli/test-worker/model-a" : role.DefaultModel,
            SourceId = role.Id == "cmm_implementer" ? "gateway-cli:test-worker" : null
        }).ToList();
        duplicateRoles.Insert(0, new SubagentRoleSelection
        {
            RoleId = "cmm_implementer",
            WorkerKind = SubagentWorkerKind.External,
            ModelId = "cli/test-worker/model-a",
            SourceId = "gateway-cli:test-worker"
        });
        var duplicateDocument = new SubagentConfigurationDocument
        {
            SchemaVersion = 3,
            Roles = duplicateRoles,
            ManagedMcpBlockHash = managedEdit.CandidateManagedBlockSha256,
            SourceAuthorizations = new List<SubagentSourceAuthorization>
            {
                new()
                {
                    SourceId = "gateway-cli:test-worker",
                    ExpectedFingerprint = new string('B', 64),
                    Enabled = true
                }
            }
        };
        await File.WriteAllTextAsync(
            duplicateDataPath,
            JsonSerializer.Serialize(duplicateDocument),
            new UTF8Encoding(false));
        var duplicateBackend = new TestWorkerBackend(new[] { "cli/test-worker/model-a" });
        using var duplicateWorker = new ExternalWorkerService(
            new SubagentExternalWorkerConfigurationSource(duplicateService),
            duplicateBackend,
            new TestWorkerAuditSink());
        var duplicateRejected = false;
        try
        {
            _ = await duplicateWorker.DelegateAsync(new ExternalWorkerInvocation(
                "cmm_implementer", "must not call backend", null, 64));
        }
        catch (ExternalWorkerException ex) when (ex.Code == "role_configuration_invalid")
        {
            duplicateRejected = true;
        }
        Ensure(duplicateRejected
               && duplicateService.LoadWarning is not null
               && duplicateBackend.Requests.Count == 0,
            "A duplicate role configuration reached the model backend.");

        Console.WriteLine("SECURITY_BOUNDARIES_OK cli_cases=13 fresh_port_guards=2 migration_cases=3 duplicate_roles=blocked network_calls=0 model_calls=0");
    }
    finally
    {
        DeleteOwnedTestRoot(root);
    }
}

static PoolDefinition ClonePoolForTest(
    PoolDefinition source,
    string? baseUrl = null,
    string? providerId = null,
    string? id = null,
    int? localPort = null) => new()
{
    Id = id ?? source.Id,
    DisplayName = source.DisplayName,
    Description = source.Description,
    Transport = source.Transport,
    Product = source.Product,
    IsProtected = source.IsProtected,
    Enabled = source.Enabled,
    RouteAlias = source.RouteAlias,
    ProviderId = providerId ?? source.ProviderId,
    NativeAccountId = source.NativeAccountId,
    DefaultModel = source.DefaultModel,
    BaseUrl = baseUrl ?? source.BaseUrl,
    LocalPort = localPort ?? source.LocalPort,
    AdminUser = source.AdminUser,
    CreatedAt = source.CreatedAt
};

static async Task AssertCatalogStartupIsolationAsync(
    string root,
    string caseName,
    string json)
{
    var directory = Path.Combine(root, $"catalog-startup-isolation-{caseName}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "pools.json");
    await File.WriteAllTextAsync(path, json);
    var originalBytes = await File.ReadAllBytesAsync(path);
    var originalMtime = File.GetLastWriteTimeUtc(path);

    var app = AppServices.Create(directory);
    var views = await app.AccountPools.ReadViewsAsync();
    var writeRejected = false;
    try { app.PoolCatalog.SetEnabled(PoolCatalogDefaults.PlusPoolId, false); }
    catch (InvalidOperationException) { writeRejected = true; }
    var finalBytes = await File.ReadAllBytesAsync(path);

    Ensure(app.PoolCatalog.LoadWarning is not null
           && app.PoolCatalog.GetPools().Count == 2
           && views.Count == 2
           && views.All(view => !view.CanSwitch && !view.CanAddAccount && !view.CanConfigure)
           && writeRejected
           && originalBytes.SequenceEqual(finalBytes)
           && originalMtime == File.GetLastWriteTimeUtc(path),
        $"Catalog startup isolation failed for {caseName}.");
}

static async Task AssertCatalogCaseInsensitiveJsonAsync(string root)
{
    var directory = Path.Combine(root, "catalog-case-insensitive");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "pools.json");
    await File.WriteAllTextAsync(path,
        """{"schemaVersion":4,"pools":[{"id":"official-pro","displayName":"官方保底","description":"测试","transport":"OfficialCodex","product":"CodexPro","isProtected":true,"enabled":true,"defaultModel":"gpt-5.6-sol","baseUrl":"OpenAI 原生账号"},{"id":"plus-api-1","displayName":"Plus","description":"未绑定","transport":"NativeCodexAccount","product":"CodexPlus","enabled":false,"defaultModel":"gpt-5.6-sol","baseUrl":"OpenAI 原生账号"},{"id":"plus-agent-api-1","displayName":"Plus Agent","description":"测试","transport":"CliProxyApi","product":"CodexPlus","enabled":true,"routeAlias":"cmm/agent-plus-1","providerId":"cmm-plus-agent-api-1","defaultModel":"gpt-5.6-sol","baseUrl":"http://127.0.0.1:8411/v1","localPort":8411},{"id":"pro-agent-api-1","displayName":"Pro Agent","description":"测试","transport":"CliProxyApi","product":"CodexPro","enabled":true,"routeAlias":"cmm/agent-pro-1","providerId":"cmm-pro-agent-api-1","defaultModel":"gpt-5.6-sol","baseUrl":"http://127.0.0.1:8412/v1","localPort":8412}],"active":{"poolId":"official-pro","model":"gpt-5.6-sol","verification":"official-direct"}}""");

    var catalog = new PoolCatalogService(directory);
    Ensure(catalog.LoadWarning is null
           && catalog.GetActive().PoolId == PoolCatalogDefaults.OfficialPoolId
           && catalog.GetPools().Count == 4,
        "pools.json 的 camelCase/PascalCase 大小写兼容失败。");
}

static SubagentApplyPlan CreateCanonicalExternalPlan(
    SubagentConfigurationService service,
    IEnumerable<SubagentRoleSelection> selections)
{
    var context = CanonicalExternalContext();
    var normalized = selections.Select(CloneTestSelection).ToArray();
    foreach (var selection in normalized.Where(item => item.WorkerKind == SubagentWorkerKind.External))
        selection.SourceId = "gateway-cli:test-worker";
    return service.CreatePlan(
        normalized,
        new[] { context.Grant },
        new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
        new[] { context.Source });
}

static SubagentApplyResult ApplyCanonicalExternal(
    SubagentConfigurationService service,
    IEnumerable<SubagentRoleSelection> selections,
    string baselineRevision)
{
    var context = CanonicalExternalContext();
    var normalized = selections.Select(CloneTestSelection).ToArray();
    foreach (var selection in normalized.Where(item => item.WorkerKind == SubagentWorkerKind.External))
        selection.SourceId = "gateway-cli:test-worker";
    return service.Apply(
        normalized,
        new[] { context.Grant },
        new[] { "gpt-5.6-sol", "gpt-5.6-terra" },
        new[] { context.Source },
        baselineRevision);
}

static (SubagentSourceAuthorization Grant, SubagentSourceDescriptor Source) CanonicalExternalContext()
{
    var pool = CreateTestExternalPool();
    var fingerprint = SubagentSourceIdentity.ComputeForPool(
        pool,
        "gateway-cli:test-worker",
        SubagentSourceKind.CliProxyPool,
        "cli/test-worker/",
        SubagentSourceIdentity.OpenAiChatAdapter,
        pool.ProviderId);
    var source = new SubagentSourceDescriptor(
        "gateway-cli:test-worker",
        pool.DisplayName,
        SubagentSourceKind.CliProxyPool,
        "cli/test-worker/",
        pool.BaseUrl,
        "测试 CLI 凭据槽 cmm-test-worker",
        "消耗测试 CLI 号池",
        SubagentSourceIdentity.OpenAiChatAdapter,
        fingerprint,
        true,
        true,
        true,
        new[] { "cli/test-worker/model-a" },
        "可用",
        null,
        DateTimeOffset.UtcNow);
    var grant = new SubagentSourceAuthorization
    {
        SourceId = source.SourceId,
        ExpectedFingerprint = fingerprint,
        Enabled = true,
        AuthorizedAt = DateTimeOffset.UtcNow,
        AuthorizedDisplayName = source.DisplayName,
        AuthorizedEndpoint = source.EndpointDisplay,
        AuthorizedAdapter = source.Adapter,
        AuthorizedRoutePrefix = source.RoutePrefix,
        AuthorizedCredentialScope = source.CredentialScopeText
    };
    return (grant, source);
}

static PoolDefinition CreateTestExternalPool() => new()
{
    Id = "test-worker",
    DisplayName = "Test External Worker",
    Description = "Isolated CLI worker used only by integration tests.",
    Transport = PoolTransport.CliProxyApi,
    Product = AccountProduct.CodexPlus,
    Enabled = true,
    RouteAlias = "test-worker",
    ProviderId = "cmm-test-worker",
    DefaultModel = "cli/test-worker/model-a",
    BaseUrl = "http://127.0.0.1:18450/v1",
    LocalPort = 18450
};

static SubagentRoleSelection CloneTestSelection(SubagentRoleSelection selection) => new()
{
    RoleId = selection.RoleId,
    WorkerKind = selection.WorkerKind,
    ModelId = selection.ModelId,
    SourceId = selection.SourceId
};

static async Task RunProbeFallbackAsync()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (var requestNumber = 0; requestNumber < 2; requestNumber++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                string? line;
                do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
                var valid = requestLine.Contains("/v1/models", StringComparison.Ordinal);
                var body = valid ? "{\"data\":[{\"id\":\"k3-test\"}]}" : "这不是json";
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(bytes);
            }
        });

        var result = await new ProviderProbeService().ProbeAsync($"http://127.0.0.1:{port}", "test");
        Ensure(result.BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase), "没有退到第二个模型接口。");
        Ensure(result.Models.Contains("k3-test", StringComparer.OrdinalIgnoreCase), "第二个接口的模型没有读到。");
        await server;
    }
    finally
    {
        listener.Stop();
    }
}

static async Task AssertCliProxyOwnedInstanceReconciliationAsync(string root)
{
    var testRoot = Path.Combine(root, "cliproxy-owned-instance-reconciliation");
    var settings = new AppSettingsService(testRoot);
    var catalog = new PoolCatalogService(testRoot, settings.ReservedLocalPorts);
    var secrets = new SecretStore(testRoot);
    var binary = RequireCliProxyTestArtifact();

    // Trusted old instance: after its catalog port changes, reconciliation must
    // stop exactly the recorded PID and remove the matching historical record.
    var trustedPool = catalog.AddCliProxyPool(AccountProduct.CodexPlus);
    var trustedService = new CliProxyPoolService(settings, secrets, binaryPath: binary, poolCatalog: catalog);
    Ensure(await trustedService.EnsureRunningAsync(trustedPool),
        "CLIProxy 可信旧实例没有启动，无法验证端口变更回收。 ");
    var trustedRecordPath = Path.Combine(testRoot, "cli-proxy", "pools", trustedPool.Id, "instance.json");
    using var trustedRecord = JsonDocument.Parse(await File.ReadAllTextAsync(trustedRecordPath));
    var trustedPid = trustedRecord.RootElement.GetProperty("Pid").GetInt32();
    var oldPort = trustedPool.LocalPort;
    catalog.ReassignCliPort(trustedPool);
    Ensure(trustedPool.LocalPort != oldPort, "CLIProxy 端口变更测试没有获得新端口。 ");
    await trustedService.ReconcileOwnedInstancesAsync(force: true);
    Ensure(await WaitForProcessExitAsync(trustedPid, TimeSpan.FromSeconds(5))
           && !File.Exists(trustedRecordPath),
        "端口变化后，证据完整的旧 CLIProxy 实例没有被精确回收。 ");

    // Deleted pool: a trusted historical instance no longer present in the
    // catalog is also owned and should be reclaimed.
    var deletedPool = catalog.AddCliProxyPool(AccountProduct.CodexPro);
    var deletedService = new CliProxyPoolService(settings, secrets, binaryPath: binary, poolCatalog: catalog);
    Ensure(await deletedService.EnsureRunningAsync(deletedPool),
        "CLIProxy 待删除号池实例没有启动。 ");
    var deletedRecordPath = Path.Combine(testRoot, "cli-proxy", "pools", deletedPool.Id, "instance.json");
    using var deletedRecord = JsonDocument.Parse(await File.ReadAllTextAsync(deletedRecordPath));
    var deletedPid = deletedRecord.RootElement.GetProperty("Pid").GetInt32();
    catalog.RemoveCliProxyPool(deletedPool.Id);
    await deletedService.ReconcileOwnedInstancesAsync(force: true);
    Ensure(await WaitForProcessExitAsync(deletedPid, TimeSpan.FromSeconds(5)),
        "号池从清单删除后，证据完整的旧 CLIProxy 实例没有被回收。 ");

    // Forged record: even with a live trusted binary and listening port, one
    // mismatched field must make reconciliation fail closed and leave it alone.
    var forgedPool = catalog.AddCliProxyPool(AccountProduct.CodexPlus);
    var forgedService = new CliProxyPoolService(settings, secrets, binaryPath: binary, poolCatalog: catalog);
    Ensure(await forgedService.EnsureRunningAsync(forgedPool),
        "CLIProxy 伪造记录保护测试实例没有启动。 ");
    var forgedRecordPath = Path.Combine(testRoot, "cli-proxy", "pools", forgedPool.Id, "instance.json");
    var forgedNode = JsonNode.Parse(await File.ReadAllTextAsync(forgedRecordPath))!.AsObject();
    var forgedPid = forgedNode["Pid"]!.GetValue<int>();
    var forgedRecordPaths = Directory.GetFiles(
        Path.Combine(testRoot, "cli-proxy", "pools", forgedPool.Id),
        "*.json",
        SearchOption.AllDirectories);
    foreach (var recordPath in forgedRecordPaths)
    {
        var recordNode = JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!.AsObject();
        recordNode["BinaryPath"] = Path.Combine(testRoot, "not-the-running-binary.exe");
        await File.WriteAllTextAsync(
            recordPath,
            recordNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    catalog.ReassignCliPort(forgedPool);
    await forgedService.ReconcileOwnedInstancesAsync(force: true);
    Ensure(IsProcessRunning(forgedPid),
        "二进制路径不匹配的伪造记录导致总管家误杀了进程。 ");

    // The test created this exact process through this live service instance,
    // so the normal in-memory owned-stop path can clean it without trusting the
    // deliberately forged disk records.
    await forgedService.StopOwnedAsync(forgedPool.Id);
    Ensure(await WaitForProcessExitAsync(forgedPid, TimeSpan.FromSeconds(5)),
        "CLIProxy 伪造记录保护测试的自有进程没有完成清理。 ");
}

static bool IsProcessRunning(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (!IsProcessRunning(pid)) return true;
        await Task.Delay(50);
    }
    return !IsProcessRunning(pid);
}

static string RequireCliProxyTestArtifact()
{
    var configured = Environment.GetEnvironmentVariable("CMM_TEST_CLIPROXY_ARTIFACT") ?? string.Empty;
    Ensure(!string.IsNullOrWhiteSpace(configured),
        "CMM_TEST_CLIPROXY_ARTIFACT must point to the explicit, external CLIProxyAPI test artifact.");
    Ensure(Path.IsPathFullyQualified(configured),
        "CMM_TEST_CLIPROXY_ARTIFACT must be an absolute path.");
    var path = Path.GetFullPath(configured);
    Ensure(File.Exists(path), "The external CLIProxyAPI test artifact does not exist.");
    using var stream = File.OpenRead(path);
    var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    Ensure(actual.Equals(CliProxyPoolService.BundledSha256, StringComparison.OrdinalIgnoreCase),
        "The external CLIProxyAPI test artifact does not match the source-controlled trust anchor.");
    return path;
}

static string CreateOwnedTestRoot(string prefix)
{
    if (string.IsNullOrWhiteSpace(prefix)
        || prefix.Length > 80
        || prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        throw new ArgumentException("测试根目录前缀格式不安全。", nameof(prefix));

    var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var allowedPrefix = temporaryRoot + Path.DirectorySeparatorChar;
    var root = Path.GetFullPath(Path.Combine(
        temporaryRoot,
        $"{prefix}-{Guid.NewGuid():N}"));
    if (!root.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("测试根目录逃逸出临时目录。 ");

    Directory.CreateDirectory(root);
    var item = new DirectoryInfo(root);
    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidOperationException("测试根目录不能是重解析点。 ");
    var marker = Path.Combine(root, ".cmm-owned-test-root");
    File.WriteAllText(
        marker,
        "cmm-owned-test-root-v1\n" + item.Name,
        new UTF8Encoding(false));
    return root;
}

static void CopyIdentityKeyForTest(string source, string destination)
{
    File.Copy(source, destination, overwrite: true);
    if (!OperatingSystem.IsWindows()) return;

    var currentSid = WindowsIdentity.GetCurrent().User
                     ?? throw new InvalidOperationException("无法解析测试进程 Windows SID。");
    var security = new FileSecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.AddAccessRule(new FileSystemAccessRule(
        currentSid,
        FileSystemRights.FullControl,
        InheritanceFlags.None,
        PropagationFlags.None,
        AccessControlType.Allow));
    new FileInfo(destination).SetAccessControl(security);
}

static void DeleteOwnedTestRoot(string root)
{
    if (!Directory.Exists(root))
        throw new InvalidOperationException("待清理的测试根目录不存在。 ");
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var allowedPrefix = temporaryRoot + Path.DirectorySeparatorChar;
    var fullRoot = Path.GetFullPath(root);
    if (!fullRoot.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("拒绝清理临时目录之外的路径。 ");

    var item = new DirectoryInfo(fullRoot);
    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidOperationException("拒绝清理重解析点测试根目录。 ");
    var marker = Path.Combine(fullRoot, ".cmm-owned-test-root");
    if (!File.Exists(marker))
        throw new InvalidOperationException("测试根目录所有权标记缺失。 ");
    var expectedMarker = "cmm-owned-test-root-v1\n" + item.Name;
    var actualMarker = File.ReadAllText(marker, Encoding.UTF8);
    if (!string.Equals(actualMarker, expectedMarker, StringComparison.Ordinal))
        throw new InvalidOperationException("测试根目录所有权标记不匹配。 ");

    Directory.Delete(fullRoot, recursive: true);
    if (Directory.Exists(fullRoot))
        throw new IOException("测试根目录清理后仍然存在。 ");
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static IReadOnlyDictionary<string, byte[]> CaptureDirectoryFileBytes(string root)
{
    return Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(
            path => Path.GetRelativePath(root, path),
            File.ReadAllBytes,
            StringComparer.OrdinalIgnoreCase);
}

static bool DirectoryFileBytesEqual(
    IReadOnlyDictionary<string, byte[]> expected,
    IReadOnlyDictionary<string, byte[]> actual)
{
    return expected.Count == actual.Count
           && expected.All(pair =>
               actual.TryGetValue(pair.Key, out var actualBytes)
               && pair.Value.SequenceEqual(actualBytes));
}

internal sealed record RemoteSeedPayload(string AdminPassword, string ClientKey);

internal sealed record TestCodexValidationRequest(
    byte[] ConfigBytes,
    IReadOnlyDictionary<string, byte[]> AgentFiles);

internal sealed class TestCodexConfigValidator : ICodexConfigValidator
{
    private readonly CodexConfigValidationResult _result;

    public TestCodexConfigValidator(CodexConfigValidationResult result) => _result = result;

    public List<TestCodexValidationRequest> Requests { get; } = new();

    public Task<CodexConfigValidationResult> ValidateAsync(
        ReadOnlyMemory<byte> configBytes,
        IReadOnlyDictionary<string, byte[]>? agentFiles = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(new TestCodexValidationRequest(
            configBytes.ToArray(),
            (agentFiles ?? new Dictionary<string, byte[]>())
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase)));
        return Task.FromResult(_result);
    }
}

internal sealed class ControlledTextReader : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public void AddLine(string line)
    {
        if (!_lines.Writer.TryWrite(line))
            throw new InvalidOperationException("测试输入已关闭。");
    }

    public void Complete() => _lines.Writer.TryComplete();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Complete();
        base.Dispose(disposing);
    }
}

internal sealed class SignalingTextWriter : TextWriter
{
    private readonly int _expectedLineCount;
    private readonly List<string> _lines = new();
    private readonly object _gate = new();

    public SignalingTextWriter(int expectedLineCount)
    {
        _expectedLineCount = expectedLineCount;
    }

    public override Encoding Encoding => Encoding.UTF8;
    public TaskCompletionSource<bool> ExpectedLinesReached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task WriteLineAsync(string? value)
    {
        lock (_gate)
        {
            _lines.Add(value ?? string.Empty);
            if (_lines.Count >= _expectedLineCount) ExpectedLinesReached.TrySetResult(true);
        }
        return Task.CompletedTask;
    }

    public override Task FlushAsync() => Task.CompletedTask;

    public string[] ReadLines()
    {
        lock (_gate) return _lines.ToArray();
    }
}

internal sealed class TestWorkerConfiguration : IExternalWorkerConfigurationSource
{
    public TestWorkerConfiguration(
        IReadOnlyList<SubagentRoleDefinition> roles,
        SubagentConfigurationDocument draft)
    {
        Roles = roles;
        Draft = draft;
    }

    public IReadOnlyList<SubagentRoleDefinition> Roles { get; }
    public string? LoadWarning { get; init; }
    public SubagentConfigurationDocument Draft { get; }
    public SubagentConfigurationDocument LoadDraft() => Draft;
}

internal sealed class TestWorkerBackend : IExternalWorkerBackend
{
    private readonly IReadOnlyList<string> _models;

    public TestWorkerBackend(IReadOnlyList<string> models) => _models = models;

    public List<ExternalWorkerBackendRequest> Requests { get; } = new();
    public IReadOnlyList<string> ReadConfiguredModels() => _models;

    public Task<ExternalWorkerBackendResponse> CompleteAsync(
        ExternalWorkerBackendRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(new ExternalWorkerBackendResponse(
            "OK_EXTERNAL_WORKER",
            "stop",
            new ExternalWorkerTokenUsage(12, 3, 15),
            200,
            "upstream/model-a"));
    }
}

internal sealed class BlockingWorkerBackend : IExternalWorkerBackend
{
    private readonly IReadOnlyList<string> _models;
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requestCount;
    private int _cancellationObserved;

    public BlockingWorkerBackend(IReadOnlyList<string> models) => _models = models;

    public TaskCompletionSource<bool> Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int RequestCount => Volatile.Read(ref _requestCount);
    public bool CancellationObserved => Volatile.Read(ref _cancellationObserved) == 1;
    public IReadOnlyList<string> ReadConfiguredModels() => _models;

    public async Task<ExternalWorkerBackendResponse> CompleteAsync(
        ExternalWorkerBackendRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requestCount);
        Started.TrySetResult(true);
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _cancellationObserved, 1);
            throw;
        }
        return new ExternalWorkerBackendResponse(
            "OK_BLOCKING_WORKER",
            "stop",
            new ExternalWorkerTokenUsage(1, 1, 2),
            200,
            "upstream/model-a");
    }

    public void Release() => _release.TrySetResult(true);
}

internal sealed class TestWorkerAuditSink : IExternalWorkerAuditSink
{
    public List<ExternalWorkerAuditEntry> Entries { get; } = new();

    public ValueTask AppendAsync(
        ExternalWorkerAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entries.Add(entry);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FailingWorkerAuditSink : IExternalWorkerAuditSink
{
    public ValueTask AppendAsync(
        ExternalWorkerAuditEntry entry,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new IOException("test audit failure"));
}

internal sealed class TestRuntimeTruthSource : IRuntimeTruthSource
{
    private int _concurrentExecutionReads;
    private int _maxConcurrentExecutionReads;

    public RuntimeTruthPreferenceSource? Preference { get; init; }
    public string? CodexDefaultModel { get; init; }
    public CodexDesktopState? Task { get; init; }
    public ActiveRoute? Route { get; init; }
    public RuntimeRouteExecution? Execution { get; init; }
    public TimeSpan ExecutionDelay { get; set; }
    public TaskCompletionSource<bool>? ExecutionStarted { get; set; }
    public TaskCompletionSource<bool>? ExecutionRelease { get; set; }
    public int MaxConcurrentExecutionReads => Volatile.Read(ref _maxConcurrentExecutionReads);

    public RuntimeTruthPreferenceSource? ReadPreference() => Preference;

    public string? ReadCodexDefaultModel() => CodexDefaultModel;

    public Task<CodexDesktopState?> ReadTaskAsync(CancellationToken cancellationToken = default) =>
        System.Threading.Tasks.Task.FromResult(Task);

    public Task<ActiveRoute?> ReadConfiguredRouteAsync(CancellationToken cancellationToken = default) =>
        System.Threading.Tasks.Task.FromResult(Route);

    public async Task<RuntimeRouteExecution?> ReadLatestExecutionAsync(
        CancellationToken cancellationToken = default)
    {
        var current = Interlocked.Increment(ref _concurrentExecutionReads);
        UpdateMaximum(current);
        try
        {
            ExecutionStarted?.TrySetResult(true);
            if (ExecutionDelay > TimeSpan.Zero)
                await System.Threading.Tasks.Task.Delay(ExecutionDelay, cancellationToken);
            if (ExecutionRelease is not null)
                await ExecutionRelease.Task.WaitAsync(cancellationToken);
            return Execution;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentExecutionReads);
        }
    }

    public TestRuntimeTruthSource Clone(
        CodexDesktopState? task = null,
        RuntimeRouteExecution? execution = null) =>
        new()
        {
            Preference = Preference,
            CodexDefaultModel = CodexDefaultModel,
            Task = task ?? Task,
            Route = Route,
            Execution = execution ?? Execution,
            ExecutionDelay = ExecutionDelay
        };

    private void UpdateMaximum(int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxConcurrentExecutionReads);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref _maxConcurrentExecutionReads, value, current) == current) return;
        }
    }
}

internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public ScriptedHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => _handler(request, cancellationToken);

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
