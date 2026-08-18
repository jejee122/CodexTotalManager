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
    private readonly object _gate = new();

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
        return BuildEnvironmentName(provider);
    }

    private static string BuildEnvironmentName(string provider)
    {
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
        lock (_gate)
        {
            using var fileLock = LocalFileTransaction.Acquire(_path);
            var all = LoadEncrypted();
            EnsureWritable();
            if (!name.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                EnsureNoEnvironmentNameCollisions(all.Keys.Append(name));
            }
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
        return Decrypt(encoded);
    }

    public IReadOnlyDictionary<string, string> GetProviderProcessEnvironment(
        IEnumerable<string>? allowedProviders = null)
    {
        lock (_gate)
        {
            using var fileLock = LocalFileTransaction.Acquire(_path);
            var allow = allowedProviders is null
                ? null
                : new HashSet<string>(allowedProviders, StringComparer.OrdinalIgnoreCase);
            var encrypted = LoadEncrypted();
            var providers = encrypted.Keys
                .Where(provider => !provider.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(provider => allow is null || allow.Contains(provider))
                .ToArray();
            EnsureNoEnvironmentNameCollisions(providers);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in providers)
            {
                var secret = Decrypt(encrypted[provider]);
                if (secret is not null) result[BuildEnvironmentName(provider)] = secret;
            }
            return result;
        }
    }

    public void SaveInternal(string name, string secret) => SaveCore(InternalName(name), secret);

    public string? ReadInternal(string name) => ReadCore(InternalName(name));

    public void RemoveInternal(string name) => RemoveCore(InternalName(name));

    /// <summary>列出所有以指定前缀开头的内部凭据名（已去掉 internal: 前缀），供网关客户端钥匙枚举使用。</summary>
    public IReadOnlyList<string> ListInternalNames(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("内部凭据前缀不能为空。", nameof(prefix));
        var fullPrefix = InternalPrefix + prefix;
        return LoadEncrypted()
            .Keys
            .Where(name => name.StartsWith(fullPrefix, StringComparison.OrdinalIgnoreCase)
                           && name.Length > fullPrefix.Length)
            .Select(name => name[InternalPrefix.Length..])
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Remove(string provider)
    {
        EnsureExternalProvider(provider);
        RemoveCore(provider);
    }

    private void RemoveCore(string name)
    {
        lock (_gate)
        {
            using var fileLock = LocalFileTransaction.Acquire(_path);
            var all = LoadEncrypted();
            EnsureWritable();
            if (all.Remove(name)) SaveEncrypted(all);
        }
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

    private static void EnsureNoEnvironmentNameCollisions(IEnumerable<string> providers)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers
                     .Where(provider => !provider.StartsWith(InternalPrefix, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var environmentName = BuildEnvironmentName(provider);
            if (owners.TryGetValue(environmentName, out var existing)
                && !string.Equals(existing, provider, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"来源编号“{provider}”与“{existing}”会共用同一个 API Key 环境变量。请修改其中一个来源编号后再试。");
            }
            owners[environmentName] = provider;
        }
    }

    private Dictionary<string, string> LoadEncrypted()
    {
        if (LoadWarning is not null) return new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("密钥文件根节点必须是对象。");

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new JsonException("密钥值必须是字符串。");
                if (!values.TryAdd(property.Name, property.Value.GetString() ?? string.Empty))
                    throw new JsonException("密钥文件包含大小写重复的名称。");
            }
            return values;
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
        LocalFileTransaction.WriteAtomic(
            _path,
            JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Decrypt(string encoded)
    {
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
}
