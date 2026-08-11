using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexOpenCodexNative.OAuth;

public sealed class OAuthCredentials
{
    public string Access { get; set; } = string.Empty;
    public string Refresh { get; set; } = string.Empty;
    public long ExpiresAtUnixMs { get; set; }
    public string? AccountId { get; set; }
    public string? Email { get; set; }
}

public sealed class OAuthTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexTotalManager:OAuth:v1");
    private readonly string _path;

    public OAuthTokenStore(string? dataRoot = null)
    {
        var root = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTotalManager", "runtime-v3", "native-proxy");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "oauth-tokens.json");
    }

    public OAuthCredentials? Load(string providerId)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var all = LoadEncrypted();
            return all is not null && all.TryGetValue(providerId, out var credentials) ? credentials : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string providerId, OAuthCredentials credentials)
    {
        var existing = LoadEncrypted();
        if (existing is null && File.Exists(_path))
            throw new InvalidOperationException("OAuth token 文件损坏或无法解密，已拒绝写入以防丢失已有凭据。");
        var all = existing ?? new Dictionary<string, OAuthCredentials>();
        all[providerId] = credentials;
        SaveEncrypted(all);
    }

    private Dictionary<string, OAuthCredentials>? LoadEncrypted()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, OAuthCredentials>();
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length == 0) return new Dictionary<string, OAuthCredentials>();
            var clear = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, OAuthCredentials>>(clear)
                       ?? new Dictionary<string, OAuthCredentials>();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch
        {
            return null;
        }
    }

    private void SaveEncrypted(Dictionary<string, OAuthCredentials> values)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(values, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }
}
