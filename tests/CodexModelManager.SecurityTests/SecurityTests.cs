using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CodexModelManager.Models;
using CodexModelManager.Services;
using CodexOpenCodexNative.Config;
using CodexOpenCodexNative.Adapters;
using CodexOpenCodexNative.Host;
using CodexOpenCodexNative.Logging;
using CodexOpenCodexNative.Models;
using CodexOpenCodexNative.Providers;
using CodexOpenCodexNative.Responses;
using Xunit;

namespace CodexModelManager.SecurityTests;

public sealed class ReleaseAcceptanceGateTests
{
    [Fact]
    public void AppCrashFallback_RedactsCredentials_AndPreviewRequiresExplicitMode()
    {
        var repo = FindRepositoryRoot();
        var appSource = File.ReadAllText(
            Path.Combine(repo, @"src\CodexModelManager\App.xaml.cs"), Encoding.UTF8);

        Assert.Contains("DispatcherUnhandledException +=", appSource, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", appSource, StringComparison.Ordinal);
        Assert.Contains("TaskScheduler.UnobservedTaskException +=", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseDirectory.Contains(\"ui-preview\"", appSource, StringComparison.Ordinal);

        var redacted = App.RedactCrashText(
            "Authorization: Bearer session-secret Cookie=private-cookie api_key=sk-private admissionToken:local-secret");
        Assert.DoesNotContain("session-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("private-cookie", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("local-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeEngineShutdownPath_WaitsForEngineCleanup()
    {
        var stopped = false;
        var engine = new NativeEngineService { StoppedForTest = () => stopped = true };
        var root = Path.Combine(Path.GetTempPath(), "cmm-native-shutdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Assert.True(engine.Start(port, root));
            Assert.True(App.StopNativeEngineForShutdown(engine));
            Assert.True(stopped);
            Assert.Equal(NativeEngineState.Stopped, engine.State);
        }
        finally
        {
            engine.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExternalAcceptanceScripts_ParseOnWindowsPowerShellGrammar()
    {
        var repo = FindRepositoryRoot();
        foreach (var relativePath in new[]
                 {
                     @"scripts\validate-external-acceptance.ps1",
                     @"scripts\emit-evidence.ps1"
                 })
        {
            var path = Path.Combine(repo, relativePath);
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            var quotedPath = path.Replace("'", "''", StringComparison.Ordinal);
            startInfo.ArgumentList.Add(
                "$tokens=$null;$errors=$null;"
                + $"[void][System.Management.Automation.Language.Parser]::ParseFile('{quotedPath}',[ref]$tokens,[ref]$errors);"
                + "if($errors.Count){$errors|ForEach-Object Message;exit 1}");
            using var process = System.Diagnostics.Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start.");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10_000) && process.ExitCode == 0,
                $"{relativePath} is not compatible with Windows PowerShell grammar: {output}");
        }
    }

    [Fact]
    public void DeployableDecision_RequiresHashBoundRealCodexEvidence()
    {
        var repo = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repo, @"scripts\emit-evidence.ps1"), Encoding.UTF8);
        var validator = File.ReadAllText(
            Path.Combine(repo, @"scripts\validate-external-acceptance.ps1"), Encoding.UTF8);

        Assert.Contains("ExternalAcceptanceEvidencePath", script, StringComparison.Ordinal);
        Assert.Contains("PayloadManifestPath", script, StringComparison.Ordinal);
        Assert.Contains("$externalAcceptancePassed", script, StringComparison.Ordinal);
        Assert.Contains("candidateManifestSha256", validator, StringComparison.Ordinal);
        Assert.Contains("dedicatedTestComputer", validator, StringComparison.Ordinal);
        foreach (var check in new[]
                 {
                     "officialModelMessaging", "officialStreamingToolCalls",
                     "thirdPartyModelMessaging", "thirdPartyToolCalls",
                     "conversationContinuity", "accountPoolSwitch", "billingAttribution",
                     "codexNotRestarted", "skinCompatibility", "disconnectRestoresConfiguration"
                 })
            Assert.Contains(check, validator, StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerRetention_RemainsFailClosedUntilCrashSafeCompactionExists()
    {
        var repo = FindRepositoryRoot();
        var policy = File.ReadAllText(Path.Combine(repo, @"docs\LEDGER-RETENTION.md"), Encoding.UTF8);
        var service = File.ReadAllText(
            Path.Combine(repo, @"src\CodexModelManager\Services\AccountUsageLedgerService.cs"), Encoding.UTF8);

        Assert.Contains("No runtime path automatically deletes", policy, StringComparison.Ordinal);
        Assert.Contains("idempotent duplicate", policy, StringComparison.Ordinal);
        Assert.Contains("crash after every transaction stage", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactOldLedger", service, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteExpiredLedger", service, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexTotalManager.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("CodexTotalManager repository root was not found.");
    }
}

public sealed class ExtensionSecurityTests
{
    [Fact]
    public async Task Plugin_DefaultsDisabled_UsesLiteralArguments_AndDoesNotInheritSensitiveEnvironment()
    {
        using var fixture = new ExtensionFixture();
        fixture.AddPackage(
            "weather.demo",
            arguments: new[] { "hello & whoami", "two words" },
            capabilities: new[] { "network" });
        var service = fixture.CreateService();

        var discovered = Assert.Single(service.Discover().Packages);
        Assert.False(discovered.Enabled);
        Assert.Equal(new[] { "network" }, discovered.Manifest.Capabilities);

        Environment.SetEnvironmentVariable("CMM_TEST_SENSITIVE", "must-not-leak");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync("weather.demo"));
            service.Enable("weather.demo", discovered.Fingerprint);
            var output = new List<string>();
            var result = await service.RunAsync("weather.demo", output.Add);

            Assert.True(result.Success, result.Message);
            Assert.Contains(output, line => line.Contains("ARGS=hello & whoami|two words", StringComparison.Ordinal));
            Assert.Contains(output, line => line.Contains("ID=weather.demo", StringComparison.Ordinal));
            Assert.Contains(output, line => line.Contains("SENSITIVE=<absent>", StringComparison.Ordinal));
            Assert.DoesNotContain(output, line => line.Contains("must-not-leak", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CMM_TEST_SENSITIVE", null);
        }
    }

    [Fact]
    public void Plugin_FileChange_InvalidatesPreviousTrust()
    {
        using var fixture = new ExtensionFixture();
        var packageDirectory = fixture.AddPackage("weather.demo");
        var service = fixture.CreateService();
        var discovered = Assert.Single(service.Discover().Packages);
        Assert.True(service.Enable("weather.demo", discovered.Fingerprint).Enabled);

        File.AppendAllText(Path.Combine(packageDirectory, "plugin.exe"), "changed", Encoding.UTF8);
        var changed = Assert.Single(service.Discover().Packages);

        Assert.False(changed.Enabled);
        Assert.True(changed.TrustInvalidated);
    }

    [Fact]
    public void Plugin_EnableWithStaleDisplayedFingerprint_FailsClosed()
    {
        using var fixture = new ExtensionFixture();
        var packageDirectory = fixture.AddPackage("weather.demo");
        var service = fixture.CreateService();
        var displayed = Assert.Single(service.Discover().Packages);
        File.AppendAllText(Path.Combine(packageDirectory, "plugin.json"), " ", Encoding.UTF8);

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.Enable("weather.demo", displayed.Fingerprint));

        Assert.Contains("确认期间发生变化", error.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(service.Discover().Packages).Enabled);
    }

    [Fact]
    public void Plugin_PathTraversal_DuplicateIds_AndUnknownManifestFields_FailClosed()
    {
        using var fixture = new ExtensionFixture();
        fixture.AddRawPackage("escape", """
            {"schemaVersion":1,"id":"escape.demo","name":"Escape","version":"1.0.0","publisher":"test","description":"test","entry":"..\\plugin.exe","arguments":[],"capabilities":[]}
            """);
        fixture.AddRawPackage("unknown", """
            {"schemaVersion":1,"id":"unknown.demo","name":"Unknown","version":"1.0.0","publisher":"test","description":"test","entry":"plugin.exe","arguments":[],"capabilities":[],"secret":"no"}
            """, copyEntry: true);
        fixture.AddPackage("duplicate.demo", folderName: "duplicate-a");
        fixture.AddPackage("duplicate.demo", folderName: "duplicate-b");

        var result = fixture.CreateService().Discover();

        Assert.Empty(result.Packages);
        Assert.Equal(4, result.Issues.Count);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("越过", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("JSON property", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.Issues.Count(issue => issue.Message.Contains("重复", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Plugin_Crash_IsReportedWithoutCrashingManagerProcess()
    {
        using var fixture = new ExtensionFixture();
        fixture.AddPackage("crash.demo", arguments: new[] { "--crash" });
        var service = fixture.CreateService();
        var discovered = Assert.Single(service.Discover().Packages);
        service.Enable("crash.demo", discovered.Fingerprint);

        var result = await service.RunAsync("crash.demo");

        Assert.False(result.Success);
        Assert.Equal(7, result.ExitCode);
        Assert.False(service.IsRunning("crash.demo"));
    }

    private sealed class ExtensionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cmm-extension-tests-" + Guid.NewGuid().ToString("N"));
        private string Packages => Path.Combine(_root, "extensions", "packages");

        public ExtensionFixture() => Directory.CreateDirectory(Packages);

        public ExtensionService CreateService() => new(_root, Path.Combine(_root, "extensions"));

        public string AddPackage(
            string id,
            string? folderName = null,
            IReadOnlyList<string>? arguments = null,
            IReadOnlyList<string>? capabilities = null)
        {
            var directory = Path.Combine(Packages, folderName ?? id);
            Directory.CreateDirectory(directory);
            CopyTestPluginPayload(directory);
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                id,
                name = "Test plugin",
                version = "1.0.0",
                publisher = "tests",
                description = "Inert extension test helper.",
                entry = "plugin.exe",
                arguments = arguments ?? Array.Empty<string>(),
                capabilities = capabilities ?? Array.Empty<string>()
            });
            File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest, new UTF8Encoding(false));
            return directory;
        }

        public void AddRawPackage(string folderName, string manifest, bool copyEntry = false)
        {
            var directory = Path.Combine(Packages, folderName);
            Directory.CreateDirectory(directory);
            if (copyEntry) CopyTestPluginPayload(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static void CopyTestPluginPayload(string destination)
        {
            var directory = Path.GetDirectoryName(typeof(ExtensionTestPlugin.Marker).Assembly.Location)!;
            var payloads = Directory.EnumerateFiles(directory, "ExtensionTestPlugin.*").ToArray();
            if (!payloads.Any(path => Path.GetFileName(path).Equals("ExtensionTestPlugin.exe", StringComparison.OrdinalIgnoreCase)))
                throw new FileNotFoundException("扩展测试替身不存在。", Path.Combine(directory, "ExtensionTestPlugin.exe"));
            foreach (var source in payloads)
            {
                var name = Path.GetFileName(source);
                if (name.Equals("ExtensionTestPlugin.exe", StringComparison.OrdinalIgnoreCase))
                    name = "plugin.exe";
                File.Copy(source, Path.Combine(destination, name), overwrite: true);
            }
        }
    }
}

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
    public void OrdinaryDisconnectedLaunch_DoesNotBecomeIsolationMode()
    {
        var emptyEnvironment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        Assert.False(RuntimeMode.RequiresExplicitIsolation(
            Array.Empty<string>(),
            name => emptyEnvironment.GetValueOrDefault(name)));
        Assert.True(RuntimeMode.RequiresExplicitIsolation(
            new[] { "--detached-ui" },
            name => emptyEnvironment.GetValueOrDefault(name)));
        Assert.True(RuntimeMode.RequiresExplicitIsolation(
            Array.Empty<string>(),
            name => name == "CMM_DETACHED_DATA_ROOT" ? @"C:\fake-cmm" : null));
    }

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
                Path.Combine(root, "runtime", "native-proxy", "request-log.jsonl"),
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
            var skin = await services.DreamSkin.ApplyInstalledThemeAsync("test-theme", allowRestart: true);
            Assert.Equal(DreamSkinOperationStatus.Failed, skin.Status);
        }
        finally
        {
            services.AccountUsageLedger.Dispose();
        }
    }
}

public sealed class DisconnectedViewIsolationTests
{
    [Fact]
    public async Task AccountPools_DisconnectedView_DoesNotCallNativeCodexAccountApi()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-disconnected-pools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var requests = 0;
        var http = new HttpClient(new CountingHandler(() => Interlocked.Increment(ref requests)))
        {
            BaseAddress = new Uri("http://127.0.0.1:1")
        };

        try
        {
            var settings = new AppSettingsService(root);
            var secrets = new SecretStore(root);
            var catalog = new PoolCatalogService(root, settings.ReservedLocalPorts);
            var client = new OpenCodexClient(http);
            var codexPath = Path.Combine(root, "codex-home", "config.toml");
            var codex = new CodexConfigService(codexPath, Path.Combine(root, "codex-backups"));
            var process = new OpenCodexProcessService(
                settings,
                secrets,
                client,
                codex,
                new CodexModelCatalogService(codex),
                catalog,
                Path.Combine(root, "native-proxy"));
            var service = new AccountPoolService(
                catalog,
                new CliProxyPoolService(settings, secrets, poolCatalog: catalog),
                client,
                process,
                codex,
                new CodexDesktopBridgeService(),
                new ConfigBackupService(Path.Combine(root, "native-proxy", "config.json"), Path.Combine(root, "backups")),
                settings,
                secrets);

            var views = await service.ReadDisconnectedViewsAsync();

            Assert.Equal(0, requests);
            Assert.All(
                views.Where(view => view.SectionOrder == 0),
                view => Assert.Contains("没有读取 Codex 账号", view.StatusDetail, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Subagents_DraftOnly_DoesNotReadCodexConfigOrAgentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-draft-only-" + Guid.NewGuid().ToString("N"));
        var codexPath = Path.Combine(root, "codex-home", "config.toml");
        var agentsPath = Path.Combine(root, "codex-home", "agents");
        var draftPath = Path.Combine(root, "manager", "subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(codexPath)!);
        Directory.CreateDirectory(agentsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        File.WriteAllText(codexPath, "invalid = [", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(agentsPath, "private.toml"), "secret", new UTF8Encoding(false));

        try
        {
            var service = new SubagentConfigurationService(
                configPath: codexPath,
                agentsDirectory: agentsPath,
                dataPath: draftPath,
                backupRoot: Path.Combine(root, "backups"),
                bridgeExecutablePath: Environment.ProcessPath);

            var snapshot = service.InspectDraftOnly();

            Assert.False(snapshot.ConfigReadable);
            Assert.Equal("Codex 未连接（路径未读取）", snapshot.ConfigPath);
            Assert.Contains("没有读取 MCP 区块", snapshot.Bridge.StatusText, StringComparison.Ordinal);
            Assert.All(snapshot.AppliedRoles.Values, state => Assert.False(state.ExactMatch));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingHandler(Action count) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            count();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}

public sealed class ServerAllowlistTests
{
    [Fact]
    public void ExactlyFiveConfiguredAliases_AreAccepted_AndExtraSshHostsAreIgnored()
    {
        using var fixture = new ServerAllowlistFixture();
        var selected = fixture.Aliases.Take(5).ToArray();

        var validated = AppSettingsService.ValidateServerAliases(fixture.ConfigPath, selected);
        var dashboard = new DashboardStatusService(
            fixture.ResourceRoot,
            fixture.ConfigPath,
            fixture.ConfigHash,
            selected);

        Assert.Equal(selected, validated);
        Assert.Equal(5, dashboard.ExpectedServerCount);
        Assert.Equal(selected, dashboard.DiscoveredServerAliases);
        Assert.DoesNotContain(fixture.Aliases[5], dashboard.DiscoveredServerAliases);
    }

    [Fact]
    public void MissingDuplicateOrUnknownAliases_AreRejectedBeforeAnyServerCheck()
    {
        using var fixture = new ServerAllowlistFixture();

        Assert.Throws<ArgumentException>(() => AppSettingsService.ValidateServerAliases(
            fixture.ConfigPath,
            fixture.Aliases.Take(4).ToArray()));
        Assert.Throws<ArgumentException>(() => AppSettingsService.ValidateServerAliases(
            fixture.ConfigPath,
            new[] { fixture.Aliases[0], fixture.Aliases[0], fixture.Aliases[1], fixture.Aliases[2], fixture.Aliases[3] }));
        Assert.Throws<ArgumentException>(() => AppSettingsService.ValidateServerAliases(
            fixture.ConfigPath,
            new[] { fixture.Aliases[0], fixture.Aliases[1], fixture.Aliases[2], fixture.Aliases[3], "not-configured" }));
    }

    [Fact]
    public void SettingsRemainDisabledUntilPathHashAndFiveAliasesAllMatch()
    {
        using var fixture = new ServerAllowlistFixture();
        var data = Path.Combine(fixture.Root, "settings");
        var settings = new AppSettingsService(data);

        Assert.False(settings.ServerMonitoringEnabled);
        settings.SetServerMonitoringConfiguration(
            fixture.ConfigPath,
            fixture.ConfigHash,
            fixture.Aliases.Take(5).ToArray());
        Assert.True(settings.ServerMonitoringEnabled);
        Assert.Equal(5, settings.ServerAliases.Count);

        settings.SetServerMonitoringConfiguration(null, null, Array.Empty<string>());
        Assert.False(settings.ServerMonitoringEnabled);
        Assert.Empty(settings.ServerAliases);
    }

    private sealed class ServerAllowlistFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cmm-server-allowlist-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Root, "ssh-config");
        public string ResourceRoot => Path.Combine(Root, "resources");
        public string[] Aliases { get; } = ["srv-one", "srv-two", "srv-three", "srv-four", "srv-five", "srv-extra"];
        public string ConfigHash { get; }

        public ServerAllowlistFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(
                ConfigPath,
                string.Join(Environment.NewLine, Aliases.Select(alias => $"Host {alias}{Environment.NewLine}  HostName 127.0.0.1")),
                new UTF8Encoding(false));
            ConfigHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ConfigPath)));
            Directory.CreateDirectory(Path.Combine(ResourceRoot, "Server"));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Resources", "Server", "health-check.ps1"),
                Path.Combine(ResourceRoot, "Server", "health-check.ps1"));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
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

public sealed class RequestLogSummaryTests
{
    [Fact]
    public void Summarize_OfficialPassThrough2xxCountsAsCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-request-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new RequestLogService(root);
            log.Record(new RequestLogEntry
            {
                Provider = "openai",
                Status = "passed-through",
                HttpStatus = 200
            });

            var summary = log.Summarize();
            Assert.Equal(1, summary.TotalRequests);
            Assert.Equal(1, summary.CompletedRequests);
            Assert.Equal(1, summary.ByProvider["openai"].CompletedRequests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Summarize_FailedPassThroughDoesNotCountAsCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-request-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new RequestLogService(root);
            log.Record(new RequestLogEntry
            {
                Provider = "openai",
                Status = "passed-through",
                HttpStatus = 502
            });

            var summary = log.Summarize();
            Assert.Equal(1, summary.TotalRequests);
            Assert.Equal(0, summary.CompletedRequests);
            Assert.Equal(0, summary.ByProvider["openai"].CompletedRequests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

public class NativeProxyConfigStoreTests
{
    [Fact]
    public void Load_AcceptsLegacyCamelCaseAndSaveUsesUniqueTemporaryFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-cfg-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {"listenPort":10123,"admissionToken":"legacy-token","autoSwitchThreshold":71,"failoverThreshold":4,"providers":[],"combos":[]}
                """);
            var store = new NativeProxyConfigStore(dir);
            var loaded = store.Load();
            Assert.Equal(10123, loaded.ListenPort);
            Assert.Equal("legacy-token", loaded.AdmissionToken);
            Assert.Equal(71, loaded.AutoSwitchThreshold);
            Assert.Equal(4, loaded.FailoverThreshold);

            store.Save(loaded);
            Assert.Empty(Directory.GetFiles(dir, "config.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_RejectsStaleSnapshotInsteadOfOverwritingNewerConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-cfg-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = new NativeProxyConfigStore(dir);
            first.Save(new NativeProxyConfig { ListenPort = 10100, AdmissionToken = "admission" });
            var stale = first.Load();
            var second = new NativeProxyConfigStore(dir);
            second.Update(config => config.AutoSwitchThreshold = 70);
            stale.FailoverThreshold = 4;

            var error = Assert.Throws<InvalidOperationException>(() => first.Save(stale));
            Assert.Contains("另一个进程更新", error.Message, StringComparison.Ordinal);
            var current = first.Load();
            Assert.Equal(70, current.AutoSwitchThreshold);
            Assert.Equal(0, current.FailoverThreshold);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

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

    [Fact]
    public async Task CodexBearer_IsRestrictedToOfficialPassThrough_WhileAdmissionTokenCanUseThirdParty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-admission-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        NativeProxyHost? host = null;
        try
        {
            var port = GetUnusedLoopbackPort();
            var captured = new List<(string Host, string? Authorization)>();
            using var upstream = new HttpClient(new AdmissionRoutingHandler(request =>
            {
                lock (captured)
                    captured.Add((request.RequestUri?.Host ?? string.Empty,
                        request.Headers.Authorization?.ToString()));
                var body = request.RequestUri?.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) == true
                    ? "{\"id\":\"resp_official\",\"object\":\"response\",\"status\":\"completed\",\"output\":[]}"
                    : "{\"id\":\"resp_third_party\",\"object\":\"response\",\"status\":\"completed\",\"output\":[]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }));
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig
            {
                ListenPort = port,
                AdmissionToken = "local-admission-secret",
                DefaultProvider = "openai",
                Providers =
                [
                    new ProviderDefinition
                    {
                        Id = "third-party",
                        Name = "Third party",
                        Adapter = "openai-responses",
                        BaseUrl = "https://models.example.test/v1",
                        ApiKey = "third-party-key",
                        DefaultModel = "model-a",
                        Models = ["model-a"]
                    }
                ]
            });
            host = new NativeProxyHost(store, upstream: upstream, dataRootOverride: dir);
            await host.Application.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using var official = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"hello\",\"stream\":false}",
                    Encoding.UTF8, "application/json")
            };
            official.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "real-codex-session");
            using var officialResponse = await client.SendAsync(official);
            Assert.Equal(HttpStatusCode.OK, officialResponse.StatusCode);

            using var forbidden = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent("{\"model\":\"third-party/model-a\",\"input\":\"hello\",\"stream\":false}",
                    Encoding.UTF8, "application/json")
            };
            forbidden.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "arbitrary-unvalidated-session");
            using var forbiddenResponse = await client.SendAsync(forbidden);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);

            using var officialWarmup = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent("{\"model\":\"gpt-5.6-sol\",\"input\":\"validate session\",\"stream\":false}",
                    Encoding.UTF8, "application/json")
            };
            officialWarmup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "second-real-codex-session");
            using var officialWarmupResponse = await client.SendAsync(officialWarmup);
            Assert.Equal(HttpStatusCode.OK, officialWarmupResponse.StatusCode);

            using var validatedSession = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent("{\"model\":\"third-party/model-a\",\"input\":\"hello\",\"stream\":false}",
                    Encoding.UTF8, "application/json")
            };
            validatedSession.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "second-real-codex-session");
            using var validatedSessionResponse = await client.SendAsync(validatedSession);
            Assert.Equal(HttpStatusCode.OK, validatedSessionResponse.StatusCode);

            using var admitted = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent("{\"model\":\"third-party/model-a\",\"input\":\"hello\",\"stream\":false}",
                    Encoding.UTF8, "application/json")
            };
            admitted.Headers.TryAddWithoutValidation("X-CMM-Admission", "Bearer local-admission-secret");
            using var admittedResponse = await client.SendAsync(admitted);
            Assert.Equal(HttpStatusCode.OK, admittedResponse.StatusCode);

            lock (captured)
            {
                Assert.Equal(4, captured.Count);
                Assert.Contains(captured, item => item.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
                                                   && item.Authorization == "Bearer real-codex-session");
                Assert.Contains(captured, item => item.Host.Equals("models.example.test", StringComparison.OrdinalIgnoreCase)
                                                   && item.Authorization == "Bearer third-party-key");
            }
        }
        finally
        {
            if (host is not null) await host.StopAsync();
            Directory.Delete(dir, true);
        }
    }

    private static int GetUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class AdmissionRoutingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(factory(request));
    }
}

public class NativeProxyStreamingCompatibilityTests
{
    [Fact]
    public async Task OfficialCodexResponsesStream_IsReturnedByteForByteWithoutLocalEventRepackaging()
    {
        const string upstream = "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_official_exact\"}}\n\n"
                                + "event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_exact\",\"delta\":\"{}\"}\n\n";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream")
        }));
        var adapter = new OpenAiResponsesAdapter(client);
        var provider = new ProviderDefinition
        {
            Id = "openai",
            Adapter = "openai-responses",
            BaseUrl = "https://chatgpt.com/backend-api/codex",
            DefaultModel = "gpt-test",
            Models = ["gpt-test"]
        };
        var request = new OcxParsedRequest
        {
            Stream = true,
            RawBody = "{\"model\":\"gpt-test\",\"input\":\"hello\",\"stream\":true}",
            ForwardHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer real-codex-session" }
        };

        await using var result = await adapter.FetchAsync(provider, request, "gpt-test", CancellationToken.None);

        Assert.True(result.Streaming);
        Assert.NotNull(result.RawStream);
        Assert.Null(result.Events);
        using var reader = new StreamReader(result.RawStream!, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(upstream, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ThirdPartyResponsesStream_PreservesFunctionCallEvents()
    {
        const string upstream = "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"weather\",\"arguments\":\"\"}}\n\n"
                                + "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":0,\"delta\":\"{\\\"city\\\":\"}\n\n"
                                + "data: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":0,\"delta\":\"\\\"Beijing\\\"}\"}\n\n"
                                + "data: {\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_1\",\"output_index\":0,\"arguments\":\"{\\\"city\\\":\\\"Beijing\\\"}\"}\n\n"
                                + "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"weather\",\"arguments\":\"{\\\"city\\\":\\\"Beijing\\\"}\"}],\"usage\":{\"input_tokens\":4,\"output_tokens\":2,\"total_tokens\":6}}}\n\n"
                                + "data: [DONE]\n\n";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream")
        }));
        var adapter = new OpenAiResponsesAdapter(client);
        var provider = new ProviderDefinition
        {
            Id = "private",
            Adapter = "openai-responses",
            BaseUrl = "https://models.example.test/v1",
            ApiKey = "provider-only-key",
            DefaultModel = "model-a",
            Models = ["model-a"]
        };

        await using var result = await adapter.FetchAsync(
            provider,
            new OcxParsedRequest { Stream = true, Messages = [new OcxMessage { Role = "user", Content = "hello" }] },
            "model-a",
            CancellationToken.None);
        var events = await CollectAsync(result.Events!);

        Assert.Contains(events, value => value.Type == "function_call"
                                         && value.CallId == "call_1"
                                         && value.FunctionName == "weather"
                                         && value.ToolCallIndex == 0);
        var done = Assert.Single(events, value => value.Type == "function_call_done");
        Assert.Equal("call_1", done.CallId);
        Assert.Equal("{\"city\":\"Beijing\"}", done.Arguments);
        Assert.Equal("done", events.Last().Type);
    }

    [Fact]
    public async Task ChatStream_PreservesMultipleToolCallIdsIndexesAndArguments()
    {
        const string upstream = "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"type\":\"function\",\"function\":{\"name\":\"alpha\",\"arguments\":\"{\"}},{\"index\":1,\"id\":\"call_b\",\"type\":\"function\",\"function\":{\"name\":\"beta\",\"arguments\":\"{\"}}]},\"finish_reason\":null}]}\n\n"
                                + "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"}\"}},{\"index\":1,\"function\":{\"arguments\":\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n"
                                + "data: [DONE]\n\n";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream")
        }));
        var adapter = new OpenAiChatAdapter(client);
        var provider = new ProviderDefinition { Id = "chat", BaseUrl = "https://chat.example.test/v1", ApiKey = "key" };

        await using var result = await adapter.FetchAsync(
            provider,
            new OcxParsedRequest { Stream = true, Messages = [new OcxMessage { Role = "user", Content = "hello" }] },
            "m",
            CancellationToken.None);
        var events = await CollectAsync(result.Events!);
        var completed = events.Where(value => value.Type == "function_call_done").OrderBy(value => value.ToolCallIndex).ToArray();

        Assert.Equal(2, completed.Length);
        Assert.Equal(("call_a", "alpha", "{}", 0), (completed[0].CallId, completed[0].FunctionName, completed[0].Arguments, completed[0].ToolCallIndex));
        Assert.Equal(("call_b", "beta", "{}", 1), (completed[1].CallId, completed[1].FunctionName, completed[1].Arguments, completed[1].ToolCallIndex));

        var bridge = new ResponsesBridge("m");
        var bridgedFrames = new StringBuilder();
        await foreach (var frame in bridge.StreamAsync(ToAsync(events), CancellationToken.None))
            bridgedFrames.Append(frame);
        Assert.Contains("\"call_id\":\"call_a\"", bridgedFrames.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"call_id\":\"call_b\"", bridgedFrames.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, bridge.GetContinuationMessages().Sum(message => message.ToolCalls?.Count ?? 0));
    }

    [Fact]
    public async Task AnthropicToolUse_IsParsedAndBridgedInBothDirections()
    {
        const string upstream = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}\n\n"
                                + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"weather\",\"input\":{}}}\n\n"
                                + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"city\\\":\\\"Beijing\\\"}\"}}\n\n"
                                + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
                                + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":2}}\n\n"
                                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream")
        }));
        var adapter = new AnthropicAdapter(client);
        var provider = new ProviderDefinition { Id = "claude", BaseUrl = "https://claude.example.test", ApiKey = "key" };

        await using var result = await adapter.FetchAsync(
            provider,
            new OcxParsedRequest { Stream = true, Messages = [new OcxMessage { Role = "user", Content = "hello" }] },
            "claude-test",
            CancellationToken.None);
        var events = await CollectAsync(result.Events!);
        var toolDone = Assert.Single(events, value => value.Type == "function_call_done");
        Assert.Equal(("toolu_1", "weather", "{\"city\":\"Beijing\"}"),
            (toolDone.CallId, toolDone.FunctionName, toolDone.Arguments));
        Assert.Contains(events, value => value.Type == "finish" && value.FinishReason == "tool_calls");

        var bridge = new AnthropicOutboundBridge();
        var frames = new StringBuilder();
        await foreach (var frame in bridge.StreamAsync(ToAsync(events), "claude-test", CancellationToken.None))
            frames.Append(frame);
        Assert.Contains("\"type\":\"tool_use\"", frames.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"type\":\"input_json_delta\"", frames.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stop_reason\":\"tool_use\"", frames.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicNonStreamingToolUse_IsNotCollapsedIntoEmptyText()
    {
        const string upstream = "{\"id\":\"msg_upstream\",\"type\":\"message\",\"role\":\"assistant\","
                                + "\"content\":[{\"type\":\"text\",\"text\":\"I will check.\"},{\"type\":\"tool_use\",\"id\":\"toolu_weather\",\"name\":\"weather\",\"input\":{\"city\":\"Beijing\"}}],"
                                + "\"stop_reason\":\"tool_use\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "application/json")
        }));
        var adapter = new AnthropicAdapter(client);
        var provider = new ProviderDefinition { Id = "claude", BaseUrl = "https://claude.example.test", ApiKey = "key" };

        await using var result = await adapter.FetchAsync(
            provider,
            new OcxParsedRequest { Stream = false, Messages = [new OcxMessage { Role = "user", Content = "hello" }] },
            "claude-test",
            CancellationToken.None);

        Assert.Equal("tool_calls", result.FinishReason);
        Assert.Equal("I will check.", result.Message?.Content);
        var call = Assert.Single(result.Message?.ToolCalls ?? []);
        Assert.Equal(("toolu_weather", "weather", "{\"city\":\"Beijing\"}"),
            (call.Id, call.Function?.Name, call.Function?.Arguments));
        Assert.Equal((5L, 3L, 8L),
            (result.Usage?.PromptTokens, result.Usage?.CompletionTokens, result.Usage?.TotalTokens));

        var anthropic = NativeProxyHost.BuildAnthropicNonStreamingResponse(
            "claude-test", result.Message!, result.FinishReason, 5, 3).ToJsonString();
        Assert.Contains("\"type\":\"tool_use\"", anthropic, StringComparison.Ordinal);
        Assert.Contains("\"stop_reason\":\"tool_use\"", anthropic, StringComparison.Ordinal);

        var responses = NativeProxyHost.BuildResponsesNonStreamingResponse(
            "claude-test", result.Message, result.Usage, result.FinishReason).ToJsonString();
        Assert.Contains("\"type\":\"function_call\"", responses, StringComparison.Ordinal);
        Assert.Contains("\"call_id\":\"toolu_weather\"", responses, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoogleNonStreamingFunctionCall_AndLengthStop_AreNormalized()
    {
        const string upstream = "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"weather\",\"args\":{\"city\":\"Beijing\"}}}]},\"finishReason\":\"STOP\"}],"
                                + "\"usageMetadata\":{\"promptTokenCount\":4,\"candidatesTokenCount\":2,\"totalTokenCount\":6}}";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "application/json")
        }));
        var adapter = new GoogleAdapter(client);
        var provider = new ProviderDefinition { Id = "gemini", BaseUrl = "https://google.example.test", ApiKey = "key" };

        await using var result = await adapter.FetchAsync(
            provider,
            new OcxParsedRequest { Stream = false, Messages = [new OcxMessage { Role = "user", Content = "hello" }] },
            "gemini-test",
            CancellationToken.None);

        Assert.Equal("tool_calls", result.FinishReason);
        var call = Assert.Single(result.Message?.ToolCalls ?? []);
        Assert.Equal("weather", call.Function?.Name);
        Assert.Equal("{\"city\":\"Beijing\"}", call.Function?.Arguments);
    }

    [Fact]
    public async Task ResponsesBridge_LengthFinish_IsIncompleteRatherThanSuccessful()
    {
        var bridge = new ResponsesBridge("model-a");
        var frames = new StringBuilder();
        await foreach (var frame in bridge.StreamAsync(ToAsync(
                           [
                               new AdapterEvent { Type = "text", Text = "partial", Role = "assistant" },
                               new AdapterEvent { Type = "finish", FinishReason = "length" }
                           ]), CancellationToken.None))
            frames.Append(frame);

        Assert.Equal("incomplete", bridge.Status);
        Assert.Contains("response.incomplete", frames.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("response.completed", frames.ToString(), StringComparison.Ordinal);
    }

    private static async Task<List<AdapterEvent>> CollectAsync(IAsyncEnumerable<AdapterEvent> source)
    {
        var values = new List<AdapterEvent>();
        await foreach (var value in source) values.Add(value);
        return values;
    }

    private static async IAsyncEnumerable<AdapterEvent> ToAsync(IEnumerable<AdapterEvent> values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private sealed class StaticHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(factory(request));
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
            Assert.Equal(1.0m, await ledger.GetSpentAsync("cmm_test"));

            // 预算耗尽 → 拒绝
            var block = await ledger.CheckBeforeCallAsync(role);
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
            Assert.Equal(0.4m, await ledger.GetSpentAsync("cmm_test2"));

            var remaining = await ledger.GetRemainingAsync(role);
            Assert.NotNull(remaining);
            Assert.Equal(9.6m, remaining);
            Assert.Null(await ledger.CheckBeforeCallAsync(role));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CheckBeforeCall_RoleWithoutPricing_Rejects()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var ledger = new WorkerBudgetLedger(path);
            var role = new SubagentRoleDefinition(
                "cmm_noprice", "无价", "t", "gpt-5.6-sol", "low", "read-only", true, "dev");
            var block = await ledger.CheckBeforeCallAsync(role);
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

    [Fact]
    public async Task ConcurrentInstances_CannotBothReserveTheSameRemainingBudget()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var role = CreateRole("cmm_concurrent", price: 1_000_000m, limit: 1m);
            var first = new WorkerBudgetLedger(path);
            var second = new WorkerBudgetLedger(path);

            var attempts = await Task.WhenAll(
                TryReserveAsync(first, role, 1),
                TryReserveAsync(second, role, 1));

            Assert.Equal(1, attempts.Count(value => value));
            Assert.Single(await first.PendingReservationsAsync());
            Assert.Equal(1m, await first.GetSpentAsync(role.Id));
        }
        finally
        {
            DeleteBudgetFiles(path);
        }
    }

    [Fact]
    public async Task ExpiredReservation_IsReclaimedAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var expiredAt = DateTimeOffset.UtcNow.AddHours(-25);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                roles = Array.Empty<object>(),
                pendingReservations = new[]
                {
                    new
                    {
                        id = "expired-reservation",
                        roleId = "cmm_ttl",
                        currency = "USD",
                        reservedCost = 1m,
                        createdAt = expiredAt
                    }
                }
            }));

            var ledger = new WorkerBudgetLedger(path);
            Assert.Empty(await ledger.PendingReservationsAsync());
            Assert.Equal(0m, await ledger.GetSpentAsync("cmm_ttl"));

            var role = CreateRole("cmm_ttl", price: 1_000_000m, limit: 1m);
            var reservation = await ledger.ReserveAsync(role, 1);
            Assert.Equal(1m, reservation.ReservedCost);
        }
        finally
        {
            DeleteBudgetFiles(path);
        }
    }

    [Fact]
    public async Task ManualRelease_RequiresMatchingRoleAndOnlyReleasesRequestedReservation()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmm-budget-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var role = CreateRole("cmm_release", price: 1_000_000m, limit: 2m);
            var ledger = new WorkerBudgetLedger(path);
            var first = await ledger.ReserveAsync(role, 1);
            var second = await ledger.ReserveAsync(role, 1);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ledger.ReleaseReservationAsync(first.Id, "wrong-role"));
            Assert.Equal(2, (await ledger.PendingReservationsAsync()).Count);

            await ledger.ReleaseReservationAsync(first.Id, role.Id);
            var remaining = Assert.Single(await ledger.PendingReservationsAsync());
            Assert.Equal(second.Id, remaining.Id);
            Assert.Equal(1m, await ledger.GetSpentAsync(role.Id));
        }
        finally
        {
            DeleteBudgetFiles(path);
        }
    }

    private static SubagentRoleDefinition CreateRole(string id, decimal price, decimal limit) =>
        new(id, id, "test", "model", "low", "read-only", true, "dev",
            PricePerMillionTokens: price,
            Currency: "USD",
            BudgetLimit: limit,
            MaxTimeoutSeconds: 60);

    private static async Task<bool> TryReserveAsync(
        WorkerBudgetLedger ledger,
        SubagentRoleDefinition role,
        long maximumTokens)
    {
        try
        {
            await ledger.ReserveAsync(role, maximumTokens);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void DeleteBudgetFiles(string path)
    {
        File.Delete(path);
        File.Delete(path + ".lock");
    }
}

public class LocalConfigurationTransactionTests
{
    [Fact]
    public async Task SecretStore_ConcurrentInstancesMergeDifferentSecretsWithoutLostUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-secrets-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var first = new SecretStore(root);
            var second = new SecretStore(root);
            await Task.WhenAll(
                Task.Run(() => first.Save("provider-a", "secret-a")),
                Task.Run(() => second.Save("provider-b", "secret-b")));

            var reloaded = new SecretStore(root);
            Assert.Equal("secret-a", reloaded.Read("provider-a"));
            Assert.Equal("secret-b", reloaded.Read("provider-b"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AppSettings_StaleInstanceRefusesToOverwriteNewerSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var first = new AppSettingsService(root);
            var second = new AppSettingsService(root);
            first.SetBackupRetention(17, 45, true);

            var error = Assert.Throws<InvalidOperationException>(() =>
                second.SetProviderName("provider-b", "Provider B"));
            Assert.Contains("另一个总管家进程", error.Message, StringComparison.Ordinal);

            var reloaded = new AppSettingsService(root);
            Assert.Equal(17, reloaded.BackupRetentionCount);
            Assert.Equal(45, reloaded.BackupRetentionDays);
            Assert.True(reloaded.BackupAutoCleanup);
            Assert.False(reloaded.TryGetProviderName("provider-b", out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PoolCatalog_StaleInstanceRefusesToOverwriteNewerCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-pools-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var first = new PoolCatalogService(root);
            var second = new PoolCatalogService(root);
            var added = first.AddCliProxyPool(AccountProduct.CodexPlus);

            var error = Assert.Throws<InvalidOperationException>(() =>
                second.AddCliProxyPool(AccountProduct.CodexPro));
            Assert.Contains("另一个总管家进程", error.Message, StringComparison.Ordinal);

            var reloaded = new PoolCatalogService(root);
            Assert.NotNull(reloaded.Find(added.Id));
            Assert.Single(reloaded.GetPools(), pool => pool.Id == added.Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
