using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexModelManager.Models;
using Microsoft.Win32;

namespace CodexModelManager;

public partial class MainWindow
{
    private async Task RefreshLocalManagementAsync()
    {
        try
        {
            var statuses = await _services.LocalServices.GetStatusesAsync();
            Replace(_localServices, statuses);
            var usable = statuses.Count(item => !item.Capability.Equals("不可用", StringComparison.OrdinalIgnoreCase));
            var fullyRunning = statuses.Count(item => item.Running);
            LocalServiceSummaryTitle.Text = usable == statuses.Count
                ? "本机功能都能使用"
                : $"{statuses.Count - usable} 项需要处理";
            LocalServiceSummaryDetail.Text = string.Join(" · ", statuses.Select(item => $"{item.Name}：{item.PlainStatus}"));
            LocalServiceNextStep.Text = statuses.All(item => item.Running)
                ? "不用操作。只有遇到连接问题时再展开对应卡片。"
                : statuses.Any(item => item.Id == "opencodex" && !item.Running)
                    ? "建议先启动 OpenCodex；官方 Codex 直连仍可作为保底。"
                    : statuses.Any(item => item.Id == "v2rayn" && !item.Running)
                        ? "需要国外模型时再启动 v2rayN；不使用时可以保持现状。"
                        : "Dream Skin 当前未实时运行；这只影响皮肤，不影响模型和聊天。";
            LocalServiceCountBadge.Text = $"{fullyRunning}/{statuses.Count} 正在运行 · {usable}/{statuses.Count} 功能可用";
            var backups = _services.BackupCatalog.List();
            if (_services.Settings.BackupAutoCleanup)
            {
                var candidates = _services.BackupCatalog.RetentionCandidates(
                    backups,
                    _services.Settings.BackupRetentionCount,
                    _services.Settings.BackupRetentionDays);
                foreach (var item in candidates) item.IsSelected = true;
                BackupActionResult.Text = candidates.Count == 0
                    ? "自动规则：当前没有待清理备份"
                    : $"自动规则已标记 {candidates.Count} 项；移入回收站仍需你确认";
            }
            Replace(_backupItems, backups);
            BackupRetentionCountBox.Text = _services.Settings.BackupRetentionCount.ToString();
            BackupRetentionDaysBox.Text = _services.Settings.BackupRetentionDays.ToString();
            BackupAutoCleanupBox.IsChecked = _services.Settings.BackupAutoCleanup;
            V2rayPathTextBox.Text = _services.Settings.V2rayPath;
            V2rayProxyUrlTextBox.Text = _services.Settings.V2rayProxyUrl;
            NativeEnginePortTextBox.Text = _services.Settings.NativeEnginePort.ToString();
            UnifiedGatewayPortTextBox.Text = _services.Settings.UnifiedGatewayPort.ToString();
            BackupServiceTitle.Text = $"共 {backups.Count} 项 · 可恢复 {backups.Count(item => item.CanRestore)} 项 · 删除一律进入回收站";
            HomeBackupCapability.Text = backups.Any(item => item.CanRestore) ? "已经可用" : "部分可用";
            HomeBackupCapabilityDetail.Text = $"{backups.Count} 项；唯一关键备份受保护";
        }
        catch (Exception ex)
        {
            BackupActionResult.Text = $"状态没有读完：{FriendlyError(ex)}";
        }
    }

    private async void StartLocalServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string serviceId }) return;
        SetBusy(true, $"正在启动 {ServiceName(serviceId)}…");
        try
        {
            switch (serviceId)
            {
                case "opencodex":
                    if (!await _services.Process.EnsureNativeEngineOnlyAsync())
                        throw new InvalidOperationException("OpenCodex 启动后没有通过健康检查。");
                    break;
                case "v2rayn":
                    if (!await _services.LocalServices.StartV2rayAsync())
                        throw new InvalidOperationException($"v2rayN 启动后代理 {_services.Settings.V2rayProxyUrl} 没有就绪。");
                    break;
                case "dreamskin":
                    await SetDreamSkinServiceStateAsync(start: true, restart: false);
                    break;
                default:
                    throw new InvalidOperationException("未知的本机服务。");
            }
            if (_services.CodexConfig.IsManagedNativeProviderSelected()) await ReloadAsync();
            await RefreshLocalManagementAsync();
            FooterMessage.Text = $"{ServiceName(serviceId)} 已启动并完成复查。";
        }
        catch (Exception ex)
        {
            FooterMessage.Text = $"启动没有完成：{FriendlyError(ex)}";
        }
        finally { SetBusy(false); }
    }

    private void BrowseV2rayPathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 v2rayN.exe",
            Filter = "v2rayN (v2rayN.exe)|v2rayN.exe|程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) V2rayPathTextBox.Text = dialog.FileName;
    }

    private async void SaveV2raySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (!int.TryParse(NativeEnginePortTextBox.Text.Trim(), out var nativePort)
                || !int.TryParse(UnifiedGatewayPortTextBox.Text.Trim(), out var gatewayPort))
                throw new InvalidOperationException("两个核心端口都必须是数字。");
            if (!Uri.TryCreate(V2rayProxyUrlTextBox.Text.Trim(), UriKind.Absolute, out var proxyUri))
                throw new InvalidOperationException("v2rayN 代理地址格式不正确。");
            var conflictingPool = _services.PoolCatalog.GetPools().FirstOrDefault(pool =>
                pool.Transport == PoolTransport.CliProxyApi
                && pool.LocalPort is not null
                && (pool.LocalPort == nativePort || pool.LocalPort == gatewayPort || pool.LocalPort == proxyUri.Port));
            if (conflictingPool is not null)
                throw new InvalidOperationException($"端口 {conflictingPool.LocalPort} 已分给 {conflictingPool.DisplayName}，不能再给核心服务使用。");
            _services.Settings.SetLocalNetworkConfiguration(
                V2rayPathTextBox.Text.Trim(),
                V2rayProxyUrlTextBox.Text.Trim(),
                nativePort,
                gatewayPort);
            V2raySettingsResultText.Text = "已一次性保存全部设置；没有启动或重启任何程序。核心端口有改动时，请先关闭 Codex 和总管家，再重新打开。";
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            V2raySettingsResultText.Text = $"没有保存：{FriendlyError(ex)}";
        }
    }

    private async void StopLocalServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string serviceId }) return;
        var consequence = serviceId switch
        {
            "opencodex" => "外部 API 模型会暂时不可用；Codex 官方直连模型仍可使用。",
            "v2rayn" => "国外模型和其他依赖 v2rayN 的程序会暂时断网。",
            "dreamskin" => "只暂停皮肤并恢复官方外观；模型、账号和聊天不会改变。",
            _ => "服务会停止。"
        };
        if (MessageBox.Show(
                $"确定停止 {ServiceName(serviceId)} 吗？\n\n{consequence}\n\n停止后会再次检查真实状态。",
                $"停止 {ServiceName(serviceId)}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true, $"正在安全停止 {ServiceName(serviceId)}…");
        try
        {
            switch (serviceId)
            {
                case "opencodex":
                    _services.Backups.Create();
                    if (!await _services.Process.StopOpenCodexAsync())
                        throw new InvalidOperationException("OpenCodex 没有确认停止。");
                    if ((await _services.OpenCodex.GetRuntimeStatusAsync()).Healthy)
                        throw new InvalidOperationException("OpenCodex 停止后健康端点仍然在线。");
                    break;
                case "v2rayn":
                    if (!await _services.LocalServices.StopV2rayAsync())
                        throw new InvalidOperationException("v2rayN 或受控核心进程没有完全停止。");
                    break;
                case "dreamskin":
                    await SetDreamSkinServiceStateAsync(start: false, restart: false);
                    break;
                default:
                    throw new InvalidOperationException("未知的本机服务。");
            }
            await RefreshLocalManagementAsync();
            FooterMessage.Text = $"{ServiceName(serviceId)} 已停止并完成复查。";
        }
        catch (Exception ex) { FooterMessage.Text = $"停止没有完成：{FriendlyError(ex)}"; }
        finally { SetBusy(false); }
    }

    private async void RestartLocalServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string serviceId }) return;
        if (MessageBox.Show(
                $"确定重启 {ServiceName(serviceId)} 吗？\n\n会先检查并保留配置；只重启这一项，其他本机服务不动。",
                $"重启 {ServiceName(serviceId)}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true, $"正在重启 {ServiceName(serviceId)}…");
        try
        {
            switch (serviceId)
            {
                case "opencodex":
                    _services.Backups.Create();
                    if (!await _services.Process.RestartNativeEngineOnlyAsync())
                        throw new InvalidOperationException("OpenCodex 重启后没有通过健康检查。");
                    break;
                case "v2rayn":
                    if (!await _services.LocalServices.RestartV2rayAsync())
                        throw new InvalidOperationException($"v2rayN 重启后代理 {_services.Settings.V2rayProxyUrl} 没有就绪。");
                    break;
                case "dreamskin":
                    await SetDreamSkinServiceStateAsync(start: true, restart: true);
                    break;
                default:
                    throw new InvalidOperationException("未知的本机服务。");
            }
            if (_services.CodexConfig.IsManagedNativeProviderSelected()) await ReloadAsync();
            await RefreshLocalManagementAsync();
            FooterMessage.Text = $"{ServiceName(serviceId)} 已重启并完成复查。";
        }
        catch (Exception ex) { FooterMessage.Text = $"重启没有完成：{FriendlyError(ex)}"; }
        finally { SetBusy(false); }
    }

    private async Task SetDreamSkinServiceStateAsync(bool start, bool restart)
    {
        DreamSkinOperationResult result;
        if (!start)
        {
            result = await _services.DreamSkin.UseOfficialAppearanceAsync(allowRestart: false);
            if (result.Status == DreamSkinOperationStatus.NeedsRestart)
            {
                if (!ConfirmCodexRestart(result.Message, "暂停皮肤并恢复官方外观"))
                    throw new InvalidOperationException("你取消了 Codex 重启，皮肤服务没有停止。");
                result = await _services.DreamSkin.UseOfficialAppearanceAsync(allowRestart: true);
            }
        }
        else
        {
            result = await _services.DreamSkin.PrepareLiveSessionAsync(allowRestart: false);
            if (result.Status == DreamSkinOperationStatus.NeedsRestart)
            {
                if (!ConfirmCodexRestart(result.Message, restart ? "重新连接换肤通道" : "连接换肤通道"))
                    throw new InvalidOperationException("你取消了 Codex 重启，换肤通道没有连接。");
                result = await _services.DreamSkin.PrepareLiveSessionAsync(allowRestart: true);
            }
        }
        if (!result.Success) throw new InvalidOperationException(result.Message);
        await RefreshThemeUiAsync();
    }

    private void CreateSafetyBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var openCodex = _services.Backups.Create();
            var codex = _services.CodexConfig.CreateSnapshot();
            BackupActionResult.Text = $"已创建：{Path.GetFileName(openCodex)}、{Path.GetFileName(codex)}";
            _ = RefreshLocalManagementAsync();
        }
        catch (Exception ex) { BackupActionResult.Text = $"快照没有创建：{FriendlyError(ex)}"; }
    }

    private async void CreateV2rayBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (MessageBox.Show(
                "为了得到一致的 v2rayN 数据库快照，会短暂停止 v2rayN、复制 guiConfigs、核对文件数量和大小，然后自动重新启动。\n\n继续吗？",
                "备份 v2rayN",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true, "正在创建一致的 v2rayN 配置备份…");
        try
        {
            var path = await _services.LocalServices.CreateV2rayBackupAsync();
            await RefreshLocalManagementAsync();
            BackupActionResult.Text = $"v2rayN 备份已验证：{Path.GetFileName(path)}";
        }
        catch (Exception ex) { BackupActionResult.Text = $"v2rayN 备份没有完成：{FriendlyError(ex)}"; }
        finally { SetBusy(false); }
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string path }) return;
        var item = _backupItems.FirstOrDefault(value => value.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is null || !item.CanRestore) return;
        if (MessageBox.Show(
                $"即将恢复下面这份备份：\n\n类型：{item.Type}\n时间：{item.CreatedText}\n大小：{item.SizeText}\n文件：{item.Path}\n\n总管家会先备份当前状态，再恢复、重启需要的服务并验证；失败会自动回退。继续吗？",
                "恢复指定备份",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true, $"正在恢复 {item.Name}…");
        try
        {
            switch (item.Type)
            {
                case "OpenCodex 配置":
                    await RestoreOpenCodexBackupAsync(item.Path);
                    break;
                case "Codex 配置":
                    RestoreCodexBackup(item.Path);
                    break;
                case "v2rayN 配置":
                    await _services.LocalServices.RestoreV2rayBackupAsync(item.Path);
                    break;
                default:
                    throw new InvalidOperationException("这种归档目前只允许查看，不能在总管家内恢复。");
            }
            if (_services.CodexConfig.IsManagedNativeProviderSelected())
                await ReloadAsync();
            else
                await InitializeManagerOnlyAsync();
            await RefreshLocalManagementAsync();
            BackupActionResult.Text = $"恢复并验证完成：{item.Name}";
        }
        catch (Exception ex) { BackupActionResult.Text = $"恢复没有完成：{FriendlyError(ex)}"; }
        finally { SetBusy(false); }
    }

    private async Task RestoreOpenCodexBackupAsync(string path)
    {
        var connectionState = _services.Process.CaptureManagedCodexConnectionState();
        var current = _services.Backups.Create();
        try
        {
            await _services.Process.StopOpenCodexAsync();
            _services.Backups.Restore(path);
            if (!await _services.Process.StartPreservingConnectionStateAsync(connectionState))
                throw new InvalidOperationException("恢复后的本机模型引擎没有通过健康检查，或 Codex 连接状态发生了变化。");
        }
        catch
        {
            try
            {
                await _services.Process.StopOpenCodexAsync();
                _services.Backups.Restore(current);
                await _services.Process.StartPreservingConnectionStateAsync(connectionState);
            }
            catch { }
            throw;
        }
    }

    private void RestoreCodexBackup(string path)
    {
        var connectionState = _services.Process.CaptureManagedCodexConnectionState();
        if (!connectionState.WasConnected)
            throw new InvalidOperationException("Codex 当前没有连接总管家，因此禁止恢复 Codex 配置备份。请先通过“一键连接 Codex”明确授权。 ");
        var current = _services.CodexConfig.CreateSnapshot();
        try
        {
            _services.CodexConfig.RestoreSnapshot(path);
            if (_services.CodexConfig.IsManagedNativeProviderSelected() != connectionState.WasConnected)
                throw new InvalidOperationException("这份备份会改变 Codex 与总管家的连接状态。请先用“一键连接 Codex”开关切到对应状态，再恢复备份。");
            if (!_services.CodexConfig.MemoryProtectionLooksSafe())
                throw new InvalidOperationException("恢复结果触发了记忆保护锁。");
        }
        catch
        {
            try { _services.CodexConfig.RestoreSnapshot(current); } catch { }
            throw;
        }
    }

    private async void DeleteBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string path }) return;
        var item = _backupItems.FirstOrDefault(value => value.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        await DeleteBackupsWithConfirmationAsync(new[] { item });
    }

    private async void DeleteSelectedBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await DeleteBackupsWithConfirmationAsync(_backupItems.Where(item => item.IsSelected).ToArray());
    }

    private async void CleanupOldBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!TryReadRetention(out var count, out var days)) return;
        var candidates = _services.BackupCatalog.RetentionCandidates(_backupItems.ToArray(), count, days);
        if (candidates.Count == 0)
        {
            BackupActionResult.Text = "当前没有符合清理规则的旧备份。";
            return;
        }
        await DeleteBackupsWithConfirmationAsync(candidates);
    }

    private async Task DeleteBackupsWithConfirmationAsync(IReadOnlyList<BackupItemView> items)
    {
        if (items.Count == 0)
        {
            BackupActionResult.Text = "请先勾选要清理的备份。";
            return;
        }
        var protectedItem = items.FirstOrDefault(item => !item.CanDelete);
        if (protectedItem is not null)
        {
            BackupActionResult.Text = $"不能删除 {protectedItem.Name}：{protectedItem.ProtectionReason}";
            return;
        }
        var exactList = string.Join("\n", items.Select((item, index) => $"{index + 1}. {item.Type} | {item.CreatedText} | {item.Path}"));
        if (MessageBox.Show(
                $"请逐项核对，准备清理 {items.Count} 个备份：\n\n{exactList}\n\n不会永久删除，下一步还会再次确认。",
                "第一次确认：核对具体备份",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (MessageBox.Show(
                $"第二次确认：把上面列出的 {items.Count} 个备份移入 Windows 回收站？\n\n唯一关键备份已自动排除。",
                "第二次确认：移入回收站",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true, "正在把已确认的备份移入回收站…");
        try
        {
            _services.BackupCatalog.MoveToRecycleBin(items);
            await RefreshLocalManagementAsync();
            BackupActionResult.Text = $"已移入回收站 {items.Count} 项，可以从 Windows 回收站恢复。";
        }
        catch (Exception ex) { BackupActionResult.Text = $"清理没有完成：{FriendlyError(ex)}"; }
        finally { SetBusy(false); }
    }

    private void SaveBackupRetentionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRetention(out var count, out var days)) return;
        try
        {
            _services.Settings.SetBackupRetention(count, days, BackupAutoCleanupBox.IsChecked == true);
            BackupActionResult.Text = BackupAutoCleanupBox.IsChecked == true
                ? $"已保存：保留 {count} 份 / {days} 天；启动后自动标记，删除仍需二次确认"
                : $"已保存：保留 {count} 份 / {days} 天；自动标记已关闭";
            _ = RefreshLocalManagementAsync();
        }
        catch (Exception ex) { BackupActionResult.Text = $"规则没有保存：{FriendlyError(ex)}"; }
    }

    private bool TryReadRetention(out int count, out int days)
    {
        count = 0;
        days = 0;
        if (!int.TryParse(BackupRetentionCountBox.Text.Trim(), out count) || count is < 1 or > 200
            || !int.TryParse(BackupRetentionDaysBox.Text.Trim(), out days) || days is < 1 or > 3650)
        {
            BackupActionResult.Text = "保留份数请填 1–200，保留天数请填 1–3650。";
            return false;
        }
        return true;
    }

    private static string ServiceName(string id) => id switch
    {
        "opencodex" => "OpenCodex",
        "v2rayn" => "v2rayN",
        "dreamskin" => "Dream Skin",
        _ => id
    };
}
