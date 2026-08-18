using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CodexModelManager.Models;
using CodexModelManager.Services;
using CodexModelManager.Controls;
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
    public void ProductMaintenance_UsesSemanticVersionsAndDiagnosticSummaryStaysPathFree()
    {
        Assert.True(ProductMaintenanceService.CompareVersionText("3.0.0-rc.29", "3.0.0-rc.28") > 0);
        Assert.True(ProductMaintenanceService.CompareVersionText("3.0.0", "3.0.0-rc.99") > 0);
        Assert.True(ProductMaintenanceService.CompareVersionText("4.0.0", "3.99.99") > 0);

        var root = Path.Combine(
            Path.GetTempPath(),
            "cmm-product-shell-secret-user-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettingsService(root);
            using var product = new ProductMaintenanceService(root);
            var summary = product.BuildDiagnosticSummary(settings);
            Assert.DoesNotContain(root, summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("不包含用户名", summary, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
    public void LocalGateways_BoundRequestBodiesBeforeBufferingOrDecompression()
    {
        var repo = FindRepositoryRoot();
        var nativeHost = File.ReadAllText(
            Path.Combine(repo, @"src\CodexOpenCodexNative\Host\NativeProxyHost.cs"), Encoding.UTF8);
        var unifiedHost = File.ReadAllText(
            Path.Combine(repo, @"src\CodexModelManager\Services\UnifiedGatewayHost.cs"), Encoding.UTF8);

        Assert.Contains("MaximumRequestBodyBytes = 32L * 1024 * 1024", nativeHost, StringComparison.Ordinal);
        Assert.Contains("new SizeLimitedReadStream(originalBody, MaximumRequestBodyBytes)", nativeHost, StringComparison.Ordinal);
        Assert.Contains("Status413PayloadTooLarge", nativeHost, StringComparison.Ordinal);
        Assert.Contains("MaximumRequestBodyBytes = 32L * 1024 * 1024", unifiedHost, StringComparison.Ordinal);
        Assert.Contains("ReadRequestBodyAsync(context.Request", unifiedHost, StringComparison.Ordinal);
        Assert.Contains("Status413PayloadTooLarge", unifiedHost, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalAcceptanceScripts_ParseOnWindowsPowerShellGrammar()
    {
        var repo = FindRepositoryRoot();
        foreach (var relativePath in new[]
                 {
                     @"scripts\validate-external-acceptance.ps1",
                     @"scripts\emit-evidence.ps1",
                     @"scripts\install-local-release.ps1",
                     @"scripts\uninstall-local-release.ps1",
                     @"scripts\repair-local-runtime.ps1"
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
    public void RuntimeRepair_PreservesCustomProvidersPoolsAndCredentialsByDefault()
    {
        var repo = FindRepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(repo, @"scripts\repair-local-runtime.ps1"), Encoding.UTF8);

        Assert.Contains("[switch]$PurgeCredentialStores", script, StringComparison.Ordinal);
        Assert.Contains("ConfirmPurgeCredentialStores -cne 'DELETE_LOCAL_CREDENTIALS'", script,
            StringComparison.Ordinal);
        Assert.Contains("$obsoletePoolIds = @('ollama-pro')", script, StringComparison.Ordinal);
        Assert.Contains("$obsoleteSecretNames = @('ollama-pro', 'cmm-ollama-pro')", script,
            StringComparison.Ordinal);
        Assert.Contains("if ($PurgeCredentialStores)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$_.Id -in @('official-pro', 'plus-api-1')", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeRepair_ActuallyKeepsCustomProviderKeyPoolAndCliAccountWithoutPurgeFlag()
    {
        var repo = FindRepositoryRoot();
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installRoot = Path.Combine(localData, "CodexTotalManager-repair-test-" + Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(installRoot, "runtime-v3");
        var authFile = Path.Combine(runtimeRoot, "cli-proxy", "pools", "custom", "auth", "account.json");
        Directory.CreateDirectory(Path.GetDirectoryName(authFile)!);
        try
        {
            File.WriteAllText(authFile, "{}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(runtimeRoot, "pools.json"),
                """
                {
                  "SchemaVersion": 4,
                  "Pools": [
                    { "Id": "official-pro", "Enabled": true },
                    { "Id": "plus-api-1", "Enabled": false },
                    { "Id": "custom-cli", "Enabled": true },
                    { "Id": "ollama-pro", "Enabled": false }
                  ],
                  "Active": { "PoolId": "official-pro", "Model": "gpt-5.6-sol" }
                }
                """, Encoding.UTF8);
            File.WriteAllText(Path.Combine(runtimeRoot, "secrets.json"),
                """
                {
                  "internal:unified-gateway:client": "gateway",
                  "custom-provider": "custom-secret",
                  "ollama-pro": "obsolete"
                }
                """, Encoding.UTF8);

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
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            var scriptPath = Path.Combine(repo, @"scripts\repair-local-runtime.ps1")
                .Replace("'", "''", StringComparison.Ordinal);
            var quotedInstallRoot = installRoot.Replace("'", "''", StringComparison.Ordinal);
            startInfo.ArgumentList.Add(
                $"$ConfirmPreference='None'; & '{scriptPath}' -InstallRoot '{quotedInstallRoot}' -Apply -Confirm:$false");
            using var process = System.Diagnostics.Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Windows PowerShell did not start.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000) && process.ExitCode == 0, stdout + stderr);

            using var pools = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeRoot, "pools.json")));
            var poolIds = pools.RootElement.GetProperty("Pools").EnumerateArray()
                .Select(item => item.GetProperty("Id").GetString()).ToArray();
            Assert.Contains("custom-cli", poolIds);
            Assert.DoesNotContain("ollama-pro", poolIds);

            using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeRoot, "secrets.json")));
            Assert.Equal("custom-secret", secrets.RootElement.GetProperty("custom-provider").GetString());
            Assert.Equal("gateway", secrets.RootElement.GetProperty("internal:unified-gateway:client").GetString());
            Assert.False(secrets.RootElement.TryGetProperty("ollama-pro", out _));
            Assert.True(File.Exists(authFile));
        }
        finally
        {
            if (Directory.Exists(installRoot)) Directory.Delete(installRoot, recursive: true);
        }
    }

    [Fact]
    public void LegacyMigration_MergesProviderSecretsAndRollsBackOnlyTouchedLedgerFiles()
    {
        var repo = FindRepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(repo, @"scripts\migrate-legacy-runtime.ps1"), Encoding.UTF8);

        Assert.Contains("$mergedSecrets", script, StringComparison.Ordinal);
        Assert.Contains("preserving current provider API keys", script, StringComparison.Ordinal);
        Assert.Contains("$movedCanonicalLedgerNames", script, StringComparison.Ordinal);
        Assert.Contains("$installedLegacyLedgerNames", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Copy-Item -LiteralPath $legacySecretsPath -Destination (Join-Path $canonicalFull 'secrets.json')",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DestructiveReleaseScripts_RejectReparsePointTreesAndUnownedInstallRoots()
    {
        var repo = FindRepositoryRoot();
        foreach (var relativePath in new[]
                 {
                     "build.ps1",
                     @"scripts\install-local-release.ps1",
                     @"scripts\uninstall-local-release.ps1",
                     @"scripts\repair-local-runtime.ps1",
                     @"scripts\migrate-legacy-runtime.ps1"
                 })
        {
            var script = File.ReadAllText(Path.Combine(repo, relativePath), Encoding.UTF8);
            Assert.Contains("[IO.FileAttributes]::ReparsePoint", script, StringComparison.Ordinal);
        }

        var uninstaller = File.ReadAllText(
            Path.Combine(repo, @"scripts\uninstall-local-release.ps1"), Encoding.UTF8);
        Assert.Contains("$ownedInstallation", uninstaller, StringComparison.Ordinal);
        Assert.Contains("Test-ManagerJsonMarker", uninstaller, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployableDecision_RequiresHashBoundRealCodexEvidence()
    {
        var repo = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repo, @"scripts\emit-evidence.ps1"), Encoding.UTF8);
        var installer = File.ReadAllText(
            Path.Combine(repo, @"scripts\install-local-release.ps1"), Encoding.UTF8);
        var validator = File.ReadAllText(
            Path.Combine(repo, @"scripts\validate-external-acceptance.ps1"), Encoding.UTF8);

        Assert.Contains("ExternalAcceptanceEvidencePath", script, StringComparison.Ordinal);
        Assert.Contains("PayloadManifestPath", script, StringComparison.Ordinal);
        Assert.Contains("CliProxyApiArtifactPath", script, StringComparison.Ordinal);
        Assert.Contains("CMM_TEST_CLIPROXY_ARTIFACT", script, StringComparison.Ordinal);
        Assert.Contains("$previousCliProxyArtifact", script, StringComparison.Ordinal);
        Assert.Contains("$externalAcceptancePassed", script, StringComparison.Ordinal);
        Assert.Contains("candidateManifestSha256", validator, StringComparison.Ordinal);
        Assert.Contains("dedicatedTestComputer", validator, StringComparison.Ordinal);
        Assert.Contains("$approvalPath", script, StringComparison.Ordinal);
        Assert.Contains("$approval.externalAcceptance.valid", installer, StringComparison.Ordinal);
        Assert.Contains("candidateManifestSha256", installer, StringComparison.Ordinal);
        Assert.Contains("$approvedSha256 -cne $manifestSha256", installer, StringComparison.Ordinal);
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

public sealed class ProviderPresetSecurityTests
{
    [Fact]
    public void Catalog_UsesOnlyHttpsOfficialApiMetadata_AndKeepsCustomGatewayExplicit()
    {
        var presets = ProviderPresetCatalog.All;
        Assert.NotEmpty(presets);
        Assert.Equal(presets.Count, presets.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("custom", presets[0].Id);

        foreach (var preset in presets.Where(item => !item.IsCustom))
        {
            Assert.True(Uri.TryCreate(preset.BaseUrl, UriKind.Absolute, out var uri));
            Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
            Assert.True(string.IsNullOrEmpty(uri.UserInfo));
            Assert.True(string.IsNullOrEmpty(uri.Query));
            Assert.True(string.IsNullOrEmpty(uri.Fragment));
            Assert.Contains(preset.Adapter, new[] { "openai-chat", "openai-responses", "anthropic", "google" });
        }

        var xai = Assert.Single(presets, item => item.Id == "xai");
        Assert.Equal("https://api.x.ai/v1", xai.BaseUrl);
        Assert.Equal("openai-responses", xai.Adapter);
        Assert.Contains("不读取 Grok 网页 Cookie", xai.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeAuthentication_UsesProviderNativeHeader_AndNeverAddsCookies()
    {
        using var openAi = new HttpRequestMessage(HttpMethod.Get, "https://api.x.ai/v1/models");
        ProviderProbeService.ApplyAuthentication(openAi, "xai-secret", "openai-responses");
        Assert.Equal("Bearer", openAi.Headers.Authorization?.Scheme);
        Assert.Equal("xai-secret", openAi.Headers.Authorization?.Parameter);
        Assert.False(openAi.Headers.Contains("Cookie"));

        using var anthropic = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        ProviderProbeService.ApplyAuthentication(anthropic, "claude-secret", "anthropic");
        Assert.Equal("claude-secret", Assert.Single(anthropic.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(anthropic.Headers.GetValues("anthropic-version")));
        Assert.Null(anthropic.Headers.Authorization);

        using var google = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models");
        ProviderProbeService.ApplyAuthentication(google, "gemini-secret", "google");
        Assert.Equal("gemini-secret", Assert.Single(google.Headers.GetValues("x-goog-api-key")));
        Assert.Null(google.Headers.Authorization);
    }

    [Fact]
    public void ProbeCandidates_NormalizeAnthropicAndGoogleWithoutDuplicatingVersionSegments()
    {
        var anthropic = Assert.Single(ProviderProbeService.BuildCandidates(
            new Uri("https://api.anthropic.com/v1/models"), "anthropic"));
        Assert.Equal("https://api.anthropic.com", anthropic.BaseUrl);
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", anthropic.ModelsUrl);

        var google = Assert.Single(ProviderProbeService.BuildCandidates(
            new Uri("https://generativelanguage.googleapis.com/v1beta"), "google"));
        Assert.Equal("https://generativelanguage.googleapis.com", google.BaseUrl);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000", google.ModelsUrl);

        var perplexity = Assert.Single(ProviderProbeService.BuildCandidates(
            new Uri("https://api.perplexity.ai"), "openai-chat"));
        Assert.Equal("https://api.perplexity.ai", perplexity.BaseUrl);
        Assert.Equal("https://api.perplexity.ai/v1/models", perplexity.ModelsUrl);
    }

    [Fact]
    public void GoogleModelPage_KeepsOnlyGenerateContentModels_AndReadsPagination()
    {
        var page = ProviderProbeService.ParseModelPage("""
        {
          "models": [
            {"name":"models/gemini-test","supportedGenerationMethods":["generateContent","countTokens"]},
            {"name":"models/text-embedding-test","supportedGenerationMethods":["embedContent"]}
          ],
          "nextPageToken": "next-token"
        }
        """, "google");

        Assert.Equal(new[] { "gemini-test" }, page.Models);
        Assert.Equal("next-token", page.NextPageToken);
    }

    [Fact]
    public async Task ProviderTransports_RejectRedirects_AndProbeResponseIsBounded()
    {
        using var probe = ProviderProbeService.CreateHandler();
        using var native = NativeProxyHost.CreateUpstreamHandler();
        using var gateway = UnifiedGatewayHost.CreateUpstreamHandler();
        using var adapter = AdapterHttpTransport.CreateHandler();
        Assert.False(probe.AllowAutoRedirect);
        Assert.False(native.AllowAutoRedirect);
        Assert.False(gateway.AllowAutoRedirect);
        Assert.False(adapter.AllowAutoRedirect);

        using var oversized = new ByteArrayContent(new byte[ProviderProbeService.MaximumResponseBytes + 1]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderProbeService.ReadBoundedStringAsync(oversized, CancellationToken.None));
        Assert.Contains("超过 4 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderIds_AvoidInternalNamespaceAndRapidDuplicateOverwrite()
    {
        var reserved = ProviderId.From("cmm-test", "https://example.test/v1", Array.Empty<string>());
        Assert.StartsWith("relay-", reserved, StringComparison.Ordinal);

        var first = ProviderId.From("Grok", "https://api.x.ai/v1", Array.Empty<string>());
        var second = ProviderId.From("Grok", "https://api.x.ai/v1", new[] { first });
        var third = ProviderId.From("Grok", "https://api.x.ai/v1", new[] { first, second });
        Assert.Equal(first + "-2", second);
        Assert.Equal(first + "-3", third);
    }

    [Fact]
    public void ManagerOwnedCliProvider_IsNotDuplicatedAsCustomGatewayRoute()
    {
        var routes = UnifiedGatewayService.BuildCustomProviderRoutes(
            new ProviderView
            {
                Id = "cmm-plus-api-1",
                DisplayName = "Plus API 1",
                Adapter = "openai-responses"
            },
            [
                new ModelOption
                {
                    Provider = "cmm-plus-api-1",
                    Id = "gpt-test",
                    Namespaced = "cmm-plus-api-1/gpt-test"
                }
            ],
            LocalPortPolicy.DefaultNativeEnginePort);

        Assert.Empty(routes);
    }

    [Fact]
    public void CustomProviderGatewayRoutes_AreNamespacedOnly()
    {
        var provider = new ProviderView
        {
            Id = "grok-a",
            DisplayName = "Grok A",
            Adapter = "openai-responses"
        };
        var routes = UnifiedGatewayService.BuildCustomProviderRoutes(
            provider,
            new[]
            {
                new ModelOption
                {
                    Provider = "grok-a",
                    Id = "grok-4",
                    Namespaced = "grok-a/grok-4"
                }
            },
            10100);

        var route = Assert.Single(routes);
        Assert.Equal("grok-a/grok-4", route.GatewayModel);
        Assert.Equal("grok-a/grok-4", route.UpstreamModel);
        Assert.NotEqual("grok-4", route.GatewayModel);
        Assert.Equal(UnifiedGatewayKeys.NativeEngineAdmissionRouteSecretName, route.SecretName);
        Assert.True(SubagentSourceIdentity.IsRouteIdentityValid(route));
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
    public void V2rayPath_DefaultsToUnconfiguredWithoutGuessingHostPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-default-v2ray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettingsService(root);

            Assert.Equal(string.Empty, settings.V2rayPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NativeProxy_DefaultPort_DoesNotCollideWithUnifiedGateway()
    {
        var config = new NativeProxyConfig();
        Assert.Equal(LocalPortPolicy.DefaultNativeEnginePort, config.ListenPort);
        Assert.NotEqual(LocalPortPolicy.DefaultUnifiedGatewayPort, config.ListenPort);
    }

    [Fact]
    public void PoolAllocator_UsesCorePortsSavedDuringTheCurrentSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-port-refresh-" + Guid.NewGuid().ToString("N"));
        var baselineRoot = Path.Combine(root, "baseline");
        var refreshedRoot = Path.Combine(root, "refreshed");
        Directory.CreateDirectory(baselineRoot);
        Directory.CreateDirectory(refreshedRoot);
        try
        {
            var baseline = new PoolCatalogService(baselineRoot, [10100, 10110, 10808]);
            var portThatWouldHaveBeenChosen = baseline.AddCliProxyPool(AccountProduct.CodexPlus).LocalPort;
            Assert.NotNull(portThatWouldHaveBeenChosen);

            var refreshed = new PoolCatalogService(refreshedRoot, [10100, 10110, 10808]);
            refreshed.UpdateReservedPorts([10100, 10110, 10808, portThatWouldHaveBeenChosen!.Value]);
            var allocated = refreshed.AddCliProxyPool(AccountProduct.CodexPlus);

            Assert.NotEqual(portThatWouldHaveBeenChosen, allocated.LocalPort);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CorePorts_CanBeSavedWithoutInstallingV2rayN()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-optional-v2ray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettingsService(root);
            settings.SetLocalNetworkConfiguration(
                string.Empty,
                "socks5://127.0.0.1:10808",
                12000,
                12010);

            Assert.Equal(string.Empty, settings.V2rayPath);
            Assert.Equal(12000, settings.NativeEnginePort);
            Assert.Equal(12010, settings.UnifiedGatewayPort);
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
    public void Record_DiskFailureDoesNotBreakInferenceAccounting()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-request-log-failure-" + Guid.NewGuid().ToString("N"));
        var log = new RequestLogService(root);
        Directory.Delete(root, true);
        File.WriteAllText(root, "blocks the journal directory");
        try
        {
            log.Record(new RequestLogEntry
            {
                Provider = "test",
                Status = "completed",
                HttpStatus = 200
            });

            Assert.Single(log.Recent());
            Assert.Equal(1, log.PersistenceFailures);
            Assert.False(string.IsNullOrWhiteSpace(log.LastPersistenceError));
        }
        finally
        {
            if (File.Exists(root)) File.Delete(root);
        }
    }

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
    public async Task ManagementEndpoints_RejectMalformedOrNonObjectJsonAsBadRequest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-management-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        NativeProxyHost? host = null;
        try
        {
            var port = GetUnusedLoopbackPort();
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig
            {
                ListenPort = port,
                AdmissionToken = "management-admission-secret"
            });
            host = new NativeProxyHost(store, dataRootOverride: dir);
            await host.Application.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var cases = new (HttpMethod Method, string Path, string Body)[]
            {
                (HttpMethod.Post, "/api/providers", "{"),
                (HttpMethod.Patch, "/api/providers?name=openai", "[]"),
                (HttpMethod.Put, "/api/codex-auth/active", "[]"),
                (HttpMethod.Put, "/api/codex-auth/auto-switch", "null"),
                (HttpMethod.Put, "/api/codex-auth/failover", "true"),
                (HttpMethod.Put, "/api/combos", "\"not-an-object\"")
            };

            foreach (var item in cases)
            {
                using var request = new HttpRequestMessage(item.Method, item.Path)
                {
                    Content = new StringContent(item.Body, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation(
                    "X-CMM-Admission",
                    "Bearer management-admission-secret");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains("error", body, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (host is not null) await host.StopAsync();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ManagementEndpoint_AllowsOnlyLoopbackManagerOwnedCliPoolProviderIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-cli-provider-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        NativeProxyHost? host = null;
        try
        {
            var port = GetUnusedLoopbackPort();
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig
            {
                ListenPort = port,
                AdmissionToken = "management-admission-secret"
            });
            host = new NativeProxyHost(store, dataRootOverride: dir);
            await host.Application.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using var allowed = new HttpRequestMessage(HttpMethod.Post, "/api/providers")
            {
                Content = new StringContent(
                    """
                    {"name":"cmm-plus-api-1","provider":{"adapter":"openai-responses","baseUrl":"http://127.0.0.1:18401/v1","apiKey":"${CMM_CMM_PLUS_API_1_API_KEY}","models":["gpt-test"],"selectedModels":["gpt-test"],"allowPrivateNetwork":true}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
            allowed.Headers.TryAddWithoutValidation("X-CMM-Admission", "Bearer management-admission-secret");
            using var allowedResponse = await client.SendAsync(allowed);
            Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);

            using var forbidden = new HttpRequestMessage(HttpMethod.Post, "/api/providers")
            {
                Content = new StringContent(
                    """
                    {"name":"cmm-fake-external","provider":{"adapter":"openai-responses","baseUrl":"https://example.test/v1","apiKey":"${CMM_FAKE_EXTERNAL_API_KEY}","models":["gpt-test"],"selectedModels":["gpt-test"],"allowPrivateNetwork":true}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
            forbidden.Headers.TryAddWithoutValidation("X-CMM-Admission", "Bearer management-admission-secret");
            using var forbiddenResponse = await client.SendAsync(forbidden);
            Assert.Equal(HttpStatusCode.BadRequest, forbiddenResponse.StatusCode);

            var saved = store.Load();
            var provider = Assert.Single(saved.Providers);
            Assert.Equal("cmm-plus-api-1", provider.Id);
            Assert.Equal("http://127.0.0.1:18401/v1", provider.BaseUrl);
            Assert.Equal("openai-responses", provider.Adapter);
        }
        finally
        {
            if (host is not null) await host.StopAsync();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task FailedProviderPatch_DoesNotLeakPartialChangesIntoLaterSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-provider-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        NativeProxyHost? host = null;
        try
        {
            var port = GetUnusedLoopbackPort();
            var store = new NativeProxyConfigStore(dir);
            store.Save(new NativeProxyConfig
            {
                ListenPort = port,
                AdmissionToken = "management-admission-secret",
                Providers =
                [
                    new ProviderDefinition
                    {
                        Id = "external-a",
                        Name = "External A",
                        Adapter = "openai-chat",
                        BaseUrl = "https://old.example.test/v1",
                        ApiKey = "test-key",
                        DefaultModel = "model-a",
                        Models = ["model-a"]
                    }
                ]
            });
            host = new NativeProxyHost(store, dataRootOverride: dir);
            await host.Application.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using var invalid = new HttpRequestMessage(HttpMethod.Patch, "/api/providers?name=external-a")
            {
                Content = new StringContent(
                    "{\"baseUrl\":\"https://new.example.test/v1\",\"adapter\":\"not-supported\"}",
                    Encoding.UTF8,
                    "application/json")
            };
            invalid.Headers.TryAddWithoutValidation("X-CMM-Admission", "Bearer management-admission-secret");
            using var invalidResponse = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

            using var valid = new HttpRequestMessage(HttpMethod.Patch, "/api/providers?name=external-a")
            {
                Content = new StringContent("{\"disabled\":true}", Encoding.UTF8, "application/json")
            };
            valid.Headers.TryAddWithoutValidation("X-CMM-Admission", "Bearer management-admission-secret");
            using var validResponse = await client.SendAsync(valid);
            Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);

            var saved = store.Load();
            var provider = Assert.Single(saved.Providers);
            Assert.Equal("https://old.example.test/v1", provider.BaseUrl);
            Assert.Equal("openai-chat", provider.Adapter);
            Assert.True(provider.Disabled);
        }
        finally
        {
            if (host is not null) await host.StopAsync();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CustomProviderRoute_RevalidatesAndAuthenticatesThroughNativeEngine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-custom-gateway-" + Guid.NewGuid().ToString("N"));
        var nativeRoot = Path.Combine(dir, "native-proxy");
        Directory.CreateDirectory(nativeRoot);
        NativeProxyHost? nativeHost = null;
        CancellationTokenSource? gatewayCancellation = null;
        Task<int>? gatewayRun = null;
        try
        {
            var nativePort = GetUnusedLoopbackPort();
            var gatewayPort = GetUnusedLoopbackPort();
            string? upstreamAuthorization = null;
            using var upstream = new HttpClient(new AdmissionRoutingHandler(request =>
            {
                upstreamAuthorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"id\":\"chatcmpl_custom\",\"object\":\"chat.completion\",\"model\":\"grok-4\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"CUSTOM_ROUTE_OK\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }));

            var store = new NativeProxyConfigStore(nativeRoot);
            store.Save(new NativeProxyConfig
            {
                ListenPort = nativePort,
                AdmissionToken = "native-admission-secret",
                Providers =
                [
                    new ProviderDefinition
                    {
                        Id = "grok-a",
                        Name = "Grok A",
                        Adapter = "openai-chat",
                        BaseUrl = "https://models.example.test/v1",
                        ApiKey = "provider-api-key",
                        DefaultModel = "grok-4",
                        Models = ["grok-4"]
                    }
                ]
            });
            nativeHost = new NativeProxyHost(store, upstream: upstream, dataRootOverride: nativeRoot);
            await nativeHost.Application.StartAsync();

            var route = Assert.Single(UnifiedGatewayService.BuildCustomProviderRoutes(
                new ProviderView { Id = "grok-a", DisplayName = "Grok A", Adapter = "openai-chat" },
                [new ModelOption { Provider = "grok-a", Id = "grok-4", Namespaced = "grok-a/grok-4" }],
                nativePort));
            new SecretStore(dir).SaveInternal(UnifiedGatewayKeys.MasterSecretName, "gateway-client-key");
            var configuration = new UnifiedGatewayConfiguration
            {
                Port = gatewayPort,
                DataDirectory = dir,
                Routes = [route]
            };
            configuration.ConfigurationFingerprint = UnifiedGatewayConfigurationIdentity.Compute(configuration);
            var gatewayPath = Path.Combine(dir, "unified-gateway.json");
            File.WriteAllText(gatewayPath, JsonSerializer.Serialize(configuration));
            gatewayCancellation = new CancellationTokenSource();
            gatewayRun = UnifiedGatewayHost.RunAsync(gatewayPath, gatewayCancellation.Token);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ready = false;
            for (var attempt = 0; attempt < 50 && !ready; attempt++)
            {
                try
                {
                    using var health = await client.GetAsync($"http://127.0.0.1:{gatewayPort}/health");
                    ready = health.IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(40);
                }
            }
            Assert.True(ready);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{gatewayPort}/v1/chat/completions")
            {
                Content = new StringContent(
                    "{\"model\":\"grok-a/grok-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gateway-client-key");
            request.Headers.TryAddWithoutValidation(
                UnifiedGatewayHost.SourceFingerprintHeader,
                route.SourceFingerprint);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("CUSTOM_ROUTE_OK", responseBody, StringComparison.Ordinal);
            Assert.Equal("Bearer provider-api-key", upstreamAuthorization);

            using var ordinaryHarnessRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{gatewayPort}/v1/chat/completions")
            {
                Content = new StringContent(
                    "{\"model\":\"grok-a/grok-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hello again\"}]}",
                    Encoding.UTF8,
                    "application/json")
            };
            ordinaryHarnessRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "gateway-client-key");
            using var ordinaryHarnessResponse = await client.SendAsync(ordinaryHarnessRequest);
            Assert.Equal(HttpStatusCode.OK, ordinaryHarnessResponse.StatusCode);
        }
        finally
        {
            if (gatewayCancellation is not null)
            {
                gatewayCancellation.Cancel();
                if (gatewayRun is not null) await gatewayRun.WaitAsync(TimeSpan.FromSeconds(5));
                gatewayCancellation.Dispose();
            }
            if (nativeHost is not null) await nativeHost.StopAsync();
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
    public void AnthropicRequest_UsesValidSystemShapeAndGroupsParallelToolResults()
    {
        var request = new OcxParsedRequest
        {
            Messages =
            [
                new OcxMessage { Role = "system", Content = "first instruction" },
                new OcxMessage { Role = "developer", Content = "second instruction" },
                new OcxMessage
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new OcxToolCall { Id = "tool_a", Function = new OcxToolCallFunction { Name = "alpha", Arguments = "{}" } },
                        new OcxToolCall { Id = "tool_b", Function = new OcxToolCallFunction { Name = "beta", Arguments = "{}" } }
                    ]
                },
                new OcxMessage { Role = "tool", ToolCallId = "tool_a", Content = "A" },
                new OcxMessage { Role = "tool", ToolCallId = "tool_b", Content = "B" }
            ]
        };

        using var document = JsonDocument.Parse(AnthropicAdapter.BuildRequestJson(request, "claude-test"));
        var root = document.RootElement;
        Assert.Equal("first instruction\n\nsecond instruction", root.GetProperty("system").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("system").ValueKind);
        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        var resultTurn = messages[1];
        Assert.Equal("user", resultTurn.GetProperty("role").GetString());
        var results = resultTurn.GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("tool_a", results[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("tool_b", results[1].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public void AnthropicParser_PreservesEveryToolResultInOneUserTurn()
    {
        const string json = """
        {
          "model": "claude-test",
          "messages": [
            {
              "role": "assistant",
              "content": [
                {"type":"tool_use","id":"tool_a","name":"alpha","input":{}},
                {"type":"tool_use","id":"tool_b","name":"beta","input":{}}
              ]
            },
            {
              "role": "user",
              "content": [
                {"type":"tool_result","tool_use_id":"tool_a","content":"A"},
                {"type":"tool_result","tool_use_id":"tool_b","content":[{"type":"text","text":"B"}]}
              ]
            }
          ]
        }
        """;
        var parsed = AnthropicParser.Parse(JsonSerializer.Deserialize<AnthropicMessagesRequest>(json)!);

        Assert.Equal(3, parsed.Messages.Count);
        var calls = parsed.Messages[0].ToolCalls!;
        Assert.Equal(2, calls.Count);
        Assert.Equal(("tool_a", "alpha"), (calls[0].Id, calls[0].Function?.Name));
        Assert.Equal(("tool_b", "beta"), (calls[1].Id, calls[1].Function?.Name));
        Assert.Equal(("tool_a", "A"), (parsed.Messages[1].ToolCallId, parsed.Messages[1].Content));
        Assert.Equal(("tool_b", "B"), (parsed.Messages[2].ToolCallId, parsed.Messages[2].Content));
    }

    [Fact]
    public void GoogleRequest_GroupsParallelToolResultsInOneUserTurn()
    {
        var request = new OcxParsedRequest
        {
            Messages =
            [
                new OcxMessage
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new OcxToolCall { Id = "tool_a", Function = new OcxToolCallFunction { Name = "alpha", Arguments = "{}" } },
                        new OcxToolCall { Id = "tool_b", Function = new OcxToolCallFunction { Name = "beta", Arguments = "{}" } }
                    ]
                },
                new OcxMessage { Role = "tool", ToolCallId = "tool_a", Content = "{\"value\":\"A\"}" },
                new OcxMessage { Role = "tool", ToolCallId = "tool_b", Content = "{\"value\":\"B\"}" }
            ]
        };

        using var document = JsonDocument.Parse(GoogleAdapter.BuildRequestJson(request));
        var contents = document.RootElement.GetProperty("contents");
        Assert.Equal(2, contents.GetArrayLength());
        var resultTurn = contents[1];
        Assert.Equal("user", resultTurn.GetProperty("role").GetString());
        var parts = resultTurn.GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("alpha", parts[0].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal("beta", parts[1].GetProperty("functionResponse").GetProperty("name").GetString());
    }

    [Fact]
    public async Task RealResponsesToolShape_IsNormalizedForEveryThirdPartyAdapter()
    {
        const string requestJson = """
        {
          "model": "grok-source/grok-4",
          "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"check weather"}]}],
          "tools": [{"type":"function","name":"weather","description":"weather lookup","parameters":{"type":"object","properties":{"city":{"type":"string"}}},"strict":true}],
          "tool_choice": "auto",
          "parallel_tool_calls": true,
          "reasoning": {"effort":"low"},
          "stream": false
        }
        """;
        var request = JsonSerializer.Deserialize<ResponsesRequest>(requestJson)!;
        var parsed = ResponsesParser.Parse(request);
        var function = Assert.Single(parsed.Tools!).Function;
        Assert.NotNull(function);
        Assert.Equal("weather", function!.Name);
        Assert.True(function.Strict);

        var chat = OpenAiChatAdapter.BuildRequestJson(
            new ProviderDefinition(), parsed, "grok-4");
        using (var document = JsonDocument.Parse(chat))
        {
            var root = document.RootElement;
            Assert.Equal("check weather", root.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("weather", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        }

        var anthropic = AnthropicAdapter.BuildRequestJson(parsed, "claude-test");
        using (var document = JsonDocument.Parse(anthropic))
        {
            var root = document.RootElement;
            Assert.Equal("check weather", root.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("weather", root.GetProperty("tools")[0].GetProperty("name").GetString());
        }

        var google = GoogleAdapter.BuildRequestJson(parsed);
        using (var document = JsonDocument.Parse(google))
        {
            var root = document.RootElement;
            Assert.Equal("check weather", root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal("weather", root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        }

        string? capturedBody = null;
        using var client = new HttpClient(new StaticHttpHandler(message =>
        {
            capturedBody = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"resp_test\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var adapter = new OpenAiResponsesAdapter(client);
        await using var response = await adapter.FetchAsync(
            new ProviderDefinition
            {
                Id = "grok-source",
                Adapter = "openai-responses",
                BaseUrl = "https://api.x.ai/v1",
                ApiKey = "provider-key"
            },
            parsed,
            "grok-4",
            CancellationToken.None);

        using var captured = JsonDocument.Parse(capturedBody!);
        var capturedRoot = captured.RootElement;
        Assert.Equal("grok-4", capturedRoot.GetProperty("model").GetString());
        var capturedTool = capturedRoot.GetProperty("tools")[0];
        Assert.Equal("weather", capturedTool.GetProperty("name").GetString());
        Assert.False(capturedTool.TryGetProperty("function", out _));
        Assert.True(capturedTool.GetProperty("strict").GetBoolean());
        Assert.Equal("auto", capturedRoot.GetProperty("tool_choice").GetString());
        Assert.True(capturedRoot.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("low", capturedRoot.GetProperty("reasoning").GetProperty("effort").GetString());
    }

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
    public async Task ThirdPartyResponsesText_IsForwardedIncrementally_AndBridgeEmitsMessageLifecycle()
    {
        const string upstream = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}\n\n"
                                + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo\"}\n\n"
                                + "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello\"}]}],\"usage\":{\"input_tokens\":2,\"output_tokens\":1,\"total_tokens\":3}}}\n\n"
                                + "data: [DONE]\n\n";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream")
        }));
        var adapter = new OpenAiResponsesAdapter(client);
        await using var result = await adapter.FetchAsync(
            new ProviderDefinition
            {
                Id = "third-party",
                BaseUrl = "https://models.example.test/v1",
                ApiKey = "provider-key"
            },
            new OcxParsedRequest { Stream = true, Messages = [new OcxMessage { Role = "user", Content = "hello" }] },
            "model-a",
            CancellationToken.None);
        var events = await CollectAsync(result.Events!);
        Assert.Equal(new[] { "Hel", "lo" }, events.Where(item => item.Type == "text").Select(item => item.Text));

        var bridge = new ResponsesBridge("model-a");
        var frames = new StringBuilder();
        await foreach (var frame in bridge.StreamAsync(ToAsync(events), CancellationToken.None))
            frames.Append(frame);
        var all = frames.ToString();
        Assert.True(all.IndexOf("response.output_item.added", StringComparison.Ordinal)
                    < all.IndexOf("response.output_text.delta", StringComparison.Ordinal));
        Assert.True(all.IndexOf("response.content_part.added", StringComparison.Ordinal)
                    < all.IndexOf("response.output_text.delta", StringComparison.Ordinal));
        Assert.Contains("\"input_tokens\":2", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThirdPartyResponsesStream_UsesCompletedMessageWhenProviderOmitsTextDeltas()
    {
        const string upstream = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Completed-only answer\"}]}],\"usage\":{\"input_tokens\":2,\"output_tokens\":3,\"total_tokens\":5}}}\n\n"
                                + "data: [DONE]\n\n";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream")
        }));
        var adapter = new OpenAiResponsesAdapter(client);
        await using var result = await adapter.FetchAsync(
            new ProviderDefinition
            {
                Id = "third-party",
                BaseUrl = "https://models.example.test/v1",
                ApiKey = "provider-key"
            },
            new OcxParsedRequest
            {
                Stream = true,
                Messages = [new OcxMessage { Role = "user", Content = "hello" }]
            },
            "model-a",
            CancellationToken.None);

        var events = await CollectAsync(result.Events!);
        Assert.Equal("Completed-only answer", Assert.Single(events, item => item.Type == "text").Text);
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
    public async Task GoogleStreamingFunctionCall_IsNotLost_AndUsagePrecedesToolFinish()
    {
        const string upstream = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"weather\",\"args\":{\"city\":\"Beijing\"}}}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":4,\"candidatesTokenCount\":2}}\n\n";
        using var client = new HttpClient(new StaticHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(upstream, Encoding.UTF8, "text/event-stream")
        }));
        var adapter = new GoogleAdapter(client);
        await using var result = await adapter.FetchAsync(
            new ProviderDefinition { Id = "gemini", BaseUrl = "https://google.example.test", ApiKey = "key" },
            new OcxParsedRequest { Stream = true, Messages = [new OcxMessage { Role = "user", Content = "hello" }] },
            "gemini-test",
            CancellationToken.None);
        var events = await CollectAsync(result.Events!);

        var call = Assert.Single(events, item => item.Type == "function_call");
        Assert.Equal("weather", call.FunctionName);
        Assert.Equal("{\"city\":\"Beijing\"}", call.Arguments);
        Assert.Contains(events, item => item.Type == "function_call_done");
        Assert.Contains(events, item => item.Type == "usage" && item.Usage?.TotalTokens == 6);
        Assert.Equal("tool_calls", Assert.Single(events, item => item.Type == "finish").FinishReason);

        var bridge = new ResponsesBridge("gemini-test");
        var frames = new StringBuilder();
        await foreach (var frame in bridge.StreamAsync(ToAsync(events), CancellationToken.None))
            frames.Append(frame);
        Assert.Contains("\"end_turn\":false", frames.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicBridge_DoesNotTurnTruncatedUpstreamIntoSuccessfulEndTurn()
    {
        var bridge = new AnthropicOutboundBridge();
        var frames = new StringBuilder();
        await foreach (var frame in bridge.StreamAsync(
                           ToAsync([new AdapterEvent { Type = "incomplete", Text = "truncated" }]),
                           "claude-test",
                           CancellationToken.None))
            frames.Append(frame);

        Assert.Equal("incomplete", bridge.Status);
        Assert.Contains("upstream_incomplete", frames.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("message_stop", frames.ToString(), StringComparison.Ordinal);
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
    public void Resolve_ThirdPartyModelRequiresNamespaceAndCatalogPublishesNamespace()
    {
        var registry = new ProviderRegistry(new NativeProxyConfig
        {
            Providers =
            [
                new ProviderDefinition { Id = "deepseek", Name = "DeepSeek", Models = ["deepseek-v4-flash"], DefaultModel = "deepseek-v4-flash" }
            ]
        });
        Assert.Throws<CodexOpenCodexNative.Providers.ModelNotFoundException>(
            () => CodexOpenCodexNative.Providers.RouteResolver.Resolve(registry, "deepseek-v4-flash"));
        var result = CodexOpenCodexNative.Providers.RouteResolver.Resolve(
            registry,
            "deepseek/deepseek-v4-flash");
        Assert.Equal("deepseek", result.ProviderId);
        Assert.Equal("deepseek-v4-flash", result.ModelId);
        var catalog = registry.ListModels();
        Assert.Contains(catalog, model => model.Id == "deepseek/deepseek-v4-flash"
                                          && model.Namespaced == model.Id);
        Assert.DoesNotContain(catalog, model => model.Id == "deepseek-v4-flash");
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
    public void AppSettings_LoadsNestedProviderNamesCaseInsensitively()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-settings-provider-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), """
            {
              "providerNames": { "Grok-One": "Grok 主线路" },
              "v2rayPath": "",
              "v2rayProxyUrl": "socks5://127.0.0.1:10808",
              "nativeEnginePort": 10100,
              "unifiedGatewayPort": 10110,
              "serverAliases": []
            }
            """);

            var settings = new AppSettingsService(root);

            Assert.True(settings.TryGetProviderName("grok-one", out var displayName));
            Assert.Equal("Grok 主线路", displayName);
            Assert.Null(settings.LoadWarning);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AppSettings_CaseDuplicateProviderNamesEnterReadOnlyRecoveryMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-settings-provider-duplicate-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "settings.json");
            var original = """
            {
              "ProviderNames": { "Grok-One": "A", "grok-one": "B" },
              "V2rayPath": "",
              "V2rayProxyUrl": "socks5://127.0.0.1:10808",
              "NativeEnginePort": 10100,
              "UnifiedGatewayPort": 10110,
              "ServerAliases": []
            }
            """;
            File.WriteAllText(path, original);

            var settings = new AppSettingsService(root);

            Assert.NotNull(settings.LoadWarning);
            Assert.Throws<InvalidOperationException>(() => settings.SetProviderName("safe-provider", "Safe"));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(root, "settings.corrupt-*.json"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SecretStore_CaseDuplicateNamesEnterReadOnlyRecoveryMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-secrets-case-duplicate-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "secrets.json");
            var original = "{\"Provider\":\"AA==\",\"provider\":\"BB==\"}";
            File.WriteAllText(path, original);

            var store = new SecretStore(root);

            Assert.NotNull(store.LoadWarning);
            Assert.Throws<InvalidOperationException>(() => store.Save("safe-provider", "new-secret"));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(root, "secrets.corrupt-*.json"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SecretStore_RejectsProviderIdsThatShareAnEnvironmentVariable()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-secrets-environment-collision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SecretStore(root);
            store.Save("foo-bar", "secret-a");

            var error = Assert.Throws<InvalidOperationException>(() => store.Save("foo_bar", "secret-b"));

            Assert.Contains("共用同一个 API Key 环境变量", error.Message, StringComparison.Ordinal);
            Assert.Equal("secret-a", store.Read("foo-bar"));
            Assert.Null(store.Read("foo_bar"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SecretStore_EngineEnvironmentFailsClosedOnPreexistingCollision()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmm-secrets-startup-collision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SecretStore(root);
            store.Save("foo-bar", "secret-a");
            var path = Path.Combine(root, "secrets.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var encoded = document.RootElement.GetProperty("foo-bar").GetString();
            File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["foo-bar"] = encoded!,
                ["foo_bar"] = encoded!
            }));

            var reloaded = new SecretStore(root);
            var error = Assert.Throws<InvalidOperationException>(() => reloaded.GetProviderProcessEnvironment());

            Assert.Contains("共用同一个 API Key 环境变量", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

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

public sealed class UnifiedGatewayRotationGateTests
{
    private static UnifiedGatewayRoute TestRoute(string gatewayModel, string upstreamModel, string poolId) => new()
    {
        GatewayModel = gatewayModel,
        UpstreamModel = upstreamModel,
        BaseUrl = "http://127.0.0.1:18100/v1",
        SecretName = "cmm-test",
        PoolId = poolId,
        PoolLabel = poolId,
        SourceId = SubagentSourceIdentity.CliSourceId(poolId),
        SourceKind = SubagentSourceKind.CliProxyPool.ToString(),
        RoutePrefix = "cli/" + poolId + "/",
        Adapter = SubagentSourceIdentity.OpenAiChatAdapter,
        CredentialIdentity = "acct:AA",
        SourceFingerprint = SubagentSourceIdentity.Compute(
            SubagentSourceIdentity.CliSourceId(poolId),
            SubagentSourceKind.CliProxyPool.ToString(),
            "http://127.0.0.1:18100/v1",
            SubagentSourceIdentity.OpenAiChatAdapter,
            "cmm-test",
            "cli/" + poolId + "/",
            "acct:AA")
    };

    [Fact]
    public void ConfigurationIdentity_EmptyRotationGroups_KeepsLegacyFingerprint()
    {
        var routes = new List<UnifiedGatewayRoute> { TestRoute("cli/a/m", "m", "a") };
        var legacy = new UnifiedGatewayConfiguration
        {
            Port = 10110,
            DataDirectory = @"C:\data",
            Routes = routes
        };
        legacy.ConfigurationFingerprint = UnifiedGatewayConfigurationIdentity.Compute(legacy);
        var upgraded = new UnifiedGatewayConfiguration
        {
            Port = 10110,
            DataDirectory = @"C:\data",
            Routes = routes,
            RotationGroups = new List<UnifiedGatewayRotationGroup>()
        };
        Assert.Equal(legacy.ConfigurationFingerprint, UnifiedGatewayConfigurationIdentity.Compute(upgraded));
    }

    [Fact]
    public void ConfigurationIdentity_IncludesRotationGroupMembers()
    {
        var baseConfig = new UnifiedGatewayConfiguration
        {
            Port = 10110,
            DataDirectory = @"C:\data",
            Routes = new List<UnifiedGatewayRoute> { TestRoute("cli/a/m", "m", "a") }
        };
        var withGroup = new UnifiedGatewayConfiguration
        {
            Port = 10110,
            DataDirectory = @"C:\data",
            Routes = baseConfig.Routes,
            RotationGroups = new List<UnifiedGatewayRotationGroup>
            {
                new() { GatewayModel = "codex-auto/m", UpstreamModel = "m", Candidates = new List<string> { "cli/a/m" } }
            }
        };
        var withOtherGroup = new UnifiedGatewayConfiguration
        {
            Port = 10110,
            DataDirectory = @"C:\data",
            Routes = baseConfig.Routes,
            RotationGroups = new List<UnifiedGatewayRotationGroup>
            {
                new() { GatewayModel = "codex-auto/m", UpstreamModel = "m", Candidates = new List<string> { "cli/other/m" } }
            }
        };
        var baseHash = UnifiedGatewayConfigurationIdentity.Compute(baseConfig);
        Assert.NotEqual(baseHash, UnifiedGatewayConfigurationIdentity.Compute(withGroup));
        Assert.NotEqual(
            UnifiedGatewayConfigurationIdentity.Compute(withGroup),
            UnifiedGatewayConfigurationIdentity.Compute(withOtherGroup));
    }

    [Fact]
    public void ValidateRotationGroups_RejectsCollisionsAndDanglingCandidates()
    {
        var routeA = TestRoute("cli/a/m", "m", "a");
        var routeOtherModel = TestRoute("cli/b/other", "other", "b");
        var valid = new UnifiedGatewayConfiguration
        {
            Routes = new List<UnifiedGatewayRoute> { routeA, routeOtherModel },
            RotationGroups = new List<UnifiedGatewayRotationGroup>
            {
                new() { GatewayModel = "codex-auto/m", UpstreamModel = "m", Candidates = new List<string> { "cli/a/m" } }
            }
        };
        Assert.Null(UnifiedGatewayHost.ValidateRotationGroups(valid));

        var collidesWithRoute = new UnifiedGatewayConfiguration
        {
            Routes = valid.Routes,
            RotationGroups = new List<UnifiedGatewayRotationGroup>
            {
                new() { GatewayModel = "cli/a/m", UpstreamModel = "m", Candidates = new List<string> { "cli/a/m" } }
            }
        };
        Assert.NotNull(UnifiedGatewayHost.ValidateRotationGroups(collidesWithRoute));

        var danglingCandidate = new UnifiedGatewayConfiguration
        {
            Routes = valid.Routes,
            RotationGroups = new List<UnifiedGatewayRotationGroup>
            {
                new() { GatewayModel = "codex-auto/m", UpstreamModel = "m", Candidates = new List<string> { "cli/missing/m" } }
            }
        };
        Assert.NotNull(UnifiedGatewayHost.ValidateRotationGroups(danglingCandidate));

        var upstreamMismatch = new UnifiedGatewayConfiguration
        {
            Routes = valid.Routes,
            RotationGroups = new List<UnifiedGatewayRotationGroup>
            {
                new() { GatewayModel = "codex-auto/m", UpstreamModel = "other", Candidates = new List<string> { "cli/a/m" } }
            }
        };
        Assert.NotNull(UnifiedGatewayHost.ValidateRotationGroups(upstreamMismatch));

        var duplicateGroups = new UnifiedGatewayConfiguration
        {
            Routes = valid.Routes,
            RotationGroups = new List<UnifiedGatewayRotationGroup>
            {
                new() { GatewayModel = "codex-auto/m", UpstreamModel = "m", Candidates = new List<string> { "cli/a/m" } },
                new() { GatewayModel = "codex-auto/m", UpstreamModel = "m", Candidates = new List<string> { "cli/a/m" } }
            }
        };
        Assert.NotNull(UnifiedGatewayHost.ValidateRotationGroups(duplicateGroups));
    }

    [Fact]
    public void BuildCodexAutoRotationGroups_ClustersCliPoolsOnlyAndSkipsCollisions()
    {
        var cliA = TestRoute("cli/a/gpt-x", "gpt-x", "a");
        var cliB = TestRoute("codex-plus/gpt-x", "gpt-x", "b");
        var custom = new UnifiedGatewayRoute
        {
            GatewayModel = "custom/prov/gpt-x",
            UpstreamModel = "gpt-x",
            BaseUrl = "http://127.0.0.1:10100/v1",
            SourceId = "custom:prov",
            SourceKind = SubagentSourceKind.OpenAiCompatible.ToString(),
            RoutePrefix = "prov/",
            Adapter = "openai-responses",
            SourceFingerprint = "not-empty"
        };
        var groups = UnifiedGatewayService.BuildCodexAutoRotationGroups(
            new List<UnifiedGatewayRoute> { cliA, cliB, custom });
        var group = Assert.Single(groups);
        Assert.Equal("codex-auto/gpt-x", group.GatewayModel);
        Assert.Equal("gpt-x", group.UpstreamModel);
        Assert.Equal(new[] { "cli/a/gpt-x", "codex-plus/gpt-x" }, group.Candidates);

        var occupied = new UnifiedGatewayConfiguration
        {
            Routes = new List<UnifiedGatewayRoute> { cliA, cliB, custom }
        };
        occupied.Routes.Add(TestRoute("codex-auto/gpt-x", "gpt-x", "c"));
        var noCollisionGroups = UnifiedGatewayService.BuildCodexAutoRotationGroups(occupied.Routes);
        Assert.Empty(noCollisionGroups);
    }
}

public sealed class UnifiedGatewayClientKeyTests
{
    [Fact]
    public void UnifiedGatewayKeys_LabelRulesAndNameParsing()
    {
        Assert.True(UnifiedGatewayKeys.IsValidLabel("dsh"));
        Assert.True(UnifiedGatewayKeys.IsValidLabel("opencode-2"));
        Assert.False(UnifiedGatewayKeys.IsValidLabel("Dsh"));
        Assert.False(UnifiedGatewayKeys.IsValidLabel("a b"));
        Assert.False(UnifiedGatewayKeys.IsValidLabel("-x"));
        Assert.False(UnifiedGatewayKeys.IsValidLabel(new string('a', 33)));
        Assert.False(UnifiedGatewayKeys.IsValidLabel(null));

        Assert.Equal("master", UnifiedGatewayKeys.LabelForSecretName("unified-gateway:client"));
        Assert.Equal("dsh", UnifiedGatewayKeys.LabelForSecretName("unified-gateway:client:dsh"));
        Assert.Null(UnifiedGatewayKeys.LabelForSecretName("unified-gateway:client:"));
        Assert.Null(UnifiedGatewayKeys.LabelForSecretName("unified-gateway:other"));
        Assert.Equal("unified-gateway:client:dsh", UnifiedGatewayKeys.SecretNameForLabel("dsh"));
        var generated = UnifiedGatewayKeys.GenerateKeyValue("dsh");
        Assert.StartsWith("cmm-gw-dsh-", generated, StringComparison.Ordinal);
        Assert.Equal(64, generated.Length - "cmm-gw-dsh-".Length);
    }

    [Fact]
    public void SecretStore_ListInternalNames_OnlyReturnsMatchingPrefix()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cmm-gateway-key-list-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secrets = new SecretStore(root);
            secrets.SaveInternal("unified-gateway:client", "master-key");
            secrets.SaveInternal("unified-gateway:client:dsh", "dsh-key");
            secrets.SaveInternal("unified-gateway:client:opencode", "oc-key");
            secrets.SaveInternal("unrelated-secret", "x");
            var names = secrets.ListInternalNames(UnifiedGatewayKeys.ClientPrefix);
            Assert.Equal(
                new[] { "unified-gateway:client:dsh", "unified-gateway:client:opencode" },
                names);
            Assert.Equal("dsh-key", secrets.ReadInternal("unified-gateway:client:dsh"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void UnifiedGatewayService_ClientKeyLifecycleIsEnforced()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "cmm-gateway-key-lifecycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettingsService(root);
            var secrets = new SecretStore(root);
            var gateway = new UnifiedGatewayService(
                settings,
                secrets,
                new CliProxyPoolService(settings, secrets),
                new OpenCodexClient(),
                new PoolCatalogService(root, settings.ReservedLocalPorts));

            var key = gateway.CreateGatewayClientKey("dsh");
            Assert.StartsWith("cmm-gw-dsh-", key, StringComparison.Ordinal);
            Assert.Equal(key, secrets.ReadInternal(UnifiedGatewayKeys.SecretNameForLabel("dsh")));
            Assert.Throws<InvalidOperationException>(() => gateway.CreateGatewayClientKey("dsh"));
            Assert.Throws<InvalidOperationException>(() => gateway.CreateGatewayClientKey("Bad Label"));

            var views = gateway.ReadGatewayClientKeys();
            Assert.Contains(views, view => view.Label == "master");
            Assert.Contains(views, view => view.Label == "dsh" && view.KeyHint != "未生成");
            Assert.DoesNotContain(views, view => view.KeyHint.Contains(key, StringComparison.Ordinal));

            Assert.Throws<InvalidOperationException>(() => gateway.RevokeGatewayClientKey("master"));
            gateway.RevokeGatewayClientKey("dsh");
            Assert.Null(secrets.ReadInternal(UnifiedGatewayKeys.SecretNameForLabel("dsh")));
            Assert.Throws<InvalidOperationException>(() => gateway.RevokeGatewayClientKey("dsh"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RotationRuntimeState_AffinityAndFailureScore()
    {
        var state = new UnifiedGatewayRotationRuntimeState();
        Assert.Null(state.TryGetAffinity("resp:s1"));
        state.BindAffinity("resp:s1", "pool-a");
        Assert.Equal("pool-a", state.TryGetAffinity("resp:s1"));
        state.BindAffinity("resp:s1", "pool-b");
        Assert.Equal("pool-b", state.TryGetAffinity("resp:s1"));

        Assert.False(state.IsCoolingDown("pool-a"));
        Assert.Empty(state.CoolingPools());
        state.MarkCooldown("pool-a", TimeSpan.FromSeconds(30));
        Assert.True(state.IsCoolingDown("pool-a"));
        Assert.Equal(new[] { "pool-a" }, state.CoolingPools());

        Assert.Equal(0, state.FailureScore("pool-a"));
        state.RecordAttempt("pool-a", success: false);
        state.RecordAttempt("pool-a", success: false);
        state.RecordAttempt("pool-a", success: false);
        state.RecordAttempt("pool-a", success: true);
        Assert.Equal(0.75, state.FailureScore("pool-a"), 6);
        Assert.Equal(0, state.FailureScore("pool-b"));
    }
}

public sealed class UsageFlowMathTests
{
    [Fact]
    public void SmoothCurve_EmptySingleAndPairAreSafe()
    {
        Assert.Empty(UsageFlowMath.SmoothCurve(Array.Empty<FlowPoint>()));
        var single = UsageFlowMath.SmoothCurve(new[] { new FlowPoint(3, 7) });
        var only = Assert.Single(single);
        Assert.Equal(new FlowPoint(3, 7), only.Start);
        Assert.Equal(new FlowPoint(3, 7), only.End);

        var pair = UsageFlowMath.SmoothCurve(new[] { new FlowPoint(0, 0), new FlowPoint(10, 5) });
        var line = Assert.Single(pair);
        Assert.Equal(new FlowPoint(0, 0), line.Start);
        Assert.Equal(new FlowPoint(10, 5), line.End);
    }

    [Fact]
    public void SmoothCurve_PassesThroughAnchorsInOrder()
    {
        var anchors = new[]
        {
            new FlowPoint(0, 10), new FlowPoint(10, 4), new FlowPoint(20, 18),
            new FlowPoint(30, 6), new FlowPoint(40, 12)
        };
        var segments = UsageFlowMath.SmoothCurve(anchors);
        Assert.Equal(anchors.Length - 1, segments.Count);
        Assert.Equal(anchors[0], segments[0].Start);
        Assert.Equal(anchors[^1], segments[^1].End);
        for (var index = 0; index < segments.Count; index++)
        {
            Assert.Equal(anchors[index], segments[index].Start);
            Assert.Equal(anchors[index + 1], segments[index].End);
        }
        foreach (var segment in segments)
        {
            Assert.False(double.IsNaN(segment.Control1.X) || double.IsNaN(segment.Control1.Y)
                         || double.IsNaN(segment.Control2.X) || double.IsNaN(segment.Control2.Y));
        }
    }

    [Fact]
    public void SmoothCurve_CollinearPointsStayNearBoundingBox()
    {
        var anchors = new List<FlowPoint>();
        for (var index = 0; index < 8; index++) anchors.Add(new FlowPoint(index * 12, 30));
        var segments = UsageFlowMath.SmoothCurve(anchors);
        foreach (var segment in segments)
        {
            Assert.InRange(segment.Control1.Y, -6, 36);
            Assert.InRange(segment.Control2.Y, -6, 36);
            Assert.InRange(segment.Control1.X, -6, anchors[^1].X + 6);
            Assert.InRange(segment.Control2.X, -6, anchors[^1].X + 6);
        }
    }

    [Fact]
    public void NiceCeiling_UsesReadableSteps()
    {
        Assert.Equal(1, UsageFlowMath.NiceCeiling(0));
        Assert.Equal(0.5, UsageFlowMath.NiceCeiling(0.4));
        Assert.Equal(1, UsageFlowMath.NiceCeiling(1));
        Assert.Equal(2, UsageFlowMath.NiceCeiling(1.5));
        Assert.Equal(5, UsageFlowMath.NiceCeiling(3));
        Assert.Equal(10, UsageFlowMath.NiceCeiling(7.2));
        Assert.Equal(20, UsageFlowMath.NiceCeiling(11));
        Assert.Equal(50_000, UsageFlowMath.NiceCeiling(26_000));
        Assert.Equal(100, UsageFlowMath.NiceCeiling(100));
        Assert.Equal(200, UsageFlowMath.NiceCeiling(101));
        Assert.Equal(1, UsageFlowMath.NiceCeiling(double.NaN));
        Assert.Equal(1, UsageFlowMath.NiceCeiling(-5));
    }
}

public sealed class DailyTokenSeriesTests
{
    private static AccountUsageAttemptFact Fact(
        string idempotencyKey,
        DateTimeOffset occurredAt,
        bool selected,
        RuntimeExecutionOutcome outcome,
        AttemptTokenUsageFact? usage) => new(
        SchemaVersion: 4,
        IdempotencyKey: idempotencyKey,
        PayloadHash: "hash-" + idempotencyKey,
        RequestId: null,
        RequestIdentity: "req-" + idempotencyKey,
        AttemptOrdinal: 1,
        RequestLevelUsage: false,
        ProviderId: "openai",
        AccountId: "__main__",
        AccountAttributed: true,
        AccountKeyVersion: 1,
        AccountKeyId: "k1",
        StableAccountIdentity: "acct:A",
        AccountIdentitySource: RuntimeAccountIdentitySource.Unknown,
        Model: "gpt-5.6-terra",
        RequestedRoute: "cmm/main",
        OccurredAt: occurredAt,
        Result: outcome,
        HttpStatus: 200,
        ErrorClassification: RuntimeFailoverReason.None,
        Selected: selected,
        SelectionEvidence: RuntimeAttemptSelectionEvidence.ExplicitFlag,
        LogSelectionBasis: RuntimeLogSelectionBasis.Timestamp,
        ErrorCode: null,
        ErrorMessage: null,
        Usage: usage,
        SourceNamespace: "test",
        SourceEventIdentity: "evt-" + idempotencyKey,
        Source: "unit",
        EvidenceStrength: AccountUsageEvidenceStrength.Strong,
        RecordedAt: occurredAt);

    private static AttemptTokenUsageFact Usage(long input, long output, long? total = null) => new(
        input, null, null, null, output, null, total ?? input + output,
        TokenTotalSource.Upstream, TokenTotalValidationState.Valid, string.Empty, string.Empty);

    [Fact]
    public void AggregateDaily_FiltersDedupesAndBucketsByLocalDay()
    {
        var now = new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(8));
        var todayAt = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.FromHours(8));
        var yesterdayAt = new DateTimeOffset(2026, 8, 15, 22, 0, 0, TimeSpan.FromHours(8));
        var ancientAt = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.FromHours(8));
        var facts = new List<AccountUsageAttemptFact>
        {
            Fact("a", todayAt, true, RuntimeExecutionOutcome.Succeeded, Usage(100, 50)),
            Fact("a", todayAt, true, RuntimeExecutionOutcome.Succeeded, Usage(100, 50)),
            Fact("b", todayAt, true, RuntimeExecutionOutcome.Succeeded, Usage(10, 5)),
            Fact("c", yesterdayAt, true, RuntimeExecutionOutcome.Succeeded, Usage(7, 3)),
            Fact("d", yesterdayAt, true, RuntimeExecutionOutcome.Failed, Usage(500, 500)),
            Fact("e", ancientAt, true, RuntimeExecutionOutcome.Succeeded, Usage(999, 999)),
            Fact("f", todayAt, false, RuntimeExecutionOutcome.Succeeded, Usage(888, 888)),
            Fact("g", todayAt, true, RuntimeExecutionOutcome.Succeeded, null)
        };

        var series = AccountUsageLedgerService.AggregateDaily(facts, 14, now);

        Assert.Equal(14, series.Count);
        Assert.Equal(new DateOnly(2026, 8, 3), series[0].LocalDate);
        Assert.Equal(new DateOnly(2026, 8, 16), series[^1].LocalDate);
        var today = series[^1];
        Assert.Equal(110, today.InputTokens);
        Assert.Equal(55, today.OutputTokens);
        Assert.Equal(165, today.TotalTokens);
        Assert.Equal(2, today.Requests);
        var yesterday = series[^2];
        Assert.Equal(10, yesterday.TotalTokens);
        Assert.Equal(1, yesterday.Requests);
        Assert.All(series.Take(12), point =>
        {
            Assert.Equal(0, point.TotalTokens);
            Assert.Equal(0, point.Requests);
        });
    }

    [Fact]
    public void AggregateDaily_EmptyInputYieldsZeroSeries()
    {
        var now = new DateTimeOffset(2026, 8, 16, 20, 0, 0, TimeSpan.FromHours(8));
        var series = AccountUsageLedgerService.AggregateDaily(Array.Empty<AccountUsageAttemptFact>(), 7, now);
        Assert.Equal(7, series.Count);
        Assert.All(series, point => Assert.Equal(0, point.TotalTokens));
    }
}
