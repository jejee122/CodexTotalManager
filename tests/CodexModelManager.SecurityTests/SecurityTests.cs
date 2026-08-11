using System.Text;
using System.Text.Json;
using CodexModelManager.Models;
using CodexModelManager.Services;
using CodexOpenCodexNative.Config;
using CodexOpenCodexNative.Host;
using CodexOpenCodexNative.Models;
using CodexOpenCodexNative.OAuth;
using CodexOpenCodexNative.Providers;
using Xunit;

namespace CodexModelManager.SecurityTests;

public sealed class LocalPortDefaultsTests
{
    [Fact]
    public void NativeProxy_DefaultPort_DoesNotCollideWithUnifiedGateway()
    {
        var config = new NativeProxyConfig();
        Assert.Equal(LocalPortPolicy.DefaultNativeEnginePort, config.ListenPort);
        Assert.NotEqual(LocalPortPolicy.DefaultUnifiedGatewayPort, config.ListenPort);
    }
}

public sealed class CodexConnectionSwitchTests
{
    [Fact]
    public void ConnectThenDisconnect_UsesOnlyTemporaryCodexHome_AndRestoresExactBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-codex-switch-" + Guid.NewGuid().ToString("N"));
        var codexHome = Path.Combine(root, ".codex");
        var configPath = Path.Combine(codexHome, "config.toml");
        Directory.CreateDirectory(codexHome);
        const string original =
            "model = \"gpt-user\"\n" +
            "model_provider = \"openai\"\n" +
            "model_reasoning_effort = \"high\"\n\n" +
            "[history]\n" +
            "keep = \"user-owned\"\n";
        File.WriteAllText(configPath, original, new UTF8Encoding(false));

        try
        {
            var service = new CodexConfigService(configPath, Path.Combine(root, "backups"));
            var before = service.ReadGatewaySnapshot();
            Assert.True(before.CanToggle);
            Assert.False(before.IsManagedConnected);
            Assert.Equal("openai", before.SelectedProviderId);

            Assert.True(service.EnsureManagedNativeProvider(createSnapshot: false));
            var connected = service.ReadGatewaySnapshot();
            Assert.True(connected.CanToggle);
            Assert.True(connected.IsManagedConnected);
            Assert.Equal("openai", connected.SelectedProviderId);
            Assert.Equal(CodexConfigService.DefaultManagedNativeBaseUrl, connected.CurrentGateway);
            var managed = File.ReadAllText(configPath, Encoding.UTF8);
            Assert.Contains("native-routing v2", managed, StringComparison.Ordinal);
            Assert.Contains("openai_base_url = \"http://127.0.0.1:10100/v1\"", managed, StringComparison.Ordinal);
            Assert.DoesNotContain("cmm_native", managed, StringComparison.Ordinal);
            Assert.Contains("keep = \"user-owned\"", managed, StringComparison.Ordinal);

            Assert.True(service.RemoveManagedNativeProvider(createSnapshot: false));
            Assert.Equal(original, File.ReadAllText(configPath, Encoding.UTF8));
            var disconnected = service.ReadGatewaySnapshot();
            Assert.True(disconnected.CanToggle);
            Assert.False(disconnected.IsManagedConnected);
            Assert.Equal("openai", disconnected.SelectedProviderId);
            Assert.False(Directory.Exists(Path.Combine(root, "backups")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class RuntimeModeIsolationTests
{
    [Fact]
    public async Task Initialize_ExplicitDetachedRoot_BindsEveryFakeStoreBelowIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-detached-mode-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CMM_DETACHED_UI", "1");
        Environment.SetEnvironmentVariable("CMM_DETACHED_NO_EXTERNAL_NETWORK", "1");
        Environment.SetEnvironmentVariable("CMM_DETACHED_DATA_ROOT", root);

        RuntimeMode.Initialize(Array.Empty<string>());

        Assert.True(RuntimeMode.IsDetachedUi);
        Assert.False(RuntimeMode.AllowsExternalStatusConnections);
        Assert.False(RuntimeMode.AllowsRealCodexConnectionToggle);
        Assert.Equal(Path.Combine(root, "codex-home"), Environment.GetEnvironmentVariable("CMM_SANDBOX_CODEX_HOME"));
        Assert.Equal(Path.Combine(root, "runtime"), Environment.GetEnvironmentVariable("CMM_SANDBOX_APPDATA"));
        Assert.Equal(Path.Combine(root, "native-home"), Environment.GetEnvironmentVariable("CMM_SANDBOX_OPENCODEX_HOME"));
        Assert.Equal(Path.Combine(root, "dream-skin"), Environment.GetEnvironmentVariable("CMM_SANDBOX_DREAMSKIN"));
        Assert.Equal(Path.Combine(root, "runtime"), Environment.GetEnvironmentVariable("CMM_RUNTIME_ROOT"));
        Assert.Equal("http://127.0.0.1:1", Environment.GetEnvironmentVariable("CMM_SANDBOX_OCX_URL"));

        var services = AppServices.Create();
        try
        {
            Assert.Equal(Path.Combine(root, "runtime"), services.Settings.DataDirectory);
            Assert.Equal(
                Path.Combine(root, "native-home", "usage.jsonl"),
                services.AccountUsageLedger.SourcePath);
            Assert.False(services.AccountUsageLedger.SourceMustBeAvailable);
            Assert.Empty(services.Dashboard.DiscoveredServerAliases);
            Assert.False(services.Dashboard.ServerCheckExists);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => services.LocalServices.GetStatusesAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => services.LocalServices.StartV2rayAsync());
            var desktop = await services.CodexDesktop.ReadStateAsync();
            Assert.False(desktop.Connected);
            var switchResult = await services.CodexDesktop.EnsureCurrentChatUsesAliasAsync("gpt-test");
            Assert.Equal(CodexAliasSwitchStatus.Unavailable, switchResult.Status);
            var restart = await services.CodexDesktop.RestartCodexWithDreamSkinAsync();
            Assert.False(restart.Success);
            var skin = await services.DreamSkin.ApplyInstalledThemeAsync("test-theme", allowRestart: true);
            Assert.Equal(DreamSkinOperationStatus.Failed, skin.Status);
        }
        finally
        {
            services.AccountUsageLedger.Dispose();
        }
    }
}

public sealed class CodexConversationContinuityTests
{
    private const string TargetModel = "cmm/main";

    [Fact]
    public void VerifySameTask_SameProcessTaskMessagesAndFingerprint_Succeeds()
    {
        var result = CodexConversationContinuity.VerifySameTask(Before(), After(), TargetModel);
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void VerifySameTask_ProcessChanged_Fails()
    {
        var result = CodexConversationContinuity.VerifySameTask(
            Before(),
            After() with { HostProcessId = 202 },
            TargetModel);
        Assert.False(result.Success);
    }

    [Fact]
    public void VerifySameTask_TaskIdentityChanged_Fails()
    {
        var result = CodexConversationContinuity.VerifySameTask(
            Before(),
            After() with { TaskIdentity = "task-2" },
            TargetModel);
        Assert.False(result.Success);
    }

    [Fact]
    public void VerifySameTask_MessageCountChanged_Fails()
    {
        var result = CodexConversationContinuity.VerifySameTask(
            Before(),
            After() with { VisibleMessageCount = 5 },
            TargetModel);
        Assert.False(result.Success);
    }

    [Fact]
    public void VerifySameTask_ConversationFingerprintChanged_Fails()
    {
        var result = CodexConversationContinuity.VerifySameTask(
            Before(),
            After() with { ConversationFingerprint = "fingerprint-b" },
            TargetModel);
        Assert.False(result.Success);
    }

    [Fact]
    public void VerifySameTask_NoContinuityEvidence_FailsClosed()
    {
        var before = new CodexDesktopState(true, false, "gpt-old", "before");
        var after = new CodexDesktopState(true, false, TargetModel, "after");
        var result = CodexConversationContinuity.VerifySameTask(before, after, TargetModel);
        Assert.False(result.Success);
    }

    [Fact]
    public void VerifySameTask_EvidenceMissingAfterSwitch_FailsClosed()
    {
        var after = new CodexDesktopState(true, false, TargetModel, "after");
        var result = CodexConversationContinuity.VerifySameTask(Before(), after, TargetModel);
        Assert.False(result.Success);
    }

    [Fact]
    public void AccountPoolPolicy_UsesStableDesktopAlias_AndNeverRestartsCodexAutomatically()
    {
        Assert.Equal(OpenCodexClient.SwitchAlias, AccountPoolService.StableDesktopAlias);
        Assert.Equal("cmm/main", AccountPoolService.StableDesktopAlias);
        Assert.False(AccountPoolService.AllowsAutomaticCodexRestart);
    }

    private static CodexDesktopState Before() => new(
        true,
        false,
        "gpt-old",
        "before",
        HostProcessId: 101,
        TaskIdentity: "task-1",
        VisibleMessageCount: 4,
        ConversationFingerprint: "fingerprint-a");

    private static CodexDesktopState After() => new(
        true,
        false,
        TargetModel,
        "after",
        HostProcessId: 101,
        TaskIdentity: "task-1",
        VisibleMessageCount: 4,
        ConversationFingerprint: "fingerprint-a");
}

public sealed class InternalRouteNamingTests
{
    [Fact]
    public void ActiveRouter_AcceptsOnlyCurrentCmmNamespace()
    {
        Assert.True(InternalRouteNames.IsAlias("cmm/main"));
        Assert.True(InternalRouteNames.IsAlias("CMM/pool-1"));
        Assert.False(InternalRouteNames.IsAlias("zcode/main"));
        Assert.False(InternalRouteNames.IsAlias("gpt-5.6-sol"));
    }

    [Fact]
    public void NativeConfig_Load_RewritesLegacyRouteNamesAndRemovesThemFromDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-route-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig
            {
                Combos =
                [
                    new ComboDefinition
                    {
                        Id = "zcode-switch",
                        Alias = "zcode/main",
                        Targets = [new ComboTargetDefinition { Provider = "test", Model = "model" }]
                    }
                ]
            });

            var loaded = store.Load();
            var combo = Assert.Single(loaded.Combos);
            Assert.Equal(InternalRouteNames.SwitchComboId, combo.Id);
            Assert.Equal(InternalRouteNames.MainAlias, combo.Alias);
            var raw = File.ReadAllText(store.ConfigPath);
            Assert.DoesNotContain("zcode", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PoolCatalog_Load_RewritesLegacyAliasesAndAdvancesSchema()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-pool-route-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "pools.json"), """
                {
                  "SchemaVersion": 3,
                  "Pools": [
                    {
                      "Id": "legacy-pool",
                      "DisplayName": "Legacy Pool",
                      "Transport": "CliProxyApi",
                      "Product": "CodexPlus",
                      "Enabled": true,
                      "RouteAlias": "zcode/custom",
                      "ProviderId": "cmm-legacy-pool",
                      "DefaultModel": "gpt-5.6-sol",
                      "BaseUrl": "http://127.0.0.1:18455/v1",
                      "LocalPort": 18455
                    }
                  ],
                  "Active": {
                    "PoolId": "legacy-pool",
                    "Model": "zcode/main",
                    "Verification": "legacy-route-test"
                  }
                }
                """);

            var catalog = new PoolCatalogService(dir);
            Assert.Null(catalog.LoadWarning);
            Assert.Equal("cmm/custom", catalog.Find("legacy-pool")?.RouteAlias);
            Assert.Equal(InternalRouteNames.MainAlias, catalog.GetActive().Model);
            using var json = JsonDocument.Parse(File.ReadAllText(catalog.FilePath));
            Assert.Equal(PoolCatalogService.CurrentSchemaVersion, json.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.DoesNotContain("zcode", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class V2rayProcessScopeTests
{
    [Fact]
    public void ManagedProcessScope_RejectsUnrelatedExecutablesInTheInstallDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "v2rayN-test-root");
        var configured = Path.Combine(root, "v2rayN.exe");
        Assert.True(LocalServiceControlService.IsManagedV2rayProcessPath(configured, configured, root));
        Assert.True(LocalServiceControlService.IsManagedV2rayProcessPath(
            Path.Combine(root, "bin", "xray.exe"), configured, root));
        Assert.False(LocalServiceControlService.IsManagedV2rayProcessPath(
            Path.Combine(root, "updater.exe"), configured, root));
        Assert.False(LocalServiceControlService.IsManagedV2rayProcessPath(
            Path.Combine(root, "tools", "helper.exe"), configured, root));
        Assert.False(LocalServiceControlService.IsManagedV2rayProcessPath(
            Path.Combine(Path.GetTempPath(), "outside", "xray.exe"), configured, root));
    }
}

public class OAuthTokenStoreTests
{
    [Fact]
    public void Save_ProducesEncryptedFile_NoPlaintextTokens()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OAuthTokenStore(dir);
            store.Save("chatgpt", new OAuthCredentials
            {
                Access = "test-access-token-secret-123",
                Refresh = "rt-refresh-token-secret-456",
                AccountId = "acct-1"
            });

            var raw = File.ReadAllText(Path.Combine(dir, "oauth-tokens.json"));
            Assert.DoesNotContain("test-access-token-secret-123", raw);
            Assert.DoesNotContain("rt-refresh-token-secret-456", raw);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OAuthTokenStore(dir);
            store.Save("chatgpt", new OAuthCredentials { Access = "tok-a", Refresh = "tok-r" });
            var loaded = store.Load("chatgpt");
            Assert.NotNull(loaded);
            Assert.Equal("tok-a", loaded.Access);
            Assert.Equal("tok-r", loaded.Refresh);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class NativeProxyConfigStoreTests
{
    [Fact]
    public void Save_EncryptsApiKeyAndAdmissionToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig
            {
                ListenPort = 10100,
                AdmissionToken = "admission-secret-xyz",
                Providers =
                [
                    new ProviderDefinition { Id = "deepseek", BaseUrl = "http://x/v1", ApiKey = "sk-plain-123" }
                ]
            });

            var raw = File.ReadAllText(Path.Combine(dir, "config.json"));
            Assert.DoesNotContain("admission-secret-xyz", raw);
            Assert.DoesNotContain("sk-plain-123", raw);
            Assert.Contains("dpapi:", raw);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_RoundTripsDecryptedSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig
            {
                ListenPort = 10100,
                AdmissionToken = "adm-1",
                Providers = [new ProviderDefinition { Id = "p", ApiKey = "key-1" }]
            });
            var loaded = store.Load();
            Assert.Equal("adm-1", loaded.AdmissionToken);
            Assert.Equal("key-1", loaded.Providers[0].ApiKey);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_CorruptConfig_ThrowsInsteadOfDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{corrupt-json!!!");
            var store = new NativeProxyConfigStore(dir);
            Assert.Throws<InvalidOperationException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpgradePlaintextSecrets_ConvertsLegacyPlaintext()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """
                {
                  "ListenPort": 10100,
                  "AdmissionToken": "legacy-plain-token",
                  "Providers": [
                    { "Id": "p", "BaseUrl": "http://x/v1", "ApiKey": "legacy-plain-key" }
                  ]
                }
                """);
            var store = new NativeProxyConfigStore(dir);
            var config = store.Load();
            Assert.Equal("legacy-plain-token", config.AdmissionToken);
            Assert.Equal("legacy-plain-key", config.Providers[0].ApiKey);

            var upgraded = store.UpgradePlaintextSecrets(config);
            Assert.True(upgraded);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("legacy-plain-token", raw);
            Assert.DoesNotContain("legacy-plain-key", raw);

            var reloaded = store.Load();
            Assert.Equal("legacy-plain-token", reloaded.AdmissionToken);
            Assert.Equal("legacy-plain-key", reloaded.Providers[0].ApiKey);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class NativeProxyAdmissionTests
{
    [Fact]
    public void Host_MissingAdmissionToken_FailsClosedBeforeListening()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig { ListenPort = 10100, AdmissionToken = null });
            var error = Assert.Throws<InvalidOperationException>(() =>
                new NativeProxyHost(store, dataRootOverride: dir));
            Assert.Contains("Admission Token", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class RouteResolverTests
{
    [Fact]
    public void Resolve_UnknownModel_ThrowsModelNotFound()
    {
        var registry = new ProviderRegistry(new NativeProxyConfig
        {
            Providers =
            [
                new ProviderDefinition { Id = "openai", Name = "OpenAI", Models = ["gpt-5.6-sol"], DefaultModel = "gpt-5.6-sol" }
            ]
        });
        Assert.Throws<CodexOpenCodexNative.Providers.ModelNotFoundException>(
            () => CodexOpenCodexNative.Providers.RouteResolver.Resolve(registry, "no-such-model"));
    }

    [Fact]
    public void Resolve_BareModelName_FindsProvider()
    {
        var registry = new ProviderRegistry(new NativeProxyConfig
        {
            Providers =
            [
                new ProviderDefinition { Id = "deepseek", Name = "DeepSeek", Models = ["deepseek-v4-flash"], DefaultModel = "deepseek-v4-flash" }
            ]
        });
        var result = CodexOpenCodexNative.Providers.RouteResolver.Resolve(registry, "deepseek-v4-flash");
        Assert.Equal("deepseek", result.ProviderId);
        Assert.Equal("deepseek-v4-flash", result.ModelId);
    }
}

public class WorkerBrokerPricingTests
{
    [Fact]
    public void GetRolePricing_ReturnsConfiguredValues()
    {
        // 通过 SubagentConfigurationService 的默认角色验证
        var service = new SubagentConfigurationService();
        var broker = new WorkerBroker(
            new ExternalWorkerService(
                new FakeConfigSource(),
                new FakeBackend(),
                new FakeAudit()),
            service);

        var pricing = broker.GetRolePricing("cmm_explorer");
        Assert.NotNull(pricing);
        Assert.Equal(2m, pricing.PricePerMillionTokens);
        Assert.Equal("USD", pricing.Currency);
        Assert.Equal(50m, pricing.BudgetLimit);
        Assert.Equal(300, pricing.MaxTimeoutSeconds);
    }

    [Fact]
    public void DelegateAsync_UnknownRole_FailsClosed()
    {
        var service = new SubagentConfigurationService();
        var broker = new WorkerBroker(
            new ExternalWorkerService(new FakeConfigSource(), new FakeBackend(), new FakeAudit()),
            service);

        var ex = Assert.Throws<WorkerBrokerException>(() =>
            broker.DelegateAsync("no_such_role", "task").GetAwaiter().GetResult());
        Assert.Equal("role_not_found", ex.Code);
    }

    [Fact]
    public void DelegateAsync_RoleWithoutPricing_FailsClosed()
    {
        var service = new SubagentConfigurationService();
        var broker = new WorkerBroker(
            new ExternalWorkerService(new FakeConfigSource(), new FakeBackend(), new FakeAudit()),
            service);

        // cmm_supervisor 不允许外部工人（AllowsExternalWorker=false）→ 失败关闭
        var ex = Assert.Throws<WorkerBrokerException>(() =>
            broker.DelegateAsync("cmm_supervisor", "task").GetAwaiter().GetResult());
        Assert.Equal("role_external_forbidden", ex.Code);
    }

    private sealed class FakeConfigSource : IExternalWorkerConfigurationSource
    {
        public string? LoadWarning => null;
        public IReadOnlyList<SubagentRoleDefinition> Roles { get; } = Array.Empty<SubagentRoleDefinition>();
        public SubagentConfigurationDocument LoadDraft() => new();
    }

    private sealed class FakeBackend : IExternalWorkerBackend
    {
        public IReadOnlyList<string> ReadConfiguredModels() => Array.Empty<string>();
        public Task<ExternalWorkerBackendResponse> CompleteAsync(ExternalWorkerBackendRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalWorkerBackendResponse("ok", "stop", new ExternalWorkerTokenUsage(1, 1, 2), 200, request.Model));
    }

    private sealed class FakeAudit : IExternalWorkerAuditSink
    {
        public ValueTask AppendAsync(ExternalWorkerAuditEntry entry, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}

public class WorkerBudgetLedgerTests
{
    [Fact]
    public async Task Deduct_Then_CheckBeforeCall_RejectsWhenBudgetExhausted()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var ledger = new WorkerBudgetLedger(path);
            var role = new SubagentRoleDefinition(
                "cmm_test", "测试", "t", "gpt-5.6-sol", "low", "read-only", true,
                "dev",
                PricePerMillionTokens: 1000m, // 每百万 token 1000 美元
                Currency: "USD",
                BudgetLimit: 0.001m,          // 预算 0.001 美元
                MaxTimeoutSeconds: 60);

            // 用 1000 token → 成本 0.001 美元 → 正好打满预算
            var cost = await ledger.DeductAsync(role, 1000, 0);
            Assert.Equal(1.0m, cost);
            Assert.Equal(1.0m, ledger.GetSpent("cmm_test"));

            // 预算耗尽 → 拒绝
            var block = ledger.CheckBeforeCall(role);
            Assert.NotNull(block);
            Assert.Contains("已耗尽", block);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Deduct_AccumulatesAcrossCalls()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var ledger = new WorkerBudgetLedger(path);
            var role = new SubagentRoleDefinition(
                "cmm_test2", "测试2", "t", "gpt-5.6-sol", "low", "read-only", true,
                "dev",
                PricePerMillionTokens: 2m,
                Currency: "USD",
                BudgetLimit: 10m,
                MaxTimeoutSeconds: 60);

            var c1 = await ledger.DeductAsync(role, 100000, 0);  // 0.2 美元
            var c2 = await ledger.DeductAsync(role, 100000, 0);  // 0.2 美元
            Assert.Equal(0.2m, c1);
            Assert.Equal(0.2m, c2);
            Assert.Equal(0.4m, ledger.GetSpent("cmm_test2"));

            var remaining = ledger.GetRemaining(role);
            Assert.NotNull(remaining);
            Assert.Equal(9.6m, remaining);
            Assert.Null(ledger.CheckBeforeCall(role));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CheckBeforeCall_RoleWithoutPricing_Rejects()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var ledger = new WorkerBudgetLedger(path);
            var role = new SubagentRoleDefinition(
                "cmm_noprice", "无价", "t", "gpt-5.6-sol", "low", "read-only", true, "dev");
            var block = ledger.CheckBeforeCall(role);
            Assert.NotNull(block);
            Assert.Contains("未配置价格", block);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Deduct_MissingUsage_ThrowsFailClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var ledger = new WorkerBudgetLedger(path);
            var role = new SubagentRoleDefinition(
                "cmm_nousage", "无用量", "t", "gpt-5.6-sol", "low", "read-only", true,
                "dev",
                PricePerMillionTokens: 2m,
                Currency: "USD",
                BudgetLimit: 10m,
                MaxTimeoutSeconds: 60);

            // usage 完全缺失时不得按 0 计费，必须失败关闭
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ledger.DeductAsync(role, null, null));
            Assert.Contains("未返回 token 用量", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
