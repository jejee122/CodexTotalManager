namespace CodexModelManager.Models;

public sealed class DreamSkinThemeView
{
    public string Id { get; init; } = string.Empty;
    public string ManifestId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string AppearanceText { get; init; } = string.Empty;
    public string MotionText { get; init; } = string.Empty;
    public string PreviewBackground { get; init; } = "#101A27";
    public string PreviewAccent { get; init; } = "#D69A4B";
    public string? PreviewImagePath { get; init; }
    public bool IsDynamic { get; init; }
    public bool IsActive { get; init; }
    public bool WasLastSelected { get; init; }
    public bool CanApply => !IsActive;
    public string ActionText => IsActive
        ? "正在使用"
        : WasLastSelected ? "恢复此皮肤" : "应用到 Codex";
    public string StateText => IsActive
        ? "当前显示"
        : WasLastSelected ? "上次使用" : string.Empty;
}

public sealed record DreamSkinSnapshot(
    bool EngineReady,
    bool ManagerScriptTrusted,
    bool LiveSessionConnected,
    bool IsPaused,
    string EngineVersion,
    string ActiveThemeId,
    string ActiveThemeName,
    string StatusTitle,
    string StatusDetail,
    string StateRoot,
    string ThemesRoot,
    IReadOnlyList<DreamSkinThemeView> Themes);

public enum DreamSkinOperationStatus
{
    Success,
    NeedsRestart,
    Failed,
    Canceled
}

public sealed record DreamSkinOperationResult(
    DreamSkinOperationStatus Status,
    string Message,
    bool PreviousThemeRecovered = false,
    string? BackupPath = null)
{
    public bool Success => Status == DreamSkinOperationStatus.Success;
}
