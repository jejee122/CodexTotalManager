using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodexModelManager.Services;

/// <summary>
/// 统一网关客户端钥匙体系：
/// - 主钥匙 internal 名 "unified-gateway:client"（兼容历史，等价于 label = master，不可删除）；
/// - 每个 harness 一把独立钥匙 internal 名 "unified-gateway:client:&lt;label&gt;"，值为 cmm-gw-&lt;label&gt;-&lt;随机&gt;，
///   可单独吊销，网关请求日志按 label 记账。
/// </summary>
public static class UnifiedGatewayKeys
{
    public const string MasterSecretName = "unified-gateway:client";
    public const string ClientPrefix = "unified-gateway:client:";
    public const string MasterLabel = "master";
    // Route marker only. The native admission token is never copied into the
    // gateway configuration or SecretStore; the gateway reads the DPAPI-backed
    // native config at call time and verifies that the target is the same
    // loopback engine before attaching it.
    public const string NativeEngineAdmissionRouteSecretName = "native-engine:admission";

    private static readonly Regex SafeLabel = new(
        "^[a-z0-9][a-z0-9-]{0,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label) && SafeLabel.IsMatch(label);

    public static string SecretNameForLabel(string label) => ClientPrefix + label.ToLowerInvariant();

    /// <summary>从 internal 名解析 label；主钥匙返回 master；不属于钥匙体系返回 null。</summary>
    public static string? LabelForSecretName(string internalName)
    {
        if (internalName.Equals(MasterSecretName, StringComparison.OrdinalIgnoreCase))
            return MasterLabel;
        if (internalName.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase)
            && internalName.Length > ClientPrefix.Length)
        {
            var label = internalName[ClientPrefix.Length..];
            return IsValidLabel(label) ? label : null;
        }
        return null;
    }

    public static string GenerateKeyValue(string label) =>
        "cmm-gw-" + label.ToLowerInvariant() + "-"
        + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
