using Microsoft.VisualBasic.FileIO;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class BackupCatalogService
{
    private readonly string _managerRoot;
    private readonly string _dreamRoot;

    public BackupCatalogService(string? managerRoot = null, string? dreamRoot = null)
    {
        var dataRoot = AppSettingsService.ResolveDefaultDataDirectory();
        _managerRoot = Path.GetFullPath(managerRoot ?? Path.Combine(dataRoot, "backups"));
        _dreamRoot = Path.GetFullPath(dreamRoot ?? Path.Combine(dataRoot, "dreamskin-backups"));
    }

    public IReadOnlyList<BackupItemView> List()
    {
        var items = new List<BackupItemView>();
        if (Directory.Exists(_managerRoot))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_managerRoot))
            {
                var name = Path.GetFileName(path);
                if (File.Exists(path) && name.StartsWith("config-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        items.Add(Create(path, "总管家本机引擎配置", "模型来源、账号池和路由", "总管家本机引擎", true));
                else if (File.Exists(path) && name.StartsWith("codex-config-", StringComparison.OrdinalIgnoreCase)
                         && name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                    items.Add(Create(path, "Codex 配置", "默认模型入口", "Codex", true));
                else if (Directory.Exists(path) && name.StartsWith("v2ray-config-", StringComparison.OrdinalIgnoreCase))
                    items.Add(Create(path, "v2rayN 配置", "节点、路由和本机设置", "v2rayN", true));
                else
                    items.Add(Create(path, "总管家归档", "程序或迁移快照", "只读归档", false));
            }
        }

        var managerThemeRoot = Path.Combine(_dreamRoot, "manager-theme-backups");
        if (Directory.Exists(managerThemeRoot))
        {
            foreach (var path in Directory.EnumerateDirectories(managerThemeRoot))
                items.Add(Create(path, "Dream Skin 主题", "活动主题快照（暂仅查看）", ReadDreamSkinVersion(), false));
        }
        var manualRoot = Path.Combine(_dreamRoot, "manual-backups");
        if (Directory.Exists(manualRoot))
        {
            foreach (var path in Directory.EnumerateDirectories(manualRoot))
                items.Add(Create(path, "Dream Skin 完整归档", "引擎与主题历史", ReadDreamSkinVersion(), false));
        }

        foreach (var group in items.GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderByDescending(item => item.CreatedAt).ToArray();
            if (ordered.Length == 1 && IsCriticalType(group.Key))
            {
                ordered[0].CanDelete = false;
                ordered[0].ProtectionReason = "这是该类型唯一可恢复的关键备份";
            }
        }
        return items.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public IReadOnlyList<BackupItemView> RetentionCandidates(
        IReadOnlyList<BackupItemView> items,
        int keepCount,
        int keepDays)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-keepDays);
        var result = new List<BackupItemView>();
        foreach (var group in items.Where(item => item.CanDelete)
                     .GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderByDescending(item => item.CreatedAt).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var item = ordered[index];
                if (index >= keepCount || item.CreatedAt < cutoff) result.Add(item);
            }
            if (result.Count(item => item.Type.Equals(group.Key, StringComparison.OrdinalIgnoreCase)) >= ordered.Length)
                result.Remove(ordered[0]);
        }
        return result.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void MoveToRecycleBin(IEnumerable<BackupItemView> items)
    {
        var selected = items.ToArray();
        var all = List();
        foreach (var criticalType in all.Select(item => item.Type).Distinct(StringComparer.OrdinalIgnoreCase).Where(IsCriticalType))
        {
            var total = all.Count(item => item.Type.Equals(criticalType, StringComparison.OrdinalIgnoreCase));
            var removing = selected.Count(item => item.Type.Equals(criticalType, StringComparison.OrdinalIgnoreCase));
            if (total - removing < 1)
                throw new InvalidOperationException($"至少要保留 1 份 {criticalType}，本次没有删除。");
        }
        foreach (var item in selected)
        {
            if (!item.CanDelete) throw new InvalidOperationException($"受保护的备份不能删除：{item.Name}");
            var full = Path.GetFullPath(item.Path);
            if (!IsAllowed(full)) throw new InvalidOperationException($"备份不在受控目录中：{item.Name}");
            if (ContainsReparsePoint(full)) throw new InvalidOperationException($"备份包含重解析点，已拒绝删除：{item.Name}");
            if (File.Exists(full))
                FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else if (Directory.Exists(full))
                FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    }

    private BackupItemView Create(string path, string type, string function, string version, bool canRestore)
    {
        var full = Path.GetFullPath(path);
        var info = File.Exists(full) ? new FileInfo(full) as FileSystemInfo : new DirectoryInfo(full);
        return new BackupItemView
        {
            Path = full,
            Name = Path.GetFileName(full),
            Type = type,
            Function = function,
            Version = version,
            CreatedAt = new DateTimeOffset(info.CreationTime),
            SizeBytes = MeasureSize(full),
            CanRestore = canRestore,
            CanDelete = true
        };
    }

    private bool IsAllowed(string fullPath)
    {
        var manager = _managerRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dreamManager = Path.Combine(_dreamRoot, "manager-theme-backups").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dreamManual = Path.Combine(_dreamRoot, "manual-backups").TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(manager, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(dreamManager, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(dreamManual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static bool ContainsReparsePoint(string path)
    {
        if (HasReparsePoint(path)) return true;
        if (!Directory.Exists(path)) return false;
        try
        {
            return Directory.EnumerateFileSystemEntries(path, "*", System.IO.SearchOption.AllDirectories)
                .Any(HasReparsePoint);
        }
        catch { return true; }
    }

    private static long MeasureSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories)
                .Sum(file => { try { return new FileInfo(file).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }

    private string ReadDreamSkinVersion()
    {
        try { return $"Dream Skin {File.ReadAllText(Path.Combine(_dreamRoot, "engine", "VERSION")).Trim()}"; }
        catch { return "Dream Skin"; }
    }

    private static bool IsCriticalType(string type) => type is
            "总管家本机引擎配置" or
        "Codex 配置" or
        "v2rayN 配置" or
        "Dream Skin 主题" or
        "Dream Skin 完整归档" or
        "总管家归档";
}
