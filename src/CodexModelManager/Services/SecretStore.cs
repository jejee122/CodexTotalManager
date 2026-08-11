using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexModelManager.Services;

public sealed class SecretStore
{
    private const string InternalPrefix = "internal:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexModelManager:v1");
    private readonly string _path;
    private readonly string _directory;

    public string? LoadWarning { get; private set; }

    public SecretStore(string? directory = null)
    {
        _directory = directory ?? AppSettingsService.ResolveDefaultDataDirectory();
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "secrets.json");
        _ = LoadEncrypted();
    }

    public string GetEnvironmentName(string provider)
    {
        EnsureExternalProvider(provider);
        var safe = new string(provider.ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        return $"CMM_{safe}_API_KEY";
    }

    public void Save(string provider, string secret)
    {
        EnsureExternalProvider(provider);
        SaveCore(provider, secret);
    }

    private void SaveCore(string name, string secret)
    {
        var all = LoadEncrypted();
        EnsureWritable();
        var clear = Encoding.UTF8.GetBytes(secret ?? string.Empty);
        try
        {
            var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            all[name] = Convert.ToBase64String(encrypted);
            SaveEncrypted(all);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public string? Read(string provider)
    {
        EnsureExternalProvider(provider);
        return ReadCore(provider);
    }

    private string? ReadCore(string name)
    {
        var all = LoadEncrypted();
        if (!all.TryGetValue(name, out var encoded)) return null;
        try
        {
            var encrypted = Convert.FromBase64String(encoded);
            var clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyDictionary<string, string> GetProviderProcessEnvironment(
        IEnumerable<string>? allowedProviders = null)
    {
        var allow = allowedProviders is null
            ? null
            : new HashSet<string>(allowedProviders, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in LoadEncrypted().Keys)
        {
            if (provider.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (allow is not null && !allow.Contains(provider)) continue;
            var secret = Read(provider);
            if (secret is not null) result[GetEnvironmentName(provider)] = secret;
        }
        return result;
    }

    public void SaveInternal(string name, string secret) => SaveCore(InternalName(name), secret);

    public string? ReadInternal(string name) => ReadCore(InternalName(name));

    public void RemoveInternal(string name) => RemoveCore(InternalName(name));

    public void Remove(string provider)
    {
        EnsureExternalProvider(provider);
        RemoveCore(provider);
    }

    private void RemoveCore(string name)
    {
        var all = LoadEncrypted();
        EnsureWritable();
        if (all.Remove(name)) SaveEncrypted(all);
    }

    private static void EnsureExternalProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)
            || provider.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("外部 provider 不能读取、写入或删除总管家内部凭据槽。");
    }

    private static string InternalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("内部凭据名称格式不正确。", nameof(name));
        return InternalPrefix + name;
    }

    private Dictionary<string, string> LoadEncrypted()
    {
        if (LoadWarning is not null) return new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                   ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            PreserveCorruptFile();
            LoadWarning = "密钥文件已损坏。软件已经保留副本，并停止写入，防止把原密钥覆盖掉。";
            return new(StringComparer.OrdinalIgnoreCase);
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
                $"secrets.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(_path, backup, false);
        }
        catch
        {
            // 原文件仍然保留；这里绝不能为了做副本而覆盖它。
        }
    }

    private void SaveEncrypted(Dictionary<string, string> values)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }
}
