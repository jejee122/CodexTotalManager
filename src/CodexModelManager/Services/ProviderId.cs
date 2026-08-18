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
        if (string.IsNullOrWhiteSpace(ascii)
            || ascii is "openai" or "combo" or "cmm"
            || ascii.StartsWith("cmm-", StringComparison.OrdinalIgnoreCase))
            ascii = "relay";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(displayName + "\n" + baseUrl)))
            .ToLowerInvariant()[..8];
        var candidate = $"{ascii}-{hash}";
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(candidate)) return candidate;

        // Never derive a collision suffix from the current clock: two rapid
        // additions could otherwise receive the same ID and silently replace
        // the first provider in the Native Engine registry.
        for (var suffix = 2; suffix <= 9999; suffix++)
        {
            var numbered = $"{candidate}-{suffix}";
            if (!used.Contains(numbered)) return numbered;
        }
        throw new InvalidOperationException("可用的模型来源编号已经耗尽；请删除重复来源后再试。");
    }
}
