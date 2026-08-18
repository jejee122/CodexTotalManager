using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

/// <summary>
/// Builds the official Codex startup catalog consumed through model_catalog_json.
/// Files are written only inside the Codex home selected by CodexConfigService;
/// sandbox tests therefore never touch the workstation's real Codex home.
/// </summary>
public sealed class CodexModelCatalogService
{
    private const string OwnershipProperty = "codex_total_manager_catalog";
    private readonly CodexConfigService _config;

    public CodexModelCatalogService(CodexConfigService config) =>
        _config = config ?? throw new ArgumentNullException(nameof(config));

    public string CatalogPath => _config.ModelCatalogPath;
    public string CachePath => _config.ModelsCachePath;

    public int WriteCatalog(IReadOnlyList<ModelOption> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        RefuseUnownedCatalogOverwrite();

        var rows = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priority = 1;
        foreach (var official in LoadPreservedOfficialModels())
        {
            var slug = ReadSlug(official);
            if (string.IsNullOrWhiteSpace(slug) || !seen.Add(slug)) continue;
            official["priority"] = priority++;
            rows.Add(official);
        }
        foreach (var model in models.Where(item => !item.Disabled))
        {
            var slug = CatalogSlug(model);
            if (string.IsNullOrWhiteSpace(slug) || !seen.Add(slug)) continue;
            var contextWindow = Math.Clamp(model.ContextWindow ?? 128_000, 8_192, 2_000_000);
            rows.Add(new JsonObject
            {
                ["slug"] = slug,
                ["display_name"] = string.IsNullOrWhiteSpace(model.DisplayName)
                    ? slug
                    : model.DisplayName!.Trim(),
                ["description"] = model.IsOfficial
                ? "OpenAI official model routed through AI Gateway Manager."
                : $"{model.ProviderLabel} model routed locally by AI Gateway Manager.",
                ["default_reasoning_level"] = "medium",
                ["supported_reasoning_levels"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["effort"] = "medium",
                        ["description"] = "Balanced reasoning for everyday coding tasks"
                    }
                },
                ["shell_type"] = "shell_command",
                ["visibility"] = "list",
                ["supported_in_api"] = true,
                ["priority"] = priority++,
                ["context_window"] = contextWindow,
                ["max_context_window"] = contextWindow,
                ["effective_context_window_percent"] = 90,
                ["input_modalities"] = new JsonArray { "text" },
                ["supports_parallel_tool_calls"] = true,
                ["truncation_policy"] = new JsonObject
                {
                    ["mode"] = "bytes",
                    ["limit"] = 10_000
                },
                ["apply_patch_tool_type"] = "freeform",
                ["comp_hash"] = "codex-total-manager-v2"
            });
        }
        if (rows.Count == 0)
            throw new InvalidOperationException("Native Engine 没有提供可写入 Codex 的模型；连接已取消。");

        var root = new JsonObject
        {
            [OwnershipProperty] = true,
            ["models"] = rows
        };
        WriteJsonAtomically(CatalogPath, root);
        CreateOwnedStaleCacheOnlyWhenSafe();
        VerifyCatalog(rows.Count);
        return rows.Count;
    }

    private IEnumerable<JsonObject> LoadPreservedOfficialModels()
    {
        // model_catalog_json replaces Codex's whole bundled catalog. Preserve the
        // exact official metadata Codex already cached before appending third-party models.
        foreach (var path in new[] { CachePath, CatalogPath })
        {
            if (!File.Exists(path)) continue;
            JsonNode? root;
            try { root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch { continue; }
            if (root?["models"] is not JsonArray models) continue;
            foreach (var node in models)
            {
                if (node is not JsonObject model) continue;
                var slug = ReadSlug(model);
                if (string.IsNullOrWhiteSpace(slug)
                    || slug.Contains("/", StringComparison.Ordinal)) continue;
                yield return (JsonObject)model.DeepClone();
            }
        }
    }

    private static string ReadSlug(JsonObject model) =>
        model["slug"]?.GetValue<string>()?.Trim() ?? string.Empty;

    public void VerifyCatalog(int expectedCount)
    {
        if (!File.Exists(CatalogPath))
            throw new InvalidOperationException("总管家模型目录没有写入成功。");
        using var json = JsonDocument.Parse(File.ReadAllText(CatalogPath, Encoding.UTF8));
        var root = json.RootElement;
        if (!IsOwned(root)
            || !root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array
            || models.GetArrayLength() != expectedCount)
            throw new InvalidOperationException("总管家模型目录写入后校验失败。");
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models.EnumerateArray())
        {
            if (!model.TryGetProperty("slug", out var slugValue)
                || string.IsNullOrWhiteSpace(slugValue.GetString())
                || !slugs.Add(slugValue.GetString()!))
                throw new InvalidOperationException("模型目录中出现空名称或重复名称。");
        }
    }

    /// <summary>
    /// Deletes only files that still carry Total Manager's ownership marker.
    /// If Codex has replaced models_cache.json with a normal cache, it is left alone.
    /// </summary>
    public void RemoveOwnedArtifacts()
    {
        DeleteIfOwned(CatalogPath);
        DeleteIfOwned(CachePath);
    }

    private void RefuseUnownedCatalogOverwrite()
    {
        if (!File.Exists(CatalogPath)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(CatalogPath, Encoding.UTF8));
            if (IsOwned(json.RootElement)) return;
        }
        catch
        {
            // A malformed existing file is still user-owned and must not be replaced.
        }
        throw new InvalidOperationException(
            $"模型目录文件已经存在但不属于总管家：{CatalogPath}。为了保护原文件，本次没有覆盖。");
    }

    private void CreateOwnedStaleCacheOnlyWhenSafe()
    {
        if (File.Exists(CachePath))
        {
            try
            {
                using var current = JsonDocument.Parse(File.ReadAllText(CachePath, Encoding.UTF8));
                if (!IsOwned(current.RootElement)) return;
            }
            catch
            {
                return;
            }
        }

        var stale = new Dictionary<string, object?>
        {
            ["fetched_at"] = "2000-01-01T00:00:00Z",
            ["client_version"] = "0.0.0",
            [OwnershipProperty] = true,
            ["models"] = Array.Empty<object>()
        };
        WriteJsonAtomically(CachePath, stale);
    }

    private static string CatalogSlug(ModelOption model)
    {
        if (model.IsOfficial || model.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
            return model.Id.Trim();
        if (!string.IsNullOrWhiteSpace(model.Namespaced)
            && model.Namespaced.Contains('/'))
            return model.Namespaced.Trim();
        return $"{model.Provider.Trim()}/{model.Id.Trim()}";
    }

    private static bool IsOwned(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(OwnershipProperty, out var marker)
        && marker.ValueKind == JsonValueKind.True;

    private static void DeleteIfOwned(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (IsOwned(json.RootElement)) File.Delete(path);
        }
        catch
        {
            // Unknown or changed files are user-owned by default.
        }
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
