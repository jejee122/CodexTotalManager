using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using CodexOpenCodexNative.Models;
using CodexOpenCodexNative.Providers;

namespace CodexOpenCodexNative.Config;

public sealed class NativeProxyConfigStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexTotalManager:NativeProxy:v1");
    private const string SecretPrefix = "dpapi:";
    private readonly string _path;
    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public NativeProxyConfigStore(string? dataRoot = null)
    {
        var root = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTotalManager", "runtime-v3", "native-proxy");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "config.json");
    }

    public string ConfigPath => _path;

    public NativeProxyConfig Load()
    {
        var gate = PathGates.GetOrAdd(Path.GetFullPath(_path), static _ => new object());
        lock (gate)
        {
            using var fileLock = AcquireCrossProcessLock();
            var config = LoadUnlocked();
            if (MigrateLegacyRouteNames(config)) SaveUnlocked(config, config.Revision);
            return config;
        }
    }

    private NativeProxyConfig LoadUnlocked()
    {
        if (!File.Exists(_path)) return new NativeProxyConfig();
        NativeProxyConfig config;
        try
        {
            config = JsonSerializer.Deserialize<NativeProxyConfig>(File.ReadAllText(_path), ReadOptions)
                     ?? throw new InvalidOperationException("原生代理配置为空。");
            config.Providers ??= new List<ProviderDefinition>();
            config.Combos ??= new List<ComboDefinition>();
            foreach (var provider in config.Providers) provider.Models ??= new List<string>();
            foreach (var combo in config.Combos) combo.Targets ??= new List<ComboTargetDefinition>();
        }
        catch (Exception ex)
        {
            PreserveCorrupt();
            throw new InvalidOperationException($"原生代理配置损坏，已拒绝启动：{ex.Message}", ex);
        }

        if (!string.IsNullOrWhiteSpace(config.AdmissionToken))
            config.AdmissionToken = DecryptIfNeeded(config.AdmissionToken);
        foreach (var provider in config.Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
                provider.ApiKey = DecryptIfNeeded(provider.ApiKey);
        }
        return config;
    }

    /// <summary>
    /// 升级存量明文配置：把明文 ApiKey/AdmissionToken 加密后写回。
    /// 返回是否发生了写回。
    /// </summary>
    public bool UpgradePlaintextSecrets(NativeProxyConfig config)
    {
        var hasPlaintext = (!string.IsNullOrWhiteSpace(config.AdmissionToken)
                            && !config.AdmissionToken.StartsWith(SecretPrefix, StringComparison.Ordinal))
                           || config.Providers.Any(provider =>
                               !string.IsNullOrWhiteSpace(provider.ApiKey)
                               && !provider.ApiKey.StartsWith(SecretPrefix, StringComparison.Ordinal));
        if (!hasPlaintext) return false;
        Save(config);
        return true;
    }

    public void Save(NativeProxyConfig config)
    {
        var gate = PathGates.GetOrAdd(Path.GetFullPath(_path), static _ => new object());
        lock (gate)
        {
            using var fileLock = AcquireCrossProcessLock();
            var currentRevision = File.Exists(_path) ? LoadUnlocked().Revision : 0;
            if (config.Revision != currentRevision)
                throw new InvalidOperationException(
                    $"原生代理配置已被另一个进程更新（当前版本 {currentRevision}，待保存版本 {config.Revision}）；已拒绝用旧副本覆盖。请重新读取后再试。");
            SaveUnlocked(config, currentRevision);
        }
    }

    public NativeProxyConfig Update(Action<NativeProxyConfig> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var gate = PathGates.GetOrAdd(Path.GetFullPath(_path), static _ => new object());
        lock (gate)
        {
            using var fileLock = AcquireCrossProcessLock();
            var config = LoadUnlocked();
            var expectedRevision = config.Revision;
            mutation(config);
            SaveUnlocked(config, expectedRevision);
            return Clone(config);
        }
    }

    private void SaveUnlocked(NativeProxyConfig config, long expectedRevision)
    {
        var copy = Clone(config);
        copy.Revision = checked(expectedRevision + 1);
        if (!string.IsNullOrWhiteSpace(copy.AdmissionToken) && !copy.AdmissionToken.StartsWith(SecretPrefix, StringComparison.Ordinal))
            copy.AdmissionToken = Encrypt(copy.AdmissionToken);
        foreach (var provider in copy.Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.ApiKey) && !provider.ApiKey.StartsWith(SecretPrefix, StringComparison.Ordinal))
                provider.ApiKey = Encrypt(provider.ApiKey);
        }
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
            config.Revision = copy.Revision;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { }
        }
    }

    private FileStream AcquireCrossProcessLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var lockPath = _path + ".lock";
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new IOException("等待原生代理配置跨进程写锁超时；没有覆盖现有配置。", ex);
            }
        }
    }

    private static NativeProxyConfig Clone(NativeProxyConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<NativeProxyConfig>(json, ReadOptions) ?? new NativeProxyConfig();
    }

    private static bool MigrateLegacyRouteNames(NativeProxyConfig config)
    {
        var changed = false;
        foreach (var combo in config.Combos)
        {
            if (InternalRouteNames.TryMigrateLegacyComboId(combo.Id, out var migratedId))
            {
                combo.Id = migratedId;
                changed = true;
            }
            if (InternalRouteNames.TryMigrateLegacyAlias(combo.Alias, out var migratedAlias))
            {
                combo.Alias = migratedAlias;
                changed = true;
            }
        }
        if (!changed) return false;

        var duplicateId = config.Combos
            .GroupBy(combo => combo.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        var duplicateAlias = config.Combos
            .GroupBy(combo => combo.Alias, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId is not null || duplicateAlias is not null)
            throw new InvalidOperationException(
                $"内部路由改名后发生冲突：ID={duplicateId ?? "无"}，别名={duplicateAlias ?? "无"}；已拒绝覆盖原配置。");
        return true;
    }

    private static string Encrypt(string clear)
    {
        var bytes = Encoding.UTF8.GetBytes(clear);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return SecretPrefix + Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string DecryptIfNeeded(string value)
    {
        if (!value.StartsWith(SecretPrefix, StringComparison.Ordinal)) return value;
        try
        {
            var encrypted = Convert.FromBase64String(value[SecretPrefix.Length..]);
            var clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(clear);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            throw new InvalidOperationException("原生代理凭据无法解密（DPAPI 用户不匹配或数据损坏），已拒绝启动。");
        }
    }

    private void PreserveCorrupt()
    {
        try
        {
            var backup = _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
            File.Copy(_path, backup, false);
        }
        catch
        {
        }
    }
}
