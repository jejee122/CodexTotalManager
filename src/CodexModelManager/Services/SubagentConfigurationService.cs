using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexModelManager.Models;

namespace CodexModelManager.Services;

public sealed class SubagentConfigurationService
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private const string ManagedAgentMarker = "# Managed by Codex Total Manager.";
    private static readonly Regex SafeNativeModel = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex SafeExternalModel = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}(?:/[A-Za-z0-9][A-Za-z0-9._:/-]{0,191})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SafeSourceId = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly string _configPath;
    private readonly string _agentsDirectory;
    private readonly string _dataPath;
    private readonly string _backupRoot;
    private readonly string _bridgeExecutablePath;
    private readonly bool _bridgePathTracksCurrentExecutable;
    private readonly string _bridgeStatePath;
    private readonly Func<CancellationToken, Task<string?>>? _applyBlockReason;
    private readonly ICodexConfigValidator _codexConfigValidator;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SubagentSourceDescriptor>>>? _sourceDiscovery;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly string _applyMutexName;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SubagentConfigurationService(
        string? configPath = null,
        string? agentsDirectory = null,
        string? dataPath = null,
        string? backupRoot = null,
        string? bridgeExecutablePath = null,
        string? bridgeStatePath = null,
        Func<CancellationToken, Task<string?>>? applyBlockReason = null,
        ICodexConfigValidator? codexConfigValidator = null,
        Func<CancellationToken, Task<IReadOnlyList<SubagentSourceDescriptor>>>? sourceDiscovery = null)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localData = AppSettingsService.ResolveDefaultDataDirectory();
        _configPath = configPath ?? Path.Combine(userProfile, ".codex", "config.toml");
        _agentsDirectory = agentsDirectory ?? Path.Combine(userProfile, ".codex", "agents");
        _dataPath = dataPath ?? Path.Combine(localData, "subagents.json");
        _backupRoot = backupRoot ?? Path.Combine(localData, "backups", "subagents");
        _bridgePathTracksCurrentExecutable = string.IsNullOrWhiteSpace(bridgeExecutablePath);
        _bridgeExecutablePath = ResolveBridgeExecutablePath(bridgeExecutablePath);
        _bridgeStatePath = bridgeStatePath ?? Path.Combine(localData, "external-worker-state.json");
        _applyBlockReason = applyBlockReason;
        _codexConfigValidator = codexConfigValidator
                                 ?? new CodexCliConfigurationValidator(Path.GetDirectoryName(_dataPath));
        _sourceDiscovery = sourceDiscovery;
        var mutexId = Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(Path.GetFullPath(_configPath))))[..24];
        _applyMutexName = $@"Local\CodexModelManager.Subagents.{mutexId}";
    }

    public string? LoadWarning { get; private set; }
    public string ConfigPath => _configPath;
    public string AgentsDirectory => _agentsDirectory;
    public string DataPath => _dataPath;
    public string BackupRoot => _backupRoot;
    public string BridgeExecutablePath => CurrentBridgeExecutablePath();
    public string BridgeStatePath => _bridgeStatePath;

    public IReadOnlyList<SubagentRoleDefinition> Roles { get; } = new[]
    {
        new SubagentRoleDefinition(
            "cmm_supervisor", "总监督", "拆解任务、检查风险、汇总子代理结论；不直接改文件。",
            "gpt-5.6-sol", "max", "read-only", false,
            "你是总监督。负责拆解目标、分配可独立工作的任务、核对证据和测试结果，并向主代理返回简洁结论。不要直接修改文件；发现高风险写操作或证据不足时明确阻止。"),
        new SubagentRoleDefinition(
            "cmm_explorer", "代码探查", "搜索源码、梳理调用链、定位文件；默认只读。",
            "gpt-5.6-terra", "medium", "read-only", true,
            "只做代码库探查。优先使用快速搜索和定点读取，给出准确文件、符号和调用链；不要修改文件，也不要把猜测写成事实。",
            PricePerMillionTokens: 2m, Currency: "USD", BudgetLimit: 50m, MaxTimeoutSeconds: 300),
        new SubagentRoleDefinition(
            "cmm_implementer", "实现工人", "在范围明确后完成小而可验证的代码修改。",
            "gpt-5.6-terra", "medium", "workspace-write", true,
            "只实现主代理明确交付的改动。保持修改最小，保留用户已有改动，不碰范围外文件；完成后运行与改动直接相关的验证并如实报告。",
            PricePerMillionTokens: 2m, Currency: "USD", BudgetLimit: 100m, MaxTimeoutSeconds: 600),
        new SubagentRoleDefinition(
            "cmm_tester", "测试验证", "运行测试、复现问题、检查结果；只在工作区内写测试产物。",
            "gpt-5.6-terra", "medium", "workspace-write", true,
            "负责复现和验证。先记录基线，再运行最小相关测试；允许在工作区生成测试产物，但不要修改真实配置、凭据、服务器或用户数据。",
            PricePerMillionTokens: 2m, Currency: "USD", BudgetLimit: 100m, MaxTimeoutSeconds: 600),
        new SubagentRoleDefinition(
            "cmm_reviewer", "质量审查", "检查缺陷、回归、安全风险和缺失测试；默认只读。",
            "gpt-5.6-sol", "high", "read-only", true,
            "像代码所有者一样审查。优先报告真实缺陷、行为回归、安全风险和缺失测试，附复现或证据；不要为了风格而制造问题，也不要修改文件。",
            PricePerMillionTokens: 3m, Currency: "USD", BudgetLimit: 50m, MaxTimeoutSeconds: 300),
        new SubagentRoleDefinition(
            "cmm_documenter", "文档整理", "归纳说明、变更摘要和操作清单；默认只读。",
            "gpt-5.6-terra", "low", "read-only", true,
            "整理已有事实为清晰文档、变更摘要或操作清单。保留事实与计划的边界，不编造已验证状态，不修改源码或真实配置。",
            PricePerMillionTokens: 2m, Currency: "USD", BudgetLimit: 30m, MaxTimeoutSeconds: 300)
    };

    public async Task<CodexConfigValidationResult> ValidateCurrentConfigAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
        {
            return new CodexConfigValidationResult(
                false,
                false,
                "找不到 Codex 主配置；应用已锁定。");
        }

        try
        {
            var configBytes = await File.ReadAllBytesAsync(_configPath, cancellationToken);
            var agentFiles = ReadAllAgentFiles();
            return await _codexConfigValidator.ValidateAsync(configBytes, agentFiles, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or SecurityException
                                   or NotSupportedException)
        {
            return new CodexConfigValidationResult(
                false,
                false,
                "无法读取完整的 Codex 配置进行自身解析器检查；应用已锁定。");
        }
    }

    public SubagentConfigurationDocument LoadDraft()
    {
        LoadWarning = null;
        if (!File.Exists(_dataPath)) return CreateDefaultDocument();
        try
        {
            var document = JsonSerializer.Deserialize<SubagentConfigurationDocument>(
                File.ReadAllText(_dataPath, Encoding.UTF8), _jsonOptions);
            if (document is null) throw new InvalidDataException("配置内容为空。");
            if (document.SchemaVersion > 3)
                throw new InvalidDataException(
                    $"子代理草稿版本 {document.SchemaVersion} 高于当前支持的 3；已进入只读保护，防止降级覆盖未知字段。");
            return NormalizeDocument(document);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LoadWarning = $"子代理草稿读取失败，已使用安全默认值：{ex.Message}";
            return CreateDefaultDocument();
        }
    }

    public SubagentConfigurationSnapshot Inspect()
    {
        var draft = LoadDraft();
        var readable = File.Exists(_configPath);
        var encodingValid = true;
        var configText = string.Empty;
        if (readable)
        {
            try { configText = Utf8NoBom.GetString(File.ReadAllBytes(_configPath)); }
            catch (DecoderFallbackException) { encodingValid = false; }
        }
        var safety = encodingValid
            ? ManagedTomlBlockEditor.InspectCodexSafetySettings(configText)
            : new CodexTomlSafetyInspection(false, false, false);
        var safe = readable && encodingValid && safety.SyntaxValid && !safety.UnsafeCustomProvider;
        var agentsEnabled = readable && encodingValid && safety.SyntaxValid && !safety.AgentsExplicitlyDisabled;
        var bridge = InspectBridge(configText, draft);
        var configWarning = !encodingValid
            ? "Codex 主配置不是有效 UTF-8，已进入只读保护。"
            : !safety.SyntaxValid
                ? "Codex 主配置的 TOML 结构不完整或无法安全解析，已进入只读保护。"
                : null;
        if (configWarning is not null)
            bridge = bridge with { HasConflict = true, ConfigurationExact = false, StatusText = configWarning };
        var applied = new Dictionary<string, SubagentAppliedRoleState>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in Roles)
        {
            var selection = draft.Roles.First(item => item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase));
            if (selection.WorkerKind == SubagentWorkerKind.External)
            {
                applied[role.Id] = new SubagentAppliedRoleState(
                    role.Id,
                    selection.ModelId,
                    bridge.ConfigurationExact,
                    bridge.ConfigurationExact
                        ? "MCP 已写入磁盘 · 新任务后可调用"
                        : bridge.HasConflict ? "MCP 配置冲突 · 已停止自动修改" : "草稿已保存 · MCP 尚未应用");
                continue;
            }

            var path = ManagedAgentPath(role.Id);
            if (!File.Exists(path))
            {
                applied[role.Id] = new SubagentAppliedRoleState(role.Id, null, false, "尚未写入 Codex");
                continue;
            }

            try
            {
                var fields = ReadAgentFields(File.ReadAllText(path, Encoding.UTF8));
                var exact = fields.TryGetValue("name", out var name)
                            && name.Equals(role.Id, StringComparison.Ordinal)
                            && fields.TryGetValue("model", out var model)
                            && model.Equals(selection.ModelId, StringComparison.OrdinalIgnoreCase)
                            && fields.TryGetValue("model_reasoning_effort", out var effort)
                            && effort.Equals(role.ReasoningEffort, StringComparison.OrdinalIgnoreCase)
                            && fields.TryGetValue("sandbox_mode", out var sandbox)
                            && sandbox.Equals(role.SandboxMode, StringComparison.OrdinalIgnoreCase)
                            && fields.ContainsKey("description")
                            && fields.ContainsKey("developer_instructions");
                applied[role.Id] = new SubagentAppliedRoleState(
                    role.Id,
                    fields.GetValueOrDefault("model"),
                    exact,
                    exact ? "已写入磁盘 · 新子代理任务后生效" : "磁盘配置与草稿不一致");
            }
            catch (Exception ex)
            {
                applied[role.Id] = new SubagentAppliedRoleState(role.Id, null, false, $"配置无法验证：{ex.Message}");
            }
        }

        var exactCount = applied.Values.Count(item => item.ExactMatch);
        var externalCount = draft.Roles.Count(item => item.WorkerKind == SubagentWorkerKind.External);
        var summary = !readable
            ? "找不到 Codex 主配置，已禁止应用。"
            : !encodingValid || !safety.SyntaxValid
                ? configWarning!
                : safety.UnsafeCustomProvider
                ? "检测到旧 custom provider，已进入只读保护。"
                : !agentsEnabled
                    ? "Codex 已明确关闭子代理，已禁止应用。"
                    : bridge.HasConflict
                        ? "检测到总管家 MCP 区块冲突，已进入只读保护。"
                    : $"{exactCount} 个角色与磁盘一致；{externalCount} 个角色使用外部纯文本工人。";
        var baselineRevision = ComputeBaselineRevision();
        return new SubagentConfigurationSnapshot(
            readable, safe, agentsEnabled, _configPath, _agentsDirectory, draft, applied,
            bridge, baselineRevision, summary,
            string.Join(" ", new[] { LoadWarning, configWarning }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    public SubagentConfigurationSnapshot InspectDraftOnly()
    {
        var draft = LoadDraft();
        var applied = Roles.ToDictionary(
            role => role.Id,
            role => new SubagentAppliedRoleState(
                role.Id,
                null,
                false,
                "Codex 未连接 · 只显示总管家草稿"),
            StringComparer.OrdinalIgnoreCase);
        var bridge = new SubagentBridgeStatus(
            false,
            false,
            false,
            "Codex 未连接；没有读取 MCP 区块或 Codex Agent 文件。",
            null, null, null, null, null, null, null, null, null, null, null);
        return new SubagentConfigurationSnapshot(
            false,
            false,
            false,
            "Codex 未连接（路径未读取）",
            "Codex 未连接（目录未读取）",
            draft,
            applied,
            bridge,
            ComputeDraftOnlyBaselineRevision(),
            "总管家草稿可查看；Codex 配置、Agent 文件和任务状态保持隔离。",
            LoadWarning);
    }

    private string ComputeDraftOnlyBaselineRevision()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddFileRevision(hash, "draft", _dataPath);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public SubagentApplyPlan CreatePlan(
        IEnumerable<SubagentRoleSelection> selections,
        IReadOnlyCollection<string> availableNativeModels,
        IReadOnlyCollection<string> availableExternalModels)
    {
        var legacy = CreateLegacyExternalContext(availableExternalModels);
        var legacySelections = selections.Select(CloneSelection).ToArray();
        return CreatePlan(
            legacySelections,
            legacy.Authorizations,
            availableNativeModels,
            legacy.Sources);
    }

    public SubagentApplyPlan CreatePlan(
        IEnumerable<SubagentRoleSelection> selections,
        IEnumerable<SubagentSourceAuthorization> sourceAuthorizations,
        IReadOnlyCollection<string> availableNativeModels,
        IReadOnlyCollection<SubagentSourceDescriptor> discoveredSources)
    {
        var normalized = selections.Select(CloneSelection).ToList();
        var authorizations = sourceAuthorizations.Select(CloneAuthorization).ToList();
        var issues = new List<string>();
        var nativeSet = availableNativeModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourcesById = discoveredSources
            .GroupBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicate in authorizations
                     .GroupBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() != 1))
            issues.Add($"来源授权“{duplicate.Key}”重复，必须且只能保存一次。");
        foreach (var authorization in authorizations.Where(item => item.Enabled))
        {
            if (!SafeSourceId.IsMatch(authorization.SourceId) || !IsSha256(authorization.ExpectedFingerprint))
            {
                issues.Add($"来源授权“{authorization.SourceId}”的 ID 或身份指纹格式不安全。");
                continue;
            }
            if (!sourcesById.TryGetValue(authorization.SourceId, out var matches) || matches.Length != 1)
            {
                issues.Add($"已授权来源“{authorization.AuthorizedDisplayName}”当前已移除或发现结果不唯一，请撤销或重新核对。");
                continue;
            }
            var source = matches[0];
            if (!SubagentSourceIdentity.FixedTimeEquals(
                    authorization.ExpectedFingerprint, source.Fingerprint))
                issues.Add($"来源“{source.DisplayName}”的端点、provider、凭据槽或适配器已变化，必须重新授权。");
            if (!source.Enabled)
                issues.Add($"已授权来源“{source.DisplayName}”当前已停用。");
            if (!source.SupportsTextWorker)
                issues.Add($"来源“{source.DisplayName}”当前不支持安全的纯文本子代理执行。");
        }

        foreach (var role in Roles)
        {
            var matches = normalized.Where(item => item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                issues.Add($"角色“{role.DisplayName}”必须且只能配置一次。");
                continue;
            }

            var selected = matches[0];
            if (selected.WorkerKind == SubagentWorkerKind.CodexNative)
            {
                if (!SafeNativeModel.IsMatch(selected.ModelId) || !nativeSet.Contains(selected.ModelId))
                    issues.Add($"角色“{role.DisplayName}”选择的 Codex 模型当前不可用：{selected.ModelId}");
            }
            else
            {
                if (!role.AllowsExternalWorker)
                    issues.Add($"角色“{role.DisplayName}”必须使用 Codex 原生模型。");
                if (!SafeExternalModel.IsMatch(selected.ModelId))
                    issues.Add($"角色“{role.DisplayName}”的外部模型路由格式不安全。");
                if (string.IsNullOrWhiteSpace(selected.SourceId) || !SafeSourceId.IsMatch(selected.SourceId))
                {
                    issues.Add($"角色“{role.DisplayName}”没有选择有效的外部来源。");
                    continue;
                }
                var grants = authorizations.Where(item => item.Enabled
                    && item.SourceId.Equals(selected.SourceId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (grants.Length != 1)
                {
                    issues.Add($"角色“{role.DisplayName}”选择的来源尚未明确授权。");
                    continue;
                }
                if (!sourcesById.TryGetValue(selected.SourceId, out var sourceMatches)
                    || sourceMatches.Length != 1)
                {
                    issues.Add($"角色“{role.DisplayName}”选择的来源当前不存在或不唯一。");
                    continue;
                }
                var source = sourceMatches[0];
                if (!SubagentSourceIdentity.FixedTimeEquals(grants[0].ExpectedFingerprint, source.Fingerprint))
                    issues.Add($"角色“{role.DisplayName}”选择的来源身份已变化。");
                if (!source.Ready)
                    issues.Add($"角色“{role.DisplayName}”选择的来源当前不可执行：{source.StatusText}");
                else if (!source.Models.Contains(selected.ModelId, StringComparer.OrdinalIgnoreCase))
                    issues.Add($"角色“{role.DisplayName}”选择的模型不属于当前获准来源：{selected.ModelId}");
            }
        }

        var knownRoleIds = Roles.Select(role => role.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unknown in normalized.Where(item => !knownRoleIds.Contains(item.RoleId)))
            issues.Add($"发现未知角色：{unknown.RoleId}");

        var nativeCount = normalized.Count(item => item.WorkerKind == SubagentWorkerKind.CodexNative);
        var externalCount = normalized.Count(item => item.WorkerKind == SubagentWorkerKind.External);
        var bridgeExecutablePath = CurrentBridgeExecutablePath();
        if (externalCount > 0
            && (!Path.IsPathFullyQualified(bridgeExecutablePath) || !File.Exists(bridgeExecutablePath)))
            issues.Add("总管家外部纯文本 MCP 可执行文件不存在或不是绝对路径，不能启用桥接。");
        var summary = issues.Count > 0
            ? $"发现 {issues.Count} 个问题，尚未写入任何文件。"
            : $"将写入 {nativeCount} 个 Codex 原生角色；{externalCount} 个外部角色将通过纯文本 MCP 桥接；不会立即调用模型。";
        return new SubagentApplyPlan(issues.Count == 0, nativeCount, externalCount, issues, summary);
    }

    public SubagentApplyResult Apply(
        IEnumerable<SubagentRoleSelection> selections,
        IReadOnlyCollection<string> availableNativeModels,
        IReadOnlyCollection<string> availableExternalModels,
        string expectedBaselineRevision) =>
        ApplyAsync(selections, availableNativeModels, availableExternalModels, expectedBaselineRevision)
            .GetAwaiter().GetResult();

    public SubagentApplyResult Apply(
        IEnumerable<SubagentRoleSelection> selections,
        IEnumerable<SubagentSourceAuthorization> sourceAuthorizations,
        IReadOnlyCollection<string> availableNativeModels,
        IReadOnlyCollection<SubagentSourceDescriptor> discoveredSources,
        string expectedBaselineRevision) =>
        ApplyAsync(
                selections,
                sourceAuthorizations,
                availableNativeModels,
                discoveredSources,
                expectedBaselineRevision)
            .GetAwaiter().GetResult();

    public Task<SubagentApplyResult> ApplyAsync(
        IEnumerable<SubagentRoleSelection> selections,
        IReadOnlyCollection<string> availableNativeModels,
        IReadOnlyCollection<string> availableExternalModels,
        string expectedBaselineRevision,
        CancellationToken cancellationToken = default)
    {
        var legacy = CreateLegacyExternalContext(availableExternalModels);
        var legacySelections = selections.Select(CloneSelection).ToArray();
        return ApplyAsync(
            legacySelections,
            legacy.Authorizations,
            availableNativeModels,
            legacy.Sources,
            expectedBaselineRevision,
            cancellationToken);
    }

    public async Task<SubagentApplyResult> ApplyAsync(
        IEnumerable<SubagentRoleSelection> selections,
        IEnumerable<SubagentSourceAuthorization> sourceAuthorizations,
        IReadOnlyCollection<string> availableNativeModels,
        IReadOnlyCollection<SubagentSourceDescriptor> discoveredSources,
        string expectedBaselineRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedBaselineRevision))
            throw new InvalidOperationException("缺少配置基线，请先重新读取后再应用。");

        await _applyGate.WaitAsync(cancellationToken);
        using var processMutex = new Mutex(false, _applyMutexName);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = processMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new InvalidOperationException("另一个总管家进程正在应用子代理配置，请稍后重试。");

            await EnsureApplyAllowedAsync(cancellationToken);
            var normalizedSelections = selections.Select(CloneSelection).ToList();
            var normalizedAuthorizations = sourceAuthorizations.Select(CloneAuthorization).ToList();
            var currentSources = _sourceDiscovery is null
                ? discoveredSources
                : await _sourceDiscovery(cancellationToken);
            var plan = CreatePlan(
                normalizedSelections,
                normalizedAuthorizations,
                availableNativeModels,
                currentSources);
            if (!plan.CanApply) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Issues));
            if (!File.Exists(_configPath)) throw new FileNotFoundException("找不到 Codex 主配置，已禁止应用。", _configPath);

            var previousDraft = LoadDraft();
            if (!string.IsNullOrWhiteSpace(LoadWarning))
                throw new InvalidOperationException($"{LoadWarning} 为避免覆盖损坏草稿，本次保持只读。");
            if (!ComputeBaselineRevision().Equals(expectedBaselineRevision, StringComparison.OrdinalIgnoreCase))
                throw new IOException("配置、草稿或托管 Agent 在预览后发生变化，请重新读取后再应用。");

            var originalConfig = File.ReadAllBytes(_configPath);
            string configText;
            try { configText = Utf8NoBom.GetString(originalConfig); }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Codex 主配置不是有效 UTF-8，本次没有修改任何文件。", ex);
            }
            var safety = ManagedTomlBlockEditor.InspectCodexSafetySettings(configText);
            if (!safety.SyntaxValid)
                throw new InvalidDataException("Codex 主配置的 TOML 结构不完整或无法安全解析，本次没有修改任何文件。");
            if (safety.UnsafeCustomProvider)
                throw new InvalidOperationException("检测到旧的 custom provider。为防止 Codex 无法启动，本次没有修改。");
            if (safety.AgentsExplicitlyDisabled)
                throw new InvalidOperationException("Codex 主配置已设置 agents.enabled = false。请先确认是否要启用子代理。");

            var currentBridge = ManagedTomlBlockEditor.Inspect(configText, previousDraft.ManagedMcpBlockHash);
            if (currentBridge.HasManagedBlock && string.IsNullOrWhiteSpace(previousDraft.ManagedMcpBlockHash))
                throw new InvalidOperationException("发现没有事务记录的总管家 MCP 区块，已拒绝自动接管。");
            if (currentBridge.Conflict)
                throw new InvalidOperationException($"总管家 MCP 区块冲突：{currentBridge.StatusText}");

            var externalCount = normalizedSelections.Count(item => item.WorkerKind == SubagentWorkerKind.External);
            var configEdit = externalCount > 0
                ? ManagedTomlBlockEditor.Upsert(configText, BuildMcpTomlBody(), previousDraft.ManagedMcpBlockHash)
                : ManagedTomlBlockEditor.Remove(configText, previousDraft.ManagedMcpBlockHash);
            if (!configEdit.CanWrite || configEdit.CandidateText is null)
                throw new InvalidOperationException($"无法生成安全的 MCP 配置：{configEdit.StatusText}");
            var candidateConfig = Utf8NoBom.GetBytes(configEdit.CandidateText);

            var originalDraft = File.Exists(_dataPath) ? File.ReadAllBytes(_dataPath) : null;
            var originalAgentFiles = ReadAllAgentFiles();
            var managedOriginals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in Roles)
            {
                var path = ManagedAgentPath(role.Id);
                var bytes = originalAgentFiles.GetValueOrDefault(Path.GetFileName(path));
                ValidateManagedAgentOwnership(role, bytes, previousDraft);
                managedOriginals[path] = bytes;
            }

            var now = DateTimeOffset.Now;
            var intendedAgentBytes = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in Roles)
            {
                var selection = normalizedSelections.Single(item => item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase));
                var target = ManagedAgentPath(role.Id);
                if (selection.WorkerKind == SubagentWorkerKind.CodexNative)
                {
                    var content = BuildAgentToml(role, selection.ModelId);
                    ValidateGeneratedAgent(content, role, selection.ModelId);
                    var bytes = Utf8NoBom.GetBytes(content);
                    intendedAgentBytes[target] = bytes;
                }
                else
                {
                    intendedAgentBytes[target] = null;
                }
            }

            var candidateAgentFiles = new Dictionary<string, byte[]>(
                originalAgentFiles,
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in intendedAgentBytes)
            {
                var fileName = Path.GetFileName(pair.Key);
                if (pair.Value is null) candidateAgentFiles.Remove(fileName);
                else candidateAgentFiles[fileName] = pair.Value;
            }
            var codexValidation = await _codexConfigValidator.ValidateAsync(
                candidateConfig,
                candidateAgentFiles,
                cancellationToken);
            if (!codexValidation.ValidatorAvailable || !codexValidation.IsValid)
                throw new InvalidOperationException(codexValidation.StatusText);

            await EnsureApplyAllowedAsync(cancellationToken);
            if (_sourceDiscovery is not null)
            {
                currentSources = await _sourceDiscovery(cancellationToken);
                var refreshedPlan = CreatePlan(
                    normalizedSelections,
                    normalizedAuthorizations,
                    availableNativeModels,
                    currentSources);
                if (!refreshedPlan.CanApply)
                    throw new InvalidOperationException(
                        "来源在应用期间发生变化：" + string.Join("；", refreshedPlan.Issues));
            }
            if (!ComputeBaselineRevision().Equals(expectedBaselineRevision, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Codex 自身解析器检查期间配置发生变化；本次没有修改任何托管文件。");

            Directory.CreateDirectory(_agentsDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            Directory.CreateDirectory(_backupRoot);
            var transactionId = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
            var backupDirectory = Path.Combine(_backupRoot, "subagents-" + transactionId);
            Directory.CreateDirectory(backupDirectory);

            File.WriteAllBytes(Path.Combine(backupDirectory, "config.toml"), originalConfig);
            if (originalDraft is not null) File.WriteAllBytes(Path.Combine(backupDirectory, "subagents.json"), originalDraft);
            foreach (var pair in managedOriginals.Where(pair => pair.Value is not null))
                File.WriteAllBytes(Path.Combine(backupDirectory, Path.GetFileName(pair.Key)), pair.Value!);

            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var document = new SubagentConfigurationDocument
            {
                SchemaVersion = 3,
                SavedAt = now,
                LastAppliedAt = now,
                Roles = normalizedSelections,
                SourceAuthorizations = normalizedAuthorizations,
                ManagedMcpBlockHash = configEdit.CandidateManagedBlockSha256,
                ManagedAgentHashes = intendedAgentBytes
                    .Where(pair => pair.Value is not null)
                    .ToDictionary(
                        pair => Path.GetFileNameWithoutExtension(pair.Key),
                        pair => HashBytes(pair.Value!),
                        StringComparer.OrdinalIgnoreCase)
            };
            var intendedDraft = Utf8NoBom.GetBytes(JsonSerializer.Serialize(document, _jsonOptions));
            var committed = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var pair in intendedAgentBytes.Where(pair => pair.Value is not null))
                {
                    var temp = pair.Key + $".{Guid.NewGuid():N}.tmp";
                    File.WriteAllBytes(temp, pair.Value!);
                    staged[pair.Key] = temp;
                }

                await EnsureApplyAllowedAsync(cancellationToken);
                if (!ComputeBaselineRevision().Equals(expectedBaselineRevision, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("提交前检测到配置并发变化，尚未写入任何托管文件。");

                foreach (var role in Roles)
                {
                    var target = ManagedAgentPath(role.Id);
                    var intended = intendedAgentBytes[target];
                    EnsureFileStateUnchanged(
                        target,
                        managedOriginals[target],
                        $"{role.DisplayName} 的托管 Agent 在提交期间被其他程序修改");
                    if (intended is null)
                    {
                        if (File.Exists(target)) File.Delete(target);
                    }
                    else
                    {
                        File.Move(staged[target], target, true);
                        staged.Remove(target);
                    }
                    committed[target] = intended;
                }

                EnsureFileStateUnchanged(
                    _dataPath,
                    originalDraft,
                    "子代理草稿在提交期间被其他程序修改");
                WriteBytesAtomically(_dataPath, intendedDraft);
                committed[_dataPath] = intendedDraft;
                EnsureUnmanagedAgentFilesUnchanged(originalAgentFiles);
                EnsureFileStateUnchanged(
                    _configPath,
                    originalConfig,
                    "Codex 主配置在提交期间被其他程序修改");
                if (!originalConfig.AsSpan().SequenceEqual(candidateConfig))
                    WriteBytesAtomically(_configPath, candidateConfig);
                committed[_configPath] = candidateConfig;

                ValidateCommittedState(document, candidateConfig, intendedAgentBytes);
                var manifest = new
                {
                    transactionId,
                    createdAt = now,
                    managerVersion = typeof(SubagentConfigurationService).Assembly.GetName().Version?.ToString(),
                    preConfigHash = HashBytes(originalConfig),
                    postConfigHash = HashBytes(candidateConfig),
                    preDraftHash = originalDraft is null ? null : HashBytes(originalDraft),
                    postDraftHash = HashBytes(intendedDraft),
                    managedMcpBlockHash = document.ManagedMcpBlockHash,
                    nativeRoles = plan.NativeRoleCount,
                    externalTextRoles = plan.ExternalPendingCount,
                    authorizedSources = document.SourceAuthorizations.Count(item => item.Enabled),
                    files = Roles.Select(role =>
                    {
                        var path = ManagedAgentPath(role.Id);
                        return new
                        {
                            role = role.Id,
                            existedBefore = managedOriginals[path] is not null,
                            preHash = managedOriginals[path] is null ? null : HashBytes(managedOriginals[path]!),
                            postHash = intendedAgentBytes[path] is null ? null : HashBytes(intendedAgentBytes[path]!)
                        };
                    }).ToArray()
                };
                File.WriteAllText(Path.Combine(backupDirectory, "manifest.json"),
                    JsonSerializer.Serialize(manifest, _jsonOptions), Utf8NoBom);
                return new SubagentApplyResult(
                    backupDirectory,
                    plan.NativeRoleCount,
                    plan.ExternalPendingCount,
                    now,
                    $"已写入并校验 {plan.NativeRoleCount} 个 Codex 原生角色、{plan.ExternalPendingCount} 个外部纯文本工人和 {document.SourceAuthorizations.Count(item => item.Enabled)} 个来源授权。没有立即调用模型；当前任务未切换，Codex 需在新任务或重启后加载 MCP。");
            }
            catch (Exception applyError)
            {
                var rollbackErrors = RollbackCommittedFiles(
                    committed,
                    originalConfig,
                    originalDraft,
                    managedOriginals);
                if (rollbackErrors.Count > 0)
                {
                    var recovery = new
                    {
                        transactionId,
                        originalError = applyError.Message,
                        rollbackErrors,
                        backupDirectory
                    };
                    try
                    {
                        File.WriteAllText(Path.Combine(backupDirectory, "recovery-needed.json"),
                            JsonSerializer.Serialize(recovery, _jsonOptions), Utf8NoBom);
                    }
                    catch { }
                    throw new InvalidOperationException(
                        $"应用失败且回滚不完整：{applyError.Message}；恢复问题：{string.Join("；", rollbackErrors)}；备份：{backupDirectory}",
                        applyError);
                }
                throw;
            }
            finally
            {
                foreach (var temp in staged.Values)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
        }
        finally
        {
            if (ownsMutex)
            {
                try { processMutex.ReleaseMutex(); } catch { }
            }
            _applyGate.Release();
        }
    }

    public string ComputeBaselineRevision()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddFileRevision(hash, "config", _configPath);
        AddFileRevision(hash, "draft", _dataPath);
        AddAgentDirectoryRevision(hash);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task EnsureApplyAllowedAsync(CancellationToken cancellationToken)
    {
        if (_applyBlockReason is null) return;
        var reason = await _applyBlockReason(cancellationToken);
        if (!string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException(reason);
    }

    private SubagentBridgeStatus InspectBridge(string configText, SubagentConfigurationDocument draft)
    {
        var inspection = ManagedTomlBlockEditor.Inspect(configText, draft.ManagedMcpBlockHash);
        var externalCount = draft.Roles.Count(item => item.WorkerKind == SubagentWorkerKind.External);
        var exact = false;
        var statusText = inspection.StatusText;
        if (!inspection.Conflict)
        {
            if (externalCount == 0)
            {
                exact = !inspection.HasManagedBlock;
                statusText = exact ? "未启用外部纯文本角色，MCP 区块未安装。" : "存在不再需要的 MCP 区块，等待安全移除。";
            }
            else
            {
                var desired = ManagedTomlBlockEditor.Upsert(configText, BuildMcpTomlBody(), draft.ManagedMcpBlockHash);
                exact = desired.Status == ManagedTomlEditStatus.AlreadyDesired;
                statusText = exact
                    ? "总管家外部纯文本 MCP 已精确写入磁盘。"
                    : "外部角色已保存，但 MCP 区块尚未精确应用。";
            }
        }

        var runtime = ReadRuntimeState();
        return new SubagentBridgeStatus(
            inspection.HasManagedBlock,
            exact,
            inspection.Conflict,
            statusText,
            runtime.LastHandshakeAt,
            runtime.LastHandshakeClient,
            runtime.LastCallAt,
            runtime.LastCallSucceeded,
            runtime.LastRoleId,
            runtime.LastRequestedModel,
            runtime.LastResolvedModel,
            runtime.LastHttpStatus,
            runtime.InputTokens,
            runtime.OutputTokens,
            runtime.LastError,
            runtime.LastAccountSource);
    }

    private ExternalWorkerRuntimeState ReadRuntimeState()
    {
        if (!File.Exists(_bridgeStatePath))
            return new ExternalWorkerRuntimeState(null, null, null, null, null, null, null, null, null, null, null, null);
        try
        {
            using var stream = new FileStream(
                _bridgeStatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ExternalWorkerRuntimeState>(stream, _jsonOptions)
                   ?? new ExternalWorkerRuntimeState(null, null, null, null, null, null, null, null, null, null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ExternalWorkerRuntimeState(
                null, null, null, null, null, null, null, null, null, null, null,
                $"runtime_state_unreadable:{ex.GetType().Name}");
        }
    }

    private string BuildMcpTomlBody()
    {
        var bridgeExecutablePath = CurrentBridgeExecutablePath();
        if (!Path.IsPathFullyQualified(bridgeExecutablePath) || !File.Exists(bridgeExecutablePath))
            throw new InvalidOperationException("总管家外部纯文本 MCP 可执行文件不存在或路径不安全。");
        var builder = new StringBuilder();
        builder.AppendLine(ManagedTomlBlockEditor.TargetTableHeader);
        builder.Append("command = ").AppendLine(TomlString(bridgeExecutablePath));
        builder.AppendLine("args = [\"--external-worker-mcp\"]");
        builder.AppendLine("enabled = true");
        builder.AppendLine("required = false");
        builder.AppendLine("enabled_tools = [\"delegate_to_worker\"]");
        builder.AppendLine("default_tools_approval_mode = \"prompt\"");
        builder.AppendLine("startup_timeout_sec = 20");
        builder.Append("tool_timeout_sec = 300");
        return builder.ToString();
    }

    private string CurrentBridgeExecutablePath()
    {
        if (!_bridgePathTracksCurrentExecutable) return _bridgeExecutablePath;
        var current = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(current) ? _bridgeExecutablePath : Path.GetFullPath(current);
    }

    private static string ResolveBridgeExecutablePath(string? configuredPath)
    {
        var path = configuredPath ?? Environment.ProcessPath
                   ?? throw new InvalidOperationException("无法确定总管家可执行文件路径。");
        return Path.GetFullPath(path);
    }

    private void ValidateManagedAgentOwnership(
        SubagentRoleDefinition role,
        byte[]? currentBytes,
        SubagentConfigurationDocument previousDraft)
    {
        if (currentBytes is null) return;
        var content = Utf8NoBom.GetString(currentBytes);
        if (!content.StartsWith(ManagedAgentMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Path.GetFileName(ManagedAgentPath(role.Id))} 不是总管家拥有的文件，已拒绝覆盖。");

        var currentHash = HashBytes(currentBytes);
        if (previousDraft.ManagedAgentHashes.TryGetValue(role.Id, out var expectedHash))
        {
            if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{role.DisplayName} 的托管 Agent 已被手工修改，已拒绝覆盖。");
            return;
        }

        var previousSelection = previousDraft.Roles.Single(item => item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase));
        if (previousSelection.WorkerKind != SubagentWorkerKind.CodexNative)
            throw new InvalidOperationException($"{role.DisplayName} 存在未登记的同名托管文件，已拒绝自动接管。");
        var legacyExpected = Utf8NoBom.GetBytes(BuildAgentToml(role, previousSelection.ModelId));
        if (!currentBytes.AsSpan().SequenceEqual(legacyExpected))
            throw new InvalidOperationException($"{role.DisplayName} 的旧托管 Agent 内容与已保存草稿不一致，已拒绝覆盖。");
    }

    private void ValidateCommittedState(
        SubagentConfigurationDocument document,
        byte[] expectedConfig,
        IReadOnlyDictionary<string, byte[]?> expectedAgents)
    {
        var actualConfig = File.ReadAllBytes(_configPath);
        if (!actualConfig.AsSpan().SequenceEqual(expectedConfig))
            throw new IOException("Codex 主配置提交后校验不一致。");
        var configInspection = ManagedTomlBlockEditor.Inspect(
            Utf8NoBom.GetString(actualConfig), document.ManagedMcpBlockHash);
        if (configInspection.Conflict)
            throw new InvalidDataException($"Codex MCP 配置提交后无法验证：{configInspection.StatusText}");
        var externalCount = document.Roles.Count(item => item.WorkerKind == SubagentWorkerKind.External);
        if ((externalCount > 0) != configInspection.HasManagedBlock)
            throw new InvalidDataException("Codex MCP 区块的存在状态与角色计划不一致。");

        var actualDraft = JsonSerializer.Deserialize<SubagentConfigurationDocument>(
                              File.ReadAllText(_dataPath, Encoding.UTF8), _jsonOptions)
                          ?? throw new InvalidDataException("子代理草稿提交后无法读取。");
        if (!string.Equals(actualDraft.ManagedMcpBlockHash, document.ManagedMcpBlockHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("子代理草稿中的 MCP 哈希与提交结果不一致。");

        foreach (var role in Roles)
        {
            var path = ManagedAgentPath(role.Id);
            var expected = expectedAgents[path];
            if (expected is null)
            {
                if (File.Exists(path)) throw new InvalidDataException($"外部角色 {role.Id} 仍残留原生 Agent 文件。");
                continue;
            }
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"托管 Agent {role.Id} 提交后校验不一致。");
            var selection = document.Roles.Single(item => item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase));
            ValidateGeneratedAgent(Utf8NoBom.GetString(expected), role, selection.ModelId);
        }
    }

    private List<string> RollbackCommittedFiles(
        IReadOnlyDictionary<string, byte[]?> committed,
        byte[] originalConfig,
        byte[]? originalDraft,
        IReadOnlyDictionary<string, byte[]?> managedOriginals)
    {
        var errors = new List<string>();
        foreach (var pair in committed.Reverse())
        {
            try
            {
                var current = File.Exists(pair.Key) ? File.ReadAllBytes(pair.Key) : null;
                if (!FileStateEquals(current, pair.Value))
                {
                    errors.Add($"{pair.Key} 在失败后又被其他程序修改，未覆盖用户新改动。");
                    continue;
                }
                byte[]? original;
                if (pair.Key.Equals(_configPath, StringComparison.OrdinalIgnoreCase)) original = originalConfig;
                else if (pair.Key.Equals(_dataPath, StringComparison.OrdinalIgnoreCase)) original = originalDraft;
                else original = managedOriginals.GetValueOrDefault(pair.Key);

                if (original is null)
                {
                    if (File.Exists(pair.Key)) File.Delete(pair.Key);
                }
                else
                {
                    WriteBytesAtomically(pair.Key, original);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{pair.Key}：{ex.Message}");
            }
        }
        return errors;
    }

    private static bool FileStateEquals(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static void EnsureFileStateUnchanged(string path, byte[]? expected, string message)
    {
        var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (!FileStateEquals(current, expected))
            throw new IOException($"{message}；已停止提交且不会覆盖该外部修改。");
    }

    private void EnsureUnmanagedAgentFilesUnchanged(IReadOnlyDictionary<string, byte[]> originalAgentFiles)
    {
        var managedNames = Roles
            .Select(role => role.Id + ".toml")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = originalAgentFiles
            .Where(pair => !managedNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var current = ReadAllAgentFiles()
            .Where(pair => !managedNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (expected.Count != current.Count
            || expected.Any(pair => !current.TryGetValue(pair.Key, out var bytes)
                                    || !pair.Value.AsSpan().SequenceEqual(bytes)))
            throw new IOException("用户自有 Agent 配置在提交期间发生变化；已停止提交且不会覆盖该外部修改。");
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private IReadOnlyDictionary<string, byte[]> ReadAllAgentFiles()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_agentsDirectory)) return files;

        foreach (var path in Directory.EnumerateFiles(_agentsDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            if (!files.TryAdd(fileName, File.ReadAllBytes(path)))
                throw new IOException($"Agent 目录中存在大小写冲突的同名 TOML：{fileName}");
        }

        return files;
    }

    private void AddAgentDirectoryRevision(IncrementalHash hash)
    {
        var directoryPath = Path.GetFullPath(_agentsDirectory);
        hash.AppendData(Utf8NoBom.GetBytes($"agents-directory\0{directoryPath}\0"));
        // A missing Agent directory and an existing-but-empty directory represent
        // the same configuration. Apply may need to create the directory before
        // staging managed files, so directory existence itself must not invalidate
        // the compare-and-swap baseline; every top-level TOML byte is still hashed.
        if (!Directory.Exists(directoryPath)) return;

        foreach (var pair in ReadAllAgentFiles().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Utf8NoBom.GetBytes($"agent\0{pair.Key}\0"));
            hash.AppendData(SHA256.HashData(pair.Value));
        }
    }

    private static void AddFileRevision(IncrementalHash hash, string label, string path)
    {
        var header = Utf8NoBom.GetBytes($"{label}\0{Path.GetFullPath(path)}\0");
        hash.AppendData(header);
        if (!File.Exists(path))
        {
            hash.AppendData(new byte[] { 0 });
            return;
        }
        hash.AppendData(new byte[] { 1 });
        hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private SubagentConfigurationDocument CreateDefaultDocument() => new()
    {
        Roles = Roles.Select(role => new SubagentRoleSelection
        {
            RoleId = role.Id,
            WorkerKind = SubagentWorkerKind.CodexNative,
            ModelId = role.DefaultModel
        }).ToList()
    };

    private SubagentConfigurationDocument NormalizeDocument(SubagentConfigurationDocument document)
    {
        var sourceRoles = document.Roles ?? new List<SubagentRoleSelection>();
        var sourceAuthorizations = document.SourceAuthorizations ?? new List<SubagentSourceAuthorization>();
        if (sourceRoles.Any(item => item is null))
            throw new InvalidDataException("子代理草稿包含空角色项目。");
        if (sourceRoles.Any(item => !Enum.IsDefined(typeof(SubagentWorkerKind), item.WorkerKind)))
            throw new InvalidDataException("子代理草稿包含未知工人类型。");
        foreach (var role in Roles)
        {
            var count = sourceRoles.Count(item =>
                string.Equals(item.RoleId, role.Id, StringComparison.OrdinalIgnoreCase));
            if (count != 1)
                throw new InvalidDataException($"角色 {role.Id} 必须且只能保存一项配置，实际为 {count} 项。");
        }
        var normalized = new SubagentConfigurationDocument
        {
            SchemaVersion = 3,
            SavedAt = document.SavedAt,
            LastAppliedAt = document.LastAppliedAt,
            ManagedMcpBlockHash = IsSha256(document.ManagedMcpBlockHash) ? document.ManagedMcpBlockHash : null,
            ManagedAgentHashes = (document.ManagedAgentHashes ?? new Dictionary<string, string>())
                .Where(pair => Roles.Any(role => role.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))
                               && IsSha256(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
        normalized.SourceAuthorizations.AddRange(sourceAuthorizations
            .Where(item => item is not null)
            .Select(CloneAuthorization));
        foreach (var role in Roles)
        {
            var selection = sourceRoles.FirstOrDefault(item => item is not null
                                                               && string.Equals(item.RoleId, role.Id, StringComparison.OrdinalIgnoreCase));
            if (selection is null || string.IsNullOrWhiteSpace(selection.ModelId))
            {
                normalized.Roles.Add(new SubagentRoleSelection
                {
                    RoleId = role.Id,
                    WorkerKind = SubagentWorkerKind.CodexNative,
                    ModelId = role.DefaultModel
                });
                continue;
            }
            var cloned = CloneSelection(selection) with { RoleId = role.Id };
            if (cloned.WorkerKind == SubagentWorkerKind.CodexNative)
                cloned.SourceId = null;
            else if (document.SchemaVersion < 3
                     && string.IsNullOrWhiteSpace(cloned.SourceId))
                cloned = cloned with
                {
                    WorkerKind = SubagentWorkerKind.CodexNative,
                    ModelId = role.DefaultModel,
                    SourceId = null
                };
            normalized.Roles.Add(cloned);
        }
        return normalized;
    }

    private static SubagentRoleSelection CloneSelection(SubagentRoleSelection selection) => new()
    {
        RoleId = (selection.RoleId ?? string.Empty).Trim(),
        WorkerKind = selection.WorkerKind,
        ModelId = (selection.ModelId ?? string.Empty).Trim(),
        SourceId = string.IsNullOrWhiteSpace(selection.SourceId) ? null : selection.SourceId.Trim()
    };

    private static SubagentSourceAuthorization CloneAuthorization(SubagentSourceAuthorization authorization) => new()
    {
        SourceId = (authorization.SourceId ?? string.Empty).Trim(),
        ExpectedFingerprint = (authorization.ExpectedFingerprint ?? string.Empty).Trim().ToUpperInvariant(),
        Enabled = authorization.Enabled,
        AuthorizedAt = authorization.AuthorizedAt,
        AuthorizedDisplayName = (authorization.AuthorizedDisplayName ?? string.Empty).Trim(),
        AuthorizedEndpoint = (authorization.AuthorizedEndpoint ?? string.Empty).Trim(),
        AuthorizedAdapter = (authorization.AuthorizedAdapter ?? string.Empty).Trim(),
        AuthorizedRoutePrefix = (authorization.AuthorizedRoutePrefix ?? string.Empty).Trim(),
        AuthorizedCredentialScope = (authorization.AuthorizedCredentialScope ?? string.Empty).Trim()
    };

    private string ManagedAgentPath(string roleId)
    {
        var role = Roles.Single(item => item.Id.Equals(roleId, StringComparison.OrdinalIgnoreCase));
        return Path.Combine(_agentsDirectory, role.Id + ".toml");
    }

    private static string BuildAgentToml(SubagentRoleDefinition role, string model)
    {
        var builder = new StringBuilder();
        builder.AppendLine(ManagedAgentMarker + " Changes may be replaced by the next safe apply.");
        builder.Append("name = ").AppendLine(TomlString(role.Id));
        builder.Append("description = ").AppendLine(TomlString(role.Purpose));
        builder.Append("model = ").AppendLine(TomlString(model));
        builder.Append("model_reasoning_effort = ").AppendLine(TomlString(role.ReasoningEffort));
        builder.Append("sandbox_mode = ").AppendLine(TomlString(role.SandboxMode));
        builder.Append("developer_instructions = ").AppendLine(TomlString(role.DeveloperInstructions));
        return builder.ToString();
    }

    private static void ValidateGeneratedAgent(string content, SubagentRoleDefinition role, string model)
    {
        var fields = ReadAgentFields(content);
        foreach (var required in new[] { "name", "description", "developer_instructions", "model", "model_reasoning_effort", "sandbox_mode" })
            if (!fields.ContainsKey(required)) throw new InvalidDataException($"生成的代理配置缺少 {required}。");
        if (!fields["name"].Equals(role.Id, StringComparison.Ordinal)
            || !fields["model"].Equals(model, StringComparison.OrdinalIgnoreCase)
            || !fields["model_reasoning_effort"].Equals(role.ReasoningEffort, StringComparison.OrdinalIgnoreCase)
            || !fields["sandbox_mode"].Equals(role.SandboxMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("生成的代理配置与计划不一致。");
    }

    private static Dictionary<string, string> ReadAgentFields(string content)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(content,
                     "(?m)^\\s*(?<key>[A-Za-z0-9_]+)\\s*=\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"\\s*$"))
        {
            fields[match.Groups["key"].Value] = Regex.Unescape(match.Groups["value"].Value);
        }
        return fields;
    }

    private static bool ContainsUnsafeCustomProvider(string config) =>
        ManagedTomlBlockEditor.InspectCodexSafetySettings(config).UnsafeCustomProvider;

    private static bool AgentsExplicitlyDisabled(string config) =>
        ManagedTomlBlockEditor.InspectCodexSafetySettings(config).AgentsExplicitlyDisabled;

    private static string TomlString(string value) =>
        '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + '"';

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 64
        && value.All(Uri.IsHexDigit);

    private static LegacyExternalContext CreateLegacyExternalContext(
        IReadOnlyCollection<string> availableExternalModels)
    {
        // Compatibility overloads intentionally do not mint authorization. They
        // remain usable for all-native plans. External selections must use the
        // explicit discovery and authorization-aware API.
        return new LegacyExternalContext(
            Array.Empty<SubagentSourceAuthorization>(),
            Array.Empty<SubagentSourceDescriptor>());
    }

    private sealed record LegacyExternalContext(
        IReadOnlyList<SubagentSourceAuthorization> Authorizations,
        IReadOnlyList<SubagentSourceDescriptor> Sources);

    private static void WriteTextAtomically(string path, string content) =>
        WriteBytesAtomically(path, Utf8NoBom.GetBytes(content));

    private static void WriteBytesAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
