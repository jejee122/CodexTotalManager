using System.Windows;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CodexModelManager.Services;

namespace CodexModelManager;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private int _crashHandling;

    protected override void OnStartup(StartupEventArgs e)
    {
        EnsureWindowsDirectoryEnvironment();
        RuntimeMode.Initialize(e.Args);
        RegisterGlobalExceptionHandlers();

        if (RuntimeMode.ContainsBlockedServiceCommand(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MessageBox.Show(
                "这个安装是独立开发版，只允许打开界面。Native Engine、统一网关、自检和外部工人命令全部被锁定。",
                "AI 中转站总管家 - 独立开发版",
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

        if (e.Args.Any(arg => string.Equals(arg, "--gateway-key-create", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var labelIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--gateway-key-create", StringComparison.OrdinalIgnoreCase));
                var label = labelIndex >= 0 && labelIndex + 1 < e.Args.Length ? e.Args[labelIndex + 1] : string.Empty;
                var services = AppServices.Create();
                var key = Task.Run(() => services.UnifiedGateway.CreateGatewayClientKey(label)).GetAwaiter().GetResult();
                var message = $"GATEWAY_KEY_CREATED label={label} key={key}";
                Console.WriteLine(message);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GATEWAY_KEY_CREATE_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--gateway-key-list", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var services = AppServices.Create();
                var keys = Task.Run(() => services.UnifiedGateway.ReadGatewayClientKeys()).GetAwaiter().GetResult();
                var message = "GATEWAY_KEYS " + string.Join(
                    " | ", keys.Select(view => $"{view.Label}:{view.KeyHint}"));
                Console.WriteLine(message);
                File.WriteAllText(Path.Combine(services.Settings.DataDirectory, "gateway-key-list.txt"), message);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GATEWAY_KEY_LIST_FAILED: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--gateway-key-revoke", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var labelIndex = Array.FindIndex(e.Args, arg => string.Equals(arg, "--gateway-key-revoke", StringComparison.OrdinalIgnoreCase));
                var label = labelIndex >= 0 && labelIndex + 1 < e.Args.Length ? e.Args[labelIndex + 1] : string.Empty;
                var services = AppServices.Create();
                Task.Run(() => services.UnifiedGateway.RevokeGatewayClientKey(label)).GetAwaiter().GetResult();
                var message = $"GATEWAY_KEY_REVOKED label={label}";
                Console.WriteLine(message);
                File.WriteAllText(Path.Combine(services.Settings.DataDirectory, "gateway-key-revoke.txt"), message);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GATEWAY_KEY_REVOKE_FAILED: {ex.Message}");
                Shutdown(1);
            }
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
                // Legacy maintenance switch: it may prepare the Manager's loopback
                // engine, but only the in-app connection toggle may write Codex routing.
                var ok = Task.Run(() => services.Process.EnsureNativeEngineOnlyAsync()).GetAwaiter().GetResult();
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

                using var lifetime = new CancellationTokenSource();
                var interruptCount = 0;
                ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
                {
                    if (Interlocked.Increment(ref interruptCount) == 1)
                    {
                        eventArgs.Cancel = true;
                        lifetime.Cancel();
                        Console.WriteLine("NATIVE_ENGINE_STOPPING");
                    }
                };
                Console.CancelKeyPress += cancelHandler;
                var engine = new NativeEngineService();
                try
                {
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
                        Shutdown(3);
                        return;
                    }
                    Console.WriteLine($"NATIVE_ENGINE_READY: port={port}; dataRoot={engine.EngineDataRoot}");
                    lifetime.Token.WaitHandle.WaitOne();
                    Shutdown(0);
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                    if (StopNativeEngineForShutdown(engine))
                        Console.WriteLine("NATIVE_ENGINE_STOPPED");
                }
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
                          || e.Args.Any(arg => string.Equals(arg, "--ui-preview", StringComparison.OrdinalIgnoreCase));
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: previewMode ? @"Local\CodexModelManager.Gui.Preview.v2" : @"Local\CodexModelManager.Gui.v2",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AI 中转站总管家已经打开了，请使用原来的窗口。",
                "AI 中转站总管家",
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
        UnregisterGlobalExceptionHandlers();
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void UnregisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashReport("ui-thread", e.Exception, fatal: true);
        ShowFatalErrorMessage();

        // The UI may already be inconsistent. Log and stop cleanly instead of
        // pretending that an unknown exception is safe to ignore.
        e.Handled = true;
        Shutdown(1);
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
                        ?? new InvalidOperationException("进程遇到了未知的未捕获异常。");
        WriteCrashReport("process", exception, fatal: e.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashReport("background-task", e.Exception, fatal: false);
        e.SetObserved();
    }

    private void ShowFatalErrorMessage()
    {
        try
        {
            MessageBox.Show(
                "总管家遇到意外错误，已安全停止。错误记录已写入本地 diagnostics 文件夹，请重开软件；如果反复出现，请保留该记录用于排查。",
                "AI 中转站总管家遇到错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // The crash handler must never create a second failure.
        }
    }

    private void WriteCrashReport(string source, Exception exception, bool fatal)
    {
        if (Interlocked.Exchange(ref _crashHandling, 1) != 0) return;
        try
        {
            var directory = DiagnosticDirectory();
            Directory.CreateDirectory(directory);
            RotateCrashReports(directory, keepNewest: 19);
            var report = new
            {
                schemaVersion = 1,
                occurredAtUtc = DateTimeOffset.UtcNow,
                source,
                fatal,
                processId = Environment.ProcessId,
                exceptionType = exception.GetType().FullName,
                message = RedactCrashText(exception.Message),
                stackTrace = RedactCrashText(exception.StackTrace),
                innerExceptionType = exception.InnerException?.GetType().FullName,
                innerMessage = RedactCrashText(exception.InnerException?.Message)
            };
            var path = Path.Combine(directory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.json");
            LocalFileTransaction.WriteAtomic(
                path,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort only: a fatal exception may have damaged I/O state.
        }
        finally
        {
            Volatile.Write(ref _crashHandling, 0);
        }
    }

    internal static string? RedactCrashText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var redacted = Regex.Replace(
            value,
            @"(?i)(authorization)(\s*[:=]\s*)(bearer\s+)?([^\s,;\}\]]+)",
            "$1$2[REDACTED]",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        redacted = Regex.Replace(
            redacted,
            @"(?i)(cookie|api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|admission[-_ ]?token)(\s*[:=]\s*)([^\s,;\}\]]+)",
            "$1$2[REDACTED]",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        redacted = Regex.Replace(
            redacted,
            @"(?i)bearer\s+[A-Za-z0-9._~+/=-]+",
            "Bearer [REDACTED]",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return redacted.Length <= 16_384 ? redacted : redacted[..16_384] + "…[已截断]";
    }

    private static void RotateCrashReports(string directory, int keepNewest)
    {
        foreach (var stale in Directory.EnumerateFiles(directory, "crash-*.json")
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(keepNewest))
        {
            try { stale.Delete(); } catch { }
        }
    }

    internal static bool StopNativeEngineForShutdown(NativeEngineService engine) =>
        engine.Stop(TimeSpan.FromSeconds(10));

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
