using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexModelManager.Models;
using CodexModelManager.Services;

namespace CodexModelManager.Views;

public partial class ExtensionManagementView : UserControl
{
    private readonly ObservableCollection<ExtensionCardView> _cards = new();
    private readonly HashSet<string> _stopRequested = new(StringComparer.OrdinalIgnoreCase);
    private ExtensionService? _extensions;

    public ExtensionManagementView()
    {
        InitializeComponent();
        ExtensionCardsList.ItemsSource = _cards;
    }

    public void Initialize(ExtensionService extensions)
    {
        _extensions = extensions;
        ExtensionRootText.Text = extensions.PackagesDirectory;
        Refresh();
    }

    public async void Refresh()
    {
        if (_extensions is null) return;
        ExtensionDiscoveryResult result;
        try
        {
            result = await Task.Run(_extensions.Discover);
        }
        catch (Exception ex)
        {
            ShowError("插件刷新失败", ex);
            return;
        }
        var previous = _cards.ToDictionary(card => card.Id, StringComparer.OrdinalIgnoreCase);
        var refreshed = new List<ExtensionCardView>();
        foreach (var package in result.Packages)
        {
            if (!previous.TryGetValue(package.Manifest.Id, out var card))
                card = new ExtensionCardView(package);
            card.Update(package, _extensions.IsRunning(package.Manifest.Id));
            refreshed.Add(card);
        }
        _cards.Clear();
        foreach (var card in refreshed) _cards.Add(card);
        ExtensionEmptyText.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var messages = result.Issues
            .Select(issue => $"{issue.FolderName}：{issue.Message}")
            .ToList();
        if (!string.IsNullOrWhiteSpace(result.TrustStoreWarning)) messages.Insert(0, result.TrustStoreWarning);
        ExtensionIssuesText.Text = string.Join(Environment.NewLine, messages);
        ExtensionIssuesPanel.Visibility = messages.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenExtensionFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_extensions is null) return;
        Directory.CreateDirectory(_extensions.PackagesDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(_extensions.PackagesDirectory);
        Process.Start(startInfo);
    }

    private void RefreshExtensionsButton_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
    }

    private async void ToggleExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_extensions is null || sender is not Button { Tag: string id } button) return;
        var card = _cards.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (card is null) return;
        button.IsEnabled = false;
        try
        {
            if (card.IsTrusted)
            {
                if (card.IsRunning) await _extensions.StopAsync(id);
                await Task.Run(() => _extensions.Disable(id));
            }
            else
            {
                var discovery = await Task.Run(_extensions.Discover);
                var package = discovery.Packages.Single(item =>
                    item.Manifest.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                var capabilities = package.Manifest.Capabilities.Count == 0
                    ? "未声明额外能力"
                    : string.Join("、", package.Manifest.Capabilities);
                var answer = MessageBox.Show(
                    $"要启用“{package.Manifest.Name}”吗？\n\n"
                    + $"发布者：{package.Manifest.Publisher}\n"
                    + $"声明能力：{capabilities}\n"
                    + $"文件指纹：{package.Fingerprint[..16]}…\n\n"
                    + "它会以你当前 Windows 账号的权限运行。这不是沙盒；只确认你看得懂且信任的插件。",
                    "确认启用自定义插件",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
                // Enable re-reads and re-hashes the complete package to close the confirmation
                // race. Keep that potentially large operation away from the WPF UI thread.
                await Task.Run(() => _extensions.Enable(id, package.Fingerprint));
            }
            Refresh();
        }
        catch (Exception ex)
        {
            ShowError("插件状态没有改变", ex);
            Refresh();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void RunExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_extensions is null || sender is not Button { Tag: string id }) return;
        var card = _cards.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (card is null) return;
        try
        {
            if (_extensions.IsRunning(id))
            {
                _stopRequested.Add(id);
                await _extensions.StopAsync(id);
                card.SetRunning(false);
                card.AppendOutput("[总管家] 已请求停止插件。 ");
                return;
            }

            card.ClearOutput();
            card.SetRunning(true);
            var result = await _extensions.RunAsync(
                id,
                line => Dispatcher.BeginInvoke(new Action(() => card.AppendOutput(line))));
            var stoppedByUser = _stopRequested.Remove(id);
            card.AppendOutput(stoppedByUser
                ? "[总管家] 插件已由用户停止。"
                : $"[总管家] {result.Message}");
            if (!result.Success && !stoppedByUser)
                MessageBox.Show(result.Message, "插件异常退出", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            card.AppendOutput($"[总管家] {ex.Message}");
            ShowError("插件没有正常运行", ex);
        }
        finally
        {
            _stopRequested.Remove(id);
            card.SetRunning(_extensions.IsRunning(id));
        }
    }

    private static void ShowError(string title, Exception exception) =>
        MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}

internal sealed class ExtensionCardView : INotifyPropertyChanged
{
    private bool _isTrusted;
    private bool _isRunning;
    private string _trustWarning = string.Empty;
    private string _output = "尚未运行。";

    public ExtensionCardView(ExtensionPackage package)
    {
        Id = package.Manifest.Id;
        Name = package.Manifest.Name;
        Description = package.Manifest.Description;
        PublisherText = string.Empty;
        VersionText = string.Empty;
        CapabilitiesText = string.Empty;
        FingerprintText = string.Empty;
        Update(package, false);
    }

    public string Id { get; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public string PublisherText { get; private set; }
    public string VersionText { get; private set; }
    public string CapabilitiesText { get; private set; }
    public string FingerprintText { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public bool IsTrusted => _isTrusted;
    public bool IsRunning => _isRunning;
    public string EnableButtonText => _isTrusted ? "禁用" : "启用";
    public string RunButtonText => _isRunning ? "停止" : "运行";
    public bool CanRunOrStop => _isRunning || _isTrusted;
    public string StatusText => _isRunning ? "运行中" : _isTrusted ? "已启用" : "默认关闭";
    public Brush StatusColor => new SolidColorBrush(_isRunning || _isTrusted
        ? Color.FromRgb(121, 221, 186)
        : Color.FromRgb(201, 151, 71));
    public string TrustWarning
    {
        get => _trustWarning;
        private set => SetField(ref _trustWarning, value);
    }
    public string Output
    {
        get => _output;
        private set => SetField(ref _output, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ExtensionPackage package, bool running)
    {
        Name = package.Manifest.Name;
        Description = package.Manifest.Description;
        PublisherText = $"{package.Manifest.Publisher} · ID: {package.Manifest.Id}";
        VersionText = $"v{package.Manifest.Version}";
        CapabilitiesText = package.Manifest.Capabilities.Count == 0
            ? "未声明额外能力"
            : string.Join("、", package.Manifest.Capabilities);
        FingerprintText = package.Fingerprint[..16] + "…";
        Fingerprint = package.Fingerprint;
        _isTrusted = package.Enabled;
        _isRunning = running;
        TrustWarning = package.TrustInvalidated
            ? "插件文件或清单已经变化，旧授权已自动失效，请检查后重新启用。"
            : string.Empty;
        RaiseAll();
    }

    public void SetRunning(bool value)
    {
        _isRunning = value;
        RaiseAll();
    }

    public void ClearOutput() => Output = string.Empty;

    public void AppendOutput(string line)
    {
        var next = string.IsNullOrEmpty(Output) ? line : Output + Environment.NewLine + line;
        Output = next.Length <= 20_000 ? next : next[^20_000..];
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(PublisherText));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(CapabilitiesText));
        OnPropertyChanged(nameof(FingerprintText));
        OnPropertyChanged(nameof(IsTrusted));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(EnableButtonText));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(CanRunOrStop));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(TrustWarning));
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
