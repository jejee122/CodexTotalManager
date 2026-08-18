using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class LocalServiceControlService
{
    private static readonly HashSet<string> ManagedV2rayExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "v2rayN.exe",
        "xray.exe",
        "v2ray.exe",
        "sing-box.exe",
        "hysteria.exe",
        "hysteria2.exe",
        "tuic-client.exe",
        "naive.exe",
        "mihomo.exe",
        "clash-meta.exe"
    };
    private readonly AppSettingsService _settings;
    private readonly OpenCodexClient _openCodex;
    private readonly DreamSkinService _dreamSkin;
    private readonly string _backupRoot;

    public LocalServiceControlService(
        AppSettingsService settings,
        OpenCodexClient openCodex,
        DreamSkinService dreamSkin,
        string? backupRoot = null)
    {
        _settings = settings;
        _openCodex = openCodex;
        _dreamSkin = dreamSkin;
        _backupRoot = Path.GetFullPath(backupRoot ?? Path.Combine(settings.DataDirectory, "backups"));
    }

    public async Task<IReadOnlyList<LocalServiceView>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        EnsureAttachedMode("读取完整本机服务状态");
        var runtimeTask = _openCodex.GetRuntimeStatusAsync(cancellationToken);
        var v2rayConfigured = TryGetConfiguredV2rayExecutable(out _);
        var v2rayPortTask = v2rayConfigured
            ? IsPortOpenAsync(_settings.V2rayProxyPort, cancellationToken)
            : Task.FromResult(false);
        var dreamTask = _dreamSkin.DiscoverAsync(cancellationToken);
        await Task.WhenAll(runtimeTask, v2rayPortTask, dreamTask);
        var runtime = runtimeTask.Result;
        var v2rayProcesses = v2rayConfigured ? FindManagedV2rayProcesses() : [];
        var v2rayReady = v2rayPortTask.Result;
        var dream = dreamTask.Result;
        return new[]
        {
            new LocalServiceView
            {
                Id = "native-engine",
            Name = "总管家本机引擎",
                Running = runtime.Healthy,
                Status = runtime.Healthy ? "运行正常" : "不可用",
                PortText = $"监听端口 {runtime.Port} · PID {runtime.ProcessId?.ToString() ?? "--"}",
                Detail = runtime.Healthy ? $"运行 {FormatUptime(runtime.Uptime)} · 内存 {FormatBytes(runtime.WorkingSetBytes)}" : "官方模型仍可通过 Codex 直连备用入口使用",
                LastError = ReadNativeEngineError(runtime.LastError),
                Capability = runtime.Healthy ? "已经可用" : "部分可用",
                Purpose = "负责把账号池和独立 API 接到 Codex。",
                PlainStatus = runtime.Healthy ? "模型中转正常" : "模型中转暂不可用",
                ImpactText = runtime.Healthy
                    ? "切换号池和外部模型可以正常工作。"
                    : "官方 Pro / Plus 直连仍可用，但外部模型和统一 API 会受影响。",
                StateColor = runtime.Healthy ? "#79DDBA" : "#F0B45D"
            },
            new LocalServiceView
            {
                Id = "v2rayn",
                Name = "v2rayN",
                Running = v2rayReady && v2rayProcesses.Count > 0,
                Status = !v2rayConfigured
                    ? "未配置"
                    : v2rayReady ? (v2rayProcesses.Count > 0 ? "运行正常" : "端口被其他进程占用") : "未连接",
                PortText = v2rayConfigured
                    ? $"本机代理 {_settings.V2rayProxyUrl} · 已验证进程 {v2rayProcesses.Count} 个"
                    : "还没有选择 v2rayN.exe",
                Detail = v2rayConfigured
                    ? "只控制安装目录内已核对路径的 v2rayN/Xray 进程"
                    : "v2rayN 是可选项；需要代理时再手动选择程序路径",
                LastError = ReadLatestV2rayLog(),
                Capability = v2rayReady && v2rayProcesses.Count > 0 ? "已经可用" : "不可用",
                Purpose = "负责需要代理的网络连接。",
                PlainStatus = !v2rayConfigured
                    ? "未配置（可选）"
                    : v2rayReady && v2rayProcesses.Count > 0 ? "网络通道正常" : "网络通道需要处理",
                ImpactText = v2rayReady && v2rayProcesses.Count > 0
                    ? "国外接口与依赖代理的程序可以联网。"
                    : "部分国外模型可能连接失败；国内服务不一定受影响。",
                StateColor = v2rayReady && v2rayProcesses.Count > 0 ? "#79DDBA" : "#F0B45D"
            },
            new LocalServiceView
            {
                Id = "dreamskin",
                Name = "Dream Skin",
                Running = dream.EngineReady && dream.LiveSessionConnected && !dream.IsPaused,
                Status = !dream.EngineReady ? "引擎不完整" : dream.IsPaused ? "已暂停 / 官方外观" : dream.LiveSessionConnected ? "实时通道已连接" : "已安装，等待连接",
                PortText = dream.LiveSessionConnected ? "安全换肤通道 9335 已验证" : "未检测到实时换肤通道",
                Detail = dream.StatusDetail,
                LastError = ReadLastLine(Path.Combine(dream.StateRoot, "injector-error.log")),
                Capability = dream.EngineReady ? (dream.LiveSessionConnected ? "已经可用" : "部分可用") : "不可用",
                Purpose = "只负责 Codex 界面皮肤，不影响模型、账号和聊天。",
                PlainStatus = !dream.EngineReady
                    ? "皮肤引擎不完整"
                    : dream.IsPaused ? "当前使用官方外观"
                    : dream.LiveSessionConnected ? "皮肤实时通道正常" : "皮肤已安装，等待连接",
                ImpactText = !dream.EngineReady
                    ? "只影响换肤；Codex 其余功能不受影响。"
                    : dream.IsPaused ? "选择任意皮肤即可恢复；上次使用的皮肤也能重新应用。"
                    : dream.LiveSessionConnected ? "可以直接切换皮肤，不需要退出 Codex。" : "首次恢复皮肤时可能需要你确认重启一次 Codex。",
                StateColor = dream.EngineReady && (dream.LiveSessionConnected || dream.IsPaused) ? "#79DDBA" : "#F0B45D"
            }
        };
    }

    public async Task<bool> StartV2rayAsync(CancellationToken cancellationToken = default)
    {
        EnsureAttachedMode("启动 v2rayN");
        var path = GetConfiguredV2rayExecutable("启动 v2rayN");
        if (await IsPortOpenAsync(_settings.V2rayProxyPort, cancellationToken) && FindManagedV2rayProcesses().Count > 0) return true;
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return await WaitForPortAsync(_settings.V2rayProxyPort, true, TimeSpan.FromSeconds(45), cancellationToken);
    }

    public async Task<bool> StopV2rayAsync(CancellationToken cancellationToken = default)
    {
        EnsureAttachedMode("停止 v2rayN");
        _ = GetConfiguredV2rayExecutable("停止 v2rayN");
        var processes = FindManagedV2rayProcesses();
        foreach (var process in processes)
        {
            using (process)
            {
                try { process.CloseMainWindow(); } catch { }
            }
        }
        await Task.Delay(1200, cancellationToken);
        foreach (var process in FindManagedV2rayProcesses())
        {
            using (process)
            {
                try
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch { return false; }
            }
        }
        return await WaitForPortAsync(_settings.V2rayProxyPort, false, TimeSpan.FromSeconds(12), cancellationToken);
    }

    public async Task<bool> RestartV2rayAsync(CancellationToken cancellationToken = default) =>
        await StopV2rayAsync(cancellationToken) && await StartV2rayAsync(cancellationToken);

    public async Task<string> CreateV2rayBackupAsync(CancellationToken cancellationToken = default)
    {
        EnsureAttachedMode("备份 v2rayN");
        var source = GetV2rayConfigDirectory();
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("找不到 v2rayN 的 guiConfigs 目录。");
        var wasRunning = FindManagedV2rayProcesses().Count > 0 || await IsPortOpenAsync(_settings.V2rayProxyPort, cancellationToken);
        if (wasRunning && !await StopV2rayAsync(cancellationToken))
            throw new InvalidOperationException("v2rayN 没有安全停止，因此没有复制数据库。");
        Directory.CreateDirectory(_backupRoot);
        var destination = Path.Combine(_backupRoot, $"v2ray-config-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        try
        {
            CopyDirectory(source, destination);
            VerifyDirectoryCopy(source, destination);
            return destination;
        }
        finally
        {
            if (wasRunning && !await StartV2rayAsync(cancellationToken))
                throw new InvalidOperationException($"备份已创建在 {destination}，但 v2rayN 没有重新启动。");
        }
    }

    public async Task RestoreV2rayBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        EnsureAttachedMode("恢复 v2rayN");
        var backup = Path.GetFullPath(backupPath);
        var allowed = _backupRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!backup.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(backup).StartsWith("v2ray-config-", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(backup))
            throw new InvalidOperationException("选择的不是总管家受控的 v2rayN 备份。");
        if (HasReparsePoint(backup)) throw new InvalidOperationException("备份目录包含重解析点，已拒绝恢复。");
        var current = GetV2rayConfigDirectory();
        var wasRunning = FindManagedV2rayProcesses().Count > 0 || await IsPortOpenAsync(_settings.V2rayProxyPort, cancellationToken);
        if (wasRunning && !await StopV2rayAsync(cancellationToken))
            throw new InvalidOperationException("v2rayN 没有安全停止，恢复已取消。");
        Directory.CreateDirectory(_backupRoot);
        var preRestore = Path.Combine(_backupRoot, $"v2ray-config-pre-restore-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        var failedCopy = Path.Combine(_backupRoot, $"v2ray-config-failed-restore-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        try
        {
            CopyDirectory(current, preRestore);
            var staging = current + $".manager-staging-{Guid.NewGuid():N}";
            CopyDirectory(backup, staging);
            VerifyDirectoryCopy(backup, staging);
            Directory.Move(current, failedCopy);
            Directory.Move(staging, current);
            if (wasRunning && !await StartV2rayAsync(cancellationToken))
                throw new InvalidOperationException("恢复后的 v2rayN 没有通过端口检查。");
        }
        catch
        {
            try
            {
                if (Directory.Exists(current)) Directory.Move(current, failedCopy + "-new");
                if (Directory.Exists(preRestore)) CopyDirectory(preRestore, current);
                if (wasRunning) await StartV2rayAsync(cancellationToken);
            }
            catch
            {
                // 原始快照仍保留在 preRestore，交由上层明确提示人工处理。
            }
            throw;
        }
    }

    private string GetV2rayConfigDirectory()
    {
        var executable = GetConfiguredV2rayExecutable("备份或恢复 v2rayN");
        return Path.Combine(Path.GetDirectoryName(executable)!, "guiConfigs");
    }

    private List<Process> FindManagedV2rayProcesses()
    {
        if (!TryGetConfiguredV2rayExecutable(out var executable)) return [];
        var root = Path.GetDirectoryName(executable)!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)
                    && IsManagedV2rayProcessPath(path, executable, root))
                    result.Add(process);
                else process.Dispose();
            }
            catch { process.Dispose(); }
        }
        return result;
    }

    public static bool IsManagedV2rayProcessPath(
        string processPath,
        string configuredExecutable,
        string installationRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(processPath);
            var configured = Path.GetFullPath(configuredExecutable);
            var root = Path.GetFullPath(installationRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.Equals(configured, StringComparison.OrdinalIgnoreCase)
                   || fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && ManagedV2rayExecutableNames.Contains(Path.GetFileName(fullPath));
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureAttachedMode(string action)
    {
        if (RuntimeMode.IsDetachedUi)
            throw new InvalidOperationException($"独立开发模式禁止{action}；真实本机服务保持不变。");
    }

    private string ReadLatestV2rayLog()
    {
        if (!TryGetConfiguredV2rayExecutable(out var executable)) return "v2rayN 尚未配置";
        if (!File.Exists(executable)) return "配置的 v2rayN.exe 已不存在，请重新选择";
        try
        {
            var root = Path.Combine(Path.GetDirectoryName(executable)!, "guiLogs");
            var latest = new DirectoryInfo(root).GetFiles().OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
            return latest is null ? "暂无错误记录" : ReadLastLine(latest.FullName);
        }
        catch { return "日志暂时读不到"; }
    }

    private bool TryGetConfiguredV2rayExecutable(out string executable)
    {
        executable = string.Empty;
        if (string.IsNullOrWhiteSpace(_settings.V2rayPath)) return false;
        try
        {
            var candidate = Path.GetFullPath(_settings.V2rayPath);
            if (!Path.GetFileName(candidate).Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase)) return false;
            executable = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetConfiguredV2rayExecutable(string action)
    {
        if (!TryGetConfiguredV2rayExecutable(out var executable))
            throw new InvalidOperationException($"还没有配置 v2rayN.exe，不能{action}。请先在本机设置里选择程序路径。");
        if (!File.Exists(executable))
            throw new FileNotFoundException("配置的 v2rayN.exe 已不存在，请重新选择程序路径。", executable);
        return executable;
    }

    private string ReadNativeEngineError(string? runtimeError)
    {
        if (!string.IsNullOrWhiteSpace(runtimeError)) return Scrub(runtimeError);
        foreach (var path in new[]
                 {
                     Path.Combine(_settings.DataDirectory, "native-proxy", "diagnostics", "native-engine.txt"),
                     Path.Combine(_settings.DataDirectory, "diagnostics", "native-engine.txt")
                 })
        {
            if (File.Exists(path)) return ReadLastLine(path);
        }
        return "暂无错误记录";
    }

    private static string ReadLastLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return "暂无错误记录";
            var line = File.ReadLines(path).Reverse().FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return string.IsNullOrWhiteSpace(line) ? "暂无错误记录" : Scrub(line);
        }
        catch { return "日志暂时读不到"; }
    }

    private static string Scrub(string text)
    {
        var result = Regex.Replace(text, @"https?://[^\s]+", "[地址已隐藏]", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?i)(api[_-]?key|token|authorization)\s*[:=]\s*\S+", "$1=[已隐藏]");
        return result.Length > 220 ? result[..220] + "…" : result;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(800);
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitForPortAsync(int port, bool desired, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            if (await IsPortOpenAsync(port, cancellationToken) == desired) return true;
            await Task.Delay(400, cancellationToken);
        }
        return false;
    }

    private static void CopyDirectory(string source, string destination)
    {
        var sourceFull = Path.GetFullPath(source);
        if (HasReparsePoint(sourceFull)) throw new InvalidOperationException("源目录包含重解析点，已拒绝复制。");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(sourceFull))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        foreach (var directory in Directory.EnumerateDirectories(sourceFull))
        {
            if (HasReparsePoint(directory)) throw new InvalidOperationException("子目录包含重解析点，已拒绝复制。");
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void VerifyDirectoryCopy(string source, string destination)
    {
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        var destinationFiles = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).ToArray();
        var sourceBytes = sourceFiles.Sum(file => new FileInfo(file).Length);
        var destinationBytes = destinationFiles.Sum(file => new FileInfo(file).Length);
        if (sourceFiles.Length != destinationFiles.Length || sourceBytes != destinationBytes)
            throw new InvalidOperationException("备份文件数量或大小校验不一致。");
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "未知";
        var value = bytes.Value / 1024d / 1024d;
        return value >= 1024 ? $"{value / 1024d:0.#} GB" : $"{value:0.#} MB";
    }

    private static string FormatUptime(TimeSpan? uptime) => uptime is null
        ? "时长未知"
        : uptime.Value.TotalHours >= 24
            ? $"{(int)uptime.Value.TotalDays}天 {uptime.Value.Hours}小时"
            : $"{(int)uptime.Value.TotalHours}小时 {uptime.Value.Minutes}分";
}
