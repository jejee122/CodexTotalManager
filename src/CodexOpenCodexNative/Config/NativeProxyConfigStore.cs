using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexOpenCodexNative.Models;
using CodexOpenCodexNative.Providers;

namespace CodexOpenCodexNative.Config;

public sealed class NativeProxyConfigStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexTotalManager:NativeProxy:v1");
    private const string SecretPrefix = "dpapi:";
    private readonly string _path;

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
        if (!File.Exists(_path)) return new NativeProxyConfig();
        NativeProxyConfig config;
        try
        {
            config = JsonSerializer.Deserialize<NativeProxyConfig>(File.ReadAllText(_path))
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
        if (MigrateLegacyRouteNames(config)) Save(config);
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
        var copy = Clone(config);
        if (!string.IsNullOrWhiteSpace(copy.AdmissionToken) && !copy.AdmissionToken.StartsWith(SecretPrefix, StringComparison.Ordinal))
            copy.AdmissionToken = Encrypt(copy.AdmissionToken);
        foreach (var provider in copy.Providers)
        {
            if (!string.IsNullOrWhiteSpace(provider.ApiKey) && !provider.ApiKey.StartsWith(SecretPrefix, StringComparison.Ordinal))
                provider.ApiKey = Encrypt(provider.ApiKey);
        }
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temp, _path, true);
    }

    private static NativeProxyConfig Clone(NativeProxyConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        return JsonSerializer.Deserialize<NativeProxyConfig>(json) ?? new NativeProxyConfig();
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
