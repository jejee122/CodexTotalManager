namespace CodexOpenCodexNative.Providers;

/// <summary>
/// 总管家内部路由的唯一命名入口。真实模型名称不得使用这个前缀。
/// </summary>
public static class InternalRouteNames
{
    public const string Prefix = "cmm/";
    public const string MainAlias = Prefix + "main";
    public const string SwitchComboId = "cmm-switch";

    // 只用于把存量总管家数据一次性改名；路由器不接受旧前缀。
    private const string LegacyPrefix = "zcode/";
    private const string LegacySwitchComboId = "zcode-switch";

    public static bool IsAlias(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryMigrateLegacyAlias(string? value, out string migrated)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            migrated = Prefix + value[LegacyPrefix.Length..];
            return true;
        }

        migrated = value ?? string.Empty;
        return false;
    }

    public static bool TryMigrateLegacyComboId(string? value, out string migrated)
    {
        if (string.Equals(value, LegacySwitchComboId, StringComparison.OrdinalIgnoreCase))
        {
            migrated = SwitchComboId;
            return true;
        }

        migrated = value ?? string.Empty;
        return false;
    }
}
