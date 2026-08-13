using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexModelManager.Models;
using CodexModelManager.Services;

namespace CodexModelManager.Views;

public partial class SubagentManagementView : UserControl
{
    private readonly ObservableCollection<SubagentRoleRow> _rows = new();
    private readonly ObservableCollection<SubagentSourceRow> _sourceRows = new();
    private AppServices? _services;
    private SubagentConfigurationSnapshot? _snapshot;
    private IReadOnlyList<string> _nativeModels = Array.Empty<string>();
    private IReadOnlyList<string> _externalModels = Array.Empty<string>();
    private IReadOnlyList<SubagentSourceDescriptor> _sources = Array.Empty<SubagentSourceDescriptor>();
    private List<SubagentSourceAuthorization> _draftAuthorizations = new();
    private bool _busy;
    private bool _loadedOnce;
    private bool _codexApplyAvailable;
    private string _codexApplyStatus = "Codex 状态尚未确认";
    private string _codexParserStatus = "Codex 自身解析器尚未检查";

    public SubagentManagementView()
    {
        InitializeComponent();
        SubagentRolesList.ItemsSource = _rows;
        WorkerSourcesList.ItemsSource = _sourceRows;
        Loaded += async (_, _) =>
        {
            if (_loadedOnce || _services is null) return;
            _loadedOnce = true;
            if (RuntimeMode.IsDetachedUi)
            {
                ResultText.Text = RuntimeMode.DetachedStatusText;
                return;
            }
            await RefreshAsync();
        };
    }

    public void Initialize(AppServices services) => _services = services;

    public async Task RefreshAsync(bool preserveDraft = false)
    {
        if (RuntimeMode.IsDetachedUi) return;
        if (_busy || _services is null) return;
        var preservedSelections = preserveDraft && _rows.Count > 0 ? CurrentSelections() : null;
        var preservedAuthorizations = preserveDraft && _sourceRows.Count > 0
            ? CurrentAuthorizations()
            : null;
        SetBusy(true, "正在只读核对 Codex 配置和本机模型目录…");
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(143, 183, 188));
        try
        {
            if (!_services.CodexConfig.IsManagedNativeProviderSelected())
            {
                await RefreshDisconnectedAsync(preservedSelections, preservedAuthorizations);
                return;
            }
            var desktopTask = _services.CodexDesktop.ReadStateAsync();
            var sourceTask = _services.SubagentSources.DiscoverAsync();
            _snapshot = _services.Subagents.Inspect();
            var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "gpt-5.6-sol",
                "gpt-5.6-terra"
            };
            try
            {
                var catalog = await _services.OpenCodex.GetModelsAsync(_services.Settings);
                foreach (var model in catalog.Where(item => item.IsOfficial && !item.Disabled))
                    native.Add(model.Id);
            }
            catch
            {
                // Keep the verified local runtime choices available if OpenCodex is offline.
            }

            _nativeModels = native
                .OrderBy(model => model.Equals("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase) ? 0
                    : model.Equals("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _sources = await sourceTask;
            var codexIdle = false;
            var idleStatus = "Codex 状态尚未确认 · 应用已锁定";
            try
            {
                var desktop = await desktopTask;
                codexIdle = desktop.Connected
                    ? !desktop.IsTurnRunning
                    : !IsCodexProcessRunning();
                idleStatus = desktop.Connected
                    ? desktop.IsTurnRunning ? "Codex 正在回答 · 应用已锁定" : "Codex 已确认空闲"
                    : codexIdle ? "Codex 未运行 · 可以安全写入" : "Codex 状态不可确认 · 应用已锁定";
            }
            catch
            {
                codexIdle = !IsCodexProcessRunning();
                idleStatus = codexIdle
                    ? "Codex 未运行 · 可以安全写入"
                    : "Codex 状态读取失败 · 应用已锁定";
            }

            // Run this after the process-idle check: the validator itself briefly starts codex.exe.
            var parser = await _services.Subagents.ValidateCurrentConfigAsync();
            var parserAccepted = parser.ValidatorAvailable && parser.IsValid;
            _codexParserStatus = parserAccepted
                ? "Codex 自身解析器已接受当前配置"
                : parser.ValidatorAvailable
                    ? "Codex 自身解析器拒绝当前配置 · 应用已锁定"
                    : "Codex 自身解析器不可用 · 应用已锁定";
            _codexApplyAvailable = codexIdle && parserAccepted;
            _codexApplyStatus = $"{idleStatus} · {_codexParserStatus}";
            var workingDraft = new SubagentConfigurationDocument
            {
                SchemaVersion = 3,
                Roles = (preservedSelections ?? _snapshot.Draft.Roles)
                    .Select(CloneSelection).ToList(),
                SourceAuthorizations = (preservedAuthorizations ?? _snapshot.Draft.SourceAuthorizations)
                    .Select(CloneAuthorization).ToList()
            };
            _draftAuthorizations = workingDraft.SourceAuthorizations.Select(CloneAuthorization).ToList();
            BuildSourceRows(_snapshot.Draft);
            BuildRows(workingDraft);
            ApplySnapshot(_snapshot);
            ResultText.Text = _snapshot.Warning ?? "只读检查完成；尚未写入任何文件。";
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(143, 183, 188));
        }
        catch (Exception ex)
        {
            ResultText.Text = $"读取失败：{ex.Message}";
            ResultText.Foreground = Brushes.IndianRed;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshDisconnectedAsync(
        IReadOnlyList<SubagentRoleSelection>? preservedSelections,
        IReadOnlyList<SubagentSourceAuthorization>? preservedAuthorizations)
    {
        _snapshot = _services!.Subagents.InspectDraftOnly();
        _nativeModels = new[] { "gpt-5.6-sol", "gpt-5.6-terra" };
        _sources = await _services.SubagentSources.DiscoverAsync(includeCodexSources: false);
        _codexApplyAvailable = false;
        _codexApplyStatus = "Codex 未连接 · 只管理草稿和外部 Worker，禁止写入 Codex";
        _codexParserStatus = "未运行 Codex 解析器";
        var workingDraft = new SubagentConfigurationDocument
        {
            SchemaVersion = 3,
            Roles = (preservedSelections ?? _snapshot.Draft.Roles).Select(CloneSelection).ToList(),
            SourceAuthorizations = (preservedAuthorizations ?? _snapshot.Draft.SourceAuthorizations)
                .Select(CloneAuthorization).ToList()
        };
        _draftAuthorizations = workingDraft.SourceAuthorizations.Select(CloneAuthorization).ToList();
        BuildSourceRows(_snapshot.Draft);
        BuildRows(workingDraft);
        ApplySnapshot(_snapshot);
        ApplyButton.IsEnabled = false;
        ResultText.Text = "Codex 保持断开：可以查看和调整本页草稿、测试独立 Worker；点一键连接后才允许写入 Codex。";
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(143, 183, 188));
    }

    private void BuildRows(SubagentConfigurationDocument draft)
    {
        if (_services is null || _snapshot is null) return;
        _rows.Clear();
        foreach (var role in _services.Subagents.Roles)
        {
            var selection = draft.Roles.First(item => item.RoleId.Equals(role.Id, StringComparison.OrdinalIgnoreCase));
            var options = new ObservableCollection<SubagentModelChoice>(
                _nativeModels.Select(model => new SubagentModelChoice(
                    SubagentWorkerKind.CodexNative,
                    model,
                    model.Equals("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase)
                        ? "Codex 原生 · GPT-5.6 Sol（强监督）"
                        : model.Equals("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase)
                            ? "Codex 原生 · GPT-5.6 Terra（轻量）"
                            : $"Codex 原生 · {model}",
                    true,
                    null,
                    "Codex 原生 · 跟随当前总管家全局账号")));
            if (role.AllowsExternalWorker)
            {
                foreach (var source in _sourceRows.Where(item => item.CanOfferModels))
                foreach (var model in source.Descriptor.Models)
                {
                    var shortModel = model.StartsWith(source.Descriptor.RoutePrefix, StringComparison.OrdinalIgnoreCase)
                        ? model[source.Descriptor.RoutePrefix.Length..]
                        : model;
                    options.Add(new SubagentModelChoice(
                        SubagentWorkerKind.External,
                        model,
                        $"{source.Descriptor.DisplayName} · {shortModel}（纯文本）",
                        true,
                        source.Descriptor.SourceId,
                        source.Descriptor.QuotaScopeText));
                }
            }

            var selected = options.FirstOrDefault(option => option.WorkerKind == selection.WorkerKind
                                                             && option.ModelId.Equals(selection.ModelId, StringComparison.OrdinalIgnoreCase)
                                                             && (option.WorkerKind == SubagentWorkerKind.CodexNative
                                                                 || string.Equals(option.SourceId, selection.SourceId, StringComparison.OrdinalIgnoreCase)));
            if (selected is null)
            {
                var unavailableReason = selection.WorkerKind == SubagentWorkerKind.External
                    ? UnavailableSourceReason(selection.SourceId)
                    : "当前原生模型目录不可用";
                selected = new SubagentModelChoice(selection.WorkerKind, selection.ModelId,
                    $"已保存但当前不可用 · {selection.ModelId} · {unavailableReason}",
                    false,
                    selection.SourceId,
                    unavailableReason);
                options.Add(selected);
            }
            var applied = _snapshot.AppliedRoles[role.Id];
            _rows.Add(new SubagentRoleRow(role, options, selected, applied));
        }
        UpdatePlanPreview();
    }

    private void BuildSourceRows(SubagentConfigurationDocument appliedDraft)
    {
        _sourceRows.Clear();
        foreach (var source in _sources)
        {
            var applied = appliedDraft.SourceAuthorizations.FirstOrDefault(item =>
                item.SourceId.Equals(source.SourceId, StringComparison.OrdinalIgnoreCase));
            var draft = _draftAuthorizations.FirstOrDefault(item =>
                item.SourceId.Equals(source.SourceId, StringComparison.OrdinalIgnoreCase));
            _sourceRows.Add(new SubagentSourceRow(source, applied, draft));
        }
        var missingIds = appliedDraft.SourceAuthorizations.Select(item => item.SourceId)
            .Concat(_draftAuthorizations.Select(item => item.SourceId))
            .Where(id => _sources.All(source => !source.SourceId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceId in missingIds)
        {
            var applied = appliedDraft.SourceAuthorizations.FirstOrDefault(item =>
                item.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
            var draft = _draftAuthorizations.FirstOrDefault(item =>
                item.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
            var reference = draft ?? applied!;
            var missing = new SubagentSourceDescriptor(
                sourceId,
                string.IsNullOrWhiteSpace(reference.AuthorizedDisplayName)
                    ? sourceId
                    : reference.AuthorizedDisplayName,
                SubagentSourceKind.OpenAiCompatible,
                "missing/",
                "来源已从中转站移除",
                "凭据不可用",
                "不会消耗额度",
                "unavailable",
                reference.ExpectedFingerprint,
                false,
                false,
                false,
                Array.Empty<string>(),
                "来源已移除 · 已阻止调用",
                "请撤销旧授权，或在中转站恢复并重新核对该来源。",
                DateTimeOffset.Now);
            _sourceRows.Add(new SubagentSourceRow(missing, applied, draft));
        }
        _externalModels = _sourceRows.Where(item => item.CanOfferModels)
            .SelectMany(item => item.Descriptor.Models)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string UnavailableSourceReason(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return "没有保存来源授权";
        var row = _sourceRows.FirstOrDefault(item =>
            item.Descriptor.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
        return row?.UnavailableReason ?? "来源已移除或尚未发现";
    }

    private void ApplySnapshot(SubagentConfigurationSnapshot snapshot)
    {
        CodexStatusBadge.Text = !snapshot.ConfigReadable
            ? "Codex 配置缺失"
            : !snapshot.ConfigSafe
                ? "Codex 只读保护"
                : !snapshot.AgentsEnabled
                    ? "子代理已关闭"
                    : _codexApplyStatus;
        var externalRoles = snapshot.Draft.Roles
            .Where(item => item.WorkerKind == SubagentWorkerKind.External)
            .ToArray();
        var executableSources = _sourceRows.Count(item => item.Descriptor.SupportsTextWorker);
        var authorizedSources = _sourceRows.Count(item => item.IsAuthorizedApplied && !item.IsIdentityChanged);
        ExternalStatusBadge.Text = executableSources == 0
            ? $"已发现 {_sourceRows.Count} 来源 · 暂无可执行来源"
            : externalRoles.Length == 0
                ? $"{authorizedSources}/{executableSources} 来源已授权 · 未分配外部角色"
            : snapshot.Bridge.HasConflict
                ? "外部工人 MCP 配置冲突"
                : snapshot.Bridge.ConfiguredOnDisk && snapshot.Bridge.ConfigurationExact
                    ? $"{authorizedSources} 来源已授权 · MCP 已写入"
                    : $"{authorizedSources} 来源已授权 · 待应用";
        var unavailableExternalCount = externalRoles.Count(item =>
            !_sourceRows.Any(source => source.CanOfferModels
                                       && source.Descriptor.SourceId.Equals(item.SourceId, StringComparison.OrdinalIgnoreCase)
                                       && source.Descriptor.Models.Contains(item.ModelId, StringComparer.OrdinalIgnoreCase)));
        HeroStatusText.Text = $"{snapshot.Summary} {_codexApplyStatus}。"
                              + (unavailableExternalCount > 0
                                  ? $" {unavailableExternalCount} 个已保存外部模型因来源撤权、身份变化、离线或移除而不能调用。"
                                  : string.Empty);
        ConfigPathText.Text = $"主配置：{snapshot.ConfigPath}\n托管角色：{snapshot.AgentsDirectory}\nMCP：codex_total_manager_external";
        BridgeDiskStatusText.Text = $"磁盘配置：{snapshot.Bridge.StatusText}";
        if (snapshot.Bridge.LastHandshakeAt is null)
        {
            BridgeHandshakeStatusText.Text = "initialize 客户端自报：尚无记录；磁盘写入不代表 Codex 已加载";
        }
        else
        {
            var client = snapshot.Bridge.LastHandshakeClient ?? "未知客户端";
            var localHandshakeAt = snapshot.Bridge.LastHandshakeAt.Value.ToLocalTime();
            var afterApply = snapshot.Draft.LastAppliedAt is null
                             || snapshot.Bridge.LastHandshakeAt >= snapshot.Draft.LastAppliedAt;
            var selfTest = client.Contains("self-test", StringComparison.OrdinalIgnoreCase);
            BridgeHandshakeStatusText.Text = selfTest
                ? $"initialize 客户端自报：{localHandshakeAt:yyyy-MM-dd HH:mm:ss} · 总管家协议自检；不代表 Codex 已加载"
                : !afterApply
                    ? $"历史 initialize 自报：{localHandshakeAt:yyyy-MM-dd HH:mm:ss} · {client}；本次应用后尚无新记录"
                    : $"initialize 客户端自报：{localHandshakeAt:yyyy-MM-dd HH:mm:ss} · 名称 {client}（身份未经验证）";
        }

        var lastSelection = snapshot.Draft.Roles.FirstOrDefault(item =>
            item.RoleId.Equals(snapshot.Bridge.LastRoleId, StringComparison.OrdinalIgnoreCase));
        var lastRouteMatchesCurrent = snapshot.Bridge.LastCallAt is not null
                                      && (snapshot.Draft.LastAppliedAt is null
                                          || snapshot.Bridge.LastCallAt >= snapshot.Draft.LastAppliedAt)
                                      && lastSelection?.WorkerKind == SubagentWorkerKind.External
                                      && string.Equals(lastSelection.ModelId, snapshot.Bridge.LastRequestedModel,
                                          StringComparison.OrdinalIgnoreCase);
        BridgeLastCallText.Text = snapshot.Bridge.LastCallAt is null
            ? "尚未调用模型"
            : $"{(lastRouteMatchesCurrent ? "当前配置已真实路由" : "历史调用，当前配置尚未实测")} · "
              + $"{snapshot.Bridge.LastCallAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {snapshot.Bridge.LastRoleId} · "
              + $"请求 {snapshot.Bridge.LastRequestedModel} → 返回 {snapshot.Bridge.LastResolvedModel ?? "未报告"} · "
              + $"HTTP {snapshot.Bridge.LastHttpStatus?.ToString() ?? "-"} · "
              + (snapshot.Bridge.LastCallSucceeded == true ? "成功" : $"失败 {snapshot.Bridge.LastError}");
        BridgeUsageText.Text = $"额度来源：{snapshot.Bridge.LastAccountSource ?? lastSelection?.SourceId ?? "尚未报告"} · 具体上游账号：未报告 · 输入 {FormatReportedTokens(snapshot.Bridge.InputTokens)} / 输出 {FormatReportedTokens(snapshot.Bridge.OutputTokens)} Token";
        ApplyButton.IsEnabled = snapshot.ConfigReadable && snapshot.ConfigSafe && snapshot.AgentsEnabled
                                && !snapshot.Bridge.HasConflict && _codexApplyAvailable && !_busy;
        LiveWorkerTestButton.IsEnabled = snapshot.Bridge.ConfiguredOnDisk
                                         && snapshot.Bridge.ConfigurationExact
                                         && HasUsableExternalRole(snapshot)
                                         && !_busy;
        UpdatePlanPreview();
    }

    private IReadOnlyList<SubagentRoleSelection> CurrentSelections() => _rows.Select(row => new SubagentRoleSelection
    {
        RoleId = row.RoleId,
        WorkerKind = row.SelectedModel.WorkerKind,
        ModelId = row.SelectedModel.ModelId,
        SourceId = row.SelectedModel.WorkerKind == SubagentWorkerKind.External
            ? row.SelectedModel.SourceId
            : null
    }).ToArray();

    private IReadOnlyList<SubagentSourceAuthorization> CurrentAuthorizations() =>
        _draftAuthorizations.Select(CloneAuthorization).ToArray();

    private void RoleModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: SubagentRoleRow row }) row.RefreshSelectionStatus();
        UpdatePlanPreview();
    }

    private void SourceAuthorizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: SubagentSourceRow row }) return;
        if (!row.CanChangeAuthorization)
        {
            ResultText.Text = row.UnavailableReason;
            ResultText.Foreground = Brushes.IndianRed;
            return;
        }

        var currentSelections = CurrentSelections();
        var existing = _draftAuthorizations.FirstOrDefault(item =>
            item.SourceId.Equals(row.Descriptor.SourceId, StringComparison.OrdinalIgnoreCase));
        var currentlyAuthorized = existing?.Enabled == true
                                  && SubagentSourceIdentity.FixedTimeEquals(
                                      existing.ExpectedFingerprint, row.Descriptor.Fingerprint);
        var mustRevokeUnavailableGrant = existing?.Enabled == true
                                         && (!row.Descriptor.Enabled || !row.Descriptor.SupportsTextWorker);
        if (currentlyAuthorized || mustRevokeUnavailableGrant)
        {
            var usedBy = _rows.Where(role => role.SelectedModel.WorkerKind == SubagentWorkerKind.External
                                             && string.Equals(role.SelectedModel.SourceId, row.Descriptor.SourceId,
                                                 StringComparison.OrdinalIgnoreCase))
                .Select(role => role.DisplayName)
                .ToArray();
            var warning = usedBy.Length == 0
                ? $"将撤销“{row.Descriptor.DisplayName}”的子代理来源授权。不会调用模型。"
                : $"“{row.Descriptor.DisplayName}”正在被 {string.Join("、", usedBy)} 使用。撤销后会保留这些失效选择并阻止应用，直到你重新分配模型；不会自动切到其他账号。";
            if (MessageBox.Show(
                    warning,
                    "确认撤销来源授权",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;
            existing!.Enabled = false;
            ResultText.Text = "已在草稿中撤销来源授权；尚未写入 Codex，也没有调用模型。";
        }
        else
        {
            var previousIdentity = existing?.Enabled == true
                ? $"\n旧身份：{existing.ExpectedFingerprint[..Math.Min(12, existing.ExpectedFingerprint.Length)]}…"
                  + $"\n旧端点：{(string.IsNullOrWhiteSpace(existing.AuthorizedEndpoint) ? "旧版未记录" : existing.AuthorizedEndpoint)}"
                  + $"\n旧适配器/前缀：{(string.IsNullOrWhiteSpace(existing.AuthorizedAdapter) ? "旧版未记录" : existing.AuthorizedAdapter)} / {(string.IsNullOrWhiteSpace(existing.AuthorizedRoutePrefix) ? "旧版未记录" : existing.AuthorizedRoutePrefix)}"
                : string.Empty;
            var confirmation = $"准备允许“{row.Descriptor.DisplayName}”作为外部纯文本子代理来源。\n\n"
                               + $"端点：{row.Descriptor.EndpointDisplay}\n"
                               + $"适配器/前缀：{row.Descriptor.Adapter} / {row.Descriptor.RoutePrefix}\n"
                               + $"额度：{row.Descriptor.QuotaScopeText}\n"
                               + $"凭据：{row.Descriptor.CredentialScopeText}\n"
                               + $"当前身份：{row.Descriptor.Fingerprint[..Math.Min(12, row.Descriptor.Fingerprint.Length)]}…{previousIdentity}\n"
                               + "能力：仅纯文本输入输出；无文件、命令或写入权限。\n\n"
                               + "本操作只修改页面草稿，不改变任何角色，不会立即调用模型；仍需点击“验证并安全应用”。";
            if (MessageBox.Show(
                    confirmation,
                    "核对并授权子代理来源",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;
            _draftAuthorizations.RemoveAll(item =>
                item.SourceId.Equals(row.Descriptor.SourceId, StringComparison.OrdinalIgnoreCase));
            _draftAuthorizations.Add(new SubagentSourceAuthorization
            {
                SourceId = row.Descriptor.SourceId,
                ExpectedFingerprint = row.Descriptor.Fingerprint,
                Enabled = true,
                AuthorizedAt = DateTimeOffset.Now,
                AuthorizedDisplayName = row.Descriptor.DisplayName,
                AuthorizedEndpoint = row.Descriptor.EndpointDisplay,
                AuthorizedAdapter = row.Descriptor.Adapter,
                AuthorizedRoutePrefix = row.Descriptor.RoutePrefix,
                AuthorizedCredentialScope = row.Descriptor.CredentialScopeText
            });
            ResultText.Text = "来源已加入授权草稿；角色没有自动改变，也没有调用模型。";
        }
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(143, 183, 188));
        BuildSourceRows(_snapshot?.Draft ?? new SubagentConfigurationDocument());
        BuildRows(new SubagentConfigurationDocument
        {
            SchemaVersion = 3,
            Roles = currentSelections.Select(CloneSelection).ToList(),
            SourceAuthorizations = CurrentAuthorizations().Select(CloneAuthorization).ToList()
        });
    }

    private void UpdatePlanPreview()
    {
        if (_services is null || _rows.Count == 0) return;
        var plan = _services.Subagents.CreatePlan(
            CurrentSelections(), CurrentAuthorizations(), _nativeModels, _sources);
        PlanSummaryText.Text = plan.Summary;
        var authorizationChanges = CountAuthorizationChanges();
        PlanDetailText.Text = plan.Issues.Count > 0
            ? string.Join("  ·  ", plan.Issues)
            : $"来源授权变化 {authorizationChanges} 项  ·  角色模型变化 {_rows.Count(row => !row.MatchesAppliedSelection)} 项  ·  不会立即调用模型  ·  "
              + string.Join("  ·  ", _rows.Select(row => $"{row.DisplayName} → {row.SelectedModel.ModelId}"));
        if (_snapshot is not null)
            ApplyButton.IsEnabled = plan.CanApply && _snapshot.ConfigReadable && _snapshot.ConfigSafe
                                    && _snapshot.AgentsEnabled && !_snapshot.Bridge.HasConflict
                                    && _codexApplyAvailable && !_busy;
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null || _snapshot is null) return;
        if (!_services.CodexConfig.IsManagedNativeProviderSelected())
        {
            ResultText.Text = "Codex 目前未连接。草稿仍在界面里，但不会写入 Codex；请先点一键连接 Codex。";
            ResultText.Foreground = Brushes.IndianRed;
            return;
        }
        var selections = CurrentSelections();
        var plan = _services.Subagents.CreatePlan(
            selections, CurrentAuthorizations(), _nativeModels, _sources);
        if (!plan.CanApply)
        {
            ResultText.Text = string.Join("；", plan.Issues);
            ResultText.Foreground = Brushes.IndianRed;
            return;
        }

        SetBusy(true, "正在确认 Codex 空闲并创建安全备份…");
        try
        {
            var desktop = await _services.CodexDesktop.ReadStateAsync();
            if (desktop.Connected && desktop.IsTurnRunning)
                throw new InvalidOperationException("Codex 正在回答。为避免当前任务读取到半套配置，请等回答结束后再应用。");
            if (!desktop.Connected && IsCodexProcessRunning())
                throw new InvalidOperationException("检测到 Codex 正在运行，但无法确认它是否空闲。请恢复可读状态或关闭 Codex 后再应用。");

            var result = await _services.Subagents.ApplyAsync(
                selections,
                CurrentAuthorizations(),
                _nativeModels,
                _sources,
                _snapshot.BaselineRevision);
            SetBusy(false);
            await RefreshAsync();
            ResultText.Text = $"{result.Summary} 备份：{result.BackupDirectory}";
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(98, 225, 181));
        }
        catch (Exception ex)
        {
            ResultText.Text = $"没有应用：{ex.Message}";
            ResultText.Foreground = Brushes.IndianRed;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        var plan = _services.Subagents.CreatePlan(
            CurrentSelections(), CurrentAuthorizations(), _nativeModels, _sources);
        ResultText.Text = plan.CanApply
            ? $"静态预览通过：{plan.Summary} {_codexParserStatus}；精确候选会在应用前再次交给 Codex 自身解析器检查。没有写入真实配置，也没有调用任何模型。"
            : $"验证未通过：{string.Join("；", plan.Issues)}";
        ResultText.Foreground = plan.CanApply
            ? new SolidColorBrush(Color.FromRgb(98, 225, 181))
            : Brushes.IndianRed;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RefreshAsync(preserveDraft: true);
    }

    private async void BridgeSelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        SetBusy(true, "正在启动独立 MCP 进程并执行零额度协议自检…");
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = _services.Subagents.BridgeExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--external-worker-mcp");
            process = Process.Start(start)
                      ?? throw new InvalidOperationException("无法启动外部工人 MCP 进程。");
            var initialize = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "codex-total-manager-self-test", version = "1.0" }
                }
            });
            await process.StandardInput.WriteLineAsync(initialize);
            await process.StandardInput.FlushAsync();
            var initializeLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var initializeJson = JsonDocument.Parse(initializeLine
                                                          ?? throw new InvalidDataException("MCP initialize 没有返回结果。"));
            if (!initializeJson.RootElement.TryGetProperty("result", out _))
                throw new InvalidDataException("MCP initialize 返回失败。");

            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            await process.StandardInput.FlushAsync();
            var toolsLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var toolsJson = JsonDocument.Parse(toolsLine
                                                     ?? throw new InvalidDataException("MCP tools/list 没有返回结果。"));
            var tools = toolsJson.RootElement.GetProperty("result").GetProperty("tools");
            if (tools.GetArrayLength() != 1
                || tools[0].GetProperty("name").GetString() != ExternalWorkerMcpHost.ToolName)
                throw new InvalidDataException("MCP 暴露的工具集合不符合安全约束。");

            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"MCP 进程退出码 {process.ExitCode}：{await process.StandardError.ReadToEndAsync()}");

            SetBusy(false);
            await RefreshAsync();
            ResultText.Text = "零额度桥接自检通过：独立进程完成 initialize 与 tools/list，只暴露 delegate_to_worker；clientInfo 只是客户端自报，不代表 Codex 已加载，也不能证明调用者是 Sol；没有调用任何模型。";
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(98, 225, 181));
        }
        catch (Exception ex)
        {
            ResultText.Text = $"桥接自检失败：{ex.Message}";
            ResultText.Foreground = Brushes.IndianRed;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
                process.Dispose();
            }
            SetBusy(false);
        }
    }

    private async void LiveWorkerTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null || _snapshot is null) return;
        var selected = _snapshot.Draft.Roles.FirstOrDefault(item =>
            item.WorkerKind == SubagentWorkerKind.External
            && _snapshot.Draft.SourceAuthorizations.Any(grant => grant.Enabled
                && grant.SourceId.Equals(item.SourceId, StringComparison.OrdinalIgnoreCase))
            && _externalModels.Contains(item.ModelId, StringComparer.OrdinalIgnoreCase));
        if (selected is null || !_snapshot.Bridge.ConfiguredOnDisk || !_snapshot.Bridge.ConfigurationExact)
        {
            ResultText.Text = "请先选择至少一个已授权的外部纯文本角色并安全应用 MCP，再运行真实调用测试。";
            ResultText.Foreground = Brushes.IndianRed;
            return;
        }
        var selectedSource = _sourceRows.FirstOrDefault(row =>
            row.Descriptor.SourceId.Equals(selected.SourceId, StringComparison.OrdinalIgnoreCase));
        var selectedSourceLabel = selectedSource?.Descriptor.DisplayName ?? selected.SourceId ?? "未知来源";

        var confirmation = MessageBox.Show(
            $"角色：{selected.RoleId}\n来源：{selectedSourceLabel}（{selected.SourceId}）\n模型：{selected.ModelId}\n\n"
            + "将做一次 max_tokens=64 的纯文本测试（上游用量可能另含推理 Token）。"
            + "这会消耗所列来源额度，但不会读取、修改文件或运行命令。是否继续？",
            "确认真实外部模型调用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        SetBusy(true, $"正在通过 {selected.RoleId} 进行真实外部路由测试…");
        try
        {
            var completion = await _services.ExternalWorker.DelegateAsync(new ExternalWorkerInvocation(
                selected.RoleId,
                "这是总管家桥接测试。只返回 OK_TEXT_WORKER。不要声称访问过任何文件。",
                null,
                64));
            SetBusy(false);
            await RefreshAsync();
            ResultText.Text = $"真实调用成功：角色 {completion.RoleId}；请求 {completion.ConfiguredModel}；"
                              + $"返回 {completion.ResolvedModel}；HTTP {completion.HttpStatusCode}；"
                              + $"输入 {FormatReportedTokens(completion.Usage.PromptTokens)} / 输出 {FormatReportedTokens(completion.Usage.CompletionTokens)} Token；"
                              + $"额度来源 {completion.AccountSource}；具体上游账号未报告。";
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(98, 225, 181));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            await RefreshAsync();
            ResultText.Text = $"真实调用失败：{ex.Message}";
            ResultText.Foreground = Brushes.IndianRed;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ResetDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        var recommended = _services.Subagents.Roles.Select(role => new SubagentRoleSelection
        {
            RoleId = role.Id,
            WorkerKind = SubagentWorkerKind.CodexNative,
            ModelId = role.DefaultModel
        }).ToList();
        _draftAuthorizations = new List<SubagentSourceAuthorization>();
        BuildSourceRows(_snapshot?.Draft ?? new SubagentConfigurationDocument());
        BuildRows(new SubagentConfigurationDocument { Roles = recommended });
        ResultText.Text = "已恢复推荐草稿，但尚未保存或写入 Codex。";
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(143, 183, 188));
    }

    private void OpenAgentsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        Directory.CreateDirectory(_services.Subagents.AgentsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
            ArgumentList = { _services.Subagents.AgentsDirectory }
        });
    }

    private bool HasUsableExternalRole(SubagentConfigurationSnapshot snapshot) =>
        snapshot.Draft.Roles.Any(item => item.WorkerKind == SubagentWorkerKind.External
                                         && !string.IsNullOrWhiteSpace(item.SourceId)
                                         && _externalModels.Contains(item.ModelId, StringComparer.OrdinalIgnoreCase)
                                         && snapshot.Draft.SourceAuthorizations.Any(grant => grant.Enabled
                                             && grant.SourceId.Equals(item.SourceId, StringComparison.OrdinalIgnoreCase)));

    private int CountAuthorizationChanges()
    {
        if (_snapshot is null) return 0;
        var ids = _snapshot.Draft.SourceAuthorizations.Select(item => item.SourceId)
            .Concat(_draftAuthorizations.Select(item => item.SourceId))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return ids.Count(id =>
        {
            var before = _snapshot.Draft.SourceAuthorizations.FirstOrDefault(item =>
                item.SourceId.Equals(id, StringComparison.OrdinalIgnoreCase));
            var after = _draftAuthorizations.FirstOrDefault(item =>
                item.SourceId.Equals(id, StringComparison.OrdinalIgnoreCase));
            return before?.Enabled != after?.Enabled
                   || !string.Equals(before?.ExpectedFingerprint, after?.ExpectedFingerprint,
                       StringComparison.OrdinalIgnoreCase);
        });
    }

    private static SubagentRoleSelection CloneSelection(SubagentRoleSelection selection) => new()
    {
        RoleId = selection.RoleId,
        WorkerKind = selection.WorkerKind,
        ModelId = selection.ModelId,
        SourceId = selection.SourceId
    };

    private static SubagentSourceAuthorization CloneAuthorization(SubagentSourceAuthorization authorization) => new()
    {
        SourceId = authorization.SourceId,
        ExpectedFingerprint = authorization.ExpectedFingerprint,
        Enabled = authorization.Enabled,
        AuthorizedAt = authorization.AuthorizedAt,
        AuthorizedDisplayName = authorization.AuthorizedDisplayName,
        AuthorizedEndpoint = authorization.AuthorizedEndpoint,
        AuthorizedAdapter = authorization.AuthorizedAdapter,
        AuthorizedRoutePrefix = authorization.AuthorizedRoutePrefix,
        AuthorizedCredentialScope = authorization.AuthorizedCredentialScope
    };

    private static string FormatReportedTokens(long? value) => value is null
        ? "上游未报告（不等于 0）"
        : value.Value.ToString("N0");

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SubagentRolesList.IsEnabled = !busy;
        WorkerSourcesList.IsEnabled = !busy;
        BridgeSelfTestButton.IsEnabled = !busy;
        ValidateButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ResetDraftButton.IsEnabled = !busy;
        OpenAgentsFolderButton.IsEnabled = !busy;
        LiveWorkerTestButton.IsEnabled = !busy
                                         && _snapshot?.Bridge.ConfiguredOnDisk == true
                                         && _snapshot.Bridge.ConfigurationExact
                                         && HasUsableExternalRole(_snapshot);
        if (!string.IsNullOrWhiteSpace(message))
        {
            ResultText.Text = message;
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(143, 183, 188));
        }
        UpdatePlanPreview();
    }

    private static bool IsCodexProcessRunning()
    {
        try
        {
            var currentId = Environment.ProcessId;
            return Process.GetProcesses().Any(process => process.Id != currentId
                && process.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }
}

public sealed record SubagentModelChoice(
    SubagentWorkerKind WorkerKind,
    string ModelId,
    string DisplayName,
    bool IsAvailable = true,
    string? SourceId = null,
    string SourceDetail = "");

public sealed class SubagentRoleRow : INotifyPropertyChanged
{
    private SubagentModelChoice _selectedModel;
    private string _appliedStatusText;
    private Brush _appliedStatusBrush;

    public SubagentRoleRow(
        SubagentRoleDefinition role,
        ObservableCollection<SubagentModelChoice> modelOptions,
        SubagentModelChoice selectedModel,
        SubagentAppliedRoleState applied)
    {
        Role = role;
        ModelOptions = modelOptions;
        _selectedModel = selectedModel;
        Applied = applied;
        _appliedStatusText = applied.StatusText;
        _appliedStatusBrush = applied.ExactMatch && selectedModel.IsAvailable
            ? new SolidColorBrush(Color.FromRgb(98, 225, 181))
            : new SolidColorBrush(Color.FromRgb(224, 173, 87));
        RefreshSelectionStatus();
    }

    public SubagentRoleDefinition Role { get; }
    public SubagentAppliedRoleState Applied { get; }
    public string RoleId => Role.Id;
    public string DisplayName => Role.DisplayName;
    public string Purpose => Role.Purpose;
    public string ModelAutomationName => $"{DisplayName}：执行模型";
    public string PermissionText => SelectedModel.WorkerKind == SubagentWorkerKind.External
        ? "纯文本 · 无文件/命令权限"
        : Role.SandboxMode == "read-only" ? "只读权限" : "仅工作区可写";
    public string ReasoningText => $"推理 {Role.ReasoningEffort}";
    public bool MatchesAppliedSelection => Applied.ExactMatch
                                           && string.Equals(Applied.AppliedModel, SelectedModel.ModelId,
                                               StringComparison.OrdinalIgnoreCase);
    public ObservableCollection<SubagentModelChoice> ModelOptions { get; }

    public SubagentModelChoice SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (value is null || ReferenceEquals(_selectedModel, value)) return;
            _selectedModel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MatchesAppliedSelection));
            RefreshSelectionStatus();
        }
    }

    public string WorkerKindText => (SelectedModel.WorkerKind == SubagentWorkerKind.CodexNative
        ? "Codex 原生子代理"
        : $"外部纯文本工人 · {SelectedModel.SourceId ?? "来源缺失"}")
        + (SelectedModel.IsAvailable ? string.Empty : " · 当前目录不可用");
    public string AppliedStatusText
    {
        get => _appliedStatusText;
        private set { _appliedStatusText = value; OnPropertyChanged(); }
    }
    public Brush AppliedStatusBrush
    {
        get => _appliedStatusBrush;
        private set { _appliedStatusBrush = value; OnPropertyChanged(); }
    }

    public void RefreshSelectionStatus()
    {
        OnPropertyChanged(nameof(WorkerKindText));
        OnPropertyChanged(nameof(PermissionText));
        if (!SelectedModel.IsAvailable)
        {
            AppliedStatusText = "磁盘草稿已保存 · 当前模型目录不可用，不能调用";
            AppliedStatusBrush = Brushes.IndianRed;
            return;
        }
        if (SelectedModel.WorkerKind == SubagentWorkerKind.External)
        {
            var externalExact = Applied.ExactMatch
                                && string.Equals(Applied.AppliedModel, SelectedModel.ModelId, StringComparison.OrdinalIgnoreCase);
            AppliedStatusText = externalExact
                ? "MCP 已写入磁盘 · 新任务后可调用"
                : "草稿未应用 · 不会调用模型";
            AppliedStatusBrush = externalExact
                ? new SolidColorBrush(Color.FromRgb(98, 225, 181))
                : new SolidColorBrush(Color.FromRgb(224, 173, 87));
            return;
        }
        var exact = Applied.ExactMatch && string.Equals(Applied.AppliedModel, SelectedModel.ModelId, StringComparison.OrdinalIgnoreCase);
        AppliedStatusText = exact ? "已写入磁盘 · 新子代理任务后生效" : "草稿未应用";
        AppliedStatusBrush = exact
            ? new SolidColorBrush(Color.FromRgb(98, 225, 181))
            : new SolidColorBrush(Color.FromRgb(224, 173, 87));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SubagentSourceRow
{
    private readonly SubagentSourceAuthorization? _applied;
    private readonly SubagentSourceAuthorization? _draft;

    public SubagentSourceRow(
        SubagentSourceDescriptor descriptor,
        SubagentSourceAuthorization? applied,
        SubagentSourceAuthorization? draft)
    {
        Descriptor = descriptor;
        _applied = applied;
        _draft = draft;
    }

    public SubagentSourceDescriptor Descriptor { get; }
    public string DisplayName => Descriptor.DisplayName;
    public string KindText => Descriptor.Kind switch
    {
        SubagentSourceKind.CliProxyPool => "独立 CLIProxy 号池",
        SubagentSourceKind.OpenAiCompatible => "兼容 API（只发现）",
        _ => "Codex 原生账号（跟随全局）"
    };
    public string SourceIdText => $"来源 ID：{Descriptor.SourceId}";
    public string EndpointText => $"端点：{Descriptor.EndpointDisplay}";
    public string CredentialText => $"凭据：{Descriptor.CredentialScopeText}";
    public string QuotaText => $"额度：{Descriptor.QuotaScopeText}";
    public string CapabilityText => Descriptor.SupportsTextWorker
        ? "仅纯文本 · 无文件/命令权限"
        : Descriptor.UnsupportedReason ?? "暂不支持子代理执行";
    public string ModelCountText => $"模型 {Descriptor.Models.Count} 个 · 前缀 {Descriptor.RoutePrefix}";
    public string IdentityFieldsText => $"身份字段：{Descriptor.Adapter} · {Descriptor.RoutePrefix} · {Descriptor.CredentialScopeText}";
    public string FingerprintText => $"身份 {Descriptor.Fingerprint[..Math.Min(12, Descriptor.Fingerprint.Length)]}… · 发现于 {Descriptor.DiscoveredAt:HH:mm:ss}";
    public bool IsAuthorizedApplied => _applied?.Enabled == true
                                       && SubagentSourceIdentity.FixedTimeEquals(
                                           _applied.ExpectedFingerprint, Descriptor.Fingerprint);
    public bool IsAuthorizedDraft => _draft?.Enabled == true
                                     && SubagentSourceIdentity.FixedTimeEquals(
                                         _draft.ExpectedFingerprint, Descriptor.Fingerprint);
    public bool IsIdentityChanged => (_applied?.Enabled == true
                                      && !SubagentSourceIdentity.FixedTimeEquals(
                                          _applied.ExpectedFingerprint, Descriptor.Fingerprint))
                                     || (_draft?.Enabled == true
                                         && !SubagentSourceIdentity.FixedTimeEquals(
                                             _draft.ExpectedFingerprint, Descriptor.Fingerprint));
    public bool CanOfferModels => IsAuthorizedDraft
                                  && Descriptor.Enabled
                                  && Descriptor.Ready
                                  && Descriptor.SupportsTextWorker
                                  && !IsIdentityChanged;
    public bool CanChangeAuthorization => _draft?.Enabled == true
                                          || (Descriptor.Enabled && Descriptor.SupportsTextWorker);
    public string TrustStatusText => IsIdentityChanged
        ? "身份变化 · 已暂停，需重新授权"
        : IsAuthorizedDraft && IsAuthorizedApplied
            ? "已授权并应用"
            : IsAuthorizedDraft
                ? "授权草稿 · 尚未应用"
                : _applied?.Enabled == true
                    ? "撤权草稿 · 尚未应用"
                    : "待授权 · 不会进入角色下拉框";
    public string RuntimeStatusText => $"运行状态：{Descriptor.StatusText}";
    public string AuthorizationActionText => IsAuthorizedDraft
        ? "撤销授权"
        : _draft?.Enabled == true && (!Descriptor.Enabled || !Descriptor.SupportsTextWorker)
            ? "撤销失效授权"
        : IsIdentityChanged
            ? "重新核对并授权"
            : CanChangeAuthorization
                ? "查看并授权"
                : "暂不可作为子代理";
    public string AuthorizationAutomationName => $"{Descriptor.DisplayName}：{AuthorizationActionText}";
    public string IdentityChangeText => BuildIdentityChangeText();
    public string UnavailableReason => IsIdentityChanged
        ? "来源身份已变化，需重新授权"
        : !Descriptor.Enabled
            ? "来源已停用"
            : !Descriptor.SupportsTextWorker
                ? Descriptor.UnsupportedReason ?? "该来源暂不支持纯文本执行"
                : !IsAuthorizedDraft
                    ? "来源尚未授权"
                    : !Descriptor.Ready
                        ? Descriptor.StatusText
                        : string.Empty;

    private string BuildIdentityChangeText()
    {
        if (!IsIdentityChanged) return string.Empty;
        var previous = _draft?.Enabled == true ? _draft : _applied;
        if (previous is null) return "旧授权身份与当前来源不一致。";
        var changes = new List<string>();
        if (string.IsNullOrWhiteSpace(previous.AuthorizedEndpoint)
            || !previous.AuthorizedEndpoint.Equals(Descriptor.EndpointDisplay, StringComparison.OrdinalIgnoreCase))
            changes.Add($"端点 {EmptyAsUnknown(previous.AuthorizedEndpoint)} → {Descriptor.EndpointDisplay}");
        if (string.IsNullOrWhiteSpace(previous.AuthorizedAdapter)
            || !previous.AuthorizedAdapter.Equals(Descriptor.Adapter, StringComparison.OrdinalIgnoreCase))
            changes.Add($"适配器 {EmptyAsUnknown(previous.AuthorizedAdapter)} → {Descriptor.Adapter}");
        if (string.IsNullOrWhiteSpace(previous.AuthorizedRoutePrefix)
            || !previous.AuthorizedRoutePrefix.Equals(Descriptor.RoutePrefix, StringComparison.OrdinalIgnoreCase))
            changes.Add($"前缀 {EmptyAsUnknown(previous.AuthorizedRoutePrefix)} → {Descriptor.RoutePrefix}");
        if (string.IsNullOrWhiteSpace(previous.AuthorizedCredentialScope)
            || !previous.AuthorizedCredentialScope.Equals(Descriptor.CredentialScopeText, StringComparison.Ordinal))
            changes.Add($"凭据槽 {EmptyAsUnknown(previous.AuthorizedCredentialScope)} → {Descriptor.CredentialScopeText}");
        if (changes.Count == 0)
            changes.Add($"指纹 {Short(previous.ExpectedFingerprint)} → {Short(Descriptor.Fingerprint)}");
        return "身份变化：" + string.Join("；", changes);
    }

    private static string EmptyAsUnknown(string? value) => string.IsNullOrWhiteSpace(value) ? "旧版未记录" : value;
    private static string Short(string? value) => string.IsNullOrWhiteSpace(value)
        ? "未记录"
        : value[..Math.Min(12, value.Length)] + "…";
}
