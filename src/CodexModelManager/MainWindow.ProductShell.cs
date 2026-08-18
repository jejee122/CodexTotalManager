using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using CodexModelManager.Services;
using Forms = System.Windows.Forms;

namespace CodexModelManager;

public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;
    private bool _forceProductExit;
    private bool _trayHintShown;
    private Uri? _availableReleaseUri;

    private void InitializeProductShell()
    {
        ProductVersionText.Text = $"版本 {_services.ProductMaintenance.CurrentVersion}";
        DiagnosticSummaryBox.Text = _services.ProductMaintenance.BuildDiagnosticSummary(_services.Settings);
        Application.Current.SessionEnding += Application_SessionEnding;
        if (RuntimeMode.IsDetachedUi)
        {
            ProductStartWithWindowsBox.IsEnabled = false;
            ProductMinimizeToTrayBox.IsEnabled = false;
            SaveDesktopPreferencesButton.IsEnabled = false;
            CompleteFirstRunButton.IsEnabled = false;
            CheckProductUpdateButton.IsEnabled = false;
            OpenProductReleaseButton.IsEnabled = false;
            ProductDesktopStatusText.Text = "独立测试模式不会修改 Windows 启动项，也不会联网检查更新。";
            return;
        }

        _trayIconImage = TryLoadTrayIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开总管家", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("软件中心", null, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowFromTray();
            ShowSoftwareCenterPage();
        })));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("完全退出", null, (_, _) => Dispatcher.BeginInvoke(ExitProduct));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "Codex 总管家",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        RefreshProductShellUi();
    }

    private void RefreshProductShellUi()
    {
        ProductVersionText.Text = $"版本 {_services.ProductMaintenance.CurrentVersion}";
        ProductChannelText.Text = _services.ProductMaintenance.CurrentVersion.Contains('-', StringComparison.Ordinal)
            ? "当前是候选版；只能在专用测试电脑验收后升级为稳定版。"
            : "当前是稳定版本标识。";
        ProductStartWithWindowsBox.IsChecked = _services.ProductMaintenance.StartWithWindowsEnabled;
        ProductMinimizeToTrayBox.IsChecked = _services.Settings.MinimizeToTray;
        FirstRunGuidePanel.Visibility = _services.Settings.ProductSetupCompleted
            ? Visibility.Collapsed
            : Visibility.Visible;
        DiagnosticSummaryBox.Text = _services.ProductMaintenance.BuildDiagnosticSummary(_services.Settings);
        ProductDesktopStatusText.Text = _services.Settings.MinimizeToTray
            ? "点击关闭会收进托盘；请用托盘菜单或下方按钮完全退出。"
            : "点击关闭会正常退出；托盘菜单仍可用于重新打开窗口。";
    }

    private void MinimizeOrHideWindow()
    {
        if (!RuntimeMode.IsDetachedUi && _services.Settings.MinimizeToTray)
        {
            HideToTray();
            return;
        }
        WindowState = WindowState.Minimized;
    }

    private void HandleTrayStateChange()
    {
        if (RuntimeMode.IsDetachedUi || !_services.Settings.MinimizeToTray) return;
        if (WindowState == WindowState.Minimized) HideToTray();
    }

    private bool TryMinimizeToTrayOnClose(CancelEventArgs e)
    {
        if (RuntimeMode.IsDetachedUi || _forceProductExit || !_services.Settings.MinimizeToTray)
            return false;
        e.Cancel = true;
        HideToTray();
        return true;
    }

    private void HideToTray()
    {
        Hide();
        if (_trayIcon is null || _trayHintShown) return;
        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "Codex 总管家仍在运行";
        _trayIcon.BalloonTipText = "双击托盘图标可以重新打开；右键可完全退出。";
        _trayIcon.ShowBalloonTip(2500);
    }

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitProduct()
    {
        _forceProductExit = true;
        if (!IsVisible) Show();
        Close();
    }

    private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e) =>
        _forceProductExit = true;

    private void DisposeProductShell()
    {
        Application.Current.SessionEnding -= Application_SessionEnding;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayIconImage?.Dispose();
        _trayIconImage = null;
        _services.ProductMaintenance.Dispose();
    }

    private void SaveDesktopPreferencesButton_Click(object sender, RoutedEventArgs e)
    {
        if (RuntimeMode.IsDetachedUi) return;
        var oldStartup = _services.ProductMaintenance.StartWithWindowsEnabled;
        var desiredStartup = ProductStartWithWindowsBox.IsChecked == true;
        try
        {
            _services.ProductMaintenance.SetStartWithWindows(desiredStartup);
            _services.Settings.SetProductShellPreferences(
                ProductMinimizeToTrayBox.IsChecked == true);
            RefreshProductShellUi();
            ProductDesktopStatusText.Text = "软件设置已经保存。没有连接或重启 Codex。";
        }
        catch (Exception ex)
        {
            try { _services.ProductMaintenance.SetStartWithWindows(oldStartup); } catch { }
            ProductDesktopStatusText.Text = $"没有保存：{FriendlyError(ex)}";
        }
    }

    private void CompleteFirstRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (RuntimeMode.IsDetachedUi) return;
        try
        {
            _services.Settings.SetProductShellPreferences(
                ProductMinimizeToTrayBox.IsChecked == true,
                markSetupCompleted: true);
            FirstRunGuidePanel.Visibility = Visibility.Collapsed;
            ProductDesktopStatusText.Text = "首次设置已经完成。总管家仍然默认不连接 Codex。";
        }
        catch (Exception ex)
        {
            ProductDesktopStatusText.Text = $"首次设置没有保存：{FriendlyError(ex)}";
        }
    }

    private async void CheckProductUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (RuntimeMode.IsDetachedUi || _busy) return;
        CheckProductUpdateButton.IsEnabled = false;
        OpenProductReleaseButton.IsEnabled = false;
        ProductUpdateStatusText.Text = "正在向 GitHub 读取公开 Release 信息…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _services.ProductMaintenance.CheckForUpdatesAsync(timeout.Token);
            _availableReleaseUri = result.ReleaseUri;
            OpenProductReleaseButton.IsEnabled = _availableReleaseUri is not null;
            ProductUpdateStatusText.Text = result.Message;
        }
        catch (OperationCanceledException)
        {
            ProductUpdateStatusText.Text = "检查更新超时。没有下载、安装或修改任何文件。";
        }
        catch (Exception ex)
        {
            ProductUpdateStatusText.Text = $"暂时无法检查更新：{FriendlyError(ex)}";
        }
        finally
        {
            CheckProductUpdateButton.IsEnabled = true;
        }
    }

    private void OpenProductReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableReleaseUri is null) return;
        OpenExternalUri(_availableReleaseUri);
    }

    private void OpenProjectHomeButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalUri(new Uri("https://github.com/jejee122/CodexTotalManager"));

    private void CopyDiagnosticSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiagnosticSummaryBox.Text = _services.ProductMaintenance.BuildDiagnosticSummary(_services.Settings);
            Clipboard.SetText(DiagnosticSummaryBox.Text);
            ProductDiagnosticStatusText.Text = "已复制脱敏诊断摘要；不含账号、Token、Cookie、API Key、服务器地址和本机完整路径。";
        }
        catch (Exception ex)
        {
            ProductDiagnosticStatusText.Text = $"没有复制：{FriendlyError(ex)}";
        }
    }

    private void OpenDiagnosticFolderButton_Click(object sender, RoutedEventArgs e) =>
        OpenLocalDirectory(_services.ProductMaintenance.DiagnosticDirectory);

    private void OpenProductDataFolderButton_Click(object sender, RoutedEventArgs e) =>
        OpenLocalDirectory(_services.ProductMaintenance.DataDirectory);

    private void ExitProductButton_Click(object sender, RoutedEventArgs e) => ExitProduct();

    private void OpenLocalDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var info = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            info.ArgumentList.Add(Path.GetFullPath(path));
            Process.Start(info);
        }
        catch (Exception ex)
        {
            ProductDiagnosticStatusText.Text = $"目录没有打开：{FriendlyError(ex)}";
        }
    }

    private void OpenExternalUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            ProductUpdateStatusText.Text = "链接不是经过允许的 GitHub HTTPS 地址，已拒绝打开。";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ProductUpdateStatusText.Text = $"浏览器没有打开：{FriendlyError(ex)}";
        }
    }

    private static Icon TryLoadTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            var icon = string.IsNullOrWhiteSpace(processPath)
                ? null
                : System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            return icon is null ? (Icon)SystemIcons.Application.Clone() : (Icon)icon.Clone();
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
