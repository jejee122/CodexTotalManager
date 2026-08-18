using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace CodexModelManager.Services;

/// <summary>
/// Owns the small, reversible Codex configuration block used by Total Manager.
/// The managed route deliberately keeps Codex on its built-in "openai" provider;
/// only the OpenAI base URL and the startup model catalog are overridden.
/// </summary>
public sealed class CodexConfigService
{
    public const string ManagedNativeProviderId = "openai";
    public const string LegacyManagedNativeProviderId = "cmm_native";
    public const string NativeAdmissionHeaderName = "X-CMM-Admission";
    public const string NativeAdmissionEnvironmentVariable = "CMM_NATIVE_ADMISSION_TOKEN";
    public const string DefaultManagedNativeBaseUrl = "http://127.0.0.1:10100/v1";
    public const string ModelCatalogFileName = "codex-total-manager-catalog.json";

    private const string ManagedRoutingBeginMarker =
        "# BEGIN CODEX TOTAL MANAGER: native-routing v2";
    private const string ManagedRoutingEndMarker =
        "# END CODEX TOTAL MANAGER: native-routing";
    private const string LegacyProviderBeginMarker =
        "# BEGIN CODEX TOTAL MANAGER: cmm_native_provider v1";
    private const string LegacyProviderEndMarker =
        "# END CODEX TOTAL MANAGER: cmm_native_provider";
    private const string PreviousProviderAbsentComment =
        "# previous-model-provider-line: absent";
    private const string PreviousProviderBase64Prefix =
        "# previous-model-provider-line-base64: ";

    private static readonly Regex ManagedRoutingBeginLine = MarkerRegex(ManagedRoutingBeginMarker);
    private static readonly Regex ManagedRoutingEndLine = MarkerRegex(ManagedRoutingEndMarker);
    private static readonly Regex LegacyProviderBeginLine = MarkerRegex(LegacyProviderBeginMarker);
    private static readonly Regex LegacyProviderEndLine = MarkerRegex(LegacyProviderEndMarker);

    private readonly string _configPath;
    private readonly string _backupDirectory;
    private readonly string _managedNativeBaseUrl;

    public CodexConfigService(
        string? configPath = null,
        string? backupDirectory = null,
        string? managedNativeBaseUrl = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "config.toml");
        _backupDirectory = backupDirectory ?? Path.Combine(
            AppSettingsService.ResolveDefaultDataDirectory(),
            "backups",
            "codex");
        _managedNativeBaseUrl = managedNativeBaseUrl ?? DefaultManagedNativeBaseUrl;
        if (!Uri.TryCreate(_managedNativeBaseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp
            || !endpoint.IsLoopback
            || endpoint.AbsolutePath != "/v1")
            throw new ArgumentException(
                "The managed Native Engine URL must be a loopback HTTP /v1 endpoint.",
                nameof(managedNativeBaseUrl));
    }

    public string ConfigPath => _configPath;
    public string CodexHomeDirectory => Path.GetDirectoryName(Path.GetFullPath(_configPath))!;
    public string ModelCatalogPath => Path.Combine(CodexHomeDirectory, ModelCatalogFileName);
    public string ModelsCachePath => Path.Combine(CodexHomeDirectory, "models_cache.json");

    public bool MemoryProtectionLooksSafe()
    {
        if (!File.Exists(_configPath)) return false;
        try
        {
            var source = File.ReadAllText(_configPath, Encoding.UTF8);
            var document = ParseDocument(source);
            if (!document.TryGetValue("model_provider", out var value)) return true;
            if (value is not string provider) return false;
            if (provider.Equals(ManagedNativeProviderId, StringComparison.OrdinalIgnoreCase))
            {
                var block = FindManagedRoutingBlock(source);
                return block is not null && IsExpectedManagedRoutingBlock(source, block.Value);
            }
            if (!provider.Equals(LegacyManagedNativeProviderId, StringComparison.Ordinal)) return false;
            var legacyBlock = FindLegacyProviderBlock(source);
            return legacyBlock is not null && IsExpectedLegacyBlock(source, legacyBlock.Value, out _);
        }
        catch
        {
            return false;
        }
    }

    public string? ReadDefaultModel() => ReadRootString("model");
    public string? ReadModelProvider() => ReadRootString("model_provider");

    /// <summary>
    /// Reads only provider and gateway identity. Secrets and authorization values
    /// are never included in the returned snapshot.
    /// </summary>
    public CodexGatewaySnapshot ReadGatewaySnapshot()
    {
        var managedGateway = SanitizeGatewayForDisplay(_managedNativeBaseUrl);
        if (!File.Exists(_configPath))
        {
            return new CodexGatewaySnapshot(
                false, false, false, "未找到", "未找到 Codex 配置文件",
                managedGateway, "未找到", "无法确认",
                "找不到 ~/.codex/config.toml，因此没有修改任何内容。");
        }

        try
        {
            var source = File.ReadAllText(_configPath, Encoding.UTF8);
            var document = ParseDocument(source);
            var selectedProvider = ReadSelectedProvider(document);
            var managedBlock = FindManagedRoutingBlock(source);
            if (managedBlock is not null)
            {
                if (!IsExpectedManagedRoutingBlock(source, managedBlock.Value))
                    throw new InvalidOperationException(
                        "总管家的连接配置被手工改过。为了保护你的配置，开关已经锁定。");
                ValidateManagedCandidate(source);
                return new CodexGatewaySnapshot(
                    true, true, true, selectedProvider,
                    SanitizeGatewayForDisplay(_managedNativeBaseUrl), managedGateway,
                    selectedProvider, "Codex 内置官方网关",
                    "已连接：Codex 仍使用内置 openai 身份，只把请求交给总管家本机入口。");
            }

            var legacyBlock = FindLegacyProviderBlock(source);
            if (legacyBlock is not null)
            {
                if (!IsExpectedLegacyBlock(source, legacyBlock.Value, out var previousLine))
                    throw new InvalidOperationException(
                        "检测到被改动的旧版 cmm_native 配置。为了保护你的配置，开关已经锁定。");
                var restored = RestoreLegacyProviderSource(source, legacyBlock.Value, previousLine);
                var restoredDocument = ParseDocument(restored);
                var restoredProvider = ReadSelectedProvider(restoredDocument);
                return new CodexGatewaySnapshot(
                    true, true, true, LegacyManagedNativeProviderId, managedGateway,
                    managedGateway, restoredProvider,
                    ReadProviderGateway(restoredDocument, restoredProvider),
                    "检测到旧版连接。取消连接会精确移除旧块；再次连接时会自动改成新版 openai 原生身份。");
            }

            RejectUnownedManagedValues(document);
            return new CodexGatewaySnapshot(
                true, CanAttach(document), false, selectedProvider,
                ReadProviderGateway(document, selectedProvider), managedGateway,
                selectedProvider, ReadProviderGateway(document, selectedProvider),
                CanAttach(document)
                    ? "默认关闭：总管家没有接管 Codex。"
                    : "当前显式选择了别的 Provider。为了不隐藏旧任务和旧配置，连接开关已锁定。");
        }
        catch (Exception ex)
        {
            return new CodexGatewaySnapshot(
                true, false, false, "无法安全读取", "已隐藏", managedGateway,
                "无法确认", "无法确认", ex.Message);
        }
    }

    /// <summary>
    /// Kept under its historical name for callers. It now means "the owned v2
    /// native route is connected" and no longer means that a cmm_native provider
    /// was selected.
    /// </summary>
    public bool IsManagedNativeProviderSelected()
    {
        if (!File.Exists(_configPath)) return false;
        try
        {
            var source = File.ReadAllText(_configPath, Encoding.UTF8);
            var managed = FindManagedRoutingBlock(source);
            if (managed is not null)
                return IsExpectedManagedRoutingBlock(source, managed.Value)
                       && ManagedCandidateIsValid(source);

            var legacy = FindLegacyProviderBlock(source);
            return legacy is not null
                   && IsExpectedLegacyBlock(source, legacy.Value, out _)
                   && ReadRootString("model_provider") == LegacyManagedNativeProviderId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds the owned v2 block before the first TOML table. Existing user-owned
    /// openai_base_url/model_catalog_json values and non-openai providers are never overwritten.
    /// A valid legacy block is migrated in memory and committed atomically.
    /// </summary>
    public bool EnsureManagedNativeProvider(bool createSnapshot = false)
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException("找不到 Codex 配置文件。", _configPath);

        var original = File.ReadAllText(_configPath, Encoding.UTF8);
        var existing = FindManagedRoutingBlock(original);
        if (existing is not null)
        {
            if (!IsExpectedManagedRoutingBlock(original, existing.Value)
                || !ManagedCandidateIsValid(original))
                throw new InvalidOperationException(
                    "总管家的连接配置被改过。为了保护你的配置，本次拒绝覆盖。");
            return false;
        }

        var working = original;
        var legacy = FindLegacyProviderBlock(working);
        if (legacy is not null)
        {
            if (!IsExpectedLegacyBlock(working, legacy.Value, out var previousLine))
                throw new InvalidOperationException(
                    "旧版 cmm_native 配置被改过。为了保护你的配置，本次拒绝自动迁移。");
            working = RestoreLegacyProviderSource(working, legacy.Value, previousLine);
        }

        var document = ParseDocument(working);
        if (!CanAttach(document))
            throw new InvalidOperationException(
                "当前 Codex 明确选择了别的 Provider。请先在 Codex 中恢复 openai，再连接总管家，避免旧任务被隐藏。");
        RejectUnownedManagedValues(document);

        var newLine = DetectNewLine(working);
        var block = BuildManagedRoutingBlock(newLine);
        var updated = working.Length == 0
            ? block
            : block + newLine + working;
        ValidateManagedCandidate(updated);
        if (createSnapshot) CreateSnapshot();
        WriteAtomically(updated);
        return true;
    }

    /// <summary>
    /// Removes only an exact Total Manager block. User-owned lines are left byte-for-byte intact.
    /// </summary>
    public bool RemoveManagedNativeProvider(bool createSnapshot = false)
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException("找不到 Codex 配置文件。", _configPath);

        var original = File.ReadAllText(_configPath, Encoding.UTF8);
        var managed = FindManagedRoutingBlock(original);
        string updated;
        if (managed is not null)
        {
            if (!IsExpectedManagedRoutingBlock(original, managed.Value))
                throw new InvalidOperationException(
                    "总管家的连接配置被改过。为了保护你的配置，本次拒绝删除。");
            updated = RemoveWholeBlock(original, managed.Value);
        }
        else
        {
            var legacy = FindLegacyProviderBlock(original);
            if (legacy is null) return false;
            if (!IsExpectedLegacyBlock(original, legacy.Value, out var previousLine))
                throw new InvalidOperationException(
                    "旧版 cmm_native 配置被改过。为了保护你的配置，本次拒绝删除。");
            updated = RestoreLegacyProviderSource(original, legacy.Value, previousLine);
        }

        _ = ParseDocument(updated);
        if (createSnapshot) CreateSnapshot();
        WriteAtomically(updated);
        return true;
    }

    public void SetDefaultModel(string model)
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException("找不到 Codex 配置文件。", _configPath);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Codex 默认模型不能为空。", nameof(model));

        var original = File.ReadAllText(_configPath, Encoding.UTF8);
        if (!MemoryProtectionLooksSafe())
            throw new InvalidOperationException("Codex 配置没有通过安全检查，本次没有修改默认模型。");
        var document = ParseDocument(original);
        var modelLine = FindSimpleRootAssignment(original, document, "model");
        var replacement = $"model = \"{EscapeTomlString(model.Trim())}\"";
        var updated = modelLine is null
            ? replacement + DetectNewLine(original) + original
            : original.Remove(modelLine.Value.Start, modelLine.Value.Length)
                .Insert(modelLine.Value.Start, replacement);
        if (updated == original) return;
        _ = ParseDocument(updated);
        WriteAtomically(updated);
    }

    // Explicit snapshots remain available to the separate backup/restore screen.
    // Connection and model-routing transactions do not create them automatically.
    public string CreateSnapshot()
    {
        if (!File.Exists(_configPath))
            throw new FileNotFoundException("找不到 Codex 配置文件。", _configPath);
        Directory.CreateDirectory(_backupDirectory);
        var snapshot = Path.Combine(
            _backupDirectory,
            $"codex-config-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.toml");
        File.Copy(_configPath, snapshot, false);
        return snapshot;
    }

    public void RestoreSnapshot(string snapshotPath)
    {
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException("找不到 Codex 配置快照。", snapshotPath);
        var candidate = File.ReadAllText(snapshotPath, Encoding.UTF8);
        _ = ParseDocument(candidate);
        WriteAtomically(candidate);
    }

    private string? ReadRootString(string key)
    {
        if (!File.Exists(_configPath)) return null;
        try
        {
            var document = ParseDocument(File.ReadAllText(_configPath, Encoding.UTF8));
            return document.TryGetValue(key, out var value) && value is string text ? text : null;
        }
        catch
        {
            return null;
        }
    }

    private static TomlTable ParseDocument(string source)
    {
        var syntax = Toml.Validate(Toml.Parse(source));
        if (syntax.HasErrors)
            throw new InvalidOperationException("Codex config.toml 不是合法 TOML；本次拒绝修改。");
        return Toml.ToModel(syntax);
    }

    private static bool CanAttach(TomlTable document) =>
        !document.TryGetValue("model_provider", out var selected)
        || selected is string provider
        && provider.Equals(ManagedNativeProviderId, StringComparison.OrdinalIgnoreCase);

    private static void RejectUnownedManagedValues(TomlTable document)
    {
        if (document.ContainsKey("openai_base_url"))
            throw new InvalidOperationException(
                "你自己的配置里已经有 openai_base_url。总管家不会覆盖它，请先确认这条地址是否还需要。");
        if (document.ContainsKey("model_catalog_json"))
            throw new InvalidOperationException(
                "你自己的配置里已经有 model_catalog_json。总管家不会覆盖它，请先确认这个模型目录是否还需要。");
    }

    private static string ReadSelectedProvider(TomlTable document) =>
        document.TryGetValue("model_provider", out var selected) && selected is string provider
            ? provider
            : "openai（Codex 内置）";

    private static string ReadProviderGateway(TomlTable document, string providerId)
    {
        var lookupId = providerId.Equals("openai（Codex 内置）", StringComparison.Ordinal)
            ? "openai"
            : providerId;
        if (lookupId.Equals("openai", StringComparison.OrdinalIgnoreCase)
            && document.TryGetValue("openai_base_url", out var rootBaseUrl)
            && rootBaseUrl is string rootUrl
            && !string.IsNullOrWhiteSpace(rootUrl))
            return SanitizeGatewayForDisplay(rootUrl);
        if (document.TryGetValue("model_providers", out var providersValue)
            && providersValue is TomlTable providers
            && providers.TryGetValue(lookupId, out var providerValue)
            && providerValue is TomlTable provider
            && provider.TryGetValue("base_url", out var baseUrlValue)
            && baseUrlValue is string baseUrl
            && !string.IsNullOrWhiteSpace(baseUrl))
            return SanitizeGatewayForDisplay(baseUrl);
        return lookupId.Equals("openai", StringComparison.OrdinalIgnoreCase)
            ? "Codex 内置官方网关（配置文件未单独写地址）"
            : "这个 Provider 没有在配置文件里写明网关";
    }

    private static string SanitizeGatewayForDisplay(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
            return "网关地址格式异常，已隐藏";
        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, uri.AbsolutePath)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped).TrimEnd('/');
    }

    private static RootAssignment? FindSimpleRootAssignment(
        string source,
        TomlTable document,
        string key)
    {
        var hasRootValue = document.ContainsKey(key);
        var keyPattern = $"(?:{Regex.Escape(key)}|\"{Regex.Escape(key)}\"|'{Regex.Escape(key)}')";
        var pattern = new Regex(
            $"(?m)^(?<line>[ \\t]*{keyPattern}[ \\t]*=[^\\r\\n]*)\\r?$",
            RegexOptions.CultureInvariant);
        var matches = pattern.Matches(source).Cast<Match>().ToArray();
        if (!hasRootValue)
        {
            if (matches.Length == 0) return null;
            throw new InvalidOperationException($"检测到表内或无法确认归属的 {key}；本次拒绝修改。");
        }
        if (matches.Length != 1)
            throw new InvalidOperationException($"顶层 {key} 使用了无法安全保真的 TOML 写法；本次拒绝修改。");
        var firstTable = Regex.Match(source, "(?m)^[ \\t]*\\[\\[?[^\\r\\n]+\\r?$");
        if (firstTable.Success && matches[0].Index > firstTable.Index)
            throw new InvalidOperationException($"无法确认顶层 {key} 的文本位置；本次拒绝修改。");
        var line = matches[0].Groups["line"];
        return new RootAssignment(line.Index, line.Length, line.Value);
    }

    private static ManagedBlock? FindManagedRoutingBlock(string source) =>
        FindMarkedBlock(source, ManagedRoutingBeginLine, ManagedRoutingEndLine, "native-routing v2");

    private static ManagedBlock? FindLegacyProviderBlock(string source) =>
        FindMarkedBlock(source, LegacyProviderBeginLine, LegacyProviderEndLine, "cmm_native v1");

    private static ManagedBlock? FindMarkedBlock(
        string source,
        Regex beginPattern,
        Regex endPattern,
        string label)
    {
        var begins = beginPattern.Matches(source).Cast<Match>().ToArray();
        var ends = endPattern.Matches(source).Cast<Match>().ToArray();
        if (begins.Length == 0 && ends.Length == 0) return null;
        if (begins.Length != 1 || ends.Length != 1 || begins[0].Index >= ends[0].Index)
            throw new InvalidOperationException($"{label} 托管标记不完整或重复；本次拒绝修改。");
        var end = ends[0].Index + ends[0].Length;
        if (end > 0 && end <= source.Length && source[end - 1] == '\r') end--;
        return new ManagedBlock(begins[0].Index, end - begins[0].Index);
    }

    private bool IsExpectedManagedRoutingBlock(string source, ManagedBlock block)
    {
        var current = source.Substring(block.Start, block.Length)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return current.Equals(BuildManagedRoutingBlock("\n"), StringComparison.Ordinal);
    }

    private bool IsExpectedLegacyBlock(
        string source,
        ManagedBlock block,
        out string? previousLine)
    {
        var current = source.Substring(block.Start, block.Length);
        var lines = current.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 3)
        {
            previousLine = null;
            return false;
        }
        if (lines[1].Equals(PreviousProviderAbsentComment, StringComparison.Ordinal))
        {
            previousLine = null;
        }
        else if (lines[1].StartsWith(PreviousProviderBase64Prefix, StringComparison.Ordinal))
        {
            try
            {
                previousLine = Encoding.UTF8.GetString(Convert.FromBase64String(
                    lines[1][PreviousProviderBase64Prefix.Length..]));
            }
            catch
            {
                previousLine = null;
                return false;
            }
        }
        else
        {
            previousLine = null;
            return false;
        }

        var expected = BuildLegacyProviderBlock(previousLine, "\n");
        return current.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Equals(expected, StringComparison.Ordinal);
    }

    private string BuildManagedRoutingBlock(string newLine) => string.Join(newLine,
        ManagedRoutingBeginMarker,
        "# owned-by: Codex Total Manager; remove only through the connection toggle",
        $"openai_base_url = \"{EscapeTomlString(_managedNativeBaseUrl)}\"",
        $"model_catalog_json = \"{EscapeTomlString(ModelCatalogPath.Replace('\\', '/'))}\"",
        ManagedRoutingEndMarker);

    private string BuildLegacyProviderBlock(string? previousLine, string newLine)
    {
        var previous = previousLine is null
            ? PreviousProviderAbsentComment
            : PreviousProviderBase64Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(previousLine));
        return string.Join(newLine,
            LegacyProviderBeginMarker,
            previous,
            $"model_provider = \"{LegacyManagedNativeProviderId}\"",
            $"model_providers.{LegacyManagedNativeProviderId}.name = \"Codex Total Manager Native\"",
            $"model_providers.{LegacyManagedNativeProviderId}.base_url = \"{_managedNativeBaseUrl}\"",
            $"model_providers.{LegacyManagedNativeProviderId}.wire_api = \"responses\"",
            $"model_providers.{LegacyManagedNativeProviderId}.requires_openai_auth = true",
            $"model_providers.{LegacyManagedNativeProviderId}.supports_websockets = false",
            $"model_providers.{LegacyManagedNativeProviderId}.env_http_headers = {{ \"{NativeAdmissionHeaderName}\" = \"{NativeAdmissionEnvironmentVariable}\" }}",
            LegacyProviderEndMarker);
    }

    private static string RestoreLegacyProviderSource(
        string source,
        ManagedBlock block,
        string? previousLine)
    {
        var removeLength = block.Length;
        if (previousLine is null && block.Start + removeLength < source.Length)
        {
            if (source.AsSpan(block.Start + removeLength).StartsWith("\r\n")) removeLength += 2;
            else if (source[block.Start + removeLength] is '\r' or '\n') removeLength++;
        }
        return source.Remove(block.Start, removeLength)
            .Insert(block.Start, previousLine ?? string.Empty);
    }

    private static string RemoveWholeBlock(string source, ManagedBlock block)
    {
        var removeLength = block.Length;
        if (block.Start + removeLength < source.Length)
        {
            if (source.AsSpan(block.Start + removeLength).StartsWith("\r\n")) removeLength += 2;
            else if (source[block.Start + removeLength] is '\r' or '\n') removeLength++;
        }
        return source.Remove(block.Start, removeLength);
    }

    private bool ManagedCandidateIsValid(string candidate)
    {
        try
        {
            ValidateManagedCandidate(candidate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ValidateManagedCandidate(string candidate)
    {
        var document = ParseDocument(candidate);
        if (document.TryGetValue("model_provider", out var selected)
            && (selected is not string provider
                || !provider.Equals(ManagedNativeProviderId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "连接后 Codex 没有保留 openai 原生身份；本次拒绝写入。");
        if (!document.TryGetValue("openai_base_url", out var baseUrl)
            || !string.Equals(baseUrl as string, _managedNativeBaseUrl, StringComparison.Ordinal)
            || !document.TryGetValue("model_catalog_json", out var catalog)
            || !string.Equals(
                NormalizePath(catalog as string),
                NormalizePath(ModelCatalogPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "总管家的网关或模型目录没有通过静态校验；本次拒绝写入。");
    }

    private void WriteAtomically(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var temp = _configPath + $".model-manager-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, _configPath, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static Regex MarkerRegex(string marker) => new(
        "(?m)^[ \\t]*" + Regex.Escape(marker) + "[ \\t]*\\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string NormalizePath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Path.GetFullPath(value.Replace('/', Path.DirectorySeparatorChar));

    private static string DetectNewLine(string source) =>
        source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string EscapeTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private readonly record struct RootAssignment(int Start, int Length, string Value);
    private readonly record struct ManagedBlock(int Start, int Length);
}

public sealed record CodexGatewaySnapshot(
    bool ConfigExists,
    bool CanToggle,
    bool IsManagedConnected,
    string SelectedProviderId,
    string CurrentGateway,
    string ManagedGateway,
    string RestoreProviderId,
    string RestoreGateway,
    string Detail);
