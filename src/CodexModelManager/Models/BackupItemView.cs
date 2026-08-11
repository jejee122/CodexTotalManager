namespace CodexModelManager.Models;

public sealed class BackupItemView
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Function { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public long SizeBytes { get; init; }
    public bool CanRestore { get; init; }
    public bool CanDelete { get; set; }
    public string ProtectionReason { get; set; } = string.Empty;
    public bool IsSelected { get; set; }

    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText => FormatBytes(SizeBytes);
    public string Detail => $"{Type} · {Function} · {Version} · {SizeText}";
    public string RestoreText => CanRestore ? "可恢复" : "仅查看";
    public string DeleteText => CanDelete ? "移入回收站" : "受保护";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed class LocalServiceView
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Running { get; init; }
    public string Status { get; init; } = string.Empty;
    public string PortText { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string LastError { get; init; } = string.Empty;
    public string Capability { get; init; } = "已经可用";
    public string Purpose { get; init; } = string.Empty;
    public string PlainStatus { get; init; } = string.Empty;
    public string ImpactText { get; init; } = string.Empty;
    public string StateColor { get; init; } = "#79DDBA";
    public bool CanStart => !Running;
    public bool CanStop => Running;
    public bool CanRestart => Running;
    public string PrimaryActionText => Running ? "运行中" : "启动此服务";
    public string TechnicalDetail => $"{PortText} · {Detail}";
}
