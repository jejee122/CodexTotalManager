using System.Security.Cryptography;
using System.Text;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public static class UnifiedGatewayConfigurationIdentity
{
    public static string Compute(UnifiedGatewayConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var lines = new List<string>
        {
            configuration.Service.Trim(),
            configuration.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Path.GetFullPath(configuration.DataDirectory).TrimEnd(Path.DirectorySeparatorChar)
        };
        foreach (var route in configuration.Routes
                     .OrderBy(item => item.GatewayModel, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(string.Join('\u001f',
                route.GatewayModel,
                route.UpstreamModel,
                route.BaseUrl,
                route.SecretName ?? string.Empty,
                route.PoolId,
                route.SourceId,
                route.SourceKind,
                route.RoutePrefix,
                route.Adapter,
                route.CredentialIdentity,
                route.SourceFingerprint));
        }
        foreach (var group in (configuration.RotationGroups ?? new List<UnifiedGatewayRotationGroup>())
                     .OrderBy(item => item.GatewayModel, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(string.Join((char)31,
                "rotation-group",
                group.GatewayModel,
                group.UpstreamModel,
                string.Join(',', group.Candidates.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    public static bool Matches(UnifiedGatewayConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.ConfigurationFingerprint)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(configuration.ConfigurationFingerprint.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(Compute(configuration)));
}
