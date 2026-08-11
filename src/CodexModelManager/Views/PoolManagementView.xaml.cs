using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CodexModelManager.Models;
using CodexModelManager.Services;

namespace CodexModelManager.Views;

public partial class PoolManagementView : UserControl
{
    private readonly ObservableCollection<AccountPoolView> _pools = new();
    private AppServices? _services;
    private bool _busy;
    private bool _liveUsageRefreshRunning;
    private readonly DispatcherTimer _liveUsageTimer;
    private UnifiedGatewayStatus? _gatewayStatus;

    public string ActiveTitle { get; private set; } = "正在读取当前线路…";
    public string ActiveDetail { get; private set; } = "点开中转站可查看全部号池";
    public bool OfficialHealthy { get; private set; }
    public LiveTokenUsageSnapshot LastLiveUsage { get; private set; } = LiveTokenUsageSnapshot.Empty;
    public AccountUsageLedgerSnapshot LastAccountLedger { get; private set; } = AccountUsageLedgerSnapshot.Empty;
    public event EventHandler? ManageOtherModelsRequested;
    public event Action<LiveTokenUsageSnapshot>? LiveUsageUpdated;

    public PoolManagementView()
    {
        InitializeComponent();
        _liveUsageTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _liveUsageTimer.Tick += LiveUsageTimer_Tick;
        Loaded += (_, _) =>
        {
            if (!RuntimeMode.IsDetachedUi) _liveUsageTimer.Start();
        };
        Unloaded += (_, _) => _liveUsageTimer.Stop();
        PoolsList.ItemsSource = _pools;
        CollectionViewSource.GetDefaultView(_pools).GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(AccountPoolView.SectionTitle)));
    }

    public void Initialize(AppServices services) => _services = services;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeMode.IsDetachedUi) return;
        if (_services is null) return;
        await ReloadAsync(await _services.RuntimeTruth.ReadAsync(cancellationToken), cancellationToken);
    }

    public async Task ReloadAsync(RuntimeTruthSnapshot runtimeTruth, CancellationToken cancellationToken = default)
    {
        if (_services is null) return;
        var viewsTask = _services.AccountPools.ReadViewsAsync(cancellationToken);
        var gatewayTask = ReadGatewaySafelyAsync(cancellationToken);
        var routingAuditTask = _services.AccountPools.ReadNativeRoutingAuditAsync(cancellationToken);
        var liveUsageTask = _services.AccountPools.ReadLiveTokenUsageAsync(cancellationToken);
        await Task.WhenAll(viewsTask, gatewayTask, routingAuditTask, liveUsageTask);
        var views = viewsTask.Result;
        ApplyGatewayStatus(gatewayTask.Result);
        _pools.Clear();
        foreach (var view in views) _pools.Add(view);
        var active = views.FirstOrDefault(view =>
                         view.Id.Equals(runtimeTruth.Preferred.PoolId, StringComparison.OrdinalIgnoreCase))
                     ?? views.FirstOrDefault(view => view.IsActive);
        ActiveTitle = runtimeTruth.Preferred.PreferredAccountDisplayName;
        ActiveDetail = active is null
            ? runtimeTruth.Consistency.Message
            : $"{active.TypeText} · {runtimeTruth.Preferred.PreferredModel} · {active.StatusTitle}";
        OfficialHealthy = views.FirstOrDefault(view => view.Id == PoolCatalogDefaults.OfficialPoolId)?.CanSwitch == true;

        CurrentPoolText.Text = runtimeTruth.Preferred.PreferredAccountDisplayName;
        CurrentPoolDetailText.Text = active is null
            ? "首选号池来源不可用"
            : $"{active.TypeText} · {active.StatusTitle} · 快照 #{runtimeTruth.Revision}";
        CurrentPoolModelText.Text = string.IsNullOrWhiteSpace(runtimeTruth.Preferred.PreferredModel)
            ? "首选模型未知"
            : runtimeTruth.Preferred.PreferredModel;
        CurrentPoolModelDetailText.Text = $"首选入口：{runtimeTruth.Task.ExpectedModelLabel} · 不代表最近实际执行";
        CurrentTaskModelText.Text = runtimeTruth.Task.Connected
            ? runtimeTruth.Task.DisplayedModelLabel
            : "未连接到 Codex 任务";
        CurrentTaskModelDetailText.Text = !runtimeTruth.Task.Connected
            ? runtimeTruth.Task.Message
            : runtimeTruth.Task.IsAnswering
                ? "当前任务正在回答；回答结束前不会执行切换"
                : runtimeTruth.Task.MatchesPreference
                    ? "当前任务显示与首选入口一致"
                    : $"当前任务显示与首选入口不同：{runtimeTruth.Task.ExpectedModelLabel}";
        ApplyActualExecution(runtimeTruth);
        HeroCurrentAccountText.Text = runtimeTruth.Preferred.PreferredAccountDisplayName;
        HeroCurrentModelText.Text = string.IsNullOrWhiteSpace(runtimeTruth.Preferred.PreferredModel)
            ? "首选模型未知"
            : runtimeTruth.Preferred.PreferredModel;
        HeroTaskStateText.Text = RuntimeTruthStateLabel(runtimeTruth);
        ApplyRoutingAudit(routingAuditTask.Result, active, runtimeTruth);
        UpdateLiveUsage(liveUsageTask.Result);
        ApplyAccountLedger(_services.AccountUsageLedger.LastSnapshot);
        StatusMessage.Text = _services.PoolCatalog.LoadWarning
                             ?? $"统一事实快照 #{runtimeTruth.Revision}：{runtimeTruth.Consistency.Message}";
    }

    private async void LiveUsageTimer_Tick(object? sender, EventArgs e)
    {
        if (_services is null || _liveUsageRefreshRunning) return;
        _liveUsageRefreshRunning = true;
        try
        {
            UpdateLiveUsage(await _services.AccountPools.ReadLiveTokenUsageAsync());
            ApplyAccountLedger(_services.AccountUsageLedger.LastSnapshot);
        }
        catch
        {
            LiveUsageSourceText.Text = $"{DateTime.Now:HH:mm:ss} 刷新失败 · 保留上一次真实记录";
        }
        finally
        {
            _liveUsageRefreshRunning = false;
        }
    }

    public void UpdateLiveUsage(LiveTokenUsageSnapshot snapshot)
    {
        LastLiveUsage = snapshot;
        ApplyNativeUsage(snapshot.Pro, ProLiveTotalText, ProLiveBreakdownText, ProLiveMetaText, ProLiveStateText);
        ApplyNativeUsage(snapshot.Plus, PlusLiveTotalText, PlusLiveBreakdownText, PlusLiveMetaText, PlusLiveStateText);

        LiveUsageSourceText.Text = $"本地 {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss} 实时刷新 · Pro/Plus 来自本机成功请求 · 不等同于官方额度";
        LiveUsageUpdated?.Invoke(snapshot);
    }

    private static void ApplyNativeUsage(
        LiveTokenUsageView usage,
        TextBlock total,
        TextBlock breakdown,
        TextBlock meta,
        TextBlock state)
    {
        total.Text = $"{UsageFormatting.Number(usage.TodayTotalTokens)} Token";
        breakdown.Text = $"今日输入 {UsageFormatting.Number(usage.TodayInputTokens)} · 输出 {UsageFormatting.Number(usage.TodayOutputTokens)}";
        meta.Text = usage.Available
            ? $"累计 {UsageFormatting.Number(usage.TotalTokens)} · 成功 {usage.SuccessCount:N0} 次 · 最后 {FormatLastSeen(usage.LastSeen)}"
            : "本机还没有可归账的成功请求";
        state.Text = usage.LastSeen is null ? "等待请求" : $"LIVE 本地 {usage.LastSeen.Value.ToLocalTime():HH:mm:ss}";
    }

    private static string FormatLastSeen(DateTimeOffset? value) => value is null ? "暂无" : $"本地 {value.Value.ToLocalTime():MM-dd HH:mm:ss}";

    private void ApplyAccountLedger(AccountUsageLedgerSnapshot snapshot)
    {
        LastAccountLedger = snapshot;
        AccountLedgerStatusText.Text = snapshot.TokenStatus;
        var importer = snapshot.ImporterStatus;
        var tokenHealth = importer.TokenHealth == AccountUsageImporterHealth.NotStarted ? importer.Health : importer.TokenHealth;
        var effectiveTokenDegraded = IsTokenLedgerDegraded(snapshot);
        AccountLedgerImporterHealthText.Text = effectiveTokenDegraded && tokenHealth == AccountUsageImporterHealth.Healthy
            ? $"Token 归账降级 · 旧快照 {FormatLastSeen(importer.TokenLastSuccessAt)} · "
              + $"{importer.TokenErrorClass ?? (snapshot.CoverageGapDetected ? "CoverageGap" : "TokenIntegrity")}"
            : tokenHealth switch
        {
            AccountUsageImporterHealth.Healthy => $"Token 归账正常 · 最近成功 {FormatLastSeen(importer.TokenLastSuccessAt)} · identity {importer.IdentityKeyState}",
            AccountUsageImporterHealth.Degraded => $"Token 归账降级 · 旧快照 {FormatLastSeen(importer.TokenLastSuccessAt)} · {importer.TokenErrorClass ?? "Unknown"}",
            AccountUsageImporterHealth.Stopped => $"Token 归账已停止 · 旧快照 {FormatLastSeen(importer.TokenLastSuccessAt)} · {importer.StoppedReason ?? "Unknown"}",
            _ => "归账导入器尚未启动"
        };
        if (snapshot.CoverageGapDetected)
            AccountLedgerImporterHealthText.Text += $" · 覆盖缺口：{snapshot.CoverageGapMessage ?? "逐账号不是完整覆盖"}";
        AccountLedgerImporterHealthText.Foreground = effectiveTokenDegraded
            ? new SolidColorBrush(Color.FromRgb(210, 78, 78))
            : new SolidColorBrush(Color.FromRgb(143, 200, 208));
        RequestScopeUsageText.Text = snapshot.RequestScopeUsage.FactCount == 0
            ? "请求级未归属用量：暂无；不会猜测 provider/account"
            : $"请求级未归属用量：{snapshot.RequestScopeUsage.RequestCount} 请求 · total {snapshot.RequestScopeUsage.Total.DisplayText}；不按 provider/account 分摊";
        UnverifiedUsageText.Text = snapshot.UnverifiedIdentityUsage.FactCount == 0
            ? "未验证身份用量：暂无"
            : $"未验证身份用量：{snapshot.UnverifiedIdentityUsage.FactCount} fact · total {snapshot.UnverifiedIdentityUsage.Total.DisplayText}；不进入精确逐账号总计";
        UnverifiedUsageText.Visibility = snapshot.UnverifiedIdentityUsage.FactCount == 0
            ? Visibility.Collapsed : Visibility.Visible;
        AccountLedgerSummaryList.ItemsSource = snapshot.Accounts;
        AccountLedgerAttemptList.ItemsSource = snapshot.RecentAttempts;
        AccountQuotaStatusText.Text = snapshot.QuotaStatus
                                      + $" · Quota 最近成功 {FormatLastSeen(importer.QuotaLastSuccessAt)}"
                                      + (string.IsNullOrWhiteSpace(importer.QuotaErrorClass) ? string.Empty : $" · {importer.QuotaErrorClass}");
        var quotaHealth = importer.QuotaHealth == AccountUsageImporterHealth.NotStarted ? importer.Health : importer.QuotaHealth;
        AccountQuotaStatusText.Foreground = quotaHealth is AccountUsageImporterHealth.Degraded or AccountUsageImporterHealth.Stopped
            || snapshot.QuotaIntegrityFailureCount > 0
            ? new SolidColorBrush(Color.FromRgb(210, 78, 78))
            : new SolidColorBrush(Color.FromRgb(227, 190, 117));
        AccountQuotaSnapshotList.ItemsSource = snapshot.LatestQuotaSnapshots;
    }

    internal static bool IsTokenLedgerDegraded(AccountUsageLedgerSnapshot snapshot)
    {
        var importer = snapshot.ImporterStatus;
        var tokenHealth = importer.TokenHealth == AccountUsageImporterHealth.NotStarted
            ? importer.Health : importer.TokenHealth;
        return tokenHealth is AccountUsageImporterHealth.Degraded or AccountUsageImporterHealth.Stopped
               || snapshot.TokenIntegrityFailureCount > 0
               || snapshot.TokenSourceStale
               || snapshot.CoverageGapDetected;
    }

    private void ToggleDetailsButton_Click(object sender, RoutedEventArgs e) =>
        PoolsList.Visibility = PoolsList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ToggleGatewayButton_Click(object sender, RoutedEventArgs e) =>
        GatewayPanel.Visibility = GatewayPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ApplyRoutingAudit(
        NativeRoutingAudit audit,
        AccountPoolView? active,
        RuntimeTruthSnapshot runtimeTruth)
    {
        var actual = runtimeTruth.LastExecution?.ActualAttempt;
        RoutingActualAccountText.Text = actual is null
            ? "暂无可确认记录"
            : $"{actual.AccountDisplayName} · 本地 {runtimeTruth.LastExecution?.Timestamp?.ToLocalTime():HH:mm:ss}";
        RoutingProLastText.Text = audit.ProLastRequestAt is null
            ? "本机日志中没有"
            : $"本地 {audit.ProLastRequestAt.Value.ToLocalTime():MM-dd HH:mm:ss}";
        var activeShortName = active?.DisplayName.Contains("Plus", StringComparison.OrdinalIgnoreCase) == true
            ? "Plus"
            : active?.DisplayName.Contains("Pro", StringComparison.OrdinalIgnoreCase) == true
                ? "Pro"
                : "当前线路";
        RoutingSinceSwitchLabelText.Text = $"切换到 {activeShortName} 后 Pro 请求";
        RoutingProSinceSwitchText.Text = $"{audit.ProSuccessfulRequestsSinceSwitch:N0} 次";
        RoutingAuditSourceText.Text = audit.SourceAvailable
            ? $"{audit.Message.Trim()} · 当前线路切换 本地 {audit.SwitchedAt.ToLocalTime():MM-dd HH:mm:ss}"
            : audit.Message;
    }

    private void ApplyActualExecution(RuntimeTruthSnapshot snapshot)
    {
        var execution = snapshot.LastExecution;
        var actual = execution?.ActualAttempt;
        if (execution is null || actual is null)
        {
            RecentActualText.Text = "暂无可确认记录";
            RecentActualDetailText.Text = "OpenCodex 日志来源缺失或还没有请求";
            return;
        }
        var status = execution.HttpStatus ?? actual.HttpStatus;
        RecentActualText.Text = $"{actual.ProviderDisplayName}/{actual.Model}";
        var stale = snapshot.LastExecutionPredatesPreference
            ? " · 切换前旧证据"
            : snapshot.LastExecutionIsStale ? " · 记录陈旧" : string.Empty;
        RecentActualDetailText.Text = $"实际账号：{actual.AccountDisplayName} · HTTP {status?.ToString() ?? "未知"} · 本地 {execution.Timestamp?.ToLocalTime():MM-dd HH:mm:ss}{stale}"
                                      + Environment.NewLine
                                      + RuntimeTruthDisplay.FormatAttempts(execution);
    }

    private static string RuntimeTruthStateLabel(RuntimeTruthSnapshot snapshot) => snapshot.Consistency.State switch
    {
        RuntimeTruthState.Consistent => "事实一致",
        RuntimeTruthState.Pending => "回答中 · 等待新证据",
        RuntimeTruthState.Diverged => "事实不一致",
        RuntimeTruthState.Stale when snapshot.LastExecutionPredatesPreference => "旧证据 · 发生在首选切换前",
        RuntimeTruthState.Stale => "一致 · 实际记录陈旧",
        RuntimeTruthState.Failed => "最近实际执行失败",
        _ => "事实来源不完整"
    };

    private void ManageOtherModelsButton_Click(object sender, RoutedEventArgs e) =>
        ManageOtherModelsRequested?.Invoke(this, EventArgs.Empty);

    private async Task<UnifiedGatewayStatus> ReadGatewaySafelyAsync(CancellationToken cancellationToken)
    {
        try { return await _services!.UnifiedGateway.ReadAsync(cancellationToken); }
        catch (Exception ex)
        {
            return new UnifiedGatewayStatus(
                false,
                _services!.UnifiedGateway.Url,
                "不可用",
                $"统一 API 状态读取失败：{ex.Message}",
                Array.Empty<string>(),
                Array.Empty<UnifiedGatewayPoolStatus>(),
                DateTimeOffset.Now);
        }
    }

    private void ApplyGatewayStatus(UnifiedGatewayStatus status)
    {
        _gatewayStatus = status;
        GatewayStatusText.Text = status.Summary;
        GatewayBadgeText.Text = status.Running ? "本机运行中" : "需要处理";
        GatewayUrlText.Text = status.Url;
        GatewayKeyText.Text = status.KeyHint;
        var preview = status.Models.Take(8).ToArray();
        GatewayModelsText.Text = status.Models.Count == 0
            ? "当前没有已验证的 Agent API 模型。"
            : $"当前 {status.Models.Count} 个模型：{string.Join(" · ", preview)}{(status.Models.Count > preview.Length ? " · …" : string.Empty)}";
        AuthorizePlusApiButton.IsEnabled = status.Pools.Any(pool => pool.PoolId == UnifiedGatewayService.PlusPoolId && pool.CanAuthorize);
        AuthorizeProApiButton.IsEnabled = status.Pools.Any(pool => pool.PoolId == UnifiedGatewayService.ProPoolId && pool.CanAuthorize);
    }

    private async void StartGatewayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        await RunAsync("正在验证各上游并同步统一 API 模型目录…", async () =>
        {
            var status = await _services.UnifiedGateway.EnsureReadyAsync();
            ApplyGatewayStatus(status);
            return status.Summary;
        });
    }

    private void CopyGatewayUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        Clipboard.SetText(_services.UnifiedGateway.Url);
        StatusMessage.Text = "统一 API 的 Base URL 已复制。";
    }

    private void CopyGatewayKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        Clipboard.SetText(_services.UnifiedGateway.GetClientKey());
        StatusMessage.Text = "统一 API Key 已复制；界面不会明文显示或写入日志。";
    }

    private async void AuthorizePlusApiButton_Click(object sender, RoutedEventArgs e) =>
        await AuthorizeGatewayPoolAsync(UnifiedGatewayService.PlusPoolId, "Plus");

    private async void AuthorizeProApiButton_Click(object sender, RoutedEventArgs e) =>
        await AuthorizeGatewayPoolAsync(UnifiedGatewayService.ProPoolId, "Pro");

    private async Task AuthorizeGatewayPoolAsync(string poolId, string label)
    {
        if (_busy || _services is null) return;
        if (MessageBox.Show(
                Window.GetWindow(this),
                $"将为 {label} Agent API 打开一次官方 OAuth 登录。\n\n请在官方页面确认登录的是 {label} 套餐账号。这个独立出口只允许保留一个账号，不会修改 Codex Desktop 当前扣费账号。",
                $"授权 {label} Agent API",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RunAsync($"正在等待 {label} Agent API 官方授权…", async () =>
        {
            var start = await _services.UnifiedGateway.StartAuthorizationAsync(poolId);
            await _services.UnifiedGateway.CompleteAuthorizationAsync(poolId, start.State, TimeSpan.FromMinutes(5));
            var status = await _services.UnifiedGateway.EnsureReadyAsync();
            ApplyGatewayStatus(status);
            return $"{label} Agent API 已完成独立授权并加入统一模型目录。";
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunAsync("正在刷新全部号池…", async () =>
        {
            _services?.AccountPools.InvalidateReadCache();
            await ReloadAsync();
            return "号池状态已刷新。";
        });
    }

    private async void ReturnOfficialButton_Click(object sender, RoutedEventArgs e) =>
        await SwitchPoolAsync(PoolCatalogDefaults.OfficialPoolId, selectedModel: null);

    private async void SwitchPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountPoolView pool }) return;
        await SwitchPoolAsync(pool.Id, pool.SelectedModel);
    }

    private async Task SwitchPoolAsync(string poolId, string? selectedModel)
    {
        if (_busy || _services is null) return;
        var pool = _services.PoolCatalog.Find(poolId);
        if (pool is null) return;
        var model = string.IsNullOrWhiteSpace(selectedModel) ? pool.DefaultModel : selectedModel;
        var warning = pool.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
            ? $"会把 Codex 全局账号线路固定到“{pool.DisplayName}”，模型为 {model}，并关闭自动切号和故障串池。Codex 正在运行时只在当前任务内热切换，不会自动重启；切换前后会核对能读到的任务连续性证据。"
            : $"线路会接入“{pool.DisplayName}”，实际模型为 {model}；桌面统一使用固定入口 cmm/main，不会偷用官方 Pro。Codex 正在运行时只热切换当前任务，不会自动重启。";
        if (MessageBox.Show(
                Window.GetWindow(this),
                $"确定切到“{pool.DisplayName}”吗？\n\n{warning}\n\n会先备份并核对 OpenCodex 状态；失败会回滚。",
                "确认切换号池",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await RunAsync($"正在切到 {pool.DisplayName} / {model}…", async () =>
        {
            var result = await _services.AccountPools.SwitchAsync(poolId, model);
            if (!result.Success) throw new InvalidOperationException(result.Message);
            await ReloadAsync();
            return result.Message;
        });
    }

    private async void AddCodexAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        await RunAsync("正在打开 Codex 官方登录页…", async () =>
        {
            try
            {
                var start = await _services.AccountPools.StartNativeCodexLoginAsync();
                StatusMessage.Text = "官方登录页已打开。登录成功后会自动验证账号并生成卡片（最多等待 5 分钟）…";
                var completed = await _services.AccountPools.CompleteNativeCodexLoginAsync(
                    start.FlowId,
                    TimeSpan.FromMinutes(5));
                await ReloadAsync();
                var label = string.IsNullOrWhiteSpace(completed.Email) ? "新 Codex 账号" : completed.Email;
                return $"{label} 已通过官方登录验证并自动生成账号卡片；没有复制或粘贴任何令牌。";
            }
            catch (OpenCodexAccountApiUnavailableException)
            {
                return "当前内建原生引擎不支持添加第二个 Codex 账号。Plus 卡片会保持停用，不会伪造账号或打开无效管理页。";
            }
        });
    }

    private async void SyncCodexAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        await RunAsync("正在刷新已有 Codex 账号…", async () =>
        {
            await ReloadAsync();
            var nativeCount = _pools.Count(pool => pool.SectionOrder == 0 && pool.Accounts.Count > 0);
            return $"已刷新 {nativeCount} 个已有 Codex 账号的健康状态、套餐和额度；没有新增或导入账号。";
        });
    }

    private async void AddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null || sender is not Button { Tag: string poolId }) return;
        var pool = _services.PoolCatalog.Find(poolId);
        if (pool is null) return;
        if (pool.Transport == PoolTransport.CliProxyApi)
        {
            if (MessageBox.Show(
                    Window.GetWindow(this),
                    $"将打开 OpenAI 官方授权页。\n\n请确认选择要放进“{pool.DisplayName}”的账号。一个独立出口只能放一个账号；当前官方 Pro 主账号不应放进这里。",
                    "开始 OAuth 授权",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            await RunAsync("正在启动独立 CLIProxyAPI 并打开授权页…", async () =>
            {
                var start = await _services.AccountPools.StartOAuthAsync(poolId);
                StatusMessage.Text = "已打开官方授权页，正在等待登录完成（最多 5 分钟）…";
                await _services.AccountPools.CompleteOAuthAsync(poolId, start.State);
                await ReloadAsync();
                return "OAuth 账号已放进这个独立出口，API 模型目录已验证。这个出口不会再接受第二个账号。";
            });
            return;
        }

    }

    private void ConfigurePoolButton_Click(object sender, RoutedEventArgs e)
    {
        StatusMessage.Text = "这个号池不需要单独配置。";
    }

    private async void AccountActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null || sender is not Button { DataContext: PoolAccountView account }) return;
        var pool = _services.PoolCatalog.Find(account.PoolId);
        if (pool is null) return;
        if (pool.Transport == PoolTransport.NativeCodexAccount)
        {
            if (MessageBox.Show(
                    Window.GetWindow(this),
                    $"确定删除“{account.Label}”吗？\n\n删除前会备份 OpenCodex 配置、账号凭据库和大管家号池清单。Pro 主账号禁止删除；如果它是当前账号，必须先切到别的账号并发送一条消息确认。\n\n已经绑定这个账号的旧任务可能无法继续请求，建议先确认不再使用。",
                    "确认删除 Codex 账号",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await RunAsync("正在备份并删除 Codex 账号…", async () =>
            {
                var backup = await _services.AccountPools.DeleteNativeCodexAccountAsync(
                    pool.Id,
                    account.Id);
                await ReloadAsync();
                return $"已删除 {account.Label}，账号卡片已同步移除。删除前备份保存在：{backup}";
            });
            return;
        }

        if (pool.Transport == PoolTransport.CliProxyApi)
        {
            var enabled = !account.Enabled;
            var action = enabled ? "恢复" : "停用";
            if (MessageBox.Show(
                    Window.GetWindow(this),
                    $"确定{action}账号“{account.Label}”吗？\n\n只修改启用状态，OAuth 文件保留，可随时恢复。",
                    $"{action}账号",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await RunAsync($"正在{action}账号…", async () =>
            {
                await _services.AccountPools.SetCliProxyAccountEnabledAsync(pool.Id, account.Id, enabled);
                await ReloadAsync();
                return $"账号已{action}，凭据没有删除。";
            });
            return;
        }

    }

    private async void TogglePoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _services is null || sender is not Button { Tag: string poolId }) return;
        var pool = _services.PoolCatalog.Find(poolId);
        if (pool is null || pool.IsProtected) return;
        var enable = !pool.Enabled;
        var action = enable ? "恢复" : "停用";
        if (MessageBox.Show(
                Window.GetWindow(this),
                $"确定{action}“{pool.DisplayName}”吗？\n\n号池定义、凭据和账号都保留，不执行物理删除。",
                $"{action}号池",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync($"正在{action}号池…", async () =>
        {
            _services.AccountPools.SetPoolEnabled(poolId, enable);
            await ReloadAsync();
            return $"号池已{action}，配置和凭据均已保留。";
        });
    }

    private async Task RunAsync(string pending, Func<Task<string>> action)
    {
        if (_busy) return;
        _busy = true;
        IsEnabled = false;
        StatusMessage.Text = pending;
        try
        {
            var message = await action();
            StatusMessage.Text = message;
        }
        catch (Exception ex)
        {
            StatusMessage.Text = ex.Message;
            MessageBox.Show(Window.GetWindow(this), ex.Message, "操作没有完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            _busy = false;
        }
    }
}
