using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CodexOpenCodexNative.Config;
using CodexOpenCodexNative.Host;
using CodexOpenCodexNative.Models;

namespace CodexModelManager.Services;

public enum NativeEngineState
{
    Stopped,
    Running,
    PortBusy,
    Failed
}

public sealed class NativeEngineService
{
    private readonly object _gate = new();
    private NativeProxyHost? _host;
    private Task? _runTask;
    private CancellationTokenSource? _shutdown;
    private NativeEngineState _state = NativeEngineState.Stopped;
    private string? _stateDetail;
    private string? _activeDataRoot;

    public string EngineDataRoot { get; set; } = Path.Combine(
        AppSettingsService.ResolveDefaultDataDirectory(), "native-proxy");

    /// <summary>沙盒模式下覆盖默认数据根目录（Start 显式参数优先于它）。</summary>
    public string? EngineDataRootOverride { get; set; }

    public int Port { get; private set; } = 10100;

    public NativeEngineState State
    {
        get
        {
            lock (_gate)
            {
                if (_state == NativeEngineState.Running && _runTask?.IsCompleted == true)
                {
                    _state = NativeEngineState.Failed;
                    _stateDetail = "引擎进程已退出";
                }
                return _state;
            }
        }
    }

    public string? StateDetail
    {
        get
        {
            lock (_gate)
            {
                return _stateDetail;
            }
        }
    }

    public Action<Microsoft.AspNetCore.Builder.WebApplication>? ConfigurePipeline { get; set; }

    public NativeProxyHost? Host
    {
        get
        {
            lock (_gate)
            {
                return _host;
            }
        }
    }

    public bool Start(int port = 10100, string? dataRootOverride = null)
    {
        lock (_gate)
        {
            if (_state == NativeEngineState.Running)
                return true;
            if (IsPortListening(port))
            {
                _state = NativeEngineState.PortBusy;
                _stateDetail = $"端口 {port} 已被其他进程占用（可能还是外部 OpenCodex）。请先停止它。";
                return false;
            }
            try
            {
                var dataRoot = dataRootOverride ?? EngineDataRoot;
                _activeDataRoot = dataRoot;
                Directory.CreateDirectory(dataRoot);
                var configPath = Path.Combine(dataRoot, "config.json");
                if (!File.Exists(configPath))
                {
                    new NativeProxyConfigStore(dataRoot).Save(new NativeProxyConfig
                    {
                        ListenPort = port,
                        DefaultProvider = "openai",
                        AdmissionToken = GenerateAdmissionToken()
                    });
                }

                var store = new NativeProxyConfigStore(dataRoot);
                var config = store.Load();
                config.ListenPort = port;
                if (string.IsNullOrWhiteSpace(config.AdmissionToken))
                {
                    config.AdmissionToken = GenerateAdmissionToken();
                    store.UpgradePlaintextSecrets(config);
                }
                store.UpgradePlaintextSecrets(config);

                var host = new NativeProxyHost(store, admissionTokenOverride: null, dataRootOverride: dataRoot);
                ConfigurePipeline?.Invoke(host.Application);
                _host = host;
                _shutdown = new CancellationTokenSource();
                _runTask = Task.Run(() => host.RunAsync(_shutdown.Token));
                Port = port;
                _state = NativeEngineState.Running;
                _stateDetail = $"内置原生引擎运行中（端口 {port}，数据目录 {dataRoot}）";
                return true;
            }
            catch (Exception ex)
            {
                _state = NativeEngineState.Failed;
                _stateDetail = $"引擎启动失败：{ex.Message}";
                return false;
            }
        }
    }

    public bool Stop(TimeSpan? timeout = null)
    {
        lock (_gate)
        {
            if (_state != NativeEngineState.Running || _host is null)
                return false;
            try
            {
                _shutdown?.Cancel();
                try
                {
                    _host.StopAsync().Wait(timeout ?? TimeSpan.FromSeconds(10));
                }
                catch
                {
                }
                _runTask?.Wait(timeout ?? TimeSpan.FromSeconds(10));
            }
            catch
            {
            }
            finally
            {
                _host = null;
                _runTask = null;
                _shutdown = null;
                _state = NativeEngineState.Stopped;
                _stateDetail = "内置原生引擎已停止";
            }
            return true;
        }
    }

    private static string GenerateAdmissionToken() =>
        "cmm-eng-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public async Task<bool> IsHealthyAsync(int timeoutSeconds = 3)
    {
        var host = Host;
        if (host is null) return false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/healthz");
            var token = ReadAdmissionToken();
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.TryAddWithoutValidation("X-CMM-Admission", $"Bearer {token}");
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string? ReadAdmissionToken()
    {
        try
        {
            var store = new NativeProxyConfigStore(_activeDataRoot);
            var config = store.Load();
            return config.AdmissionToken;
        }
        catch
        {
            return null;
        }
    }

    public string? Diagnose()
    {
        lock (_gate)
        {
            if (_runTask is null) return null;
            if (_runTask.IsFaulted)
                return _runTask.Exception?.GetBaseException().ToString() ?? "引擎任务异常终止";
            if (!_runTask.IsCompleted) return "引擎任务仍在启动中";
            return "引擎任务已正常结束";
        }
    }

    public static bool IsPortListening(int port)
    {
        try
        {
            var connection = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .FirstOrDefault(listener => listener.Port == port);
            return connection is not null;
        }
        catch
        {
            return false;
        }
    }

    public string Describe()
    {
        var state = State;
        var detail = StateDetail ?? string.Empty;
        return $"{state}: {detail}";
    }
}
