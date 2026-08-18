using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexModelManager.Models;
using CodexModelManager.Services;

namespace CodexModelManager;

public partial class MainWindow
{
    private void ProviderPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderPresetBox.SelectedItem is not ProviderPreset preset) return;
        ProviderPresetHint.Text = preset.Summary;
        if (preset.IsCustom) return;

        ProviderNameBox.Text = preset.SuggestedName;
        ProviderUrlBox.Text = preset.BaseUrl;
        ProviderContextBox.Text = preset.ContextWindow.ToString();
        ProviderKeyBox.Clear();
        SelectProviderAdapter(preset.Adapter);
        AddProviderResult.Foreground = new SolidColorBrush(Color.FromRgb(96, 113, 122));
        AddProviderResult.Text = "模板已填好公开地址。再填你自己的 API Key，然后点测试并添加。";
    }

    private void SelectProviderAdapter(string adapter)
    {
        foreach (var item in ProviderAdapterBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag as string, adapter, StringComparison.OrdinalIgnoreCase)) continue;
            ProviderAdapterBox.SelectedItem = item;
            return;
        }
        ProviderAdapterBox.SelectedIndex = 0;
    }

    private async void TestProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string providerId }) return;
        var provider = _providers.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return;
        var connectionState = _services.Process.CaptureManagedCodexConnectionState();
        SetBusy(true, $"正在测试 {provider.DisplayName}…");
        try
        {
            if (!await _services.Process.EnsurePreservingConnectionStateAsync(connectionState))
                throw new InvalidOperationException("总管家本机模型引擎没有准备好，或 Codex 连接状态发生了变化。");
            var probe = await ProbeExistingProviderAsync(provider);
            await ReloadModelManagementAsync(connectionState);
            FooterMessage.Text = $"{provider.DisplayName} 模型列表连接和本机加载检查通过，读取到 {probe.Models.Count} 个模型，耗时 {probe.LatencyMs} 毫秒。本次没有发送付费推理请求。";
        }
        catch (Exception ex)
        {
            FooterMessage.Text = $"测试没有完成：{FriendlyError(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CopyProviderExampleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string providerId }) return;
        var provider = _providers.FirstOrDefault(item =>
            item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return;

        try
        {
            var model = _allCustomModels.FirstOrDefault(item =>
                    item.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase)
                    && !item.Disabled)
                ?.Namespaced;
            if (string.IsNullOrWhiteSpace(model)) model = $"{provider.Id}/<MODEL_ID>";
            Clipboard.SetText(_services.UnifiedGateway.BuildSafePowerShellExample(model));
            FooterMessage.Text =
                $"已复制 {provider.DisplayName} 的本机中转站调用示例。密钥位置是占位符，不包含真实 API Key，也没有发送请求或启动中转站。";
        }
        catch (Exception ex)
        {
            FooterMessage.Text = $"调用示例没有复制：{FriendlyError(ex)}";
        }
    }

    private void EditProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string providerId }) return;
        var provider = _providers.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return;
        _editingProviderId = provider.Id;
        ProviderFormTitle.Text = $"编辑模型来源：{provider.DisplayName}";
        ProviderNameBox.Text = provider.DisplayName;
        ProviderUrlBox.Text = provider.BaseUrl;
        ProviderKeyBox.Clear();
        ProviderContextBox.Text = _allCustomModels
            .Where(model => model.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase))
            .Select(model => model.ContextWindow)
            .FirstOrDefault(value => value is > 0)?.ToString() ?? "128000";
        ProviderPresetBox.SelectedIndex = 0;
        SelectProviderAdapter(provider.Adapter);
        AddProviderButton.Content = "测试并保存修改";
        CancelProviderEditButton.Visibility = Visibility.Visible;
        AddProviderResult.Foreground = new SolidColorBrush(Color.FromRgb(96, 113, 122));
        AddProviderResult.Text = "API Key 留空会继续使用原密钥；填写新 Key 才会替换。来源编号不会改变。";
        ShowSourcesPage();
    }

    private void CancelProviderEditButton_Click(object sender, RoutedEventArgs e) => ResetProviderForm();

    private void ResetProviderForm()
    {
        _editingProviderId = null;
        ProviderFormTitle.Text = "添加模型来源";
        ProviderPresetBox.SelectedIndex = 0;
        ProviderNameBox.Clear();
        ProviderUrlBox.Clear();
        ProviderKeyBox.Clear();
        ProviderContextBox.Text = "128000";
        SelectProviderAdapter("openai-chat");
        ProviderPresetHint.Text = ProviderPresetCatalog.All[0].Summary;
        AddProviderButton.Content = _services.Process.CaptureManagedCodexConnectionState().WasConnected
            ? "测试并添加到总管家和 Codex"
            : "测试并添加到总管家";
        CancelProviderEditButton.Visibility = Visibility.Collapsed;
        AddProviderResult.Text = string.Empty;
    }

    private async Task UpdateExistingProviderAsync(
        string providerId,
        string displayName,
        string url,
        string newApiKey,
        string adapter,
        int contextWindow)
    {
        var provider = _providers.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            ShowAddError("这个来源已经不存在，请刷新后再试。");
            return;
        }
        var apiKey = string.IsNullOrWhiteSpace(newApiKey) ? _services.Secrets.Read(providerId) : newApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowAddError("没有读到原密钥，请填写新的 API Key。");
            return;
        }
        var summary =
            $"即将修改：{provider.DisplayName}\n" +
            $"名称：{displayName}\n" +
            $"URL：{url}\n" +
            $"接口类型：{adapter}\n" +
            $"上下文：{contextWindow}\n" +
            $"密钥：{(string.IsNullOrWhiteSpace(newApiKey) ? "保持原密钥" : "替换为新密钥")}\n\n" +
            "会先备份，再测试、保存、重启总管家本机模型引擎并复查；失败会自动恢复。Codex 原来连着就保持连接，原来断开就保持断开。继续吗？";
        if (MessageBox.Show(summary, "确认修改模型来源", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        SetBusy(true, $"正在检查并更新 {provider.DisplayName}…");
        var connectionState = _services.Process.CaptureManagedCodexConnectionState();
        string? backup = null;
        string? codexSnapshot = null;
        var oldSecret = _services.Secrets.Read(providerId);
        _services.Settings.TryGetProviderName(providerId, out var oldName);
        try
        {
            if (!await _services.Process.EnsurePreservingConnectionStateAsync(connectionState))
                throw new InvalidOperationException("总管家本机模型引擎没有准备好，或 Codex 连接状态发生了变化。");
            var probe = await _services.Probe.ProbeAsync(url, apiKey, adapter);
            backup = _services.Backups.Create();
            if (connectionState.MayReadOrWriteCodexConfiguration)
                codexSnapshot = _services.CodexConfig.CreateSnapshot();
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
            _services.Settings.SetProviderName(providerId, displayName);
            if (!await _services.Process.RestartPreservingConnectionStateAsync(connectionState))
                throw new InvalidOperationException("本机模型引擎重启失败，或 Codex 连接状态发生了变化。");
            var test = await _services.OpenCodex.TestProviderAsync(providerId);
            if (!test.Success) throw new InvalidOperationException($"保存后的连接复查失败：{test.Message}");
            ResetProviderForm();
            await ReloadModelManagementAsync(connectionState);
            FooterMessage.Text = $"{displayName} 已修改并复查通过，共读取到 {probe.Models.Count} 个模型。";
        }
        catch (Exception ex)
        {
            var errors = await RestoreProviderBackupAsync(backup, codexSnapshot, providerId, oldSecret, oldName, connectionState);
            var recovery = errors.Count == 0 ? "原来源已经恢复。" : $"自动恢复不完整：{string.Join("；", errors)}";
            ShowAddError($"没有保存：{FriendlyError(ex)} {recovery}");
            FooterMessage.Text = $"来源修改失败。{recovery}";
        }
        finally
        {
            apiKey = string.Empty;
            newApiKey = string.Empty;
            SetBusy(false);
        }
    }

    private async void ToggleProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string providerId }) return;
        var provider = _providers.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return;
        var enabling = provider.Disabled;
        var connectionState = _services.Process.CaptureManagedCodexConnectionState();
        var verb = enabling ? "启用" : "停用";
        if (MessageBox.Show(
                $"确定{verb}“{provider.DisplayName}”吗？\n\n如果它正被使用，总管家会先切回官方 Sol。会先备份并在操作后验证，聊天记录和密钥不会删除。Codex 连接状态不会改变。",
                $"{verb}模型来源",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        SetBusy(true, $"正在{verb} {provider.DisplayName}…");
        string? backup = null;
        string? codexSnapshot = null;
        try
        {
            if (!await _services.Process.EnsurePreservingConnectionStateAsync(connectionState))
                throw new InvalidOperationException("总管家本机模型引擎没有准备好，或 Codex 连接状态发生了变化。");
            var active = await _services.OpenCodex.GetActiveTargetAsync();
            backup = _services.Backups.Create();
            if (connectionState.MayReadOrWriteCodexConfiguration)
                codexSnapshot = _services.CodexConfig.CreateSnapshot();
            if (!enabling && active is not null
                && active.Value.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                await _services.OpenCodex.SetActiveTargetAsync("openai", "gpt-5.6-sol");
                if (connectionState.MayReadOrWriteCodexConfiguration)
                    _services.CodexConfig.SetDefaultModel(Services.OpenCodexClient.SwitchAlias);
            }
            await _services.OpenCodex.SetProviderEnabledAsync(providerId, enabling);
            var refreshed = await _services.OpenCodex.GetProvidersAsync(_services.Settings);
            var verified = refreshed.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (verified is null || verified.Disabled == enabling)
            throw new InvalidOperationException("总管家本机引擎没有确认来源状态变化。");
            if (enabling)
            {
                _ = await ProbeExistingProviderAsync(provider);
            }
            await ReloadModelManagementAsync(connectionState);
            FooterMessage.Text = $"已{verb} {provider.DisplayName}。聊天记录、密钥和其他来源没有改动。";
        }
        catch (Exception ex)
        {
            var errors = await RestoreProviderBackupAsync(
                backup,
                codexSnapshot,
                providerId,
                _services.Secrets.Read(providerId),
                provider.DisplayName,
                connectionState);
            FooterMessage.Text = errors.Count == 0
                ? $"没有{verb}：{FriendlyError(ex)} 原配置已恢复。"
                : $"没有{verb}：{FriendlyError(ex)} 自动恢复不完整：{string.Join("；", errors)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<ProbeResult> ProbeExistingProviderAsync(
        ProviderView provider,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _services.Secrets.Read(provider.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("没有读到这个来源自己的 API Key，请重新填写密钥。");

        var probe = await _services.Probe.ProbeAsync(
            provider.BaseUrl,
            apiKey,
            provider.Adapter,
            cancellationToken);
        var loaded = await _services.OpenCodex.TestProviderAsync(provider.Id, cancellationToken);
        if (!loaded.Success)
            throw new InvalidOperationException($"模型列表可以访问，但总管家本机引擎没有正确加载这个来源：{loaded.Message}");
        return probe;
    }

    private async Task<List<string>> RestoreProviderBackupAsync(
        string? backup,
        string? codexSnapshot,
        string providerId,
        string? oldSecret,
        string? oldName,
        ManagedCodexConnectionState connectionState)
    {
        var errors = new List<string>();
        if (backup is not null)
        {
            try
            {
                await _services.Process.StopOpenCodexAsync();
                _services.Backups.Restore(backup);
            }
            catch (Exception rollbackException)
            {
            errors.Add($"总管家本机引擎配置恢复失败：{FriendlyError(rollbackException)}");
            }
        }
        try
        {
            if (oldSecret is null) _services.Secrets.Remove(providerId);
            else _services.Secrets.Save(providerId, oldSecret);
        }
        catch (Exception rollbackException)
        {
            errors.Add($"密钥恢复失败：{FriendlyError(rollbackException)}");
        }
        try
        {
            if (oldName is null) _services.Settings.RemoveProviderName(providerId);
            else _services.Settings.SetProviderName(providerId, oldName);
        }
        catch (Exception rollbackException)
        {
            errors.Add($"显示名称恢复失败：{FriendlyError(rollbackException)}");
        }
        if (codexSnapshot is not null)
        {
            try { _services.CodexConfig.RestoreSnapshot(codexSnapshot); }
            catch (Exception rollbackException) { errors.Add($"Codex 默认入口恢复失败：{FriendlyError(rollbackException)}"); }
        }
        try
        {
            if (!await _services.Process.StartPreservingConnectionStateAsync(connectionState))
                errors.Add("本机模型引擎恢复后没有重新启动，或 Codex 连接状态发生了变化");
        }
        catch (Exception rollbackException)
        {
            errors.Add($"本机模型引擎重启失败：{FriendlyError(rollbackException)}");
        }
        return errors;
    }

    private async Task ReloadModelManagementAsync(ManagedCodexConnectionState connectionState)
    {
        if (connectionState.WasConnected)
        {
            await ReloadAsync();
            return;
        }

        if (_services.CodexConfig.IsManagedNativeProviderSelected())
            throw new InvalidOperationException("Codex 原本处于断开状态，但操作后变成了连接状态；已停止刷新。");

        var modelsTask = _services.OpenCodex.GetModelsAsync(_services.Settings);
        var providersTask = _services.OpenCodex.GetProvidersAsync(_services.Settings);
        await Task.WhenAll(modelsTask, providersTask);
        var models = modelsTask.Result;
        var modelCounts = models
            .GroupBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        _allOfficialModels.Clear();
        _allCustomModels.Clear();
        _allCustomModels.AddRange(models
            .Where(model => !model.IsOfficial
                            && model.Provider is not "openai" and not "combo"
                            && !PoolCatalogService.IsManagerOwnedProviderId(model.Provider))
            .OrderBy(model => model.ProviderLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(model => model.Title, StringComparer.CurrentCultureIgnoreCase));
        ApplyModelFilter();

        var providers = providersTask.Result
            .Where(provider => provider.Id != "openai"
                               && !PoolCatalogService.IsManagerOwnedProviderId(provider.Id))
            .OrderBy(provider => provider.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        foreach (var provider in providers)
        {
            provider.ModelCount = modelCounts.TryGetValue(provider.Id, out var count) ? count : 0;
            provider.UsageText = "Codex 未连接；这里只管理总管家自己的模型来源";
            provider.QuotaText = "需要时可单独测试这个 API；不会把它自动切给 Codex";
        }
        Replace(_providers, providers);
        CurrentModelText.Text = "Codex 未连接（只管理模型来源）";
        CurrentModelDetail.Text = "添加、编辑、启停和测试不会改变 Codex 网关";
        RefreshCodexConnectionUi();
    }

    private void UseOfficialDirectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowAccountsPage();
        FooterMessage.Text = "请在模型与线路页点“回到官方 Pro”；它会切回官方线路和默认模型。当前任务仍由你在 Codex 自己的模型菜单中选择，软件不会自动点击或重启 Codex。";
    }

    private static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is null) return "时长未知";
        if (uptime.Value.TotalDays >= 1) return $"{(int)uptime.Value.TotalDays}天 {uptime.Value.Hours}小时";
        if (uptime.Value.TotalHours >= 1) return $"{(int)uptime.Value.TotalHours}小时 {uptime.Value.Minutes}分";
        return $"{Math.Max(0, uptime.Value.Minutes)} 分钟";
    }
}
