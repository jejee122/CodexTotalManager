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

        Assert.Contains("ç¡®è®¤æœŸé—´å‘ç”Ÿå˜åŒ–", error.Message, StringComparison.Ordinal);
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
        Assert.Contains(result.Issues, issue => issue.Message.Contains("è¶Šè¿‡", StringComparison.Ordinal));
        Assert.Contains(result.Issues, issue => issue.Message.Contains("JSON property", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.Issues.Count(issue => issue.Message.Contains("é‡å¤", StringComparison.Ordinal)));
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
                throw new FileNotFoundException("æ‰©å±•æµ‹è¯•æ›¿èº«ä¸å­˜åœ¨ã€‚", Path.Combine(directory, "ExtensionTestPlugin.exe"));
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
    ×Îv¶‰ËkºwµçpÑÉÕ”¤ì(€€€€€€€ô(€€€ô)ô()ÁÕ‰±¥ŒÍ•…±•±…ÍÌXÉÉ…åAÉ½•ÍÍM½Á•Q•ÍÑÌ)ì(€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥5…¹…•‘AÉ½•ÍÍM½Á•}I•©•ÑÍU¹É•±…Ñ•‘á•ÕÑ…‰±•Í%¹Q¡•%¹ÍÑ…±±¥É•Ñ½Éä ¤(€€€ì(€€€€€€€Ù…ÈÉ½½Ğ€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰ØÉÉ…å8µÑ•ÍĞµÉ½½Ğˆ¤ì(€€€€€€€Ù…È½¹™¥ÕÉ•€ôA…Ñ ¹½µ‰¥¹”¡É½½Ğ°€‰ØÉÉ…å8¹•á”ˆ¤ì(€€€€€€€ÍÍ•ÉĞ¹QÉÕ”¡1½…±M•ÉÙ¥•½¹ÑÉ½±M•ÉÙ¥”¹%Í5…¹…•‘XÉÉ…åAÉ½•ÍÍA…Ñ ¡½¹™¥ÕÉ•°½¹™¥ÕÉ•°É½½Ğ¤¤ì(€€€€€€€ÍÍ•ÉĞ¹QÉÕ”¡1½…±M•ÉÙ¥•½¹ÑÉ½±M•ÉÙ¥”¹%Í5…¹…•‘XÉÉ…åAÉ½•ÍÍA…Ñ  (€€€€€€€€€€€A…Ñ ¹½µ‰¥¹”¡É½½Ğ°€‰‰¥¸ˆ°€‰áÉ…ä¹•á”ˆ¤°½¹™¥ÕÉ•°É½½Ğ¤¤ì(€€€€€€€ÍÍ•ÉĞ¹…±Í”¡1½…±M•ÉÙ¥•½¹ÑÉ½±M•ÉÙ¥”¹%Í5…¹…•‘XÉÉ…åAÉ½•ÍÍA…Ñ  (€€€€€€€€€€€A…Ñ ¹½µ‰¥¹”¡É½½Ğ°€‰ÕÁ‘…Ñ•È¹•á”ˆ¤°½¹™¥ÕÉ•°É½½Ğ¤¤ì(€€€€€€€ÍÍ•ÉĞ¹…±Í”¡1½…±M•ÉÙ¥•½¹ÑÉ½±M•ÉÙ¥”¹%Í5…¹…•‘XÉÉ…åAÉ½•ÍÍA…Ñ  (€€€€€€€€€€€A…Ñ ¹½µ‰¥¹”¡É½½Ğ°€‰Ñ½½±Ìˆ°€‰¡•±Á•È¹•á”ˆ¤°½¹™¥ÕÉ•°É½½Ğ¤¤ì(€€€€€€€ÍÍ•ÉĞ¹…±Í”¡1½…±M•ÉÙ¥•½¹ÑÉ½±M•ÉÙ¥”¹%Í5…¹…•‘XÉÉ…åAÉ½•ÍÍA…Ñ  (€€€€€€€€€€€A…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰½ÕÑÍ¥‘”ˆ°€‰áÉ…ä¹•á”ˆ¤°½¹™¥ÕÉ•°É½½Ğ¤¤ì(€€€ô)ô()ÁÕ‰±¥Œ±…ÍÌ=ÕÑ¡Q½­•¹MÑ½É•Q•ÍÑÌ)ì(€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥M…Ù•}AÉ½‘Õ•Í¹ÉåÁÑ•‘¥±•}9½A±…¥¹Ñ•áÑQ½­•¹Ì ¤(€€€ì(€€€€€€€Ù…È‘¥È€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ½…ÕÑ ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤¤ì(€€€€€€€¥É•Ñ½Éä¹É•…Ñ•¥É•Ñ½Éä¡‘¥È¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÑ½É”€ô¹•Ü=ÕÑ¡Q½­•¹MÑ½É”¡‘¥È¤ì(€€€€€€€€€€€ÍÑ½É”¹M…Ù” ‰¡…ÑÁĞˆ°¹•Ü=ÕÑ¡É•‘•¹Ñ¥…±Ì(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€•ÍÌ€ô€‰Ñ•ÍĞµ…•ÍÌµÑ½­•¸µÍ•É•Ğ´ÄÈÌˆ°(€€€€€€€€€€€€€€€I•™É•Í €ô€‰ÉĞµÉ•™É•Í µÑ½­•¸µÍ•É•Ğ´ĞÔØˆ°(€€€€€€€€€€€€€€€½Õ¹Ñ%€ô€‰…Ğ´Äˆ(€€€€€€€€€€€ô¤ì((€€€€€€€€€€€Ù…ÈÉ…Ü€ô¥±”¹I•…‘±±Q•áĞ¡A…Ñ ¹½µ‰¥¹”¡‘¥È°€‰½…ÕÑ µÑ½­•¹Ì¹©Í½¸ˆ¤¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½•Í9½Ñ½¹Ñ…¥¸ ‰Ñ•ÍĞµ…•ÍÌµÑ½­•¸µÍ•É•Ğ´ÄÈÌˆ°É…Ü¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½•Í9½Ñ½¹Ñ…¥¸ ‰ÉĞµÉ•™É•Í µÑ½­•¸µÍ•É•Ğ´ĞÔØˆ°É…Ü¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥É•Ñ½Éä¹•±•Ñ”¡‘¥È°ÑÉÕ”¤ì(€€€€€€€ô(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥M…Ù•}Q¡•¹1½…‘}I½Õ¹‘QÉ¥ÁÍQ½­•¸ ¤(€€€ì(€€€€€€€Ù…È‘¥È€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ½…ÕÑ ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤¤ì(€€€€€€€¥É•Ñ½Éä¹É•…Ñ•¥É•Ñ½Éä¡‘¥È¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÑ½É”€ô¹•Ü=ÕÑ¡Q½­•¹MÑ½É”¡‘¥È¤ì(€€€€€€€€€€€ÍÑ½É”¹M…Ù” ‰¡…ÑÁĞˆ°¹•Ü=ÕÑ¡É•‘•¹Ñ¥…±Ìì•ÍÌ€ô€‰Ñ½¬µ„ˆ°I•™É•Í €ô€‰Ñ½¬µÈˆô¤ì(€€€€€€€€€€€Ù…È±½…‘•€ôÍÑ½É”¹1½… ‰¡…ÑÁĞˆ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹9½Ñ9Õ±°¡±½…‘•¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰Ñ½¬µ„ˆ°±½…‘•¹•ÍÌ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰Ñ½¬µÈˆ°±½…‘•¹I•™É•Í ¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥É•Ñ½Éä¹•±•Ñ”¡‘¥È°ÑÉÕ”¤ì(€€€€€€€ô(€€€ô)ô()ÁÕ‰±¥Œ±…ÍÌ9…Ñ¥Ù•AÉ½áå½¹™¥MÑ½É•Q•ÍÑÌ)ì(€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥M…Ù•}¹ÉåÁÑÍÁ¥-•å¹‘‘µ¥ÍÍ¥½¹Q½­•¸ ¤(€€€ì(€€€€€€€Ù…È‘¥È€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ™œ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤¤ì(€€€€€€€¥É•Ñ½Éä¹É•…Ñ•¥É•Ñ½Éä¡‘¥È¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÑ½É”€ô¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥MÑ½É”¡‘¥È¤ì(€€€€€€€€€€€ÍÑ½É”¹M…Ù”¡¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥œ(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€1¥ÍÑ•¹A½ÉĞ€ô€ÄÀÄÀÀ°(€€€€€€€€€€€€€€€‘µ¥ÍÍ¥½¹Q½­•¸€ô€‰…‘µ¥ÍÍ¥½¸µÍ•É•Ğµáåèˆ°(€€€€€€€€€€€€€€€AÉ½Ù¥‘•ÉÌ€ô(€€€€€€€€€€€€€€€l(€€€€€€€€€€€€€€€€€€€¹•ÜAÉ½Ù¥‘•É•™¥¹¥Ñ¥½¸ì%€ô€‰‘••ÁÍ••¬ˆ°	…Í•UÉ°€ô€‰¡ÑÑÀè¼½à½ØÄˆ°Á¥-•ä€ô€‰Í¬µÁ±…¥¸´ÄÈÌˆô(€€€€€€€€€€€€€€€t(€€€€€€€€€€€ô¤ì((€€€€€€€€€€€Ù…ÈÉ…Ü€ô¥±”¹I•…‘±±Q•áĞ¡A…Ñ ¹½µ‰¥¹”¡‘¥È°€‰½¹™¥œ¹©Í½¸ˆ¤¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½•Í9½Ñ½¹Ñ…¥¸ ‰…‘µ¥ÍÍ¥½¸µÍ•É•Ğµáåèˆ°É…Ü¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½•Í9½Ñ½¹Ñ…¥¸ ‰Í¬µÁ±…¥¸´ÄÈÌˆ°É…Ü¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½¹Ñ…¥¹Ì ‰‘Á…Á¤èˆ°É…Ü¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥É•Ñ½Éä¹•±•Ñ”¡‘¥È°ÑÉÕ”¤ì(€€€€€€€ô(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥1½…‘}I½Õ¹‘QÉ¥ÁÍ•ÉåÁÑ•‘M•É•ÑÌ ¤(€€€ì(€€€€€€€Ù…È‘¥È€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ™œ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤¤ì(€€€€€€€¥É•Ñ½Éä¹É•…Ñ•¥É•Ñ½Éä¡‘¥È¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÑ½É”€ô¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥MÑ½É”¡‘¥È¤ì(€€€€€€€€€€€ÍÑ½É”¹M…Ù”¡¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥œ(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€1¥ÍÑ•¹A½ÉĞ€ô€ÄÀÄÀÀ°(€€€€€€€€€€€€€€€‘µ¥ÍÍ¥½¹Q½­•¸€ô€‰…‘´´Äˆ°(€€€€€€€€€€€€€€€AÉ½Ù¥‘•ÉÌ€ôm¹•ÜAÉ½Ù¥‘•É•™¥¹¥Ñ¥½¸ì%€ô€‰Àˆ°Á¥-•ä€ô€‰­•ä´Äˆõt(€€€€€€€€€€€ô¤ì(€€€€€€€€€€€Ù…È±½…‘•€ôÍÑ½É”¹1½… ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰…‘´´Äˆ°±½…‘•¹‘µ¥ÍÍ¥½¹Q½­•¸¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰­•ä´Äˆ°±½…‘•¹AÉ½Ù¥‘•ÉÍlÁt¹Á¥-•ä¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥É•Ñ½Éä¹•±•Ñ”¡‘¥È°ÑÉÕ”¤ì(€€€€€€€ô(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥1½…‘}½ÉÉÕÁÑ½¹™¥}Q¡É½İÍ%¹ÍÑ•…‘=™•™…Õ±Ğ ¤(€€€ì(€€€€€€€Ù…È‘¥È€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ™œ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤¤ì(€€€€€€€¥É•Ñ½Éä¹É•…Ñ•¥É•Ñ½Éä¡‘¥È¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€¥±”¹]É¥Ñ•±±Q•áĞ¡A…Ñ ¹½µ‰¥¹”¡‘¥È°€‰½¹™¥œ¹©Í½¸ˆ¤°€‰í½ÉÉÕÁĞµ©Í½¸„„„ˆ¤ì(€€€€€€€€€€€Ù…ÈÍÑ½É”€ô¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥MÑ½É”¡‘¥È¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹Q¡É½İÌñ%¹Ù…±¥‘=Á•É…Ñ¥½¹á•ÁÑ¥½¸ø  ¤€ôøÍÑ½É”¹1½… ¤¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥É•Ñ½Éä¹•±•Ñ”¡‘¥È°ÑÉÕ”¤ì(€€€€€€€ô(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥UÁÉ…‘•A±…¥¹Ñ•áÑM•É•ÑÍ}½¹Ù•ÉÑÍ1•…åA±…¥¹Ñ•áĞ ¤(€€€ì(€€€€€€€Ù…È‘¥È€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ™œ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤¤ì(€€€€€€€¥É•Ñ½Éä¹É•…Ñ•¥É•Ñ½Éä¡‘¥È¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÁ…Ñ €ôA…Ñ ¹½µ‰¥¹”¡‘¥È°€‰½¹™¥œ¹©Í½¸ˆ¤ì(€€€€€€€€€€€¥±”¹]É¥Ñ•±±Q•áĞ¡Á…Ñ °€ˆˆˆ(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€‰1¥ÍÑ•¹A½ÉĞˆè€ÄÀÄÀÀ°(€€€€€€€€€€€€€€€€€€‰‘µ¥ÍÍ¥½¹Q½­•¸ˆè€‰±•…äµÁ±…¥¸µÑ½­•¸ˆ°(€€€€€€€€€€€€€€€€€€‰AÉ½Ù¥‘•ÉÌˆèl(€€€€€€€€€€€€€€€€€€€ì€‰%ˆè€‰Àˆ°€‰	…Í•UÉ°ˆè€‰¡ÑÑÀè¼½à½ØÄˆ°€‰Á¥-•äˆè€‰±•…äµÁ±…¥¸µ­•äˆô(€€€€€€€€€€€€€€€€€t(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€ˆˆˆ¤ì(€€€€€€€€€€€Ù…ÈÍÑ½É”€ô¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥MÑ½É”¡‘¥È¤ì(€€€€€€€€€€€Ù…È½¹™¥œ€ôÍÑ½É”¹1½… ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰±•…äµÁ±…¥¸µÑ½­•¸ˆ°½¹™¥œ¹‘µ¥ÍÍ¥½¹Q½­•¸¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰±•…äµÁ±…¥¸µ­•äˆ°½¹™¥œ¹AÉ½Ù¥‘•ÉÍlÁt¹Á¥-•ä¤ì((€€€€€€€€€€€Ù…ÈÕÁÉ…‘•€ôÍÑ½É”¹UÁÉ…‘•A±…¥¹Ñ•áÑM•É•ÑÌ¡½¹™¥œ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹QÉÕ”¡ÕÁÉ…‘•¤ì((€€€€€€€€€€€Ù…ÈÉ…Ü€ô¥±”¹I•…‘±±Q•áĞ¡Á…Ñ ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½•Í9½Ñ½¹Ñ…¥¸ ‰±•…äµÁ±…¥¸µÑ½­•¸ˆ°É…Ü¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½•Í9½Ñ½¹Ñ…¥¸ ‰±•…äµÁ±…¥¸µ­•äˆ°É…Ü¤ì((€€€€€€€€€€€Ù…ÈÉ•±½…‘•€ôÍÑ½É”¹1½… ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰±•…äµÁ±…¥¸µÑ½­•¸ˆ°É•±½…‘•¹‘µ¥ÍÍ¥½¹Q½­•¸¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰±•…äµÁ±…¥¸µ­•äˆ°É•±½…‘•¹AÉ½Ù¥‘•ÉÍlÁt¹Á¥-•ä¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥É•Ñ½Éä¹•±•Ñ”¡‘¥È°ÑÉÕ”¤ì(€€€€€€€ô(€€€ô)ô()ÁÕ‰±¥Œ±…ÍÌ9…Ñ¥Ù•AÉ½áå‘µ¥ÍÍ¥½¹Q•ÍÑÌ)ì(€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥!½ÍÑ}5¥ÍÍ¥¹‘µ¥ÍÍ¥½¹Q½­•¹}…¥±Í±½Í•‘	•™½É•1¥ÍÑ•¹¥¹œ ¤(€€€ì(€€€€€€€Ù…È‘¥È€ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ…‘µ¥ÍÍ¥½¸´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤¤ì(€€€€€€€¥É•Ñ½Éä¹É•…Ñ•¥É•Ñ½Éä¡‘¥È¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…ÈÍÑ½É”€ô¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥MÑ½É”¡‘¥È¤ì(€€€€€€€€€€€ÍÑ½É”¹M…Ù”¡¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥œì1¥ÍÑ•¹A½ÉĞ€ô€ÄÀÄÀÀ°‘µ¥ÍÍ¥½¹Q½­•¸€ô¹Õ±°ô¤ì(€€€€€€€€€€€Ù…È•ÉÉ½È€ôÍÍ•ÉĞ¹Q¡É½İÌñ%¹Ù…±¥‘=Á•É…Ñ¥½¹á•ÁÑ¥½¸ø  ¤€ôø(€€€€€€€€€€€€€€€¹•Ü9…Ñ¥Ù•AÉ½áå!½ÍĞ¡ÍÑ½É”°‘…Ñ…I½½Ñ=Ù•ÉÉ¥‘”è‘¥È¤¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½¹Ñ…¥¹Ì ‰‘µ¥ÍÍ¥½¸Q½­•¸ˆ°•ÉÉ½È¹5•ÍÍ…”°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥É•Ñ½Éä¹•±•Ñ”¡‘¥È°ÑÉÕ”¤ì(€€€€€€€ô(€€€ô)ô()ÁÕ‰±¥Œ±…ÍÌI½ÕÑ•I•Í½±Ù•ÉQ•ÍÑÌ)ì(€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥I•Í½±Ù•}U¹­¹½İ¹5½‘•±}Q¡É½İÍ5½‘•±9½Ñ½Õ¹ ¤(€€€ì(€€€€€€€Ù…ÈÉ•¥ÍÑÉä€ô¹•ÜAÉ½Ù¥‘•ÉI•¥ÍÑÉä¡¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥œ(€€€€€€€ì(€€€€€€€€€€€AÉ½Ù¥‘•ÉÌ€ô(€€€€€€€€€€€l(€€€€€€€€€€€€€€€¹•ÜAÉ½Ù¥‘•É•™¥¹¥Ñ¥½¸ì%€ô€‰½Á•¹…¤ˆ°9…µ”€ô€‰=Á•¹$ˆ°5½‘•±Ì€ôl‰ÁĞ´Ô¸ØµÍ½°‰t°•™…Õ±Ñ5½‘•°€ô€‰ÁĞ´Ô¸ØµÍ½°ˆô(€€€€€€€€€€€t(€€€€€€€ô¤ì(€€€€€€€ÍÍ•ÉĞ¹Q¡É½İÌñ½‘•á=Á•¹½‘•á9…Ñ¥Ù”¹AÉ½Ù¥‘•ÉÌ¹5½‘•±9½Ñ½Õ¹‘á•ÁÑ¥½¸ø (€€€€€€€€€€€€ ¤€ôø½‘•á=Á•¹½‘•á9…Ñ¥Ù”¹AÉ½Ù¥‘•ÉÌ¹I½ÕÑ•I•Í½±Ù•È¹I•Í½±Ù”¡É•¥ÍÑÉä°€‰¹¼µÍÕ µµ½‘•°ˆ¤¤ì(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥I•Í½±Ù•}	…É•5½‘•±9…µ•}¥¹‘ÍAÉ½Ù¥‘•È ¤(€€€ì(€€€€€€€Ù…ÈÉ•¥ÍÑÉä€ô¹•ÜAÉ½Ù¥‘•ÉI•¥ÍÑÉä¡¹•Ü9…Ñ¥Ù•AÉ½áå½¹™¥œ(€€€€€€€ì(€€€€€€€€€€€AÉ½Ù¥‘•ÉÌ€ô(€€€€€€€€€€€l(€€€€€€€€€€€€€€€¹•ÜAÉ½Ù¥‘•É•™¥¹¥Ñ¥½¸ì%€ô€‰‘••ÁÍ••¬ˆ°9…µ”€ô€‰••ÁM••¬ˆ°5½‘•±Ì€ôl‰‘••ÁÍ••¬µØĞµ™±…Í ‰t°•™…Õ±Ñ5½‘•°€ô€‰‘••ÁÍ••¬µØĞµ™±…Í ˆô(€€€€€€€€€€€t(€€€€€€€ô¤ì(€€€€€€€Ù…ÈÉ•ÍÕ±Ğ€ô½‘•á=Á•¹½‘•á9…Ñ¥Ù”¹AÉ½Ù¥‘•ÉÌ¹I½ÕÑ•I•Í½±Ù•È¹I•Í½±Ù”¡É•¥ÍÑÉä°€‰‘••ÁÍ••¬µØĞµ™±…Í ˆ¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰‘••ÁÍ••¬ˆ°É•ÍÕ±Ğ¹AÉ½Ù¥‘•É%¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰‘••ÁÍ••¬µØĞµ™±…Í ˆ°É•ÍÕ±Ğ¹5½‘•±%¤ì(€€€ô)ô()ÁÕ‰±¥Œ±…ÍÌ]½É­•É	É½­•ÉAÉ¥¥¹Q•ÍÑÌ)ì(€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥•ÑI½±•AÉ¥¥¹}I•ÑÕÉ¹Í½¹™¥ÕÉ•‘Y…±Õ•Ì ¤(€€€ì(€€€€€€€€¼¼ƒ¦k¢şMÕ‰…•¹Ñ½¹™¥ÕÉ…Ñ¥½¹M•ÉÙ¥”ƒj¦îc¢º“¢K¢&Ë¦ª3¢¾(€€€€€€€Ù…ÈÍ•ÉÙ¥”€ô¹•ÜMÕ‰…•¹Ñ½¹™¥ÕÉ…Ñ¥½¹M•ÉÙ¥” ¤ì(€€€€€€€Ù…È‰É½­•È€ô¹•Ü]½É­•É	É½­•È (€€€€€€€€€€€¹•ÜáÑ•É¹…±]½É­•ÉM•ÉÙ¥” (€€€€€€€€€€€€€€€¹•Ü…­•½¹™¥M½ÕÉ” ¤°(€€€€€€€€€€€€€€€¹•Ü…­•	…­•¹ ¤°(€€€€€€€€€€€€€€€¹•Ü…­•Õ‘¥Ğ ¤¤°(€€€€€€€€€€€Í•ÉÙ¥”¤ì((€€€€€€€Ù…ÈÁÉ¥¥¹œ€ô‰É½­•È¹•ÑI½±•AÉ¥¥¹œ ‰µµ}•áÁ±½É•Èˆ¤ì(€€€€€€€ÍÍ•ÉĞ¹9½Ñ9Õ±°¡ÁÉ¥¥¹œ¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° É´°ÁÉ¥¥¹œ¹AÉ¥•A•É5¥±±¥½¹Q½­•¹Ì¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰UMˆ°ÁÉ¥¥¹œ¹ÕÉÉ•¹ä¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ÔÁ´°ÁÉ¥¥¹œ¹	Õ‘•Ñ1¥µ¥Ğ¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ÌÀÀ°ÁÉ¥¥¹œ¹5…áQ¥µ•½ÕÑM•½¹‘Ì¤ì(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥•±•…Ñ•Íå¹}U¹­¹½İ¹I½±•}…¥±Í±½Í• ¤(€€€ì(€€€€€€€Ù…ÈÍ•ÉÙ¥”€ô¹•ÜMÕ‰…•¹Ñ½¹™¥ÕÉ…Ñ¥½¹M•ÉÙ¥” ¤ì(€€€€€€€Ù…È‰É½­•È€ô¹•Ü]½É­•É	É½­•È (€€€€€€€€€€€¹•ÜáÑ•É¹…±]½É­•ÉM•ÉÙ¥”¡¹•Ü…­•½¹™¥M½ÕÉ” ¤°¹•Ü…­•	…­•¹ ¤°¹•Ü…­•Õ‘¥Ğ ¤¤°(€€€€€€€€€€€Í•ÉÙ¥”¤ì((€€€€€€€Ù…È•à€ôÍÍ•ÉĞ¹Q¡É½İÌñ]½É­•É	É½­•Éá•ÁÑ¥½¸ø  ¤€ôø(€€€€€€€€€€€‰É½­•È¹•±•…Ñ•Íå¹Œ ‰¹½}ÍÕ¡}É½±”ˆ°€‰Ñ…Í¬ˆ¤¹•Ñİ…¥Ñ•È ¤¹•ÑI•ÍÕ±Ğ ¤¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰É½±•}¹½Ñ}™½Õ¹ˆ°•à¹½‘”¤ì(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥•±•…Ñ•Íå¹}I½±•]¥Ñ¡½ÕÑAÉ¥¥¹}…¥±Í±½Í• ¤(€€€ì(€€€€€€€Ù…ÈÍ•ÉÙ¥”€ô¹•ÜMÕ‰…•¹Ñ½¹™¥ÕÉ…Ñ¥½¹M•ÉÙ¥” ¤ì(€€€€€€€Ù…È‰É½­•È€ô¹•Ü]½É­•É	É½­•È (€€€€€€€€€€€¹•ÜáÑ•É¹…±]½É­•ÉM•ÉÙ¥”¡¹•Ü…­•½¹™¥M½ÕÉ” ¤°¹•Ü…­•	…­•¹ ¤°¹•Ü…­•Õ‘¥Ğ ¤¤°(€€€€€€€€€€€Í•ÉÙ¥”¤ì((€€€€€€€€¼¼µµ}ÍÕÁ•ÉÙ¥Í½Èƒ’â7–¢ºã–’[¦£–Ş—’êë¾ò!±±½İÍáÑ•É¹…±]½É­•Èõ™…±Í—¾ò'ŠHƒ–’Ç¢Ò—–Ï¦^´(€€€€€€€Ù…È•à€ôÍÍ•ÉĞ¹Q¡É½İÌñ]½É­•É	É½­•Éá•ÁÑ¥½¸ø  ¤€ôø(€€€€€€€€€€€‰É½­•È¹•±•…Ñ•Íå¹Œ ‰µµ}ÍÕÁ•ÉÙ¥Í½Èˆ°€‰Ñ…Í¬ˆ¤¹•Ñİ…¥Ñ•È ¤¹•ÑI•ÍÕ±Ğ ¤¤ì(€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ‰É½±•}•áÑ•É¹…±}™½É‰¥‘‘•¸ˆ°•à¹½‘”¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Í•…±•±…ÍÌ…­•½¹™¥M½ÕÉ”€è%áÑ•É¹…±]½É­•É½¹™¥ÕÉ…Ñ¥½¹M½ÕÉ”(€€€ì(€€€€€€€ÁÕ‰±¥ŒÍÑÉ¥¹œü1½…‘]…É¹¥¹œ€ôø¹Õ±°ì(€€€€€€€ÁÕ‰±¥Œ%I•…‘=¹±å1¥ÍĞñMÕ‰…•¹ÑI½±••™¥¹¥Ñ¥½¸øI½±•Ìì•Ğìô€ôÉÉ…ä¹µÁÑäñMÕ‰…•¹ÑI½±••™¥¹¥Ñ¥½¸ø ¤ì(€€€€€€€ÁÕ‰±¥ŒMÕ‰…•¹Ñ½¹™¥ÕÉ…Ñ¥½¹½Õµ•¹Ğ1½…‘É…™Ğ ¤€ôø¹•Ü ¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Í•…±•±…ÍÌ…­•	…­•¹€è%áÑ•É¹…±]½É­•É	…­•¹(€€€ì(€€€€€€€ÁÕ‰±¥Œ%I•…‘=¹±å1¥ÍĞñÍÑÉ¥¹œøI•…‘½¹™¥ÕÉ•‘5½‘•±Ì ¤€ôøÉÉ…ä¹µÁÑäñÍÑÉ¥¹œø ¤ì(€€€€€€€ÁÕ‰±¥ŒQ…Í¬ñáÑ•É¹…±]½É­•É	…­•¹‘I•ÍÁ½¹Í”ø½µÁ±•Ñ•Íå¹Œ¡áÑ•É¹…±]½É­•É	…­•¹‘I•ÅÕ•ÍĞÉ•ÅÕ•ÍĞ°…¹•±±…Ñ¥½¹Q½­•¸…¹•±±…Ñ¥½¹Q½­•¸€ô‘•™…Õ±Ğ¤€ôø(€€€€€€€€€€€Q…Í¬¹É½µI•ÍÕ±Ğ¡¹•ÜáÑ•É¹…±]½É­•É	…­•¹‘I•ÍÁ½¹Í” ‰½¬ˆ°€‰ÍÑ½Àˆ°¹•ÜáÑ•É¹…±]½É­•ÉQ½­•¹UÍ…” Ä°€Ä°€È¤°€ÈÀÀ°É•ÅÕ•ÍĞ¹5½‘•°¤¤ì(€€€ô((€€€ÁÉ¥Ù…Ñ”Í•…±•±…ÍÌ…­•Õ‘¥Ğ€è%áÑ•É¹…±]½É­•ÉÕ‘¥ÑM¥¹¬(€€€ì(€€€€€€€ÁÕ‰±¥ŒY…±Õ•Q…Í¬ÁÁ•¹‘Íå¹Œ¡áÑ•É¹…±]½É­•ÉÕ‘¥Ñ¹ÑÉä•¹ÑÉä°…¹•±±…Ñ¥½¹Q½­•¸…¹•±±…Ñ¥½¹Q½­•¸€ô‘•™…Õ±Ğ¤€ôø(€€€€€€€€€€€Y…±Õ•Q…Í¬¹½µÁ±•Ñ•‘Q…Í¬ì(€€€ô)ô()ÁÕ‰±¥Œ±…ÍÌ]½É­•É	Õ‘•Ñ1•‘•ÉQ•ÍÑÌ)ì(€€€m…Ñt(€€€ÁÕ‰±¥Œ…Íå¹ŒQ…Í¬•‘ÕÑ}Q¡•¹}¡•­	•™½É•…±±}I•©•ÑÍ]¡•¹	Õ‘•Ñá¡…ÕÍÑ• ¤(€€€ì(€€€€€€€Ù…ÈÁ…Ñ €ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ‰Õ‘•Ğ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤€¬€ˆ¹©Í½¸ˆ¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…È±•‘•È€ô¹•Ü]½É­•É	Õ‘•Ñ1•‘•È¡Á…Ñ ¤ì(€€€€€€€€€€€Ù…ÈÉ½±”€ô¹•ÜMÕ‰…•¹ÑI½±••™¥¹¥Ñ¥½¸ (€€€€€€€€€€€€€€€€‰µµ}Ñ•ÍĞˆ°€‹šÖ/¢¾Tˆ°€‰Ğˆ°€‰ÁĞ´Ô¸ØµÍ½°ˆ°€‰±½Üˆ°€‰É•…µ½¹±äˆ°ÑÉÕ”°(€€€€€€€€€€€€€€€€‰‘•Øˆ°(€€€€€€€€€€€€€€€AÉ¥•A•É5¥±±¥½¹Q½­•¹Ìè€ÄÀÀÁ´°€¼¼ƒš¾?fû’âÑ½­•¸€ÄÀÀÀƒú;–(€€€€€€€€€€€€€€€ÕÉÉ•¹äè€‰UMˆ°(€€€€€€€€€€€€€€€	Õ‘•Ñ1¥µ¥Ğè€À¸ÀÀÅ´°€€€€€€€€€€¼¼ƒ¦Šº\€À¸ÀÀÄƒú;–(€€€€€€€€€€€€€€€5…áQ¥µ•½ÕÑM•½¹‘Ìè€ØÀ¤ì((€€€€€€€€€€€€¼¼ƒR €ÄÀÀÀÑ½­•¸ƒŠHƒš"Cšr°€À¸ÀÀÄƒú;–ƒŠHƒš¶––÷š&Ošî‡¦Šº\(€€€€€€€€€€€Ù…È½ÍĞ€ô…İ…¥Ğ±•‘•È¹•‘ÕÑÍå¹Œ¡É½±”°€ÄÀÀÀ°€À¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° Ä¸Á´°½ÍĞ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° Ä¸Á´°±•‘•È¹•ÑMÁ•¹Ğ ‰µµ}Ñ•ÍĞˆ¤¤ì((€€€€€€€€€€€€¼¼ƒ¦Šº_¢_–ÂôƒŠHƒš.Kît(€€€€€€€€€€€Ù…È‰±½¬€ô±•‘•È¹¡•­	•™½É•…±°¡É½±”¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹9½Ñ9Õ±°¡‰±½¬¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½¹Ñ…¥¹Ì ‹–ŞË¢_–Âôˆ°‰±½¬¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥±”¹•±•Ñ”¡Á…Ñ ¤ì(€€€€€€€ô(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥Œ…Íå¹ŒQ…Í¬•‘ÕÑ}ÕµÕ±…Ñ•ÍÉ½ÍÍ…±±Ì ¤(€€€ì(€€€€€€€Ù…ÈÁ…Ñ €ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ‰Õ‘•Ğ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤€¬€ˆ¹©Í½¸ˆ¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…È±•‘•È€ô¹•Ü]½É­•É	Õ‘•Ñ1•‘•È¡Á…Ñ ¤ì(€€€€€€€€€€€Ù…ÈÉ½±”€ô¹•ÜMÕ‰…•¹ÑI½±••™¥¹¥Ñ¥½¸ (€€€€€€€€€€€€€€€€‰µµ}Ñ•ÍĞÈˆ°€‹šÖ/¢¾TÈˆ°€‰Ğˆ°€‰ÁĞ´Ô¸ØµÍ½°ˆ°€‰±½Üˆ°€‰É•…µ½¹±äˆ°ÑÉÕ”°(€€€€€€€€€€€€€€€€‰‘•Øˆ°(€€€€€€€€€€€€€€€AÉ¥•A•É5¥±±¥½¹Q½­•¹Ìè€É´°(€€€€€€€€€€€€€€€ÕÉÉ•¹äè€‰UMˆ°(€€€€€€€€€€€€€€€	Õ‘•Ñ1¥µ¥Ğè€ÄÁ´°(€€€€€€€€€€€€€€€5…áQ¥µ•½ÕÑM•½¹‘Ìè€ØÀ¤ì((€€€€€€€€€€€Ù…ÈŒÄ€ô…İ…¥Ğ±•‘•È¹•‘ÕÑÍå¹Œ¡É½±”°€ÄÀÀÀÀÀ°€À¤ì€€¼¼€À¸Èƒú;–(€€€€€€€€€€€Ù…ÈŒÈ€ô…İ…¥Ğ±•‘•È¹•‘ÕÑÍå¹Œ¡É½±”°€ÄÀÀÀÀÀ°€À¤ì€€¼¼€À¸Èƒú;–(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° À¸É´°ŒÄ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° À¸É´°ŒÈ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° À¸Ñ´°±•‘•È¹•ÑMÁ•¹Ğ ‰µµ}Ñ•ÍĞÈˆ¤¤ì((€€€€€€€€€€€Ù…ÈÉ•µ…¥¹¥¹œ€ô±•‘•È¹•ÑI•µ…¥¹¥¹œ¡É½±”¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹9½Ñ9Õ±°¡É•µ…¥¹¥¹œ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹ÅÕ…° ä¸Ù´°É•µ…¥¹¥¹œ¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹9Õ±°¡±•‘•È¹¡•­	•™½É•…±°¡É½±”¤¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥±”¹•±•Ñ”¡Á…Ñ ¤ì(€€€€€€€ô(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥ŒÙ½¥¡•­	•™½É•…±±}I½±•]¥Ñ¡½ÕÑAÉ¥¥¹}I•©•ÑÌ ¤(€€€ì(€€€€€€€Ù…ÈÁ…Ñ €ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ‰Õ‘•Ğ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤€¬€ˆ¹©Í½¸ˆ¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…È±•‘•È€ô¹•Ü]½É­•É	Õ‘•Ñ1•‘•È¡Á…Ñ ¤ì(€€€€€€€€€€€Ù…ÈÉ½±”€ô¹•ÜMÕ‰…•¹ÑI½±••™¥¹¥Ñ¥½¸ (€€€€€€€€€€€€€€€€‰µµ}¹½ÁÉ¥”ˆ°€‹š^ƒ’îÜˆ°€‰Ğˆ°€‰ÁĞ´Ô¸ØµÍ½°ˆ°€‰±½Üˆ°€‰É•…µ½¹±äˆ°ÑÉÕ”°€‰‘•Øˆ¤ì(€€€€€€€€€€€Ù…È‰±½¬€ô±•‘•È¹¡•­	•™½É•…±°¡É½±”¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹9½Ñ9Õ±°¡‰±½¬¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½¹Ñ…¥¹Ì ‹šr«¦7ö»’îßš‚ğˆ°‰±½¬¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥±”¹•±•Ñ”¡Á…Ñ ¤ì(€€€€€€€ô(€€€ô((€€€m…Ñt(€€€ÁÕ‰±¥Œ…Íå¹ŒQ…Í¬•‘ÕÑ}5¥ÍÍ¥¹UÍ…•}Q¡É½İÍ…¥±±½Í• ¤(€€€ì(€€€€€€€Ù…ÈÁ…Ñ €ôA…Ñ ¹½µ‰¥¹”¡A…Ñ ¹•ÑQ•µÁA…Ñ  ¤°€‰µ´µ‰Õ‘•Ğ´ˆ€¬Õ¥¹9•İÕ¥ ¤¹Q½MÑÉ¥¹œ ‰8ˆ¤€¬€ˆ¹©Í½¸ˆ¤ì(€€€€€€€ÑÉä(€€€€€€€ì(€€€€€€€€€€€Ù…È±•‘•È€ô¹•Ü]½É­•É	Õ‘•Ñ1•‘•È¡Á…Ñ ¤ì(€€€€€€€€€€€Ù…ÈÉ½±”€ô¹•ÜMÕ‰…•¹ÑI½±••™¥¹¥Ñ¥½¸ (€€€€€€€€€€€€€€€€‰µµ}¹½ÕÍ…”ˆ°€‹š^ƒR£¦<ˆ°€‰Ğˆ°€‰ÁĞ´Ô¸ØµÍ½°ˆ°€‰±½Üˆ°€‰É•…µ½¹±äˆ°ÑÉÕ”°(€€€€€€€€€€€€€€€€‰‘•Øˆ°(€€€€€€€€€€€€€€€AÉ¥•A•É5¥±±¥½¹Q½­•¹Ìè€É´°(€€€€€€€€€€€€€€€ÕÉÉ•¹äè€‰UMˆ°(€€€€€€€€€€€€€€€	Õ‘•Ñ1¥µ¥Ğè€ÄÁ´°(€€€€€€€€€€€€€€€5…áQ¥µ•½ÕÑM•½¹‘Ìè€ØÀ¤ì((€€€€€€€€€€€€¼¼ÕÍ…”ƒ–º3–£òë–’Çš^Û’â7–ú_š2$€Àƒ¢º‡¢Òç¾ò3–ş¦†ï–’Ç¢Ò—–Ï¦^´(€€€€€€€€€€€Ù…È•à€ô…İ…¥ĞÍÍ•ÉĞ¹Q¡É½İÍÍå¹Œñ%¹Ù…±¥‘=Á•É…Ñ¥½¹á•ÁÑ¥½¸ø  ¤€ôø(€€€€€€€€€€€€€€€±•‘•È¹•‘ÕÑÍå¹Œ¡É½±”°¹Õ±°°¹Õ±°¤¤ì(€€€€€€€€€€€ÍÍ•ÉĞ¹½¹Ñ…¥¹Ì ‹šr«¢şS–nxÑ½­•¸ƒR£¦<ˆ°•à¹5•ÍÍ…”¤ì(€€€€€€€ô(€€€€€€€™¥¹…±±ä(€€€€€€€ì(€€€€€€€€€€€¥±”¹•±•Ñ”¡Á…Ñ ¤ì(€€€€€€€ô(€€€ô)ô(