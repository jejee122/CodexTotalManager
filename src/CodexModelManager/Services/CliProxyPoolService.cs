using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class CliProxyPoolService
{
    public const string BundledVersion = "7.2.104";
    public const string BundledSha256 = "BD3456675B98CFF406B600D1361F1441879220CAD2DD4083B63409A09210629B";

    private readonly AppSettingsService _settings;
    private readonly SecretStore _secrets;
    private readonly Func<PoolDefinition, HttpClient>? _clientFactory;
    private readonly string? _binaryPath;
    private readonly PoolCatalogService? _poolCatalog;
    private readonly ConcurrentDictionary<string, Process> _ownedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _poolGates = new(StringComparer.OrdinalIgnoreCase);

    public CliProxyPoolService(
        AppSettingsService settings,
        SecretStore secrets,
        Func<PoolDefinition, HttpClient>? clientFactory = null,
        string? binaryPath = null,
        PoolCatalogService? poolCatalog = null)
    {
        _settings = settings;
        _secrets = secrets;
        _clientFactory = clientFactory;
        _poolCatalog = poolCatalog;
        if (!string.IsNullOrWhiteSpace(binaryPath))
        {
            if (!Path.IsPathFullyQualified(binaryPath))
                throw new ArgumentException("CLIProxyAPI 外部制品路径必须是绝对路径。", nameof(binaryPath));
            _binaryPath = Path.GetFullPath(binaryPath);
        }
    }

    public async Task<PoolBackendSnapshot> ReadAsync(
        PoolDefinition pool,
        bool ensureRunning = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidatePool(pool);
            if (ensureRunning && !await EnsureRunningAsync(pool, cancellationToken))
                throw new InvalidOperationException("CLIProxyAPI 没有启动成功。");
            if (_clientFactory is null
                && (pool.LocalPort is null || !await IsPortOpenAsync(pool.LocalPort.Value, cancellationToken)))
            {
                return new PoolBackendSnapshot(
                    false,
                    "未启动",
                    "点“添加账号”会安全启动本机 CLIProxyAPI 并打开官方授权。",
                    PoolCatalogService.BuildCliBaseUrl(pool),
                    Array.Empty<PoolAccountView>(),
                    Array.Empty<string>(),
                    DateTimeOffset.Now);
            }

            var roster = await ReadAccountsAsync(pool, cancellationToken);
            var accounts = roster.Accounts;
            IReadOnlyList<string> models;
            var modelDirectoryFailed = false;
            try { models = await ReadModelsAsync(pool, cancellationToken); }
            catch { models = Array.Empty<string>(); modelDirectoryFailed = true; }
            var enabledAccountCount = accounts.Count(item => item.Enabled);
            var accountLayoutValid = accounts.Count == 1 && enabledAccountCount == 1;
            var ready = accountLayoutValid && models.Count > 0;
            var statusTitle = accounts.Count switch
            {
                0 => "待授权",
                > 1 => "账号冲突，已锁定",
                _ when enabledAccountCount == 0 => "唯一账号已停用",
                _ when models.Count == 0 => "待模型验证",
                _ => "就绪"
            };
            var statusDetail = accounts.Count switch
            {
                0 => "这个独立出口还没有账号。每个出口只能放一个 OAuth 账号。",
                > 1 => $"这个出口发现 {accounts.Count} 个授权文件，已停止使用，避免串号。请把每个账号放到不同的独立出口。",
                _ when enabledAccountCount == 0 => "这个出口唯一的账号已停用，恢复后才能使用。",
                _ => $"本机独立进程已隔离运行；唯一账号可用，{models.Count} 个模型。"
                     + (modelDirectoryFailed ? " 模型目录读取失败，请稍后手动刷新。" : string.Empty)
            };
            return new PoolBackendSnapshot(
                ready,
                statusTitle,
                statusDetail,
                PoolCatalogService.BuildCliBaseUrl(pool),
                accounts,
                models,
                DateTimeOffset.Now)
            {
                AccountRosterCompleteness = roster.Completeness
            };
        }
        catch (Exception ex)
        {
            return new PoolBackendSnapshot(
                false,
                "需要处理",
                Friendly(ex),
                PoolCatalogService.BuildCliBaseUrl(pool),
                Array.Empty<PoolAccountView>(),
                Array.Empty<string>(),
                DateTimeOffset.Now)
            {
                AccountRosterCompleteness = AccountRosterCompleteness.ReadFailed
            };
        }
    }

    public async Task<bool> EnsureRunningAsync(PoolDefinition pool, CancellationToken cancellationToken = default)
    {
        ValidatePool(pool);
        var gate = _poolGates.GetOrAdd(pool.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureRunningCoreAsync(pool, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> EnsureRunningCoreAsync(PoolDefinition pool, CancellationToken cancellationToken)
    {
        var binary = GetBinaryPath();
        VerifyBinary(binary);
        var instanceDirectory = GetInstanceDirectory(pool);
        var authDirectory = Path.Combine(instanceDirectory, "auth");
        Directory.CreateDirectory(authDirectory);
        RestrictDirectoryToCurrentUser(instanceDirectory);
        var configPath = Path.Combine(instanceDirectory, "config.yaml");
        var desiredConfig = BuildConfig(pool, authDirectory);

        if (await IsPortOpenAsync(pool.LocalPort!.Value, cancellationToken))
        {
            var recorded = TryGetRecordedProcess(pool, binary, configPath, desiredConfig);
            if (recorded is not null)
            {
                if (recorded.ConfigurationMatches
                    && await CanReadManagementAsync(pool, cancellationToken))
                {
                    if (_ownedProcesses.TryGetValue(pool.Id, out var existingHandle)
                        && existingHandle.Id == recorded.Process.Id)
                    {
                        recorded.Process.Dispose();
                    }
                    else
                    {
                        _ownedProcesses[pool.Id] = recorded.Process;
                    }
                    return true;
                }
                var recordedPid = recorded.Process.Id;
                await StopProcessAsync(recorded.Process);
                DeleteInstanceRecord(pool, recordedPid);
            }
            else
            {
                if (_poolCatalog is null)
                    throw new InvalidOperationException($"Local port {pool.LocalPort} is occupied by another process.");
                _poolCatalog.ReassignCliPort(pool);
                ValidatePool(pool);
                desiredConfig = BuildConfig(pool, authDirectory);
                configPath = Path.Combine(instanceDirectory, "config.yaml");
            }
        }

        if (_ownedProcesses.TryRemove(pool.Id, out var staleProcess))
        {
            var stalePid = staleProcess.Id;
            await StopProcessAsync(staleProcess);
            DeleteInstanceRecord(pool, stalePid);
        }

        await File.WriteAllTextAsync(configPath, desiredConfig, new UTF8Encoding(false), cancellationToken);

        var start = new ProcessStartInfo
        {
            FileName = binary,
            WorkingDirectory = instanceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-config");
        start.ArgumentList.Add(configPath);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 CLIProxyAPI。");
        var ready = false;
        try
        {
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            _ownedProcesses[pool.Id] = process;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"CLIProxyAPI 启动后立即退出（代码 {process.ExitCode}）。");
                if (await IsPortOpenAsync(pool.LocalPort.Value, cancellationToken)
                    && await CanReadManagementAsync(pool, cancellationToken))
                {
                    WriteInstanceRecord(pool, process, binary, configPath);
                    ready = true;
                    return true;
                }
                await Task.Delay(300, cancellationToken);
            }
            return false;
        }
        finally
        {
            if (!ready)
            {
                if (_ownedProcesses.TryGetValue(pool.Id, out var current)
                    && ReferenceEquals(current, process))
                    _ownedProcesses.TryRemove(pool.Id, out _);
                var failedPid = process.Id;
                await StopProcessAsync(process);
                DeleteInstanceRecord(pool, failedPid);
            }
        }
    }

    public async Task StopOwnedAsync(string poolId)
    {
        var gate = _poolGates.GetOrAdd(poolId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_ownedProcesses.TryRemove(poolId, out var process))
            {
                var pid = process.Id;
                await StopProcessAsync(process);
                var pool = _poolCatalog?.Find(poolId);
                if (pool is not null) DeleteInstanceRecord(pool, pid);
            }
            else
            {
                var pool = _poolCatalog?.Find(poolId);
                if (pool is not null)
                {
                    var binary = GetBinaryPath();
                    var instanceDirectory = GetInstanceDirectory(pool);
                    var configPath = Path.Combine(instanceDirectory, "config.yaml");
                    var authDirectory = Path.Combine(instanceDirectory, "auth");
                    var recorded = TryGetRecordedProcess(pool, binary, configPath, BuildConfig(pool, authDirectory));
                    if (recorded is not null)
                    {
                        var pid = recorded.Process.Id;
                        await StopProcessAsync(recorded.Process);
                        DeleteInstanceRecord(pool, pid);
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch { }
        finally
        {
            try { process.CancelOutputRead(); } catch { }
            try { process.CancelErrorRead(); } catch { }
            process.Dispose();
        }
    }

    public async Task<PoolOAuthStartResult> StartCodexOAuthAsync(
        PoolDefinition pool,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureRunningAsync(pool, cancellationToken))
            throw new InvalidOperationException("CLIProxyAPI 没有准备好。");
        var existing = await ReadAccountsAsync(pool, cancellationToken);
        if (existing.Accounts.Count > 0)
            throw new InvalidOperationException(
                $"这个独立出口已经有 {existing.Accounts.Count} 个授权账号，不能继续添加。每个出口只能放一个账号；要加另一个账号，请新建独立出口。");
        using var client = CreateManagementClient(pool);
        using var response = await client.GetAsync("/v0/management/codex-auth-url?is_webui=true", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var url = ReadString(json.RootElement, "url")
                  ?? ReadString(json.RootElement, "auth_url")
                  ?? throw new InvalidOperationException("未读到官方授权地址。");
        var state = ReadString(json.RootElement, "state")
                    ?? throw new InvalidOperationException("未读到授权会话标识。");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("授权地址不是可信的 HTTPS 链接。");
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        return new PoolOAuthStartResult(url, state);
    }

    public async Task WaitForOAuthAsync(
        PoolDefinition pool,
        string state,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        using var client = CreateManagementClient(pool);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.GetAsync(
                $"/v0/management/get-auth-status?state={Uri.EscapeDataString(state)}",
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var status = ReadString(json.RootElement, "status") ?? string.Empty;
                if (status.Equals("ok", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("success", StringComparison.OrdinalIgnoreCase)) return;
                if (status.Equals("error", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(ReadString(json.RootElement, "error") ?? "官方授权失败。");
            }
            await Task.Delay(1000, cancellationToken);
        }
        throw new TimeoutException("等待官方授权超时，可以重新点“添加账号”。");
    }

    public async Task SetAccountEnabledAsync(
        PoolDefinition pool,
        string accountId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateManagementClient(pool);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/v0/management/auth-files/status")
        {
            Content = JsonContent.Create(new { name = accountId, disabled = !enabled })
        };
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ReadModelsAsync(
        PoolDefinition pool,
        CancellationToken cancellationToken = default)
    {
        using var client = _clientFactory is null
            ? new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{pool.LocalPort}"),
                Timeout = TimeSpan.FromSeconds(5)
            }
            : _clientFactory(pool);
        if (_clientFactory is null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RequireClientKey(pool));
        using var response = await client.GetAsync("/v1/models", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var array = json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data
            : json.RootElement;
        if (array.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return array.EnumerateArray()
            .Select(item => ReadString(item, "id"))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ParsedAccountRoster> ReadAccountsAsync(
        PoolDefinition pool,
        CancellationToken cancellationToken)
    {
        using var client = CreateManagementClient(pool);
        using var response = await client.GetAsync("/v0/management/auth-files", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseAccounts(pool, json.RootElement);
    }

    private static ParsedAccountRoster ParseAccounts(PoolDefinition pool, JsonElement root)
    {
        var array = FindArray(root, "files", "auth_files", "data");
        if (array.ValueKind != JsonValueKind.Array)
            return new ParsedAccountRoster(Array.Empty<PoolAccountView>(), AccountRosterCompleteness.Partial);
        var result = new List<PoolAccountView>();
        var stableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        foreach (var item in array.EnumerateArray())
        {
            var provider = ReadString(item, "provider") ?? ReadString(item, "type") ?? string.Empty;
            if (provider.Length > 0 && !provider.Contains("codex", StringComparison.OrdinalIgnoreCase)
                                    && !provider.Contains("openai", StringComparison.OrdinalIgnoreCase)) continue;
            var name = ReadString(item, "name") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(name))
            {
                complete = false;
                continue;
            }
            if (!stableIds.Add(name))
            {
                complete = false;
                continue;
            }
            var disabled = ReadBool(item, "disabled");
            var unavailable = ReadBool(item, "unavailable");
            var email = ReadString(item, "email") ?? "邮箱由 OAuth 文件管理";
            var accountType = ReadString(item, "account_type") ?? ReadString(item, "plan") ?? "Codex OAuth";
            var status = disabled ? "已停用" : unavailable ? "暂不可用" : "可用";
            result.Add(new PoolAccountView
            {
                PoolId = pool.Id,
                RuntimeProviderId = pool.ProviderId,
                RuntimeProviderIdentitySource = string.IsNullOrWhiteSpace(pool.ProviderId)
                    ? RuntimeProviderIdentitySource.Unknown
                    : RuntimeProviderIdentitySource.PoolDefinitionProviderId,
                Id = name,
                Label = email,
                Detail = accountType,
                Status = status,
                Enabled = !disabled,
                CanToggle = true
            });
        }
        return new ParsedAccountRoster(result, complete
            ? AccountRosterCompleteness.Complete
            : AccountRosterCompleteness.Partial);
    }

    private sealed record ParsedAccountRoster(
        IReadOnlyList<PoolAccountView> Accounts,
        AccountRosterCompleteness Completeness);

    private HttpClient CreateManagementClient(PoolDefinition pool)
    {
        ValidatePool(pool);
        if (_clientFactory is not null) return _clientFactory(pool);
        var managementKey = RequireManagementKey(pool);
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{pool.LocalPort}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementKey);
        return client;
    }

    private async Task<bool> CanReadManagementAsync(PoolDefinition pool, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateManagementClient(pool);
            using var response = await client.GetAsync("/v0/management/auth-files", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private string BuildConfig(PoolDefinition pool, string authDirectory)
    {
        var managementKey = RequireManagementKey(pool);
        var clientKey = RequireClientKey(pool);
        return $$"""
            host: "127.0.0.1"
            port: {{pool.LocalPort}}
            tls:
              enable: false
              cert: ""
              key: ""
            remote-management:
              allow-remote: false
              secret-key: "{{Yaml(managementKey)}}"
              disable-control-panel: true
            auth-dir: "{{Yaml(authDirectory.Replace('\\', '/'))}}"
            api-keys:
              - "{{Yaml(clientKey)}}"
            debug: false
            logging-to-file: false
            usage-statistics-enabled: true
            proxy-url: "{{Yaml(_settings.V2rayProxyUrl)}}"
            request-retry: 2
            max-retry-credentials: 3
            routing:
              strategy: "round-robin"
              session-affinity: true
              session-affinity-ttl: "24h"
            ws-auth: true
            """;
    }

    private string RequireManagementKey(PoolDefinition pool)
    {
        var name = $"cliproxy:{pool.Id}:management";
        var value = _secrets.ReadInternal(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        value = GenerateSecret();
        _secrets.SaveInternal(name, value);
        return value;
    }

    private string RequireClientKey(PoolDefinition pool)
    {
        ValidatePool(pool);
        var value = _secrets.Read(pool.ProviderId!);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        value = "cmm-" + GenerateSecret();
        _secrets.Save(pool.ProviderId!, value);
        return value;
    }

    private string GetBinaryPath()
    {
        var path = _binaryPath
                   ?? Path.Combine(AppContext.BaseDirectory, "Resources", "CLIProxyAPI", "cli-proxy-api.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException("没有找到经过哈希锁定的 CLIProxyAPI。它可能没有随安装包带入，或被安全软件隔离。", path);
        return path;
    }

    private static void VerifyBinary(string path)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(BundledSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CLIProxyAPI 文件校验失败，已拒绝启动。");
    }

    private string GetInstanceDirectory(PoolDefinition pool)
    {
        if (!PoolCatalogService.IsSafeCliPoolId(pool.Id))
            throw new InvalidOperationException("CLIProxyAPI 号池 ID 格式不安全。");
        var root = Path.GetFullPath(Path.Combine(_settings.DataDirectory, "cli-proxy", "pools"));
        var candidate = Path.GetFullPath(Path.Combine(root, pool.Id));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CLIProxyAPI 实例目录越出受控号池根目录。");
        return candidate;
    }

    private RecordedInstance? TryGetRecordedProcess(
        PoolDefinition pool,
        string binaryPath,
        string configPath,
        string desiredConfig)
    {
        try
        {
            var recordPath = GetInstanceRecordPath(pool);
            if (!File.Exists(recordPath) || !File.Exists(configPath)) return null;
            var record = JsonSerializer.Deserialize<CliProxyInstanceRecord>(
                File.ReadAllText(recordPath, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (record is null
                || !record.PoolId.Equals(pool.Id, StringComparison.Ordinal)
                || !record.ProviderId.Equals(pool.ProviderId, StringComparison.Ordinal)
                || record.Port != pool.LocalPort
                || record.Pid <= 0
                || !record.BinarySha256.Equals(BundledSha256, StringComparison.OrdinalIgnoreCase)
                || !record.ConfigSha256.Equals(HashFile(configPath), StringComparison.OrdinalIgnoreCase)
                || GetPidForPort(pool.LocalPort!.Value) != record.Pid)
                return null;

            var process = Process.GetProcessById(record.Pid);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != record.ProcessStartUtcTicks
                || !Path.GetFullPath(process.MainModule?.FileName ?? string.Empty)
                    .Equals(Path.GetFullPath(binaryPath), StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return null;
            }

            var desiredHash = HashText(desiredConfig);
            return new RecordedInstance(
                process,
                record.ConfigSha256.Equals(desiredHash, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private void WriteInstanceRecord(
        PoolDefinition pool,
        Process process,
        string binaryPath,
        string configPath)
    {
        var record = new CliProxyInstanceRecord
        {
            PoolId = pool.Id,
            ProviderId = pool.ProviderId!,
            Port = pool.LocalPort!.Value,
            Pid = process.Id,
            ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            BinarySha256 = HashFile(binaryPath),
            ConfigSha256 = HashFile(configPath),
            StartedAt = DateTimeOffset.UtcNow
        };
        var path = GetInstanceRecordPath(pool);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private void DeleteInstanceRecord(PoolDefinition pool, int expectedPid)
    {
        try
        {
            var path = GetInstanceRecordPath(pool);
            if (!File.Exists(path)) return;
            var record = JsonSerializer.Deserialize<CliProxyInstanceRecord>(File.ReadAllText(path, Encoding.UTF8));
            if (record?.Pid == expectedPid) File.Delete(path);
        }
        catch
        {
        }
    }

    private string GetInstanceRecordPath(PoolDefinition pool) =>
        Path.Combine(GetInstanceDirectory(pool), "instance.json");

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int? GetPidForPort(int port)
    {
        try
        {
            using var output = Process.Start(new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (output is null) return null;
            var lines = output.StandardOutput.ReadToEnd().Split('\n');
            output.WaitForExit(5000);
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

    private sealed record RecordedInstance(Process Process, bool ConfigurationMatches);

    private sealed class CliProxyInstanceRecord
    {
        public string PoolId { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public int Port { get; set; }
        public int Pid { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string BinarySha256 { get; set; } = string.Empty;
        public string ConfigSha256 { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
    }

    private static void RestrictDirectoryToCurrentUser(string directory)
    {
        try
        {
            var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
            Grant(directory, identity, directoryRule: true);
            foreach (var childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                Grant(childDirectory, identity, directoryRule: true);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                Grant(file, identity, directoryRule: false);
        }
        catch
        {
            // LocalAppData is already per-user. Failure to tighten ACLs must not delete or replace credentials.
        }

        static void Grant(string path, string identity, bool directoryRule)
        {
            static int Run(params string[] arguments)
            {
                var start = new ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start);
                if (process is null) return -1;
                process.WaitForExit(5000);
                return process.HasExited ? process.ExitCode : -1;
            }

            var permission = directoryRule ? $"{identity}:(OI)(CI)F" : $"{identity}:F";
            // Grant first so removing inherited entries can never lock out the current user.
            _ = Run(path, "/grant", permission, "/C");
            _ = Run(path, "/inheritance:r", "/C");
            if (Run(path, "/grant:r", permission, "/C") != 0)
                throw new UnauthorizedAccessException($"无法收紧凭据目录权限：{path}");
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(500);
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    private void ValidatePool(PoolDefinition pool)
    {
        if (pool.Transport != PoolTransport.CliProxyApi || pool.LocalPort is null)
            throw new InvalidOperationException("这不是本机 CLIProxyAPI 号池。");
        if (!PoolCatalogService.IsSafeCliPoolId(pool.Id))
            throw new InvalidOperationException("CLIProxyAPI 号池 ID 格式不安全。");
        if (!PoolCatalogService.IsExactCliEndpoint(pool))
            throw new InvalidOperationException("CLIProxyAPI 必须精确绑定 http://127.0.0.1:{LocalPort}/v1。");
        if (!PoolCatalogService.IsSafeCliPortBinding(pool, reservedPorts: _settings.ReservedLocalPorts))
            throw new InvalidOperationException($"CLIProxyAPI 必须使用 {LocalPortPolicy.CliProxyPortStart}-{LocalPortPolicy.CliProxyPortEnd} 内的独立端口，且不能与 Native Engine、统一网关或 v2rayN 共用。");
        if (!PoolCatalogService.IsSafeCliProviderBinding(pool))
            throw new InvalidOperationException("CLIProxyAPI 凭据槽必须精确绑定到当前号池，不能跨来源复用。");
    }

    private static JsonElement FindArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value;
        return default;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"CLIProxyAPI 返回 HTTP {(int)response.StatusCode}：{body}");
    }

    private static string GenerateSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Yaml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException => "无法连接本机 CLIProxyAPI。",
        TaskCanceledException => "读取 CLIProxyAPI 超时。",
        _ => ex.Message
    };
}
