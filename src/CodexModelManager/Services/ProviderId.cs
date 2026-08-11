using System.Security.Cryptography;
using System.Text;

namespace CodexModelManager.Services;

public static class ProviderId
{
    public static string From(string displayName, string baseUrl, IEnumerable<string> existing)
    {
        var ascii = new string(displayName.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) && ch <= 127 ? ch : '-')
            .ToArray());
        while (ascii.Contains("--", StringComparison.Ordinal)) ascii = ascii.Replace("--", "-");
        ascii = ascii.Trim('-');
        if (ascii.Length > 24) ascii = ascii[..24].TrimEnd('-');
        if (string.IsNullOrWhiteSpace(ascii) || ascii is "openai" or "combo") ascii = "relay";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(displayName + "\n" + baseUrl)))
            .ToLowerInvariant()[..8];
        var candidate = $"{ascii}-{hash}";
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return used.Contains(candidate) ? $"{ascii}-{hash}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1000}" : candidate;
    }
}
