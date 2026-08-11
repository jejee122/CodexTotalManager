using System.Security.Cryptography;
using System.Text;

namespace CodexOpenCodexNative.OAuth;

public sealed record PkcePair(string Verifier, string Challenge)
{
    public static PkcePair Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(96);
        var verifier = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new PkcePair(verifier, challenge);
    }
}
