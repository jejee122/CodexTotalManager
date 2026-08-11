using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexModelManager.Services;

public static class CliCredentialIdentity
{
    public static string? Read(string dataDirectory, string poolId)
    {
        try
        {
            if (!PoolCatalogService.IsSafeCliPoolId(poolId)) return null;
            var root = Path.GetFullPath(Path.Combine(dataDirectory, "cli-proxy", "pools"));
            var poolDirectory = Path.GetFullPath(Path.Combine(root, poolId));
            if (!poolDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            var directory = Path.Combine(poolDirectory, "auth");
            if (!Directory.Exists(directory)) return null;
            var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).ToArray();
            if (files.Length != 1) return null;
            using var stream = new FileStream(
                files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var accountId = FindStableValue(document.RootElement,
                "account_id", "accountId", "chatgpt_account_id", "user_id", "userId", "sub");
            var email = FindStableValue(document.RootElement, "email");
            var provider = FindStableValue(document.RootElement, "type", "provider");
            if (string.IsNullOrWhiteSpace(accountId) && string.IsNullOrWhiteSpace(email)) return null;
            var canonical = string.Join("\n", new[]
            {
                "cmm-cli-account-v1",
                (accountId ?? string.Empty).Trim().ToLowerInvariant(),
                (email ?? string.Empty).Trim().ToLowerInvariant(),
                (provider ?? string.Empty).Trim().ToLowerInvariant()
            });
            return "acct:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException
                                   or System.Security.SecurityException
                                   or ArgumentException)
        {
            return null;
        }
    }

    private static string? FindStableValue(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is { Length: > 0 } value)
                    return value;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    && FindStableValue(property.Value, names) is { Length: > 0 } nested)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FindStableValue(item, names) is { Length: > 0 } nested)
                    return nested;
        }
        return null;
    }
}
