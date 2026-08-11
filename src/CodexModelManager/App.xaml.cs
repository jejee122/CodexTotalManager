using System.Windows;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexModelManager.Services;

namespace CodexModelManager;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _restartAfterCodexConnectionChange;
    private bool _restartExpectedConnected;

    public void RestartAfterCodexConnectionChange(bool connectedAfterRestart)
    {
        _restartAfterCodexConnectionChange = true;
        _restartExpectedConnected = connectedAfterRestart;
        Shutdown(0);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        EnsureWindowsDirectoryEnvironment();
        RuntimeMode.Initialize(e.Args);

        if (RuntimeMode.ContainsBlockedServiceCommand(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MessageBox.Show(
                "这个安装是独立开发版，只允许打开界面。Native Engine、统一网关、自检和外部工人命令全部被锁定。",
                "Codex 总管家 - 独立开发版",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(64);
            return;
        }

        if (e.Args.Any(arg => string.Equals(
                arg,
                "--codex-test-double-self-test",
                StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (!RuntimeMode.IsCodexTestDouble)
                    throw new InvalidOperationException("Codex 测试替身模式没有通过启动边界校验。");
                var cycles = ResolveCodexTestDoubleCycles(e.Args);
                var services = AppServices.Create();
                var result = Task.Run(() => CodexTestDoubleSelfTest.RunAsync(services, cycles))
                    .GetAwaiter().GetResult();
                var reportPath = Path.Combine(
                    Environment.GetEnvironmentVariable("CMM_RUNTIME_ROOT")
                    ?? services.Settings.DataDirectory,
                    "codex-test-double-report.json");
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(
                    reportPath,
                    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(result.Success
                    ? $"CODEX_TEST_DOUBLE_OK cycles={result.Cycles} requests={result.RequestsCompleted}"
                    : $"CODEX_TEST_DOUBLE_FAILED {result.Message}");
                Shutdown(result.Success ? 0 : 1);
            }
            catch (Exception ex)
            {
                var root = Environment.GetEnvironmentVariable("CMM_RUNTIME_ROOT")
                           ?? DiagnosticDirectory();
                Directory.CreateDirectory(root);
                File.WriteAllText(
                    Path.Combine(root, "codex-test-double-report.json"),
                    JsonSerializer.Serialize(new
                    {
                        success = false,
                        errorType = ex.GetType().Name,
                        error = ex.Message
                    }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--external-worker-mcp", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create(ResolveMcpSelfTestDataDirectory(e.Args));
                var host = new ExternalWorkerMcpHost(
                    services.ExternalWorker,
                    services.ExternalWorkerAudit,
                    broker: services.WorkerBroker);
                var exitCode = Task.Run(() => host.RunAsync(Console.In, Console.Out)).GetAwaiter().GetResult();
                Shutdown(exitCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"EXTERNAL_WORKER_MCP_FAILED: {ex.GetType().Name}: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--unified-gateway", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var configIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase));
            var configPath = configIndex >= 0 && configIndex + 1 < e.Args.Length ? e.Args[configIndex + 1] : string.Empty;
            var exitCode = string.IsNullOrWhiteSpace(configPath)
                ? 2
                : Task.Run(() => UnifiedGatewayHost.RunAsync(configPath)).GetAwaiter().GetResult();
            Shutdown(exitCode);
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--gateway-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create();
                var status = Task.Run(() => services.UnifiedGateway.EnsureReadyAsync()).GetAwaiter().GetResult();
                var poolDetails = string.Join(" | ", status.Pools.Select(pool =>
                    $"{pool.PoolId}:ready={pool.Ready},models={pool.ModelCount},detail={pool.Detail}"));
                var message = status.Running
                    ? $"GATEWAY_SELF_TEST_OK: url={status.Url}; models={status.Models.Count}; readyPools={status.Pools.Count(pool => pool.Ready)}; {poolDetails}"
                    : $"GATEWAY_SELF_TEST_FAILED: {status.Summary}; {poolDetails}";
                File.WriteAllText(Path.Combine(services.Settings.DataDirectory, "gateway-self-test.txt"), message);
                Console.WriteLine(message);
                Shutdown(status.Running && status.Models.Count > 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                var directory = DiagnosticDirectory();
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "gateway-self-test.txt"), $"GATEWAY_SELF_TEST_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--gateway-http-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create();
                var result = Task.Run(() => RunGatewayHttpTestAsync(services)).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(services.Settings.DataDirectory, "gateway-http-test.txt"), result.Message);
                Console.WriteLine(result.Message);
                Shutdown(result.Success ? 0 : 1);
            }
            catch (Exception ex)
            {
                var directory = DiagnosticDirectory();
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "gateway-http-test.txt"), $"GATEWAY_HTTP_TEST_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--ensure-proxy", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create();
                var ok = Task.Run(() => services.Process.EnsureOpenCodexAsync()).GetAwaiter().GetResult();
                Shutdown(ok ? 0 : 1);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--native-engine", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var port = 10100;
                var portIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase));
                if (portIndex >= 0 && portIndex + 1 < e.Args.Length && int.TryParse(e.Args[portIndex + 1], out var parsedPort))
                    port = parsedPort;
                var dataIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--data-root", StringComparison.OrdinalIgnoreCase));
                var dataRoot = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : null;

                var engine = new NativeEngineService();
                if (!engine.Start(port, dataRoot))
                {
                    Console.WriteLine($"NATIVE_ENGINE_FAILED: {engine.StateDetail}");
                    Shutdown(2);
                    return;
                }
                Task.Delay(4000).GetAwaiter().GetResult();
                if (!Task.Run(() => engine.IsHealthyAsync()).GetAwaiter().GetResult())
                {
                    var diagnostic = $"NATIVE_ENGINE_UNHEALTHY: {engine.StateDetail}; {engine.Diagnose()}";
                    Console.WriteLine(diagnostic);
                    var directory = DiagnosticDirectory(dataRoot);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "native-engine.txt"), diagnostic);
                    engine.Stop();
                    Shutdown(3);
                    return;
                }
                Console.WriteLine($"NATIVE_ENGINE_READY: port={port}; dataRoot={engine.EngineDataRoot}");
                var lifetime = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    lifetime.Cancel();
                };
                while (!lifetime.IsCancellationRequested)
                {
                    Task.Delay(500, lifetime.Token).GetAwaiter().GetResult();
                }
                engine.Stop();
                Console.WriteLine("NATIVE_ENGINE_STOPPED");
                Shutdown(0);
            }
            catch (OperationCanceledException)
            {
                Shutdown(0);
            }
            catch (Exception ex)
            {
                var directory = DiagnosticDirectory();
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "native-engine.txt"), $"NATIVE_ENGINE_FAILED: {ex.Message}");
                Console.WriteLine($"NATIVE_ENGINE_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--theme-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create();
                var snapshot = Task.Run(() => services.DreamSkin.DiscoverAsync()).GetAwaiter().GetResult();
                var success = snapshot.EngineReady
                              && snapshot.ManagerScriptTrusted
                              && snapshot.Themes.Count > 0;
                var message = success
                    ? $"THEME_SELF_TEST_OK: Dream Skin {snapshot.EngineVersion}，主题={snapshot.Themes.Count}个，当前={snapshot.ActiveThemeName}，实时切换={(snapshot.LiveSessionConnected ? "已连接" : "待连接")}"
                    : $"THEME_SELF_TEST_FAILED: {snapshot.StatusTitle}；{snapshot.StatusDetail}";
                var resultPath = Path.Combine(services.Settings.DataDirectory, "theme-self-test.txt");
                File.WriteAllText(resultPath, message);
                Console.WriteLine(message);
                Shutdown(success ? 0 : 1);
            }
            catch (Exception ex)
            {
                var resultDirectory = DiagnosticDirectory();
                Directory.CreateDirectory(resultDirectory);
                File.WriteAllText(
                    Path.Combine(resultDirectory, "theme-self-test.txt"),
                    $"THEME_SELF_TEST_FAILED: {ex.Message}");
                Console.WriteLine($"THEME_SELF_TEST_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--server-self-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create();
                var result = Task.Run(() => services.Dashboard.RunServerHealthAsync()).GetAwaiter().GetResult();
                var detail = string.Join("；", result.Servers.Select(server =>
                    $"{server.Role}(online={server.Online},cpu={server.CpuPercent:0.#}%,mem={server.MemoryPercent:0.#}%,disk={server.DiskPercent:0.#}%,services={server.Services.Count})"))
                             + $"；入口={result.PublicEntryStatus}";
                var connectivityReady = result.Servers.Count == result.ExpectedServerCount
                                        && result.Servers.Count > 0
                                        && result.Servers.All(server => server.Online);
                var message = connectivityReady
                    ? result.Success
                        ? $"SERVER_SELF_TEST_OK: {detail}"
                        : $"SERVER_SELF_TEST_OK_WITH_WARNINGS: {result.Message}；{detail}"
                    : $"SERVER_SELF_TEST_FAILED: {result.Message}；{detail}";
                File.WriteAllText(Path.Combine(services.Settings.DataDirectory, "server-self-test.txt"), message);
                Console.WriteLine(message);
                Shutdown(connectivityReady ? 0 : 1);
            }
            catch (Exception ex)
            {
                var resultDirectory = DiagnosticDirectory();
                Directory.CreateDirectory(resultDirectory);
                File.WriteAllText(Path.Combine(resultDirectory, "server-self-test.txt"), $"SERVER_SELF_TEST_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create();
                var result = Task.Run(() => SelfTest.RunAsync(services)).GetAwaiter().GetResult();
                var resultPath = Path.Combine(services.Settings.DataDirectory, "self-test.txt");
                File.WriteAllText(resultPath, result.Message);
                Console.WriteLine(result.Message);
                Shutdown(result.Success ? 0 : 1);
            }
            catch (Exception ex)
            {
                var resultDirectory = DiagnosticDirectory();
                Directory.CreateDirectory(resultDirectory);
                File.WriteAllText(Path.Combine(resultDirectory, "self-test.txt"), $"SELF_TEST_FAILED: {ex.Message}");
                Console.WriteLine($"SELF_TEST_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        var previewMode = RuntimeMode.IsDetachedUi
                          || e.Args.Any(arg => string.Equals(arg, "--ui-preview", StringComparison.OrdinalIgnoreCase))
                          || AppContext.BaseDirectory.Contains("ui-preview", StringComparison.OrdinalIgnoreCase);
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: previewMode ? @"Local\CodexModelManager.Gui.Preview.v2" : @"Local\CodexModelManager.Gui.v2",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Codex 总管家已经打开了，请使用原来的窗口。",
                "Codex 总管家",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--ui-stress", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var configuredRuntimeRoot = Environment.GetEnvironmentVariable("CMM_RUNTIME_ROOT") ?? string.Empty;
            var stressAllowed = RuntimeMode.IsDetachedUi
                                && !RuntimeMode.AllowsExternalStatusConnections
                                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CMM_DETACHED_DATA_ROOT"))
                                && !string.IsNullOrWhiteSpace(configuredRuntimeRoot);
            var runtimeRoot = stressAllowed
                ? Path.GetFullPath(configuredRuntimeRoot)
                : Path.Combine(Path.GetTempPath(), "cmm-ui-stress-blocked");
            var reportPath = Path.Combine(runtimeRoot, "ui-stress-report.json");
            if (!stressAllowed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(
                    reportPath,
                    JsonSerializer.Serialize(new
                    {
                        marker = "DETACHED_UI_STRESS_BLOCKED",
                        reason = "必须同时启用独立界面、完全断外网和独立测试数据根目录。"
                    }));
                Shutdown(64);
                return;
            }

            var cycles = ResolveUiStressCycles(e.Args);
            var stressWindow = new MainWindow();
            MainWindow = stressWindow;
            stressWindow.Show();
            stressWindow.Hide();
            _ = Dispatcher.BeginInvoke(async () =>
            {
                var exitCode = 0;
                object report;
                try
                {
                    report = await stressWindow.RunDetachedUiStressAsync(cycles);
                }
                catch (Exception ex)
                {
                    exitCode = 1;
                    report = new
                    {
                        marker = "DETACHED_UI_STRESS_FAILED",
                        errorType = ex.GetType().Name,
                        error = ex.Message
                    };
                }

                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(
                    reportPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                stressWindow.Close();
                Shutdown(exitCode);
            });
            return;
        }

        if (!RuntimeMode.IsDetachedUi
            && !new CodexConfigService().IsManagedNativeProviderSelected())
        {
            MessageBox.Show(
                "没有找到完整、有效的总管家 Codex 连接标记。为了保护当前 Codex，已拒绝进入连接模式。",
                "Codex 总管家 - 保持断开",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(64);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static bool IsSandboxedLaunch()
    {
        // 完整沙盒必须同时隔离 Codex 配置与运行数据；
        // 只设 APPDATA 而 CODEX_HOME 未设时仍会读写真实 ~/.codex，不算沙盒。
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CMM_SANDBOX_CODEX_HOME"))
               && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CMM_SANDBOX_APPDATA"));
    }

    private static int ResolveUiStressCycles(IReadOnlyList<string> args)
    {
        var index = args
            .Select((value, position) => (value, position))
            .First(item => string.Equals(item.value, "--ui-stress", StringComparison.OrdinalIgnoreCase))
            .position;
        return index + 1 < args.Count
               && int.TryParse(args[index + 1], out var cycles)
               && cycles is >= 1 and <= 20_000
            ? cycles
            : 2_000;
    }

    private static int ResolveCodexTestDoubleCycles(IReadOnlyList<string> args)
    {
        var index = args
            .Select((value, position) => (value, position))
            .First(item => string.Equals(
                item.value,
                "--codex-test-double-self-test",
                StringComparison.OrdinalIgnoreCase))
            .position;
        return index + 1 < args.Count
               && int.TryParse(args[index + 1], out var cycles)
               && cycles is >= 1 and <= 50_000
            ? cycles
            : 5_000;
    }

    private static string? ResolveMcpSelfTestDataDirectory(IReadOnlyList<string> args)
    {
        var index = args
            .Select((value, position) => (value, position))
            .FirstOrDefault(item => string.Equals(
                item.value,
                "--self-test-data-directory",
                StringComparison.OrdinalIgnoreCase))
            .position;
        if (index <= 0 && (args.Count == 0 || !string.Equals(
                args[0],
                "--self-test-data-directory",
                StringComparison.OrdinalIgnoreCase)))
            return null;
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException("--self-test-data-directory 缺少目录路径。");

        var candidate = Path.GetFullPath(args[index + 1]);
        var tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var candidatePrefix = candidate
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        if (candidatePrefix.Equals(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !candidatePrefix.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(candidate))
            throw new InvalidOperationException("MCP 自检目录必须是已经存在的系统临时目录子目录。");
        return candidate;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var restart = _restartAfterCodexConnectionChange;
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
        if (restart && !TryStartCleanManagerProcess() && _restartExpectedConnected)
        {
            try { new CodexConfigService().RemoveManagedNativeProvider(createSnapshot: false); }
            catch { }
        }
    }

    private static bool TryStartCleanManagerProcess()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return false;

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            foreach (var name in new[]
                     {
                         "CMM_DETACHED_UI",
                         "CMM_DETACHED_NO_EXTERNAL_NETWORK",
                         "CMM_DETACHED_DATA_ROOT",
                         "CMM_SANDBOX_CODEX_HOME",
                         "CMM_SANDBOX_APPDATA",
                         "CMM_SANDBOX_OPENCODEX_HOME",
                         "CMM_SANDBOX_DREAMSKIN",
                         "CMM_SANDBOX_OCX_URL",
                         "CMM_RUNTIME_ROOT",
                         "CMM_NATIVE_ADMISSION_TOKEN"
                     })
                start.Environment.Remove(name);
            return Process.Start(start) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
        }
    }

    private static string DiagnosticDirectory(string? preferredRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(preferredRoot)
            ? AppSettingsService.ResolveDefaultDataDirectory()
            : Path.GetFullPath(preferredRoot);
        return Path.Combine(root, "diagnostics");
    }

    private static async Task<(bool Success, string Message)> RunGatewayHttpTestAsync(AppServices services)
    {
        var status = await services.UnifiedGateway.EnsureReadyAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var unauthorized = new HttpRequestMessage(HttpMethod.Get, services.UnifiedGateway.Url + "/models");
        unauthorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");
        using var unauthorizedResponse = await client.SendAsync(unauthorized);

        var key = services.UnifiedGateway.GetClientKey();
        using var catalog = new HttpRequestMessage(HttpMethod.Get, services.UnifiedGateway.Url + "/models");
        catalog.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var catalogResponse = await client.SendAsync(catalog);
        var catalogBody = await catalogResponse.Content.ReadAsStringAsync();
        using var catalogJson = JsonDocument.Parse(catalogBody);
        var models = catalogJson.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var plusCode = await PostProbeAsync(client, services.UnifiedGateway.Url + "/responses", key, new
        {
            model = "codex-plus/gpt-5.6-sol",
            input = "Return exactly OK.",
            max_output_tokens = 16,
            stream = false
        });
        var unknownCode = await PostProbeAsync(client, services.UnifiedGateway.Url + "/responses", key, new
        {
            model = "codex-plus/this-model-must-not-exist",
            input = "This request must be rejected locally.",
            max_output_tokens = 1,
            stream = false
        });

        var success = unauthorizedResponse.StatusCode == HttpStatusCode.Unauthorized
                      && catalogResponse.IsSuccessStatusCode
                      && models.Contains("codex-plus/gpt-5.6-sol")
                      && plusCode is >= 200 and < 300
                      && unknownCode == 404;
        var message = $"GATEWAY_HTTP_TEST_{(success ? "OK" : "FAILED")}: wrongKey={(int)unauthorizedResponse.StatusCode}; " +
                      $"catalog={(int)catalogResponse.StatusCode}/{models.Count}; plusSol={plusCode}; unknownModel={unknownCode}; " +
                      $"proAuthorized={models.Contains("codex-pro/gpt-5.6-sol")}";
        return (success, message);
    }

    private static async Task<int> PostProbeAsync(HttpClient client, string url, string key, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer);
        return (int)response.StatusCode;
    }
}
