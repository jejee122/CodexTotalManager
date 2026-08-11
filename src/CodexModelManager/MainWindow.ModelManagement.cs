using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexModelManager.Models;

namespace CodexModelManager;

public partial class MainWindow
{
    private async void TestProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string providerId }) return;
        var provider = _providers.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return;
        SetBusy(true, $"正在测试 {provider.DisplayName}…");
        try
        {
            var result = await _services.OpenCodex.TestProviderAsync(providerId);
            await ReloadAsync();
            FooterMessage.Text = result.Success
                ? $"{provider.DisplayName} 连接测试通过，状态和延迟已更新。"
                : $"{provider.DisplayName} 测试失败：{FriendlyError(new InvalidOperationException(result.Message))}";
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
        ProviderAdapterBox.SelectedIndex = provider.Adapter.Equals("openai-responses", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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
        ProviderNameBox.Clear();
        ProviderUrlBox.Clear();
        ProviderKeyBox.Clear();
        ProviderContextBox.Text = "128000";
        ProviderAdapterBox.SelectedIndex = 0;
        AddProviderButton.Content = "测试并添加到 Codex";
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
            "会先备份，再测试、保存、重启 OpenCodex 并复查；失败会自动恢复。继续吗？";
        if (MessageBox.Show(summary, "确认修改模型来源", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;

        SetBusy(true, $"正在检查并更新 {provider.DisplayName}…");
        string? backup = null;
        string? codexSnapshot = null;
        var oldSecret = _services.Secrets.Read(providerId);
        _services.Settings.TryGetProviderName(providerId, out var oldName);
        try
        {
            var probe = await _services.Probe.ProbeAsync(url, apiKey);
            backup = _services.Backups.Create();
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
            if (!await _services.Process.RestartOpenCodexAsync())
                throw new InvalidOperationException("OpenCodex 重启后没有通过健康检查。");
            var test = await _services.OpenCodex.TestProviderAsync(providerId);
            if (!test.Success) throw new InvalidOperationException($"保存后的连接复查失败：{test.Message}");
            ResetProviderForm();
            await ReloadAsync();
            FooterMessage.Text = $"{displayName} 已修改并复查通过，共读取到 {probe.Models.Count} 个模型。";
        }
        catch (Exception ex)
        {
            var errors = await RestoreProviderBackupAsync(backup, codexSnapshot, providerId, oldSecret, oldName);
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
        var verb = enabling ? "启用" : "停用";
        var active = await _services.OpenCodex.GetActiveTargetAsync();
        var activeNotice = !enabling && active is not null
                           && active.Value.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase)
            ? "\n\n它正是当前主模型来源，会先切到官方 Sol。"
            : string.Empty;
        if (MessageBox.Show(
                $"确定{verb}“{provider.DisplayName}”吗？{activeNotice}\n\n会先备份并在操作后验证，聊天记录和密钥不会删除。",
                $"{verb}模型来源",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        SetBusy(true, $"正在{verb} {provider.DisplayName}…");
        string? backup = null;
        string? codexSnapshot = null;
        try
        {
            backup = _services.Backups.Create();
            codexSnapshot = _services.CodexConfig.CreateSnapshot();
            if (!enabling && active is not null
                && active.Value.Provider.Equals(providerId, StringComparison.OrdinalIgnoreCase))
            {
                await _services.OpenCodex.SetActiveTargetAsync("openai", "gpt-5.6-sol");
                _services.CodexConfig.SetDefaultModel(Services.OpenCodexClient.SwitchAlias);
            }
            await _services.OpenCodex.SetProviderEnabledAsync(providerId, enabling);
            var refreshed = await _services.OpenCodex.GetProvidersAsync(_services.Settings);
            var verified = refreshed.FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            if (verified is null || verified.Disabled == enabling)
                throw new InvalidOperationException("OpenCodex 没有确认来源状态变化。");
            if (enabling)
            {
                var test = await _services.OpenCodex.TestProviderAsync(providerId);
                if (!test.Success) throw new InvalidOperationException($"来源已启用，但连接测试失败：{test.Message}");
            }
            await ReloadAsync();
            FooterMessage.Text = $"已{verb} {provider.DisplayName}。聊天记录、密钥和其他来源没有改动。";
        }
        catch (Exception ex)
        {
            var errors = await RestoreProviderBackupAsync(backup, codexSnapshot, providerId, _services.Secrets.Read(providerId), provider.DisplayName);
            FooterMessage.Text = errors.Count == 0
                ? $"没有{verb}：{FriendlyError(ex)} 原配置已恢复。"
                : $"没有{verb}：{FriendlyError(ex)} 自动恢复不完整：{string.Join("；", errors)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<List<string>> RestoreProviderBackupAsync(
        string? backup,
        string? codexSnapshot,
        string providerId,
        string? oldSecret,
        string? oldName)
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
                errors.Add($"OpenCodex 配置恢复失败：{FriendlyError(rollbackException)}");
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
            if (!await _services.Process.StartOpenCodexAsync())
                errors.Add("OpenCodex 恢复后没有重新启动");
        }
        catch (Exception rollbackException)
        {
            errors.Add($"OpenCodex 重启失败：{FriendlyError(rollbackException)}");
        }
        return errors;
    }

    private void UseOfficialDirectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowAccountsPage();
        FooterMessage.Text = "请在中转站顶部点“回到官方 Pro”；它会同时切换默认模型和当前任务，并执行回滚保护。";
    }

    private static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is null) return "时长未知";
        if (uptime.Value.TotalDays >= 1) return $"{(int)uptime.Value.TotalDays}天 {uptime.Value.Hours}小时";
        if (uptime.Value.TotalHours >= 1) return $"{(int)uptime.Value.TotalHours}小时 {uptime.Value.Minutes}分";
        return $"{Math.Max(0, uptime.Value.Minutes)} 分钟";
    }
}
