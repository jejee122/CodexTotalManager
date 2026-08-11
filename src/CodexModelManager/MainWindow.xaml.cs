using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexModelManager.Models;
using CodexModelManager.Services;
using Microsoft.Win32;

namespace CodexModelManager;

public partial class MainWindow : Window
{
    private readonly AppServices _services = AppServices.Create();
    private readonly CodexConfigService? _realCodexConfig = RuntimeMode.AllowsRealCodexConnectionToggle
        ? new CodexConfigService()
        : null;
    private readonly ObservableCollection<ModelOption> _officialModels = new();
    private readonly ObservableCollection<ModelOption> _customModels = new();
    private readonly List<ModelOption> _allOfficialModels = new();
    private readonly List<ModelOption> _allCustomModels = new();
    private readonly ObservableCollection<ProviderView> _providers = new();
    private readonly ObservableCollection<DreamSkinThemeView> _themes = new();
    private readonly ObservableCollection<LocalServiceView> _localServices = new();
    private readonly ObservableCollection<BackupItemView> _backupItems = new();
    private readonly ObservableCollection<TokenSourceLedgerRow> _tokenSourceRows = new();
    private DreamSkinSnapshot? _themeSnapshot;
    private string? _editingProviderId;
    private bool _busy;
    private bool _usageImporterStopRequested;

    public MainWindow()
    {
        InitializeComponent();
        OfficialModelsList.ItemsSource = _officialModels;
        CustomModelsList.ItemsSource = _customModels;
        ProvidersList.ItemsSource = _providers;
        AccountsPage.Initialize(_services);
        SubagentsPage.Initialize(_services);
        AccountsPage.ManageOtherModelsRequested += (_, _) => ShowModelsPage();
        AccountsPage.LiveUsageUpdated += RefreshTokenSourceLedger;
        TokenSourceRowsList.ItemsSource = _tokenSourceRows;
        ThemeCardsList.ItemsSource = _themes;
        LocalServicesList.ItemsSource = _localServices;
        BackupList.ItemsSource = _backupItems;
        ServerEventStreamList.ItemsSource = _serverFeed;
        ServerCardsList.ItemsSource = _serverCards;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximize();
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e) => ToggleWindowMaximize();

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeWindowButton is null) return;
        MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        MaximizeWindowButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshCodexConnectionUi();
        if (RuntimeMode.IsDetachedUi)
        {
            PrepareDetachedUi();
            StartServerMonitoring();
            await RefreshDetachedLocalStatusAsync();
            return;
        }
        _services.AccountUsageImporter.Start();
        await InitializeAsync();
        ShowAccountsPage();
        StartServerMonitoring();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (RuntimeMode.IsDetachedUi)
        {
            base.OnClosing(e);
            return;
        }
        if (!_usageImporterStopRequested)
        {
            _usageImporterStopRequested = true;
            try
            {
                _services.AccountUsageImporter.StopAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // Shutdown is bounded. The importer publishes a sanitized stopped/degraded state when possible.
            }
        }
        try
        {
            // Keep the loopback engine alive only while the owned v2 Codex route
            // is connected. A detached window-owned engine is safe to stop.
            if (!_services.CodexConfig.IsManagedNativeProviderSelected())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _services.Process.StopOwnedNativeEngineAsync(timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Shutdown remains bounded. A failed ownership/config check never justifies
            // killing an engine that Codex may still be using.
        }
        base.OnClosing(e);
    }

    private async Task InitializeAsync()
    {
        SetBusy(true, "正在连接 OpenCodex…");
        try
        {
            if (!await _services.OpenCodex.IsHealthyAsync())
            {
                FooterMessage.Text = "OpenCodex 没有运行，正在安全启动…";
                if (!await _services.Process.EnsureOpenCodexAsync())
                    throw new InvalidOperationException("OpenCodex 没能启动。请先确认 v2rayN 正在运行。");
            }
            await ReloadAsync();
            SetConnection(true, "本机入口正常");
            var localWarning = _services.Settings.LoadWarning ?? _services.Secrets.LoadWarning;
            FooterMessage.Text = localWarning
                ?? "准备就绪。模型、账号和本机连接都已经汇总到首页。";
        }
        catch (Exception ex)
        {
            SetConnection(false, "本机入口失败");
            FooterMessage.Text = FriendlyError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadAsync()
    {
        var modelsTask = _services.OpenCodex.GetModelsAsync(_services.Settings);
        var providersTask = _services.OpenCodex.GetProvidersAsync(_services.Settings);
        var runtimeTruthTask = _services.RuntimeTruth.ReadAsync();
        var v2rayTask = _services.Dashboard.IsV2rayReadyAsync();
        var runtimeTask = _services.OpenCodex.GetRuntimeStatusAsync();
        var quotaReportsTask = _services.OpenCodex.GetProviderQuotaReportsAsync();
        var recentUsageTask = _services.OpenCodex.GetRecentUsageAsync();
        var usageTimelineTask = _services.OpenCodex.GetUsageTimelineAsync();
        var themesTask = _services.DreamSkin.DiscoverAsync();
        await Task.WhenAll(modelsTask, providersTask, runtimeTruthTask, runtimeTask,
            quotaReportsTask, recentUsageTask, usageTimelineTask, themesTask);

        var models = modelsTask.Result;
        var recentUsage = recentUsageTask.Result;
        RefreshTokenActivityUi(usageTimelineTask.Result);
        var runtimeTruth = runtimeTruthTask.Result;
        if (ReconcilePoolSelectedInCodex(runtimeTruth.Task))
            runtimeTruth = await _services.RuntimeTruth.ReadAsync();
        await AccountsPage.ReloadAsync(runtimeTruth);
        var activePool = _services.PoolCatalog.Find(runtimeTruth.Preferred.PoolId);
        var currentTaskRouted = runtimeTruth.Task.MatchesPreference;
        (string Provider, string Model)? active = runtimeTruth.ConfiguredRoute is null
            ? null
            : (runtimeTruth.ConfiguredRoute.Provider, runtimeTruth.ConfiguredRoute.Model);
        foreach (var model in models)
        {
            model.IsActive = active is not null
                             && model.Provider.Equals(active.Value.Provider, StringComparison.OrdinalIgnoreCase)
                             && model.Id.Equals(active.Value.Model, StringComparison.OrdinalIgnoreCase);
            model.IsCurrentTaskRouted = currentTaskRouted;
            var modelUsage = recentUsage.FindModel(model.Provider, model.Id);
            model.UsageText = modelUsage is null
                ? $"本机最近 {recentUsage.LogCount} 条日志中暂无使用记录"
                : $"本机最近日志（不区分账号）：{modelUsage.RequestCount} 次 · {UsageFormatting.Number(modelUsage.TotalTokens)} Token{modelUsage.CostText}";
        }

        _allOfficialModels.Clear();
        _allOfficialModels.AddRange(models
            .Where(model => model.IsOfficial)
            .OrderByDescending(model => model.Id.Contains("5.6", StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase));
        _allCustomModels.Clear();
        _allCustomModels.AddRange(models
            .Where(model => !model.IsOfficial && model.Provider is not "openai" and not "combo")
            .OrderBy(model => model.ProviderLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(model => model.Title, StringComparer.CurrentCultureIgnoreCase));
        ApplyModelFilter();

        var modelCounts = models
            .GroupBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var customProviders = providersTask.Result.Where(provider => provider.Id != "openai").ToArray();
        var quotaReports = quotaReportsTask.Result.ToDictionary(report => report.Provider, StringComparer.OrdinalIgnoreCase);
        foreach (var provider in customProviders)
        {
            provider.ModelCount = modelCounts.TryGetValue(provider.Id, out var count) ? count : 0;
            var providerUsage = recentUsage.FindProvider(provider.Id);
            provider.UsageText = providerUsage is null
                ? $"本机最近 {recentUsage.LogCount} 条日志中暂无请求"
                : providerUsage.DetailedText + providerUsage.CostText;
            if (quotaReports.TryGetValue(provider.Id, out var quotaReport))
            {
                provider.QuotaWindows = quotaReport.Windows;
                var reverseText = quotaReport.ReverseEngineered ? " · 非官方兼容字段" : string.Empty;
                var timeText = quotaReport.UpdatedAt is null ? string.Empty : $" · 更新 {quotaReport.UpdatedAt:MM-dd HH:mm:ss}";
                provider.QuotaText = $"额度来源：{quotaReport.Source}{reverseText}{timeText}";
            }
            else
            {
                provider.QuotaText = "此 API 未提供可读取的套餐额度；下方只显示本机日志统计，不代表官方剩余额度。";
            }
        }
        Replace(_providers, customProviders.OrderBy(provider => provider.DisplayName, StringComparer.CurrentCultureIgnoreCase));

        CurrentModelText.Text = string.IsNullOrWhiteSpace(runtimeTruth.Preferred.PreferredModel)
            ? "首选模型未知"
            : runtimeTruth.Preferred.PreferredModel;
        CurrentModelDetail.Text = $"首选账号：{runtimeTruth.Preferred.PreferredAccountDisplayName} · 首选入口：{runtimeTruth.Task.ExpectedModelLabel}";
        HomeModelText.Text = string.IsNullOrWhiteSpace(runtimeTruth.Preferred.PreferredModel)
            ? "首选模型未知"
            : runtimeTruth.Preferred.PreferredModel;
        HomeModelDetail.Text = $"当前任务显示：{runtimeTruth.Task.DisplayedModelLabel} · {RuntimeTruthStateLabel(runtimeTruth)}";
        CurrentChatStatusText.Text = $"当前任务显示：{runtimeTruth.Task.DisplayedModelLabel} · {runtimeTruth.Consistency.Message}";
        CurrentChatStatusText.Foreground = runtimeTruth.Consistency.State == RuntimeTruthState.Consistent
            ? new SolidColorBrush(Color.FromRgb(20, 125, 100))
            : new SolidColorBrush(Color.FromRgb(138, 104, 24));
        RecentRouteText.Text = FormatActualExecution(runtimeTruth);
        RecentRouteText.Foreground = runtimeTruth.LastExecution?.Outcome == RuntimeExecutionOutcome.Succeeded
                                     && !runtimeTruth.LastExecutionIsStale
            ? new SolidColorBrush(Color.FromRgb(20, 125, 100))
            : new SolidColorBrush(Color.FromRgb(138, 104, 24));

        var accountsHealthy = AccountsPage.OfficialHealthy;
        HomeAccountText.Text = runtimeTruth.Preferred.PreferredAccountDisplayName;
        HomeAccountDetail.Text = FormatActualAccount(runtimeTruth);

        var v2rayReady = await v2rayTask;
        var runtime = runtimeTask.Result;
        HomeLocalServiceText.Text = runtime.Healthy && v2rayReady
            ? "OpenCodex、v2rayN 可用"
            : runtime.Healthy ? "OpenCodex 可用，v2rayN 异常" : "OpenCodex 不可用";
        HomeLocalServiceDetail.Text = runtime.Healthy && v2rayReady
            ? "已验证本机入口与代理端口"
            : runtime.Healthy ? "私人或国外模型可能无法访问" : runtime.LastError;
        OpenCodexServiceTitle.Text = runtime.Healthy ? "运行正常" : "没有运行";
        OpenCodexServiceDetail.Text = runtime.Healthy
            ? $"本地端口 {runtime.Port} · PID {runtime.ProcessId?.ToString() ?? "未知"} · 已运行 {FormatUptime(runtime.Uptime)}"
            : $"端口 {runtime.Port} 未通过健康检查 · {runtime.LastError}";
        V2rayServiceTitle.Text = v2rayReady ? "运行正常" : "没有检测到连接";
        StartServicesButton.IsEnabled = !(runtime.Healthy && v2rayReady);
        StartServicesButton.Content = runtime.Healthy && v2rayReady ? "全部正常，无需启动" : "安全启动缺少的服务";
        BackupServiceTitle.Text = $"已有 {_services.Backups.Count} 份安全备份";

        UpdateThemeUi(themesTask.Result);

        var memorySafe = _services.CodexConfig.MemoryProtectionLooksSafe();
        var localConfigWarning = _services.Settings.LoadWarning ?? _services.Secrets.LoadWarning;
        MemoryStatusTitle.Text = localConfigWarning is not null
            ? "本机配置需要处理"
            : memorySafe ? "记忆保护正常" : "发现旧配置";
        MemoryStatusDetail.Text = localConfigWarning
            ?? (memorySafe ? "不改聊天来源，不删除历史记录" : "检测到 custom，已停止自动修改");
        MemoryStatusTitle.Foreground = memorySafe && localConfigWarning is null
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(255, 221, 155));
        MemoryServiceTitle.Text = localConfigWarning is not null
            ? "配置损坏，已停止写入"
            : memorySafe ? "保护正常" : "发现旧配置，自动修改已停止";

        var allReady = memorySafe && localConfigWarning is null && accountsHealthy && v2rayReady && runtime.Healthy
                       && runtimeTruth.Consistency.State == RuntimeTruthState.Consistent;
        HomeOverallTitle.Text = allReady ? "Codex 核心功能已经验证可用" : "Codex 当前是部分可用";
        HomeOverallDetail.Text = allReady
            ? "模型入口、账号池和本机网络已现场检查；服务器状态仍以最近一次只读采样为准。"
            : "至少一项真实检查没有通过；页面不会用“全部正常”掩盖故障。";

        var fixedEntryReady = runtimeTruth.Preferred.CodexDefaultMatchesExpected;
        var configuredDefaultModel = runtimeTruth.Preferred.CodexDefaultModel;
        if (!fixedEntryReady && !string.IsNullOrWhiteSpace(configuredDefaultModel))
        {
            var visibleDefaultModel = OpenCodexClient.ToUserVisibleModelName(
                configuredDefaultModel,
                null,
                "未配置");
            HomeModelText.Text = $"当前默认：{visibleDefaultModel}";
            HomeModelDetail.Text = activePool?.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
                ? $"原生账号：{AccountsPage.ActiveTitle}"
                : active is null ? "号池路由尚未准备好" : $"号池主路由：{active.Value.Model}";
        }
        var nativeAccountActive = activePool?.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount;
        var modelCapabilityReady = runtime.Healthy && memorySafe && fixedEntryReady
                                   && (nativeAccountActive || currentTaskRouted);
        HomeModelCapability.Text = modelCapabilityReady ? "已经可用" : "部分可用";
        HomeModelCapabilityDetail.Text = runtime.Healthy
            ? nativeAccountActive
                ? fixedEntryReady
                    ? "当前账号线路和原生模型已就绪；下一条请求按当前线路发送"
                    : "当前账号线路或原生模型尚未应用"
                : fixedEntryReady && currentTaskRouted
                    ? "当前任务已接入当前外部 API"
                    : fixedEntryReady ? "外部 API 入口已就绪；当前任务尚未接入" : "当前默认模型与外部 API 入口不一致"
            : "OpenCodex 不可用，仍保留官方直连备用";
        try
        {
            var backups = _services.BackupCatalog.List();
            HomeBackupCapability.Text = backups.Any(item => item.CanRestore) ? "已经可用" : "部分可用";
            HomeBackupCapabilityDetail.Text = $"{backups.Count} 项；删除进入回收站";
        }
        catch
        {
            HomeBackupCapability.Text = "不可用";
            HomeBackupCapabilityDetail.Text = "备份目录读取失败";
        }

        FirstUseNotice.Visibility = Visibility.Visible;
    }

    private async void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: ModelOption model } || model.Disabled) return;
        var codexModel = model.IsOfficial
            ? model.Id
            : string.IsNullOrWhiteSpace(model.Namespaced) ? $"{model.Provider}/{model.Id}" : model.Namespaced;
        if (MessageBox.Show(
                this,
                $"确定把“{model.Title}”设为 Codex 默认模型吗？\n\n" +
                "总管家不会重启 Codex，也不会自动点击当前任务的模型菜单。当前任务如果也要切换，请在 Codex 自己的模型列表里选择同名模型；这样不会伪造热切换成功。",
                "确认切换模型",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true, $"正在切换到 {model.Title}…");
        try
        {
            if (!_services.CodexConfig.IsManagedNativeProviderSelected())
                throw new InvalidOperationException("Codex 还没有连接总管家。请先打开“一键连接 Codex”开关。");
            if (!await _services.Process.EnsureOpenCodexAsync())
                throw new InvalidOperationException("Native Engine、模型目录或就绪检查没有通过。");
            _services.CodexConfig.SetDefaultModel(codexModel);
            await _services.OpenCodex.SetActiveTargetAsync(model.Provider, model.Id);
            var verified = await _services.OpenCodex.GetActiveTargetAsync();
            if (verified is null
                || !verified.Value.Provider.Equals(model.Provider, StringComparison.OrdinalIgnoreCase)
                || !verified.Value.Model.Equals(model.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native Engine 没有确认这次默认路由选择。");

            await ReloadAsync();
            FooterMessage.Text = $"已把 {model.Title} 写入 Codex 默认模型；没有重启 Codex，也没有碰当前任务。";
            MessageBox.Show(
                $"默认模型已设为 {model.Title}。\n\n当前任务若要马上切换，请在 Codex 自己的模型菜单里选择“{codexModel}”。如果列表仍是旧的，请由你手动重新打开 Codex；总管家不会替你重启。",
                "模型已准备",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            FooterMessage.Text = "没有修改当前任务。";
            MessageBox.Show(
                $"没有完成：{FriendlyError(ex)}",
                "没有切换",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<List<string>> RollbackModelSwitchAsync(
        (string Provider, string Model)? oldTarget,
        string? openCodexBackup,
        string? codexSnapshot,
        CodexDesktopState? desktopBefore)
    {
        var errors = new List<string>();
        var targetRestored = false;
        string? directRestoreError = null;
        if (oldTarget is not null)
        {
            try
            {
                await _services.OpenCodex.SetActiveTargetAsync(oldTarget.Value.Provider, oldTarget.Value.Model);
                var current = await _services.OpenCodex.GetActiveTargetAsync();
                targetRestored = current is not null
                                 && current.Value.Provider.Equals(oldTarget.Value.Provider, StringComparison.OrdinalIgnoreCase)
                                 && current.Value.Model.Equals(oldTarget.Value.Model, StringComparison.OrdinalIgnoreCase);
                if (!targetRestored) directRestoreError = "原模型没有确认恢复";
            }
            catch (Exception rollbackException)
            {
                directRestoreError = $"原模型恢复失败：{FriendlyError(rollbackException)}";
            }
        }

        if (!targetRestored && openCodexBackup is not null)
        {
            var stopped = false;
            var backupRestored = false;
            try
            {
                stopped = await _services.Process.StopOpenCodexAsync();
                if (!stopped) throw new InvalidOperationException("本地模型连接没有停止");
                _services.Backups.Restore(openCodexBackup);
                backupRestored = true;
            }
            catch (Exception rollbackException)
            {
                errors.Add($"OpenCodex 备份恢复失败：{FriendlyError(rollbackException)}");
            }
            if (stopped || !await _services.OpenCodex.IsHealthyAsync())
            {
                try
                {
                    if (!await _services.Process.StartOpenCodexAsync())
                        throw new InvalidOperationException("本地模型连接没有重新启动");
                }
                catch (Exception rollbackException)
                {
                    errors.Add($"OpenCodex 重启失败：{FriendlyError(rollbackException)}");
                }
            }
            if (!backupRestored && directRestoreError is not null) errors.Add(directRestoreError);
        }
        else if (!targetRestored && directRestoreError is not null)
        {
            errors.Add(directRestoreError);
        }

        if (codexSnapshot is not null)
        {
            try
            {
                _services.CodexConfig.RestoreSnapshot(codexSnapshot);
            }
            catch (Exception rollbackException)
            {
                errors.Add($"Codex 配置恢复失败：{FriendlyError(rollbackException)}");
            }
        }
        if (desktopBefore is { Connected: true }
            && !string.IsNullOrWhiteSpace(desktopBefore.CurrentModel))
        {
            try
            {
                var restored = await _services.CodexDesktop.EnsureCurrentChatUsesAliasAsync(
                    desktopBefore.CurrentModel!);
                if (restored.Status != CodexAliasSwitchStatus.Success)
                    errors.Add($"原任务模型没有热恢复：{restored.Message}");
            }
            catch (Exception rollbackException)
            {
                errors.Add($"原任务模型热恢复失败：{FriendlyError(rollbackException)}");
            }
        }
        return errors;
    }

    private async void AddProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var displayName = ProviderNameBox.Text.Trim();
        var url = ProviderUrlBox.Text.Trim();
        var apiKey = ProviderKeyBox.Password;
        var adapter = (ProviderAdapterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "openai-chat";
        AddProviderResult.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            ShowAddError("请先给这个模型来源起个名字。");
            return;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowAddError("请填写 URL。");
            return;
        }
        if (!int.TryParse(ProviderContextBox.Text.Trim(), out var contextWindow)
            || contextWindow is < 4096 or > 2000000)
        {
            ShowAddError("上下文长度请填写 4096 到 2000000 之间的整数。拿不准就保留 128000。");
            return;
        }
        if (_editingProviderId is not null)
        {
            await UpdateExistingProviderAsync(
                _editingProviderId,
                displayName,
                url,
                apiKey,
                adapter,
                contextWindow);
            return;
        }
        var confirm = MessageBox.Show(
            "添加完成时会短暂重启本地模型连接。请先等 Codex 当前回答结束。\n\n继续添加吗？",
            "添加模型来源",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, "正在测试 URL 和 API Key…");
        string? backup = null;
        string? providerId = null;
        try
        {
            var probe = await _services.Probe.ProbeAsync(url, apiKey);
            FooterMessage.Text = $"连接成功，发现 {probe.Models.Count} 个模型。正在安全保存…";
            var existing = await _services.OpenCodex.GetProvidersAsync(_services.Settings);
            providerId = ProviderId.From(displayName, probe.BaseUrl, existing.Select(item => item.Id));
            backup = _services.Backups.Create();
            _services.Secrets.Save(providerId, apiKey);
            var envName = _services.Secrets.GetEnvironmentName(providerId);
            var allowPrivate = Uri.TryCreate(probe.BaseUrl, UriKind.Absolute, out var baseUri)
                               && (baseUri.IsLoopback || baseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
            await _services.OpenCodex.AddProviderAsync(
                providerId,
                probe.BaseUrl,
                $"${{{envName}}}",
                probe.Models,
                adapter,
                contextWindow,
                allowPrivate);
            if (!await _services.Process.RestartOpenCodexAsync())
                throw new InvalidOperationException("模型连接重启失败。");
            var test = await _services.OpenCodex.TestProviderAsync(providerId);
            if (!test.Success) throw new InvalidOperationException($"保存后的复查没有通过：{test.Message}");

            _services.Settings.SetProviderName(providerId, displayName);
            ProviderKeyBox.Clear();
            ProviderNameBox.Clear();
            ProviderUrlBox.Clear();
            AddProviderResult.Foreground = new SolidColorBrush(Color.FromRgb(20, 125, 100));
            AddProviderResult.Text = $"添加成功：发现 {probe.Models.Count} 个模型，耗时 {probe.LatencyMs} 毫秒。";
            await ReloadAsync();
            FooterMessage.Text = $"{displayName} 已加入 Codex。现在可以到“切换模型”里选择。";
        }
        catch (Exception ex)
        {
            var rollbackErrors = backup is not null && providerId is not null
                ? await RollbackAddedProviderAsync(providerId, backup)
                : new List<string>();
            var rollbackOk = rollbackErrors.Count == 0;
            ShowAddError(rollbackOk
                ? $"没有添加：{FriendlyError(ex)} 原来的 Codex 配置已恢复。"
                : $"没有添加：{FriendlyError(ex)} 自动恢复没有全部完成：{string.Join("；", rollbackErrors)}");
            FooterMessage.Text = rollbackOk
                ? "添加失败，原来的模型仍然可用。"
                : "添加失败，而且自动恢复没有完成。请不要继续切换。";
        }
        finally
        {
            apiKey = string.Empty;
            SetBusy(false);
        }
    }

    private async Task<List<string>> RollbackAddedProviderAsync(string providerId, string backup)
    {
        var errors = new List<string>();
        var stopped = false;
        var configRestored = false;
        try
        {
            var providers = await _services.OpenCodex.GetProvidersAsync(_services.Settings);
            if (providers.Any(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
                await _services.OpenCodex.DeleteProviderAsync(providerId);
            configRestored = true;
        }
        catch
        {
            // 接口撤销失败时，下面才会使用磁盘备份。
        }

        if (!configRestored)
        {
            try
            {
                stopped = await _services.Process.StopOpenCodexAsync();
                if (!stopped) throw new InvalidOperationException("本地模型连接没有停止");
                _services.Backups.Restore(backup);
                configRestored = true;
            }
            catch (Exception rollbackException)
            {
                errors.Add($"模型来源恢复失败：{FriendlyError(rollbackException)}");
            }
        }

        try { _services.Secrets.Remove(providerId); }
        catch (Exception rollbackException)
        {
            errors.Add($"临时密钥清理失败：{FriendlyError(rollbackException)}");
        }
        try { _services.Settings.RemoveProviderName(providerId); }
        catch (Exception rollbackException)
        {
            errors.Add($"临时名称清理失败：{FriendlyError(rollbackException)}");
        }

        if (stopped || !await _services.OpenCodex.IsHealthyAsync())
        {
            try
            {
                if (!await _services.Process.StartOpenCodexAsync())
                    throw new InvalidOperationException("本地模型连接没有重新启动");
            }
            catch (Exception rollbackException)
            {
                errors.Add($"OpenCodex 重启失败：{FriendlyError(rollbackException)}");
            }
        }
        return errors;
    }

    private async void DeleteProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button button || button.Tag is not string providerId) return;
        var provider = _providers.FirstOrDefault(item => item.Id == providerId);
        if (provider is null) return;
        var confirm = MessageBox.Show(
            $"这会从 Codex 模型列表里移除“{provider.DisplayName}”。\n\n聊天记录不会删除。如果当前正在使用它，会先切回官方 GPT-5.6 Sol。",
            "删除模型来源",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, $"正在移除 {provider.DisplayName}…");
        string? backup = null;
        string? codexSnapshot = null;
        string? savedSecret = null;
        string? savedName = null;
        (string Provider, string Model)? oldTarget = null;
        try
        {
            backup = _services.Backups.Create();
            codexSnapshot = _services.CodexConfig.CreateSnapshot();
            savedSecret = _services.Secrets.Read(providerId);
            if (_services.Settings.TryGetProviderName(providerId, out var storedName)) savedName = storedName;
            oldTarget = await _services.OpenCodex.GetActiveTargetAsync();
            if (oldTarget is not null && oldTarget.Value.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                await _services.OpenCodex.SetActiveTargetAsync("openai", "gpt-5.6-sol");
                _services.CodexConfig.SetDefaultModel(OpenCodexClient.SwitchAlias);
            }
            await _services.OpenCodex.DeleteProviderAsync(providerId);
            _services.Secrets.Remove(providerId);
            _services.Settings.RemoveProviderName(providerId);
            await ReloadAsync();
            FooterMessage.Text = $"已移除 {provider.DisplayName}，聊天记录没有改动。";
        }
        catch (Exception ex)
        {
            var rollbackErrors = await RollbackDeletedProviderAsync(
                providerId,
                backup,
                codexSnapshot,
                savedSecret,
                savedName,
                oldTarget);
            var rollbackOk = rollbackErrors.Count == 0;
            FooterMessage.Text = rollbackOk
                ? $"没有删除，原来的来源、密钥和名称都已恢复。{FriendlyError(ex)}"
                : $"自动恢复没有完成：{string.Join("；", rollbackErrors)}";
            MessageBox.Show(
                rollbackOk
                    ? $"没有删除：{FriendlyError(ex)}，原配置已恢复。"
                    : $"没有删除：{FriendlyError(ex)}\n\n自动恢复没有全部完成：{string.Join("；", rollbackErrors)}",
                "操作未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<List<string>> RollbackDeletedProviderAsync(
        string providerId,
        string? backup,
        string? codexSnapshot,
        string? savedSecret,
        string? savedName,
        (string Provider, string Model)? oldTarget)
    {
        var errors = new List<string>();
        var stopped = false;
        var openCodexRestored = backup is null;
        try
        {
            if (await _services.OpenCodex.IsHealthyAsync())
            {
                var providers = await _services.OpenCodex.GetProvidersAsync(_services.Settings);
                openCodexRestored = providers.Any(item =>
                    item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
            openCodexRestored = false;
        }
        if (backup is not null)
        {
            if (!openCodexRestored)
            {
                try
                {
                    stopped = await _services.Process.StopOpenCodexAsync();
                    if (!stopped) throw new InvalidOperationException("本地模型连接没有停止");
                    _services.Backups.Restore(backup);
                    openCodexRestored = true;
                }
                catch (Exception rollbackException)
                {
                    errors.Add($"模型来源恢复失败：{FriendlyError(rollbackException)}");
                }
            }
        }

        if (savedSecret is not null)
        {
            try { _services.Secrets.Save(providerId, savedSecret); }
            catch (Exception rollbackException)
            {
                errors.Add($"密钥恢复失败：{FriendlyError(rollbackException)}");
            }
        }
        if (savedName is not null)
        {
            try { _services.Settings.SetProviderName(providerId, savedName); }
            catch (Exception rollbackException)
            {
                errors.Add($"名称恢复失败：{FriendlyError(rollbackException)}");
            }
        }

        if (stopped || !await _services.OpenCodex.IsHealthyAsync())
        {
            try
            {
                if (!await _services.Process.StartOpenCodexAsync())
                    throw new InvalidOperationException("本地模型连接没有重新启动");
            }
            catch (Exception rollbackException)
            {
                errors.Add($"OpenCodex 重启失败：{FriendlyError(rollbackException)}");
            }
        }

        if (openCodexRestored && oldTarget is not null && await _services.OpenCodex.IsHealthyAsync())
        {
            try { await _services.OpenCodex.SetActiveTargetAsync(oldTarget.Value.Provider, oldTarget.Value.Model); }
            catch (Exception rollbackException)
            {
                errors.Add($"原模型恢复失败：{FriendlyError(rollbackException)}");
            }
        }
        if (codexSnapshot is not null)
        {
            try { _services.CodexConfig.RestoreSnapshot(codexSnapshot); }
            catch (Exception rollbackException)
            {
                errors.Add($"Codex 默认模型恢复失败：{FriendlyError(rollbackException)}");
            }
        }
        return errors;
    }

    private void RefreshTokenActivityUi(UsageTimelineSnapshot snapshot)
    {
        TokenHeatmapGrid.Children.Clear();
        TokenHeatmapGrid.ColumnDefinitions.Clear();
        TokenHeatmapGrid.RowDefinitions.Clear();
        TokenMonthLabelsGrid.Children.Clear();
        TokenMonthLabelsGrid.ColumnDefinitions.Clear();

        const int weekCount = 53;
        for (var column = 0; column < weekCount; column++)
        {
            TokenHeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition());
            TokenMonthLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var row = 0; row < 7; row++)
            TokenHeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

        var today = DateOnly.FromDateTime(DateTime.Today);
        var mondayOffset = ((int)today.DayOfWeek + 6) % 7;
        var currentMonday = today.AddDays(-mondayOffset);
        var start = currentMonday.AddDays(-(weekCount - 1) * 7);
        var byDate = snapshot.Days.ToDictionary(day => day.Date);
        var visibleMaximum = byDate
            .Where(pair => pair.Key >= start && pair.Key <= today)
            .Select(pair => pair.Value.TotalTokens)
            .DefaultIfEmpty(0)
            .Max();

        var lastMonth = -1;
        for (var week = 0; week < weekCount; week++)
        {
            var weekStart = start.AddDays(week * 7);
            if (week == 0 || weekStart.Month != lastMonth)
            {
                var label = new TextBlock
                {
                    Text = $"{weekStart.Month}月",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(121, 153, 158)),
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(label, week);
                Grid.SetColumnSpan(label, Math.Min(4, weekCount - week));
                TokenMonthLabelsGrid.Children.Add(label);
                lastMonth = weekStart.Month;
            }

            for (var row = 0; row < 7; row++)
            {
                var date = weekStart.AddDays(row);
                byDate.TryGetValue(date, out var point);
                var tokens = point?.TotalTokens ?? 0;
                var future = date > today;
                var cell = new Border
                {
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(2),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(70, 95, 127, 119)),
                    Background = HeatmapBrush(tokens, visibleMaximum, future),
                    Opacity = future ? 0.28 : 1
                };
                var requestText = point is null
                    ? "没有记录"
                    : $"{point.RequestCount:N0} 次请求（成功 {point.SuccessCount:N0}）";
                ToolTipService.SetToolTip(cell,
                    future
                        ? $"{date:yyyy-MM-dd} · 尚未到达"
                        : $"{date:yyyy-MM-dd} · {UsageFormatting.Number(tokens)} Token · {requestText}");
                Grid.SetColumn(cell, week);
                Grid.SetRow(cell, row);
                TokenHeatmapGrid.Children.Add(cell);
            }
        }

        var todayPoint = snapshot.Find(today);
        var weekStartDate = today.AddDays(-6);
        var weekDays = snapshot.Days.Where(day => day.Date >= weekStartDate && day.Date <= today).ToArray();
        var weekTokens = weekDays.Sum(day => day.TotalTokens);
        var weekInput = weekDays.Sum(day => day.InputTokens);
        var weekOutput = weekDays.Sum(day => day.OutputTokens);

        TokenTodayText.Text = $"{UsageFormatting.Number(todayPoint?.TotalTokens ?? 0)} Token";
        TokenTodayDetail.Text = todayPoint is null
            ? "今天还没有完整的用量记录"
            : $"{todayPoint.RequestCount:N0} 次请求 · 成功 {todayPoint.SuccessCount:N0} 次";
        TokenWeekText.Text = $"{UsageFormatting.Number(weekTokens)} Token";
        TokenWeekDetail.Text = $"输入 {UsageFormatting.Number(weekInput)} · 输出 {UsageFormatting.Number(weekOutput)}";
        TokenHistoryText.Text = $"{UsageFormatting.Number(snapshot.TotalTokens)} Token";
        var estimatedCost = snapshot.EstimatedCost > 0 ? $" · 估算 ${snapshot.EstimatedCost:0.####}" : string.Empty;
        TokenHistoryDetail.Text = snapshot.LogCount == 0
            ? "当前日志文件尚无可统计记录"
            : $"{snapshot.LogCount:N0} 条记录{estimatedCost}";

        TokenActivityRangeText.Text = snapshot.FirstSeen is null || snapshot.LastSeen is null
            ? "展示最近 53 周；当前没有可标记日期"
            : $"展示最近 53 周 · 当前日志覆盖 {snapshot.FirstSeen:yyyy-MM-dd} 至 {snapshot.LastSeen:yyyy-MM-dd}";
        TokenSourceText.Text = snapshot.SourceAvailable
            ? $"来源：~\\.opencodex\\usage.jsonl · {snapshot.Message} 输入 {UsageFormatting.Number(snapshot.InputTokens)} / 输出 {UsageFormatting.Number(snapshot.OutputTokens)}。"
            : snapshot.Message;
        TokenSourceText.Foreground = new SolidColorBrush(snapshot.SourceAvailable
            ? Color.FromRgb(156, 181, 184)
            : Color.FromRgb(177, 58, 58));
        TokenEmptyPanel.Visibility = snapshot.LogCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshTokenSourceLedger(LiveTokenUsageSnapshot snapshot)
    {
        _tokenSourceRows.Clear();
        _tokenSourceRows.Add(ToTokenSourceRow(snapshot.Pro, "#E5C47A", "本机成功上游 · 今日输入/输出可核对"));
        _tokenSourceRows.Add(ToTokenSourceRow(snapshot.Plus, "#68D7B8", "本机成功上游 · 与 Pro 独立归账"));

        var customColors = new[] { "#C798E8", "#EE9C75", "#8FC6A8", "#D8B56B" };
        for (var index = 0; index < snapshot.Others.Count; index++)
            _tokenSourceRows.Add(ToTokenSourceRow(
                snapshot.Others[index],
                customColors[index % customColors.Length],
                snapshot.Others[index].Source));

        var todayTotal = snapshot.Pro.TodayTotalTokens + snapshot.Plus.TodayTotalTokens
                         + snapshot.Others.Sum(item => item.TodayTotalTokens);
        var historyTotal = snapshot.Pro.TotalTokens + snapshot.Plus.TotalTokens
                           + snapshot.Others.Sum(item => item.TotalTokens);
        TokenSourceLedgerSummary.Text =
            $"{snapshot.UpdatedAt:HH:mm:ss} 更新 · 今日/近5小时合计 {UsageFormatting.Number(todayTotal)} · 各来源累计 {UsageFormatting.Number(historyTotal)} Token";
    }

    private static TokenSourceLedgerRow ToTokenSourceRow(LiveTokenUsageView usage, string accent, string scope) => new(
        usage.DisplayName,
        scope,
        UsageFormatting.Number(usage.TodayTotalTokens),
        UsageFormatting.Number(usage.WeekTotalTokens),
        UsageFormatting.Number(usage.TotalTokens),
        $"{usage.SuccessCount:N0}/{usage.RequestCount:N0}",
        FormatLedgerTime(usage.LastSeen),
        accent);

    private static string FormatLedgerTime(DateTimeOffset? value) => value is null ? "暂无" : value.Value.ToString("MM-dd HH:mm:ss");

    private static Brush HeatmapBrush(long tokens, long maximum, bool future)
    {
        if (future) return new SolidColorBrush(Color.FromArgb(90, 220, 227, 218));
        if (tokens <= 0 || maximum <= 0) return new SolidColorBrush(Color.FromRgb(213, 225, 218));
        var intensity = Math.Log10(tokens + 1d) / Math.Log10(maximum + 1d);
        return intensity switch
        {
            < 0.34 => new SolidColorBrush(Color.FromRgb(156, 201, 184)),
            < 0.58 => new SolidColorBrush(Color.FromRgb(101, 176, 157)),
            < 0.8 => new SolidColorBrush(Color.FromRgb(51, 139, 127)),
            _ => new SolidColorBrush(Color.FromRgb(201, 151, 71))
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await InitializeAsync();
    }

    private void HomeNavButton_Click(object sender, RoutedEventArgs e) => ShowHomePage();

    private void AccountsNavButton_Click(object sender, RoutedEventArgs e) => ShowAccountsPage();

    private void SubagentsNavButton_Click(object sender, RoutedEventArgs e) => ShowSubagentsPage();

    private void TokenNavButton_Click(object sender, RoutedEventArgs e) => ShowTokenPage();

    private void ThemesNavButton_Click(object sender, RoutedEventArgs e) => ShowThemesPage();

    private void ServicesNavButton_Click(object sender, RoutedEventArgs e) => ShowServicesPage();

    private void ServersNavButton_Click(object sender, RoutedEventArgs e) => ShowServersPage();

    private async void StudyNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !StudyNavButton.IsEnabled) return;

        StudyNavButton.IsEnabled = false;
        FooterMessage.Text = "正在启动知耕考研学习系统…";
        try
        {
            var launcherPath = FindStudyLauncher();
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy RemoteSigned -File \"{launcherPath}\"",
                WorkingDirectory = Path.GetDirectoryName(launcherPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动学习系统启动器。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                var detail = string.Join(" ", $"{output}\n{error}"
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .TakeLast(2));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? "学习系统启动器返回失败。"
                    : detail);
            }

            FooterMessage.Text = "知耕考研已经在浏览器中打开。";
        }
        catch (Exception ex)
        {
            FooterMessage.Text = "知耕考研没有打开，请查看提示。";
            MessageBox.Show(
                $"未能打开知耕考研学习系统。\n\n{FriendlyError(ex)}",
                "考研学习入口",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            StudyNavButton.IsEnabled = true;
        }
    }

    private static string FindStudyLauncher()
    {
        var configured = Environment.GetEnvironmentVariable("CMM_STUDY_LAUNCHER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            throw new DirectoryNotFoundException("没有找到桌面目录。请设置 CMM_STUDY_LAUNCHER 指向学习系统启动脚本。");

        try
        {
            var candidates = Directory.EnumerateFiles(desktop, "start-study-system.ps1", SearchOption.AllDirectories)
                .Where(path => path.Contains("study", StringComparison.OrdinalIgnoreCase)
                               || path.Contains("学习", StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            if (candidates.Length > 0) return Path.GetFullPath(candidates[0]);
        }
        catch (UnauthorizedAccessException)
        {
        }

        throw new FileNotFoundException(
            "没有找到学习系统启动文件。请设置 CMM_STUDY_LAUNCHER，避免依赖固定的桌面目录名称。",
            "start-study-system.ps1");
    }

    private void GoToAddButton_Click(object sender, RoutedEventArgs e) => ShowSourcesPage();

    private void BackToModelsButton_Click(object sender, RoutedEventArgs e) => ShowModelsPage();

    private void ModelSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyModelFilter();

    private void ApplyModelFilter()
    {
        var query = ModelSearchBox?.Text.Trim() ?? string.Empty;
        bool Matches(ModelOption model) => string.IsNullOrWhiteSpace(query)
                                           || model.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                           || model.ProviderLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                           || model.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                                           || model.Namespaced.Contains(query, StringComparison.OrdinalIgnoreCase);

        Replace(_officialModels, _allOfficialModels.Where(Matches));
        Replace(_customModels, _allCustomModels.Where(Matches));
        if (NoCustomModelsPanel is null) return;
        NoCustomModelsPanel.Visibility = _customModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (NoCustomModelsTitle is null || NoCustomModelsDetail is null) return;
        var searching = !string.IsNullOrWhiteSpace(query);
        NoCustomModelsTitle.Text = searching ? "没有找到相符的私人模型" : "还没有添加私人模型";
        NoCustomModelsDetail.Text = searching
            ? "换一个关键词试试，也可以搜索来源名称。"
            : "填写 URL 和 API Key 后，模型会自动出现在这里。";
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HomeCapabilityGrid is null) return;
        HomeCapabilityGrid.Columns = ActualWidth >= 1160 ? 4 : 2;
    }

    private void ShowHomePage() => ShowPage(
        HomePage, HomeNavButton, "首页", "一眼看清 Codex 和服务器是否正常。");

    private void ShowTokenPage() => ShowPage(
        TokenPage, TokenNavButton, "Token 流光", "按真实本机日志查看每日 Token 活动；套餐余额仍以中转站账号卡片为准。");

    private void ShowModelsPage()
    {
        ShowPage(ModelsPage, AccountsNavButton, "中转站 · 其他模型", "管理不属于号池的兼容 API 模型；号池模型请直接在中转站首页选择。");
    }

    private void ShowSourcesPage()
    {
        ShowPage(SourcesPage, AccountsNavButton, "中转站 · 添加其他模型来源", "填写 URL 和 API Key，软件会自动读取模型。");
        ProviderNameBox.Focus();
    }

    private void ShowAccountsPage() => ShowPage(
        AccountsPage, AccountsNavButton, "中转站", "在同一任务中切换 Codex Pro / Plus 扣费账号，并管理独立 API 出口。");

    private void ShowSubagentsPage()
    {
        ShowPage(SubagentsPage, SubagentsNavButton, "子代理", "为每个子代理角色选择模型，并安全应用到 Codex。");
        if (!RuntimeMode.IsDetachedUi) _ = SubagentsPage.RefreshAsync();
    }

    private bool ReconcilePoolSelectedInCodex(RuntimeTruthTask task)
    {
        if (!task.Connected || task.IsAnswering || string.IsNullOrWhiteSpace(task.DisplayedModel)) return false;
        var pools = _services.PoolCatalog.GetPools();
        var active = _services.PoolCatalog.GetActive();
        var activePool = pools.FirstOrDefault(pool => pool.Id.Equals(active.PoolId, StringComparison.OrdinalIgnoreCase));
        if (activePool?.Transport is PoolTransport.OfficialCodex or PoolTransport.NativeCodexAccount
            && task.DisplayedModel.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            if (active.Model.Equals(task.DisplayedModel, StringComparison.OrdinalIgnoreCase)) return false;
            _services.PoolCatalog.SetActive(activePool.Id, task.DisplayedModel, "selected-in-codex");
            return true;
        }
        var selected = pools.FirstOrDefault(pool => pool.Enabled
            && string.Equals(pool.RouteAlias, task.DisplayedModel, StringComparison.OrdinalIgnoreCase));
        if (selected is null) return false;
        if (active.PoolId.Equals(selected.Id, StringComparison.OrdinalIgnoreCase)) return false;
        _services.PoolCatalog.SetActive(
            selected.Id,
            selected.DefaultModel,
            "selected-in-codex");
        return true;
    }

    private static string RuntimeTruthStateLabel(RuntimeTruthSnapshot snapshot) => snapshot.Consistency.State switch
    {
        RuntimeTruthState.Consistent => "事实一致",
        RuntimeTruthState.Pending => "回答中，等待新证据",
        RuntimeTruthState.Diverged => "事实不一致",
        RuntimeTruthState.Stale when snapshot.LastExecutionPredatesPreference => "旧证据：最近执行早于首选切换",
        RuntimeTruthState.Stale => "事实一致，实际执行记录陈旧",
        RuntimeTruthState.Failed => "最近实际执行失败",
        _ => "事实来源不完整"
    };

    private static string FormatActualExecution(RuntimeTruthSnapshot snapshot)
    {
        var execution = snapshot.LastExecution;
        var actual = execution?.ActualAttempt;
        if (execution is null || actual is null) return "最近实际执行：暂无可确认记录";
        var status = execution.HttpStatus ?? actual.HttpStatus;
        var age = snapshot.LastExecutionPredatesPreference
            ? " · 切换前旧证据"
            : snapshot.LastExecutionIsStale ? " · 记录陈旧" : string.Empty;
        var duration = execution.DurationMs is null ? "耗时未知" : $"{execution.DurationMs} ms";
        var selection = execution.SelectionBasis == RuntimeLogSelectionBasis.Timestamp
            ? "按时间戳选取"
            : "时间缺失，按数组末项选取";
        return $"最近实际执行：{actual.ProviderDisplayName}/{actual.Model} · HTTP {status?.ToString() ?? "未知"} · {duration} · {execution.Timestamp:MM-dd HH:mm:ss} · {selection}{age}"
               + Environment.NewLine
               + RuntimeTruthDisplay.FormatAttempts(execution);
    }

    private static string FormatActualAccount(RuntimeTruthSnapshot snapshot)
    {
        var actual = snapshot.LastExecution?.ActualAttempt;
        if (actual is null) return "最近实际账号：暂无可确认记录";
        var stale = snapshot.LastExecutionPredatesPreference
            ? " · 切换前旧证据"
            : snapshot.LastExecutionIsStale ? " · 记录陈旧" : string.Empty;
        return $"最近实际账号：{actual.AccountDisplayName} · {actual.ProviderId}/{actual.Model}{stale}"
               + Environment.NewLine
               + RuntimeTruthDisplay.FormatAttempts(snapshot.LastExecution);
    }

    private void ShowThemesPage() => ShowPage(
        ThemesPage, ThemesNavButton, "皮肤管理", "预览、导入、在线获取，并安全切换 Codex 皮肤。");

    private void ShowServicesPage()
    {
        ShowPage(ServicesPage, ServicesNavButton, "本机服务与备份", "逐项控制本机服务，并安全恢复或清理备份。");
        RefreshCodexConnectionUi();
        if (!RuntimeMode.IsDetachedUi)
            _ = RefreshLocalManagementAsync();
        else
            _ = RefreshDetachedLocalStatusAsync();
    }

    private void ShowServersPage()
    {
        ShowPage(ServersPage, ServersNavButton, "服务器", "每 20 秒动态只读采样 SSH 配置中的五台主服务器，不提供危险操作。");
        StartServerMonitoring();
    }

    private void ShowPage(FrameworkElement page, Button navButton, string title, string subtitle)
    {
        foreach (var candidate in new FrameworkElement[]
                 { HomePage, TokenPage, ModelsPage, SourcesPage, AccountsPage, SubagentsPage, ThemesPage, ServicesPage, ServersPage })
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[]
                 { HomeNavButton, AccountsNavButton, SubagentsNavButton, TokenNavButton, ThemesNavButton, ServicesNavButton, ServersNavButton })
            button.Tag = button == navButton ? "active" : null;
        PageTitle.Text = title;
        PageSubtitle.Text = RuntimeMode.IsDetachedUi
            ? subtitle + " 〔独立展示，不连接真实 Codex〕"
            : subtitle;
        SetServerMonitoringCadence();
        if (RuntimeMode.IsDetachedUi)
        {
            DisableDetachedActionButtons();
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(DisableDetachedActionButtons));
        }
    }

    private void PrepareDetachedUi()
    {
        Title = "Codex 总管家 · 独立开发版";
        ShowHomePage();
        ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(224, 177, 92));
        ConnectionText.Text = "独立开发模式";
        MemoryStatusTitle.Text = "与真实 Codex 完全断开";
        MemoryStatusDetail.Text = "不读取聊天和账号；只安全显示当前网关，等你主动点击连接";
        HomeOverallTitle.Text = "总管家界面已独立打开";
        HomeOverallDetail.Text = "真实 Codex 保持隔离；当前只显示网关，服务器、v2rayN 和本机状态允许只读检测。";
        HomeModelText.Text = "未连接（按要求）";
        HomeModelDetail.Text = "没有读取模型、Provider、账号或扣费信息";
        HomeAccountText.Text = "未连接（按要求）";
        HomeAccountDetail.Text = "没有读取真实 Codex 账号或号池";
        HomeLocalServiceText.Text = "正在检查 v2rayN";
        HomeLocalServiceDetail.Text = "只读状态检测；不会启动 Native Engine 或统一网关";
        HomeThemeText.Text = "展示模式";
        HomeThemeDetail.Text = "没有连接或修改正在使用的 Codex";
        HomeServersSummaryText.Text = $"已发现 {_services.Dashboard.ExpectedServerCount} 台主服务器";
        HomeServersDetailText.Text = string.Join(" · ", _services.Dashboard.DiscoveredServerAliases);
        HomeModelCapability.Text = "仅展示";
        HomeModelCapabilityDetail.Text = "模型和账号操作已锁定";
        HomeSkinCapability.Text = "仅展示";
        HomeSkinCapabilityDetail.Text = "换肤操作已锁定";
        HomeServerCapability.Text = "正在连接";
        HomeServerCapabilityDetail.Text = "只读 SSH 动态检测已启用";
        HomeBackupCapability.Text = "不使用备份";
        HomeBackupCapabilityDetail.Text = "按要求直接修改固定开发版";
        FooterMessage.Text = RuntimeMode.DetachedStatusText;
        StudyNavButton.IsEnabled = false;
        StudyNavButton.ToolTip = "独立开发模式不执行外部学习系统脚本";
        DisableDetachedActionButtons();
    }

    private void RefreshCodexConnectionUi()
    {
        if (_realCodexConfig is null)
        {
            var lockedStatus = RuntimeMode.IsCodexTestDouble
                ? "测试替身模式：真实 Codex 连接开关已锁定"
                : "这个隔离测试构建不能连接真实 Codex";
            SetCodexConnectionUi(
                lockedStatus,
                "未读取真实 Codex 网关",
                CodexConfigService.DefaultManagedNativeBaseUrl,
                "自动测试只使用临时配置和假 Codex，不会碰真实配置。",
                "真实 Codex 连接已锁定",
                isEnabled: false);
            return;
        }

        var snapshot = _realCodexConfig.ReadGatewaySnapshot();
        var status = snapshot.IsManagedConnected
            ? "当前状态：已连接总管家"
            : "当前状态：未连接总管家（默认关闭）";
        var currentGateway = $"{snapshot.CurrentGateway}  ·  Provider：{snapshot.SelectedProviderId}";
        var managedGateway = snapshot.IsManagedConnected
            ? $"取消后恢复：{snapshot.RestoreGateway}  ·  Provider：{snapshot.RestoreProviderId}"
            : snapshot.ManagedGateway;
        var buttonText = snapshot.IsManagedConnected
            ? "一键取消连接并恢复原网关"
            : "一键连接 Codex 使用总管家";
        SetCodexConnectionUi(
            status,
            currentGateway,
            managedGateway,
            snapshot.Detail,
            buttonText,
            snapshot.CanToggle && !_busy);
    }

    private void SetCodexConnectionUi(
        string status,
        string currentGateway,
        string managedGateway,
        string detail,
        string buttonText,
        bool isEnabled)
    {
        CodexConnectionStatusText.Text = status;
        HomeCodexConnectionStatusText.Text = status;
        CurrentCodexGatewayText.Text = currentGateway;
        HomeCurrentCodexGatewayText.Text = currentGateway;
        ManagedCodexGatewayText.Text = managedGateway;
        HomeManagedCodexGatewayText.Text = managedGateway;
        CodexConnectionDetailText.Text = detail;
        HomeCodexConnectionDetailText.Text = detail;
        ToggleCodexConnectionButton.Content = buttonText;
        HomeToggleCodexConnectionButton.Content = buttonText;
        ToggleCodexConnectionButton.IsEnabled = isEnabled;
        HomeToggleCodexConnectionButton.IsEnabled = isEnabled;
    }

    private async void ToggleCodexConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _realCodexConfig is null) return;
        var before = _realCodexConfig.ReadGatewaySnapshot();
        if (!before.CanToggle)
        {
            MessageBox.Show(before.Detail, "连接开关已锁定", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var disconnecting = before.IsManagedConnected;
        var message = disconnecting
            ? "确定取消 Codex 使用总管家吗？\n\n" +
              $"当前网关：{before.CurrentGateway}\n" +
              $"恢复网关：{before.RestoreGateway}\n\n" +
              "总管家只删除自己写入的网关和模型目录，并停止自己启动的本机网关。不会关闭或重启 Codex。"
            : "确定让 Codex 使用总管家吗？\n\n" +
              $"当前网关：{before.CurrentGateway}\n" +
              $"连接后网关：{before.ManagedGateway}\n\n" +
              "总管家会保留 Codex 内置 openai 身份，只写入本机网关和模型目录。不会关闭或重启 Codex；正在运行的 Codex 可能要由你手动重新打开后才读取新目录。";
        var choice = MessageBox.Show(
            message,
            disconnecting ? "确认取消 Codex 连接" : "确认连接 Codex",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;

        SetBusy(true, disconnecting ? "正在恢复 Codex 原网关…" : "正在连接 Codex 与总管家…");
        try
        {
            if (disconnecting)
            {
                if (!_realCodexConfig.RemoveManagedNativeProvider(createSnapshot: false))
                    throw new InvalidOperationException("没有找到可安全移除的总管家连接标记；配置未改动。");
                var after = _realCodexConfig.ReadGatewaySnapshot();
                if (after.IsManagedConnected || !after.CanToggle)
                    throw new InvalidOperationException("原网关恢复后的校验没有通过；已停止后续操作。");
                _services.CodexModelCatalog.RemoveOwnedArtifacts();

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await _services.UnifiedGateway.StopOwnedHostAsync(timeout.Token); } catch { }
                try { await _services.Process.StopOwnedNativeEngineAsync(timeout.Token); } catch { }
                FooterMessage.Text = "原网关已恢复；真实 Codex 没有被关闭或重启。";
            }
            else
            {
                if (!await _services.Process.EnsureOpenCodexAsync())
                    throw new InvalidOperationException("Native Engine、模型目录或就绪检查没有全部通过，连接已取消。");
                var after = _realCodexConfig.ReadGatewaySnapshot();
                if (!after.IsManagedConnected || !after.CanToggle)
                    throw new InvalidOperationException("总管家网关写入后的校验没有通过；没有进入连接模式。");
                FooterMessage.Text = "Codex 网关和模型目录已准备好；真实 Codex 没有被自动重启。";
            }
            RefreshCodexConnectionUi();
            await ReloadAsync();
            SetBusy(false);
            MessageBox.Show(
                disconnecting
                    ? "已断开。总管家自己的配置和目录已经移除；如果 Codex 仍显示旧状态，请由你手动重新打开。"
                    : "已连接。Codex 仍是原生 openai 身份；如果当前 Codex 尚未显示新模型，请由你手动重新打开一次。总管家不会代替你重启。",
                disconnecting ? "已断开 Codex" : "已连接 Codex",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            RefreshCodexConnectionUi();
            if (RuntimeMode.IsDetachedUi) DisableDetachedActionButtons();
            FooterMessage.Text = FriendlyError(ex);
            MessageBox.Show(FriendlyError(ex), "连接切换没有完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DisableDetachedActionButtons()
    {
        var allowed = DetachedAllowedButtons();
        foreach (var button in VisualDescendants<Button>(this))
        {
            if (allowed.Contains(button)) continue;
            button.IsEnabled = false;
            button.ToolTip = "独立开发模式：实际操作已锁定";
        }
    }

    private async Task RefreshDetachedLocalStatusAsync()
    {
        if (!RuntimeMode.AllowsExternalStatusConnections)
        {
            HomeLocalServiceText.Text = "隔离假数据模式";
            HomeLocalServiceDetail.Text = "没有读取 v2rayN 端口或进程；没有连接服务器";
            _localServices.Clear();
            _localServices.Add(new LocalServiceView
            {
                Id = "manager-isolated-stress",
                Name = "总管家隔离压力测试",
                Running = true,
                Status = "纯本机运行",
                PortText = "外部网络连接已禁用",
                Detail = "只使用临时目录、假 Codex 和 127.0.0.1",
                Capability = "测试可用",
                Purpose = "验证界面和代码回路，不接触真实环境。",
                PlainStatus = "真实 Codex 与外部网络完全隔离",
                ImpactText = "服务器、v2rayN、Codex 网络链条均未读取或连接。",
                StateColor = "#79DDBA"
            });
            DisableDetachedActionButtons();
            return;
        }

        var v2rayPortReady = await _services.Dashboard.IsV2rayReadyAsync();
        var v2rayProcessCount = 0;
        var xrayProcessCount = 0;
        try
        {
            v2rayProcessCount = Process.GetProcessesByName("v2rayN").Length;
            xrayProcessCount = Process.GetProcessesByName("xray").Length;
        }
        catch
        {
            // 进程列表读取失败只影响状态显示，不扩大权限。
        }

        var v2rayReady = v2rayPortReady && v2rayProcessCount > 0;
        HomeLocalServiceText.Text = v2rayReady ? "v2rayN 正常" : "v2rayN 需要检查";
        HomeLocalServiceDetail.Text = v2rayReady
            ? $"本机代理端口可用 · v2rayN {v2rayProcessCount} 个 · Xray {xrayProcessCount} 个"
            : "没有同时读到 v2rayN 进程和本机代理端口";

        _localServices.Clear();
        _localServices.Add(new LocalServiceView
        {
            Id = "manager-detached",
            Name = "总管家独立开发版",
            Running = true,
            Status = "运行正常",
            PortText = "不监听 Codex 网关端口",
            Detail = "只允许服务器、v2rayN 和本机状态检测",
            Capability = "已经可用",
            Purpose = "显示非 Codex 状态，同时锁死真实 Codex 接管。",
            PlainStatus = "与真实 Codex 完全隔离",
            ImpactText = "不会读取或修改真实 .codex、账号、模型和聊天。",
            StateColor = "#79DDBA"
        });
        _localServices.Add(new LocalServiceView
        {
            Id = "v2rayn-status",
            Name = "v2rayN",
            Running = v2rayReady,
            Status = v2rayReady ? "运行正常" : "未完整连通",
            PortText = $"本机代理端口 {(_services.Settings.V2rayProxyPort)} · 已验证 v2rayN 进程 {v2rayProcessCount} 个",
            Detail = $"只读检测；Xray 进程 {xrayProcessCount} 个",
            Capability = v2rayReady ? "已经可用" : "需要检查",
            Purpose = "为除真实 Codex 外、需要代理的功能提供网络。",
            PlainStatus = v2rayReady ? "网络通道正常" : "网络通道需要检查",
            ImpactText = "总管家不会启停或改写 v2rayN。",
            StateColor = v2rayReady ? "#79DDBA" : "#F0B45D"
        });
        DisableDetachedActionButtons();
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private async void ThemeConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _themeSnapshot is not { EngineReady: true }) return;
        SetBusy(true, "正在连接 Dream Skin 实时通道…");
        try
        {
            await SetDreamSkinServiceStateAsync(start: true, restart: false);
            FooterMessage.Text = "Dream Skin 实时通道已经完成复查；现在可以选择任意皮肤。";
        }
        catch (Exception ex)
        {
            FooterMessage.Text = $"实时通道没有连接：{FriendlyError(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ThemeCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "正在读取 Dream Skin 引擎、主题库和实时连接…");
        try
        {
            await RefreshThemeUiAsync();
            FooterMessage.Text = "皮肤状态已刷新；没有改动 Codex。";
        }
        catch (Exception ex)
        {
            ThemeStatusTitle.Text = "皮肤状态读取失败";
            ThemeStatusDetail.Text = FriendlyError(ex);
            FooterMessage.Text = "皮肤状态没有读完，Codex 没有被改动。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ApplyThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string themeId }) return;
        var theme = _themes.FirstOrDefault(item =>
            item.Id.Equals(themeId, StringComparison.OrdinalIgnoreCase));
        if (theme is null || theme.IsActive) return;

        SetBusy(true, $"正在安全切换为“{theme.Name}”…");
        try
        {
            var result = await _services.DreamSkin.ApplyInstalledThemeAsync(theme.Id, allowRestart: false);
            if (result.Status == DreamSkinOperationStatus.NeedsRestart)
            {
                var restart = ConfirmCodexRestart(
                    result.Message,
                    $"重启后应用“{theme.Name}”");
                if (!restart)
                {
                    FooterMessage.Text = result.PreviousThemeRecovered
                        ? "已取消重启，旧主题保持不变。"
                        : "已取消重启，主题没有改动。";
                    await RefreshThemeUiAsync();
                    return;
                }
                FooterMessage.Text = "已获得你的确认，正在关闭并重新打开 Codex…";
                result = await _services.DreamSkin.ApplyInstalledThemeAsync(theme.Id, allowRestart: true);
            }
            await FinishThemeOperationAsync(result, "应用皮肤");
        }
        catch (Exception ex)
        {
            FooterMessage.Text = "皮肤没有切换；旧主题备份仍保留。";
            MessageBox.Show(FriendlyError(ex), "皮肤没有切换", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OfficialAppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _themeSnapshot is not { EngineReady: true }) return;
        SetBusy(true, "正在实时卸下 Dream Skin…");
        try
        {
            var result = await _services.DreamSkin.UseOfficialAppearanceAsync(allowRestart: false);
            if (result.Status == DreamSkinOperationStatus.NeedsRestart)
            {
                if (!ConfirmCodexRestart(result.Message, "恢复官方外观"))
                {
                    FooterMessage.Text = "已取消重启；暂停请求已经安全保留。";
                    await RefreshThemeUiAsync();
                    return;
                }
                FooterMessage.Text = "已获得你的确认，正在以官方外观重新打开 Codex…";
                result = await _services.DreamSkin.UseOfficialAppearanceAsync(allowRestart: true);
            }
            await FinishThemeOperationAsync(result, "恢复官方外观");
        }
        catch (Exception ex)
        {
            FooterMessage.Text = "官方外观切换没有完成；模型和账号没有改动。";
            MessageBox.Show(FriendlyError(ex), "切换没有完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ApplyOnlineThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var uri = OnlineThemeUriBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(uri))
        {
            MessageBox.Show(
                "请先从 DreamSkin.cc Gallery 复制“立即应用”链接，再粘贴到输入框。",
                "还没有主题链接",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "正在准备安全换肤通道…");
        try
        {
            var session = await _services.DreamSkin.PrepareLiveSessionAsync(allowRestart: false);
            if (session.Status == DreamSkinOperationStatus.NeedsRestart)
            {
                if (!ConfirmCodexRestart(session.Message, "连接在线换肤通道"))
                {
                    FooterMessage.Text = "已取消重启，没有下载或应用在线主题。";
                    return;
                }
                session = await _services.DreamSkin.PrepareLiveSessionAsync(allowRestart: true);
            }
            if (!session.Success)
            {
                await FinishThemeOperationAsync(session, "连接在线换肤通道");
                return;
            }

            FooterMessage.Text = "请在 Dream Skin 确认框核对主题名称、作者、大小和 SHA-256。";
            var result = await _services.DreamSkin.ApplyCommunityThemeAsync(uri);
            await FinishThemeOperationAsync(result, "在线换肤");
        }
        catch (Exception ex)
        {
            FooterMessage.Text = "在线主题没有应用；旧主题保持不变。";
            MessageBox.Show(FriendlyError(ex), "在线换肤没有完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportThemeZipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择 Dream Skin 主题 ZIP",
            Filter = "Dream Skin 主题 ZIP (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true, "正在校验并导入主题 ZIP…");
        try
        {
            var result = await _services.DreamSkin.ImportThemeZipAsync(dialog.FileName);
            await FinishThemeOperationAsync(result, "导入主题");
        }
        catch (Exception ex)
        {
            FooterMessage.Text = "主题没有导入。";
            MessageBox.Show(FriendlyError(ex), "导入没有完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenThemeFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _themeSnapshot?.ThemesRoot ?? _services.DreamSkin.ThemesRoot;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(FriendlyError(ex), "无法打开主题文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenThemeGalleryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://dreamskin.cc/gallery",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(FriendlyError(ex), "无法打开 Gallery", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task FinishThemeOperationAsync(DreamSkinOperationResult result, string action)
    {
        await RefreshThemeUiAsync();
        FooterMessage.Text = result.Message;
        if (result.Status == DreamSkinOperationStatus.Success) return;

        var recovery = result.PreviousThemeRecovered
            ? "\n\n旧主题已经自动恢复。"
            : string.IsNullOrWhiteSpace(result.BackupPath)
                ? string.Empty
                : $"\n\n旧主题快照仍保留在：\n{result.BackupPath}";
        MessageBox.Show(
            result.Message + recovery,
            $"{action}没有完成",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task RefreshThemeUiAsync()
    {
        UpdateThemeUi(await _services.DreamSkin.DiscoverAsync());
    }

    private void UpdateThemeUi(DreamSkinSnapshot snapshot)
    {
        _themeSnapshot = snapshot;
        Replace(_themes, snapshot.Themes);
        ThemeStatusTitle.Text = snapshot.StatusTitle;
        ThemeStatusDetail.Text = snapshot.StatusDetail;
        ThemeEngineBadge.Text = snapshot.EngineReady
            ? $"Dream Skin {snapshot.EngineVersion}"
            : snapshot.ManagerScriptTrusted ? "引擎不完整" : "安全组件已锁定";
        ThemeSessionBadge.Text = snapshot.LiveSessionConnected
            ? "实时切换已连接"
            : snapshot.IsPaused ? "官方外观 / 已暂停" : "重启后可连接";
        ThemeTruthText.Text = snapshot.IsPaused
            ? string.IsNullOrWhiteSpace(snapshot.ActiveThemeName)
                ? "现在显示的是官方外观；选择任意皮肤即可恢复换肤。"
                : $"现在显示的是官方外观；上次使用“{snapshot.ActiveThemeName}”，它已经重新变为可点击。"
            : snapshot.LiveSessionConnected
                ? "现在显示的皮肤与实时通道状态一致。"
                : "皮肤已选择，但实时通道尚未连接；应用时会先尝试无重启恢复。";
        ThemeLibrarySummary.Text = snapshot.EngineReady
            ? $"{snapshot.Themes.Count} 个可用主题 · 动态主题 {snapshot.Themes.Count(theme => theme.IsDynamic)} 个"
            : "Dream Skin 引擎准备好后会自动显示本地主题。";
        NoThemesPanel.Visibility = snapshot.Themes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OfficialAppearanceButton.IsEnabled = snapshot.EngineReady && !snapshot.IsPaused;
        OfficialAppearanceButton.Content = snapshot.IsPaused ? "当前是官方外观" : "切回官方外观";
        ThemeConnectButton.IsEnabled = snapshot.EngineReady && !snapshot.LiveSessionConnected;
        ThemeConnectButton.Content = !snapshot.EngineReady
            ? "皮肤引擎不可用"
            : snapshot.LiveSessionConnected ? "实时通道已连接" : "连接实时换肤";
        ApplyOnlineThemeButton.IsEnabled = snapshot.EngineReady;
        HomeSkinCapability.Text = snapshot.EngineReady ? "已经可用" : "不可用";
        HomeSkinCapabilityDetail.Text = snapshot.EngineReady
            ? snapshot.LiveSessionConnected ? "静态/动态主题可实时切换" : "可用；必要时需确认重启 Codex"
            : "Dream Skin 引擎或安全组件不完整";

        if (!snapshot.EngineReady)
        {
            HomeThemeText.Text = "皮肤管理暂不可用";
            HomeThemeDetail.Text = snapshot.StatusTitle;
        }
        else if (snapshot.IsPaused)
        {
            HomeThemeText.Text = "当前是官方外观";
            HomeThemeDetail.Text = $"Dream Skin {snapshot.EngineVersion} 已暂停";
        }
        else
        {
            HomeThemeText.Text = string.IsNullOrWhiteSpace(snapshot.ActiveThemeName)
                ? "Dream Skin 已就绪"
                : snapshot.ActiveThemeName;
            HomeThemeDetail.Text = snapshot.LiveSessionConnected
                ? "可以实时切换，无需退出 Codex"
                : "切换时可能需要重启一次 Codex";
        }
    }

    private bool ConfirmCodexRestart(string reason, string purpose)
    {
        var choice = MessageBox.Show(
            $"{reason}\n\n是否允许总管家现在关闭并重新打开 Codex，以{purpose}？\n\n未发送的输入可能丢失；模型、账号、聊天记忆和服务器配置都不会改变。",
            "需要你确认重启 Codex",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return choice == MessageBoxResult.Yes;
    }

    private async void StartServicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "正在安全启动缺少的本机服务…");
        try
        {
            if (!await _services.Process.EnsureOpenCodexAsync())
                throw new InvalidOperationException("本机服务没有全部启动，请检查 v2rayN。 ");
            await ReloadAsync();
            FooterMessage.Text = "OpenCodex 和 v2rayN 已经准备好；正常运行的服务没有重启。";
        }
        catch (Exception ex)
        {
            FooterMessage.Text = FriendlyError(ex);
            MessageBox.Show(FriendlyError(ex), "启动没有完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_services.Backups.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                ArgumentList = { _services.Backups.DirectoryPath }
            });
            FooterMessage.Text = "已打开安全备份位置。";
        }
        catch (Exception ex)
        {
            FooterMessage.Text = FriendlyError(ex);
        }
    }

    private async void ServerCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RefreshServerTelemetryAsync(showBusy: true);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HomePage.IsEnabled = !busy;
        TokenPage.IsEnabled = !busy;
        ModelsPage.IsEnabled = !busy;
        SourcesPage.IsEnabled = !busy;
        AccountsPage.IsEnabled = !busy;
        SubagentsPage.IsEnabled = !busy;
        ThemesPage.IsEnabled = !busy;
        ServicesPage.IsEnabled = !busy;
        ServersPage.IsEnabled = !busy;
        AddProviderButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(message)) FooterMessage.Text = message;
    }

    private void SetConnection(bool connected, string text)
    {
        ConnectionDot.Fill = new SolidColorBrush(connected
            ? Color.FromRgb(20, 125, 100)
            : Color.FromRgb(177, 58, 58));
        ConnectionText.Text = text;
        ConnectionText.Foreground = connected
            ? new SolidColorBrush(Color.FromRgb(20, 125, 100))
            : new SolidColorBrush(Color.FromRgb(177, 58, 58));
    }

    private void ShowAddError(string message)
    {
        AddProviderResult.Foreground = new SolidColorBrush(Color.FromRgb(177, 58, 58));
        AddProviderResult.Text = message;
    }

    private static string FriendlyError(Exception ex)
    {
        var text = ex.Message;
        if (text.Contains("401", StringComparison.OrdinalIgnoreCase))
            return "API Key 不正确，接口拒绝了连接。";
        if (text.Contains("403", StringComparison.OrdinalIgnoreCase))
            return "接口拒绝访问，请检查 API Key 权限。";
        if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("超时", StringComparison.OrdinalIgnoreCase))
            return "连接超时，请检查 URL、网络和中转站状态。";
        if (text.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return "目标拒绝连接，请检查地址和端口。";
        return text;
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values) collection.Add(value);
    }

}
