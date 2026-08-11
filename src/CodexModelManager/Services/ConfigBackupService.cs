using System.Text.Json;

namespace CodexModelManager.Services;

public sealed class ConfigBackupService
{
    private readonly string _source;
    private readonly string _directory;

    public ConfigBackupService(string? source = null, string? directory = null)
    {
        _source = source ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".opencodex",
            "config.json");
        _directory = directory ?? Path.Combine(
            AppSettingsService.ResolveDefaultDataDirectory(),
            "backups");
    }

    public string DirectoryPath => _directory;

    public int Count => Directory.Exists(_directory)
        ? Directory.GetFiles(_directory, "config-*.json").Length
        : 0;

    public string Create()
    {
        if (!File.Exists(_source)) throw new FileNotFoundException("找不到 OpenCodex 配置。", _source);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"config-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        File.Copy(_source, path, false);
        return path;
    }

    public string CreateAccountDeletionBackup(string poolCatalogPath)
    {
        if (!File.Exists(_source)) throw new FileNotFoundException("找不到 OpenCodex 配置。", _source);
        if (!File.Exists(poolCatalogPath)) throw new FileNotFoundException("找不到大管家号池清单。", poolCatalogPath);

        var sourceDirectory = Path.GetDirectoryName(_source)!;
        var oauthStore = Path.Combine(sourceDirectory, "oauth-tokens.json");
        var legacyAccountStore = Path.Combine(sourceDirectory, "codex-accounts.json");

        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, $"account-delete-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(target);
        File.Copy(_source, Path.Combine(target, "config.json"), false);
        if (File.Exists(oauthStore))
            File.Copy(oauthStore, Path.Combine(target, "oauth-tokens.json"), false);
        if (File.Exists(legacyAccountStore))
            File.Copy(legacyAccountStore, Path.Combine(target, "codex-accounts.json"), false);
        File.Copy(poolCatalogPath, Path.Combine(target, "pools.json"), false);
        File.WriteAllText(
            Path.Combine(target, "backup-manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                createdAt = DateTimeOffset.Now,
                files = new
                {
                    config = true,
                    pools = true,
                    oauthTokens = File.Exists(oauthStore),
                    legacyCodexAccounts = File.Exists(legacyAccountStore)
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
        return target;
    }

    public void RestoreAccountDeletionBackup(string backupDirectory, string poolCatalogPath)
    {
        if (!Directory.Exists(backupDirectory))
            throw new DirectoryNotFoundException($"找不到账号删除备份：{backupDirectory}");
        var configBackup = Path.Combine(backupDirectory, "config.json");
        var poolBackup = Path.Combine(backupDirectory, "pools.json");
        if (!File.Exists(configBackup)) throw new FileNotFoundException("备份中缺少 config.json。", configBackup);
        if (!File.Exists(poolBackup)) throw new FileNotFoundException("备份中缺少 pools.json。", poolBackup);

        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(poolCatalogPath)!);
        File.Copy(configBackup, _source, true);
        File.Copy(poolBackup, poolCatalogPath, true);
        RestoreOptionalStore(backupDirectory, "oauth-tokens.json");
        RestoreOptionalStore(backupDirectory, "codex-accounts.json");
    }

    public void Restore(string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("找不到回退备份。", backupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        File.Copy(backupPath, _source, true);
    }

    private void RestoreOptionalStore(string backupDirectory, string fileName)
    {
        var backup = Path.Combine(backupDirectory, fileName);
        if (!File.Exists(backup)) return;
        var target = Path.Combine(Path.GetDirectoryName(_source)!, fileName);
        File.Copy(backup, target, true);
    }
}
