using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodexModelManager.Services;

public sealed class AppSettingsService
{
    private readonly string _directory;
    private readonly string _path;
    private readonly object _saveGate = new();
    private AppSettings _settings;
    private string _loadedFingerprint;

    public string? LoadWarning { get; private set; }

    public AppSettingsService(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? ResolveDefaultDataDirectory());
        _path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        _settings = Load();
        _loadedFingerprint = LocalFileTransaction.Fingerprint(_path);
    }

    public static string ResolveDefaultDataDirectory()
    {
        var sandboxAppData = Environment.GetEnvironmentVariable("CMM_SANDBOX_APPDATA");
        if (!string.IsNullOrWhiteSpace(sandboxAppData))
            return sandboxAppData;
        var runtimeRoot = Environment.GetEnvironmentVariable("CMM_RUNTIME_ROOT");
        if (!string.IsNullOrWhiteSpace(runtimeRoot))
            return runtimeRoot;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTotalManager",
            "runtime-v3");
    }

    public string DataDirectory => _directory;

    public string V2rayPath
    {
        get => _settings.V2rayPath;
        set
        {
            EnsureWritable();
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("请选择 v2rayN.exe。", nameof(value));
            var fullPath = Path.GetFullPath(value);
            if (!File.Exists(fullPath) || !Path.GetFileName(fullPath).Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("选择的不是实际存在的 v2rayN.exe。", fullPath);
            _settings.V2rayPath = fullPath;
            Save();
        }
    }

    public string V2rayProxyUrl
    {
        get => ResolveV2rayProxyUri().AbsoluteUri.TrimEnd('/');
        set
        {
            EnsureWritable();
            var uri = ValidateV2rayProxyUri(value);
            if (uri.Port == NativeEnginePort || uri.Port == UnifiedGatewayPort)
                throw new InvalidOperationException("v2rayN 不能和总管家本机引擎或统一网关共用端口。");
            _settings.V2rayProxyUrl = uri.AbsoluteUri.TrimEnd('/');
            Save();
        }
    }

    public int V2rayProxyPort => ResolveV2rayProxyUri().Port;

    public int NativeEnginePort => ValidateLocalServicePort(_settings.NativeEnginePort, nameof(NativeEnginePort));

    public int UnifiedGatewayPort => ValidateLocalServicePort(_settings.UnifiedGatewayPort, nameof(UnifiedGatewayPort));

    public IReadOnlySet<int> ReservedLocalPorts => new HashSet<int>
    {
        NativeEnginePort,
        UnifiedGatewayPort,
        V2rayProxyPort
    };

    public void SetLocalServicePorts(int nativeEnginePort, int unifiedGatewayPort)
    {
        EnsureWritable();
        ValidateLocalServicePort(nativeEnginePort, nameof(nativeEnginePort));
        ValidateLocalServicePort(unifiedGatewayPort, nameof(unifiedGatewayPort));
        if (nativeEnginePort == unifiedGatewayPort || nativeEnginePort == V2rayProxyPort
            || unifiedGatewayPort == V2rayProxyPort)
            throw new InvalidOperationException("总管家本机引擎、统一网关和 v2rayN 必须使用不同端口。");
        _settings.NativeEnginePort = nativeEnginePort;
        _settings.UnifiedGatewayPort = unifiedGatewayPort;
        Save();
    }

    public void SetV2rayConfiguration(string executablePath, string proxyUrl)
    {
        EnsureWritable();
        var fullPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            fullPath = Path.GetFullPath(executablePath);
            if (!File.Exists(fullPath)
                || !Path.GetFileName(fullPath).Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("选择的文件不是真实存在的 v2rayN.exe。", fullPath);
        }
        var uri = ValidateV2rayProxyUri(proxyUrl);
        if (uri.Port == NativeEnginePort || uri.Port == UnifiedGatewayPort)
            throw new InvalidOperationException("v2rayN 不能和总管家本机引擎或统一网关共用端口。");
        _settings.V2rayPath = fullPath;
        _settings.V2rayProxyUrl = uri.AbsoluteUri.TrimEnd('/');
        Save();
    }

    public void SetLocalNetworkConfiguration(
        string executablePath,
        string proxyUrl,
        int nativeEnginePort,
        int unifiedGatewayPort)
    {
        EnsureWritable();
        ValidateLocalServicePort(nativeEnginePort, nameof(nativeEnginePort));
        ValidateLocalServicePort(unifiedGatewayPort, nameof(unifiedGatewayPort));
        var fullPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            fullPath = Path.GetFullPath(executablePath);
            if (!File.Exists(fullPath)
                || !Path.GetFileName(fullPath).Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("选择的文件不是真实存在的 v2rayN.exe。", fullPath);
        }
        var uri = ValidateV2rayProxyUri(proxyUrl);
        if (nativeEnginePort == unifiedGatewayPort
            || nativeEnginePort == uri.Port
            || unifiedGatewayPort == uri.Port)
            throw new InvalidOperationException("总管家本机引擎、统一网关和 v2rayN 必须使用三个不同端口。");
        _settings.V2rayPath = fullPath;
        _settings.V2rayProxyUrl = uri.AbsoluteUri.TrimEnd('/');
        _settings.NativeEnginePort = nativeEnginePort;
        _settings.UnifiedGatewayPort = unifiedGatewayPort;
        Save();
    }

    public int BackupRetentionCount => Math.Clamp(_settings.BackupRetentionCount, 1, 200);
    public int BackupRetentionDays => Math.Clamp(_settings.BackupRetentionDays, 1, 3650);
    public bool BackupAutoCleanup => _settings.BackupAutoCleanup;
    public bool MinimizeToTray => _settings.MinimizeToTray;
    public bool ProductSetupCompleted => _settings.ProductSetupCompleted;
    public bool ServerMonitoringEnabled => _settings.ServerMonitoringEnabled
                                            && !string.IsNullOrWhiteSpace(_settings.ServerSshConfigPath)
                                            && !string.IsNullOrWhiteSpace(_settings.ServerSshConfigSha256)
                                            && NormalizeServerAliases(_settings.ServerAliases).Count == 5;
    public string? ServerSshConfigPath => ServerMonitoringEnabled ? _settings.ServerSshConfigPath : null;
    public string? ServerSshConfigSha256 => ServerMonitoringEnabled ? _settings.ServerSshConfigSha256 : null;
    public IReadOnlyList<string> ServerAliases => ServerMonitoringEnabled
        ? NormalizeServerAliases(_settings.ServerAliases)
        : Array.Empty<string>();

    public void SetBackupRetention(int count, int days, bool autoCleanup)
    {
        if (count is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(count));
        if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days));
        EnsureWritable();
        _settings.BackupRetentionCount = count;
        _settings.BackupRetentionDays = days;
        _settings.BackupAutoCleanup = autoCleanup;
        Save();
    }

    public void SetProductShellPreferences(bool minimizeToTray, bool markSetupCompleted = false)
    {
        EnsureWritable();
        _settings.MinimizeToTray = minimizeToTray;
        if (markSetupCompleted) _settings.ProductSetupCompleted = true;
        Save();
    }

    public void SetServerMonitoringConfiguration(
        string? sshConfigPath,
        string? sha256,
        IReadOnlyList<string>? serverAliases)
    {
        EnsureWritable();
        if (string.IsNullOrWhiteSpace(sshConfigPath)
            && string.IsNullOrWhiteSpace(sha256)
            && (serverAliases is null || serverAliases.Count == 0))
        {
            _settings.ServerMonitoringEnabled = false;
            _settings.ServerSshConfigPath = null;
            _settings.ServerSshConfigSha256 = null;
            _settings.ServerAliases.Clear();
            Save();
            return;
        }
        if (string.IsNullOrWhiteSpace(sshConfigPath)
            || string.IsNullOrWhiteSpace(sha256)
            || serverAliases is null)
            throw new ArgumentException("服务器监控必须同时提供 SSH 配置、SHA-256 和五台服务器名称。");
        var normalizedHash = sha256.Trim().ToUpperInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("服务器监控的 SHA-256 必须是 64 位十六进制字符。", nameof(sha256));
        var fullPath = Path.GetFullPath(sshConfigPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("SSH 配置文件不存在。", fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        if (!actualHash.Equals(normalizedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SSH 配置文件已经变化，指纹不一致；没有启用服务器监控。");
        var aliases = ValidateServerAliases(fullPath, serverAliases);
        _settings.ServerMonitoringEnabled = true;
        _settings.ServerSshConfigPath = fullPath;
        _settings.ServerSshConfigSha256 = normalizedHash;
        _settings.ServerAliases = aliases.ToList();
        Save();
    }

    public static IReadOnlyList<string> ValidateServerAliases(
        string sshConfigPath,
        IReadOnlyList<string> serverAliases)
    {
        var aliases = NormalizeServerAliases(serverAliases);
        if (aliases.Count != 5)
            throw new ArgumentException("必须正好填写五个不重复的服务器名称，一行一个。", nameof(serverAliases));
        if (aliases.Any(alias => !Regex.IsMatch(alias, "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("服务器名称只能包含英文字母、数字、点、下划线和短横线。", nameof(serverAliases));
        var configured = DashboardStatusService.DiscoverSshHostAliases(sshConfigPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = aliases.Where(alias => !configured.Contains(alias)).ToArray();
        if (missing.Length > 0)
            throw new ArgumentException($"SSH 配置里找不到这些服务器名称：{string.Join("、", missing)}", nameof(serverAliases));
        return aliases;
    }

    private static IReadOnlyList<string> NormalizeServerAliases(IEnumerable<string>? aliases) =>
        (aliases ?? Array.Empty<string>())
        .Select(alias => alias?.Trim() ?? string.Empty)
        .Where(alias => alias.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string GetProviderName(string provider)
    {
        if (provider == "openai") return "OpenAI 官方";
        if (_settings.ProviderNames.TryGetValue(provider, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;
        return provider;
    }

    public void SetProviderName(string provider, string displayName)
    {
        EnsureWritable();
        _settings.ProviderNames[provider] = displayName.Trim();
        Save();
    }

    public bool TryGetProviderName(string provider, out string displayName) =>
        _settings.ProviderNames.TryGetValue(provider, out displayName!);

    public void RemoveProviderName(string provider)
    {
        EnsureWritable();
        if (_settings.ProviderNames.Remove(provider)) Save();
    }

    private Uri ResolveV2rayProxyUri() => ValidateV2rayProxyUri(_settings.V2rayProxyUrl);

    private static Uri ValidateV2rayProxyUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsLoopback
            || uri.Port is < 1 or > 65535
            || uri.Scheme is not ("socks5" or "socks5h" or "http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath is not ("" or "/")
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("v2rayN 代理地址必须是无账号密码的本机 socks5/http 地址，例如 socks5://127.0.0.1:10808。");
        return uri;
    }

    private static int ValidateLocalServicePort(int port, string name)
    {
        if (!LocalPortPolicy.IsUserPort(port))
            throw new InvalidOperationException($"{name} 必须在 1024 到 65535 之间。");
        return port;
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path)) return AppSettings.Default();
        try
        {
            var json = File.ReadAllText(_path);
            using (var document = JsonDocument.Parse(json))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("设置文件根节点必须是对象。");
                var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!rootNames.Add(property.Name))
                        throw new JsonException("设置文件包含大小写重复的字段。");
                }
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                         ?? throw new JsonException("设置文件为空。");
            if (loaded.ProviderNames is null || loaded.ServerAliases is null
                || loaded.V2rayPath is null || string.IsNullOrWhiteSpace(loaded.V2rayProxyUrl))
                throw new JsonException("设置文件包含空字段。");

            var providerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in loaded.ProviderNames)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)
                    || !providerNames.TryAdd(pair.Key, pair.Value))
                    throw new JsonException("模型来源名称表包含空值或大小写重复的编号。");
            }
            loaded.ProviderNames = providerNames;
            return loaded;
        }
        catch
        {
            PreserveCorruptFile();
            LoadWarning = "软件设置文件已损坏。软件已经保留副本，并停止写入，防止覆盖原设置。";
            return AppSettings.Default();
        }
    }

    private void Save()
    {
        lock (_saveGate)
        {
            EnsureWritable();
            using var fileLock = LocalFileTransaction.Acquire(_path);
            var currentFingerprint = LocalFileTransaction.Fingerprint(_path);
            if (!string.Equals(currentFingerprint, _loadedFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "设置文件已被另一个总管家进程修改；已拒绝覆盖，请重新打开设置后再保存。");
            LocalFileTransaction.WriteAtomic(_path, JsonSerializer.Serialize(_settings, JsonOptions));
            _loadedFingerprint = LocalFileTransaction.Fingerprint(_path);
        }
    }

    private void EnsureWritable()
    {
        if (LoadWarning is not null)
            throw new InvalidOperationException(LoadWarning);
    }

    private void PreserveCorruptFile()
    {
        try
        {
            var backup = Path.Combine(
                _directory,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(_path, backup, false);
        }
        catch
        {
            // 原文件仍然保留；这里绝不能为了做副本而覆盖它。
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class AppSettings
    {
        public Dictionary<string, string> ProviderNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string V2rayPath { get; set; } = string.Empty;
        public string V2rayProxyUrl { get; set; } = "socks5://127.0.0.1:10808";
        public int NativeEnginePort { get; set; } = LocalPortPolicy.DefaultNativeEnginePort;
        public int UnifiedGatewayPort { get; set; } = LocalPortPolicy.DefaultUnifiedGatewayPort;
        public int BackupRetentionCount { get; set; } = 20;
        public int BackupRetentionDays { get; set; } = 90;
        public bool BackupAutoCleanup { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool ProductSetupCompleted { get; set; }
        public bool ServerMonitoringEnabled { get; set; }
        public string? ServerSshConfigPath { get; set; }
        public string? ServerSshConfigSha256 { get; set; }
        public List<string> ServerAliases { get; set; } = new();

        public static AppSettings Default() => new()
        {
            // v2rayN is optional. Never guess a host-specific executable path:
            // a guessed path can accidentally control an unrelated installation
            // or carry one machine's layout into another machine's settings.
            V2rayPath = string.Empty,
            V2rayProxyUrl = "socks5://127.0.0.1:10808",
            NativeEnginePort = LocalPortPolicy.DefaultNativeEnginePort,
            UnifiedGatewayPort = LocalPortPolicy.DefaultUnifiedGatewayPort
        };
    }
}
