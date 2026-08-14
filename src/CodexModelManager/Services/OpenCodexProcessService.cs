using System.Diagnostics;
using System.Net.Sockets;
using CodexOpenCodexNative.Config;

namespace CodexModelManager.Services;

public readonly record struct ManagedCodexConnectionState(bool WasConnected)
{
    public bool MayReadOrWriteCodexConfiguration => WasConnected;
}

public sealed class OpenCodexProcessService
{
    private readonly AppSettingsService _settings;
    private readonly SecretStore _secrets;
    private readonly OpenCodexClient _client;
    private readonly CodexConfigService _codexConfig;
    private readonly CodexModelCatalogService _codexModelCatalog;
    private readonly PoolCatalogService _poolCatalog;
    private readonly string _nativeEngineDataRoot;
    private int _ownedNativeEnginePid;

    public OpenCodexProcessService(
        AppSettingsService settings,
        SecretStore secrets,
        OpenCodexClient client,
        CodexConfigService codexConfig,
        CodexModelCatalogService codexModelCatalog,
        PoolCatalogService poolCatalog,
        string nativeEngineDataRoot)
    {
        _settings = settings;
        _secrets = secrets;
        _client = client;
        _codexConfig = codexConfig;
        _codexModelCatalog = codexModelCatalog;
        _poolCatalog = poolCatalog;
        _nativeEngineDataRoot = Path.GetFullPath(nativeEngineDataRoot);
    }

    public async Task<bool> EnsureOpenCodexAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureNativeEngineOnlyAsync(cancellationToken)) return false;
        var restored = await RestoreFixedEntryAsync(cancellationToken);
        if (!restored) await StopOwnedNativeEngineAsync(cancellationToken);
        return restored;
    }

    public async Task<bool> EnsureNativeEngineOnlyAsync(CancellationToken cancellationToken = default)
    {
        if (await IsRecordedNativeEngineHealthyAsync(cancellationToken)) return true;

        var result = await RunOcxAsync("ensure", cancellationToken);
        if (!result) return false;
        if (await WaitForHealthAsync(TimeSpan.FromSeconds(20), cancellationToken)) return true;
        await StopOwnedNativeEngineAsync(cancellationToken);
        return false;
    }

    public ManagedCodexConnectionState CaptureManagedCodexConnectionState()
        => new(_codexConfig.IsManagedNativeProviderSelected());

    public async Task<bool> EnsurePreservingConnectionStateAsync(
        ManagedCodexConnectionState originalState,
        CancellationToken cancellationToken = default)
    {
        var healthy = originalState.WasConnected
            ? await EnsureOpenCodexAsync(cancellationToken)
            : await EnsureNativeEngineOnlyAsync(cancellationToken);
        return healthy && ConnectionStateMatches(originalState);
    }

    public async Task<bool> RestartOpenCodexAsync(CancellationToken cancellationToken = default)
    {
        await RunOcxAsync("stop", cancellationToken);
        var result = await RunOcxAsync("ensure", cancellationToken);
        if (!result) return false;
        if (!await WaitForHealthAsync(TimeSpan.FromSeconds(20), cancellationToken))
        {
            await StopOwnedNativeEngineAsync(cancellationToken);
            return false;
        }
        var restored = await RestoreFixedEntryAsync(cancellationToken);
        if (!restored) await StopOwnedNativeEngineAsync(cancellationToken);
        return restored;
    }

    public async Task<bool> RestartNativeEngineOnlyAsync(CancellationToken cancellationToken = default)
    {
        await RunOcxAsync("stop", cancellationToken);
        return await EnsureNativeEngineOnlyAsync(cancellationToken);
    }

    public async Task<bool> RestartPreservingConnectionStateAsync(
        ManagedCodexConnectionState originalState,
        CancellationToken cancellationToken = default)
    {
        var healthy = originalState.WasConnected
            ? await RestartOpenCodexAsync(cancellationToken)
            : await RestartNativeEngineOnlyAsync(cancellationToken);
        return healthy && ConnectionStateMatches(originalState);
    }

    public Task<bool> StopOpenCodexAsync(CancellationToken cancellationToken = default) =>
        RunOcxAsync("stop", cancellationToken);

    public async Task StopOwnedNativeEngineAsync(CancellationToken cancellationToken = default)
    {
        var pid = Interlocked.Exchange(ref _ownedNativeEnginePid, 0);
        if (pid <= 0) return;
        var record = ReadNativeEnginePid();
        if (record is null
            || record.Value.Pid != pid
            || !IsProcessAlive(pid)
            || !IsNativeEngineProcess(pid)
            || !ProcessStartTimeMatches(pid, record.Value.StartTicks))
            return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteNativeEnginePid(pid);
        }
    }

    public async Task<bool> StartOpenCodexAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunOcxAsync("ensure", cancellationToken);
        if (!result) return false;
        if (!await WaitForHealthAsync(TimeSpan.FromSeconds(20), cancellationToken))
        {
            await StopOwnedNativeEngineAsync(cancellationToken);
            return false;
        }
        var restored = await RestoreFixedEntryAsync(cancellationToken);
        if (!restored) await StopOwnedNativeEngineAsync(cancellationToken);
        return restored;
    }

    public Task<bool> StartNativeEngineOnlyAsync(CancellationToken cancellationToken = default) =>
        EnsureNativeEngineOnlyAsync(cancellationToken);

    public async Task<bool> StartPreservingConnectionStateAsync(
        ManagedCodexConnectionState originalState,
        CancellationToken cancellationToken = default)
    {
        var healthy = originalState.WasConnected
            ? await StartOpenCodexAsync(cancellationToken)
            : await StartNativeEngineOnlyAsync(cancellationToken);
        return healthy && ConnectionStateMatches(originalState);
    }

    private bool ConnectionStateMatches(ManagedCodexConnectionState originalState) =>
        _codexConfig.IsManagedNativeProviderSelected() == originalState.WasConnected;

    private async Task<bool> RestoreFixedEntryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wasConnected = _codexConfig.IsManagedNativeProviderSelected();
        var configWritten = false;
        try
        {
            var models = await _client.GetModelsAsync(_settings, cancellationToken);
            var catalogCount = _codexModelCatalog.WriteCatalog(models);
            configWritten = _codexConfig.EnsureManagedNativeProvider(createSnapshot: false);
            _codexModelCatalog.VerifyCatalog(catalogCount);
            return _codexConfig.IsManagedNativeProviderSelected()
                   && await _client.IsReadyAsync(cancellationToken);
        }
        catch
        {
            // The connection block is committed last. On a first-time failure, remove
            // only the files and block created by this transaction; no backup file is needed.
            if (!wasConnected)
            {
                try { if (configWritten) _codexConfig.RemoveManagedNativeProvider(createSnapshot: false); }
                catch { }
                try { _codexModelCatalog.RemoveOwnedArtifacts(); } catch { }
            }
            return false;
        }
    }

    public IReadOnlyDictionary<string, string> GetCodexLaunchEnvironment()
    {
        var token = new NativeProxyConfigStore(_nativeEngineDataRoot).Load().AdmissionToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Native Engine Admission Token 不存在，拒绝启动 Codex 托管线路。");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CodexConfigService.NativeAdmissionEnvironmentVariable] = $"Bearer {token}"
        };
    }

    private Task<bool> RunOcxAsync(string command, CancellationToken cancellationToken)
    {
        // The source candidate owns one implementation: the in-process C# Native
        // Engine. A stale global npm\ocx.ps1 must not silently replace it.
        return RunNativeEngineAsync(command, cancellationToken);
    }

    private async Task<bool> RunNativeEngineAsync(string command, CancellationToken cancellationToken)
    {
        if (command == "stop")
        {
            var record = ReadNativeEnginePid();
            if (record is not null
                && IsProcessAlive(record.Value.Pid)
                && IsNativeEngineProcess(record.Value.Pid)
                && ProcessStartTimeMatches(record.Value.Pid, record.Value.StartTicks))
            {
                try
                {
                    using var killer = System.Diagnostics.Process.GetProcessById(record.Value.Pid);
                    killer.Kill(true);
                    await killer.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
            if (record is not null)
            {
                DeleteNativeEnginePid(record.Value.Pid);
                ClearOwnedNativeEnginePid(record.Value.Pid);
            }
            return !await IsPortOpenAsync(_settings.NativeEnginePort, cancellationToken);
        }

        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;
        if (await IsPortOpenAsync(_settings.NativeEnginePort, cancellationToken)) return false;
        var dataRoot = _nativeEngineDataRoot;
        Directory.CreateDirectory(dataRoot);
        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--native-engine");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(_settings.NativeEnginePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(dataRoot);
        foreach (var pair in _secrets.GetProviderProcessEnvironment()) start.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(start);
        if (process is null) return false;
        SaveNativeEnginePid(process.Id);
        Volatile.Write(ref _ownedNativeEnginePid, process.Id);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    DeleteNativeEnginePid(process.Id);
                    ClearOwnedNativeEnginePid(process.Id);
                    return false;
                }
                var status = await _client.GetRuntimeStatusAsync(cancellationToken);
                if (status.Healthy && status.Port == _settings.NativeEnginePort && status.ProcessId == process.Id)
                    return true;
                await Task.Delay(250, cancellationToken);
            }
            TryTerminate(process);
            DeleteNativeEnginePid(process.Id);
            ClearOwnedNativeEnginePid(process.Id);
            return false;
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            DeleteNativeEnginePid(process.Id);
            ClearOwnedNativeEnginePid(process.Id);
            throw;
        }
    }

    private string NativeEnginePidPath => Path.Combine(_nativeEngineDataRoot, "engine.pid");

    private void SaveNativeEnginePid(int pid)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NativeEnginePidPath)!);
            long startTicks = 0;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                startTicks = process.StartTime.Ticks;
            }
            catch
            {
            }
            File.WriteAllText(NativeEnginePidPath, $"{pid}:{startTicks}");
        }
        catch
        {
        }
    }

    private (int Pid, long StartTicks)? ReadNativeEnginePid()
    {
        try
        {
            if (!File.Exists(NativeEnginePidPath)) return null;
            var text = File.ReadAllText(NativeEnginePidPath).Trim();
            var parts = text.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var ticks))
                return null;
            return (pid, ticks);
        }
        catch
        {
            return null;
        }
    }

    private void DeleteNativeEnginePid(int expectedPid)
    {
        try
        {
            var record = ReadNativeEnginePid();
            if (record?.Pid == expectedPid && File.Exists(NativeEnginePidPath))
                File.Delete(NativeEnginePidPath);
        }
        catch
        {
        }
    }

    private void ClearOwnedNativeEnginePid(int expectedPid) =>
        Interlocked.CompareExchange(ref _ownedNativeEnginePid, 0, expectedPid);

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch
        {
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNativeEngineProcess(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.ProcessName.Contains("CodexModelManager", StringComparison.OrdinalIgnoreCase))
                return false;
            var exe = process.MainModule?.FileName;
            var current = Environment.ProcessPath;
            return !string.IsNullOrWhiteSpace(exe)
                   && !string.IsNullOrWhiteSpace(current)
                   && Path.GetFullPath(exe).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ProcessStartTimeMatches(int pid, long expectedStartTicks)
    {
        if (expectedStartTicks == 0) return true; // 旧格式（无时间戳）——保留兼容
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.Ticks == expectedStartTicks;
        }
        catch
        {
            return false;
        }
    }

    private static int? GetPidForPort(int port)
    {
        try
        {
            using var output = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (output is null) return null;
            var lines = output.StandardOutput.ReadToEnd().Split('\n');
            output.WaitForExit();
            foreach (var line in lines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5
                    && parts[1].EndsWith($":{port}", StringComparison.Ordinal)
                    && parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[4], out var pid))
                    return pid;
            }
        }
        catch
        {
        }
        return null;
    }

    private async Task<bool> WaitForHealthAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _client.IsHealthyAsync(cancellationToken)) return true;
            await Task.Delay(400, cancellationToken);
        }
        return false;
    }

    private async Task<bool> IsRecordedNativeEngineHealthyAsync(CancellationToken cancellationToken)
    {
        var record = ReadNativeEnginePid();
        if (record is null
            || !IsProcessAlive(record.Value.Pid)
            || !IsNativeEngineProcess(record.Value.Pid)
            || !ProcessStartTimeMatches(record.Value.Pid, record.Value.StartTicks))
            return false;
        var status = await _client.GetRuntimeStatusAsync(cancellationToken);
        if (!status.Healthy
            || status.ProcessId != record.Value.Pid
            || status.Port != _settings.NativeEnginePort)
            return false;
        Volatile.Write(ref _ownedNativeEnginePid, record.Value.Pid);
        return true;
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            if (await IsPortOpenAsync(port, cancellationToken)) return true;
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(500);
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
