using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexModelManager.Models;

namespace CodexModelManager;

public partial class MainWindow
{
    private DispatcherTimer? _serverTimer;
    private DispatcherTimer? _serverClockTimer;
    private bool _serverRefreshRunning;
    private DateTimeOffset? _lastServerSample;
    private DateTimeOffset? _nextServerSample;
    private readonly System.Collections.ObjectModel.ObservableCollection<ServerFeedItem> _serverFeed = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<ServerCardView> _serverCards = new();

    private void StartServerMonitoring()
    {
        if (!_services.Dashboard.ServerCheckExists)
        {
            StopServerMonitoring();
            _nextServerSample = null;
            ServerResultText.Text = "没有在 SSH 配置中发现可监控的主服务器。";
            return;
        }
        _serverTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _serverTimer.Tick -= ServerTimer_Tick;
        _serverTimer.Tick += ServerTimer_Tick;
        SetServerMonitoringCadence();
        _serverTimer.Start();
        _serverClockTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _serverClockTimer.Tick -= ServerClockTimer_Tick;
        _serverClockTimer.Tick += ServerClockTimer_Tick;
        _serverClockTimer.Start();
        if (_lastServerSample is null || DateTimeOffset.Now - _lastServerSample > _serverTimer.Interval)
            _ = RefreshServerTelemetryAsync(showBusy: false);
    }

    private void StopServerMonitoring()
    {
        _serverTimer?.Stop();
        _serverClockTimer?.Stop();
    }

    private void SetServerMonitoringCadence()
    {
        if (_serverTimer is null) return;
        _serverTimer.Interval = ServersPage.Visibility == Visibility.Visible
            ? TimeSpan.FromSeconds(20)
            : TimeSpan.FromSeconds(60);
        _nextServerSample = DateTimeOffset.Now + _serverTimer.Interval;
        UpdateServerFreshnessClock();
    }

    private async void ServerTimer_Tick(object? sender, EventArgs e) =>
        await RefreshServerTelemetryAsync(showBusy: false);

    private void ServerClockTimer_Tick(object? sender, EventArgs e) => UpdateServerFreshnessClock();

    private async Task RefreshServerTelemetryAsync(bool showBusy)
    {
        if (_serverRefreshRunning) return;
        _serverRefreshRunning = true;
        var expected = _services.Dashboard.ExpectedServerCount;
        if (showBusy) SetBusy(true, $"正在只读采样动态发现的 {expected} 台服务器…");
        ServerCheckButton.IsEnabled = false;
        ServerCheckButton.Content = "采样中…";
        ServerResultText.Text = "正在读取 CPU、内存、磁盘、网络、负载和通用服务；不会修改服务器。";
        try
        {
            var result = await _services.Dashboard.RunServerHealthAsync();
            _lastServerSample = DateTimeOffset.Now;
            UpdateServerTelemetryUi(result);
            FooterMessage.Text = result.Message + " 全程只读；真实 Codex 没有参与。";
        }
        catch (Exception ex)
        {
            var message = FriendlyError(ex);
            var failedServers = _services.Dashboard.DiscoveredServerAliases
                .Select(alias => ServerTelemetry.Offline(alias, message))
                .ToArray();
            UpdateServerTelemetryUi(new ServerHealthResult(
                false,
                "没有读到",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                message,
                failedServers,
                _services.Dashboard.ExpectedServerCount));
            FooterMessage.Text = message + " 服务器没有被修改。";
        }
        finally
        {
            _serverRefreshRunning = false;
            _nextServerSample = DateTimeOffset.Now + (_serverTimer?.Interval ?? TimeSpan.FromSeconds(60));
            ServerCheckButton.IsEnabled = true;
            ServerCheckButton.Content = "立即刷新";
            UpdateServerFreshnessClock();
            if (showBusy) SetBusy(false);
        }
    }

    private void UpdateServerTelemetryUi(ServerHealthResult result)
    {
        _serverCards.Clear();
        foreach (var server in result.Servers) _serverCards.Add(BuildServerCard(server));

        PublicEntryTitle.Text = result.PublicEntryStatus;
        PublicEntryDetail.Text = result.PublicEndpoints is { Count: > 0 }
            ? string.Join(" · ", result.PublicEndpoints.Select(item => $"{item.Key} {item.Value}"))
            : "没有配置公开入口网址；这不会影响五台服务器的 SSH 只读检测";
        var cadence = ServersPage.Visibility == Visibility.Visible ? 20 : 60;
        ServerCheckedAt.Text = $"最近一次真实采样：{result.CheckedAt} · 当前自动刷新间隔 {cadence} 秒";
        ServerStreamMeta.Text = $"本轮完成 {DateTime.Now:HH:mm:ss} · 动态发现 {result.Servers.Count} 台";
        ServerResultText.Text = result.Message;
        ServerResultText.Foreground = new SolidColorBrush(result.Success
            ? Color.FromRgb(121, 221, 186)
            : Color.FromRgb(240, 180, 93));

        var alerts = result.Servers
            .SelectMany(server => server.Alerts.Select(alert => $"{server.Role}：{alert}"))
            .ToArray();
        ServerAlertText.Text = alerts.Length == 0
            ? "本次采样没有发现阈值告警。CPU/内存 90%、磁盘 85%、SSH 耗时 8000 ms 为告警线。"
            : string.Join("；", alerts);
        ServerAlertBanner.Background = alerts.Length == 0
            ? new SolidColorBrush(Color.FromRgb(25, 67, 61))
            : new SolidColorBrush(Color.FromRgb(83, 55, 31));

        var online = result.Servers.Count(server => server.Online);
        HomeServersSummaryText.Text = $"{online}/{result.Servers.Count} 台在线";
        HomeServersDetailText.Text = result.Servers.Count == 0
            ? "没有发现服务器"
            : string.Join(" · ", result.Servers.Select(server => $"{server.Role} {(server.Online ? "在线" : "离线")}"));
        HomeServerCapability.Text = online == result.Servers.Count && online > 0 ? "已经可用" : "部分可用";
        HomeServerCapabilityDetail.Text = $"动态只读采样 · {result.CheckedAt}";

        foreach (var server in result.Servers)
            AppendServerFeed(server);
        TrimServerFeed();
        UpdateServerFreshnessClock();
    }

    private static ServerCardView BuildServerCard(ServerTelemetry server)
    {
        var healthy = server.Online && server.Alerts.Count == 0;
        var activeServices = server.Services.Count(pair =>
            pair.Value.Equals("active", StringComparison.OrdinalIgnoreCase));
        return new ServerCardView
        {
            Alias = server.Role,
            StatusTitle = !server.Online
                ? "离线 / 无法读取"
                : healthy ? "在线 · 状态正常" : $"在线 · {server.Alerts.Count} 项告警",
            StatusDetail = !server.Online
                ? server.Error
                : healthy ? "本次关键指标通过" : string.Join("；", server.Alerts.Take(2)),
            DotColor = !server.Online ? "#E05C5C" : healthy ? "#41D6A3" : "#ECB14C",
            LatencyText = server.LatencyMs is null ? "延迟 --" : $"{server.LatencyMs} ms",
            CpuText = $"CPU {FormatPercent(server.CpuPercent)}",
            CpuValue = ClampPercent(server.CpuPercent),
            CpuColor = MetricColor(server.CpuPercent, 90),
            MemoryText = $"内存 {FormatPercent(server.MemoryPercent)} · {FormatBytePair(server.MemoryUsedBytes, server.MemoryTotalBytes)}",
            MemoryValue = ClampPercent(server.MemoryPercent),
            MemoryColor = MetricColor(server.MemoryPercent, 90),
            DiskText = $"磁盘 {FormatPercent(server.DiskPercent)} · {FormatBytePair(server.DiskUsedBytes, server.DiskTotalBytes)}",
            DiskValue = ClampPercent(server.DiskPercent),
            DiskColor = MetricColor(server.DiskPercent, 85),
            NetworkText = $"↓ {FormatRate(server.DownloadBytesPerSecond)}  ↑ {FormatRate(server.UploadBytesPerSecond)}",
            LoadText = server.Load1 is null ? "负载 --" : $"负载 {server.Load1:0.00} / {server.Load5:0.00} / {server.Load15:0.00}",
            UptimeText = $"运行时间 {FormatServerUptime(server.UptimeSeconds)} · 采样 {server.CheckedAt:HH:mm:ss}",
            ServicesText = server.Services.Count == 0
                ? "没有检测到已安装的通用 systemd 服务"
                : $"通用服务 {activeServices}/{server.Services.Count} 正常",
            ServiceDetailText = server.Services.Count == 0
                ? "服务明细：无可读项目"
                : "服务明细：" + string.Join(" · ", server.Services.Select(item => $"{FriendlyServiceName(item.Key)} {FriendlyServiceState(item.Value)}")),
            HealthEventText = string.IsNullOrWhiteSpace(server.HealthEvent)
                ? "本轮结果：没有额外告警"
                : $"本轮结果：{server.HealthEvent}"
        };
    }

    private void AppendServerFeed(ServerTelemetry server)
    {
        var title = !server.Online
            ? "无法读取"
            : server.Alerts.Count == 0 ? "采样正常" : $"采样完成 · {server.Alerts.Count} 项需留意";
        var detail = server.Online
            ? $"CPU {FormatPercent(server.CpuPercent)} · 内存 {FormatPercent(server.MemoryPercent)} · 磁盘 {FormatPercent(server.DiskPercent)} · 网络 ↓{FormatRate(server.DownloadBytesPerSecond)} ↑{FormatRate(server.UploadBytesPerSecond)}"
            : server.Error;
        _serverFeed.Insert(0, new ServerFeedItem
        {
            TimeText = server.CheckedAt.ToLocalTime().ToString("HH:mm:ss"),
            Source = server.Role,
            Title = title,
            Detail = detail,
            Accent = !server.Online ? "#E05C5C" : server.Alerts.Count > 0 ? "#F0B45D" : "#68D7B8"
        });
    }

    private void TrimServerFeed()
    {
        while (_serverFeed.Count > 25) _serverFeed.RemoveAt(_serverFeed.Count - 1);
    }

    private void UpdateServerFreshnessClock()
    {
        if (ServerFreshnessBadge is null) return;
        if (_lastServerSample is null)
        {
            ServerFreshnessBadge.Text = _serverRefreshRunning ? "LIVE · 正在读取第一批数据" : "LIVE · 等待首次采样";
            return;
        }
        var age = Math.Max(0, (int)(DateTimeOffset.Now - _lastServerSample.Value).TotalSeconds);
        var until = _nextServerSample is null
            ? 0
            : Math.Max(0, (int)(_nextServerSample.Value - DateTimeOffset.Now).TotalSeconds);
        ServerFreshnessBadge.Text = $"LIVE · {age} 秒前更新 · 约 {until} 秒后刷新";
    }

    private static string FriendlyServiceName(string name) => name switch
    {
        "ssh" or "sshd" => "SSH",
        "cron" or "crond" => "定时任务",
        "caddy" or "nginx" => "入口网关",
        "docker" => "Docker",
        "fail2ban" => "登录保护",
        "xray" => "Xray",
        "cloudflared" => "Cloudflare 隧道",
        _ => name
    };

    private static string FriendlyServiceState(string state) => state.ToLowerInvariant() switch
    {
        "active" => "正常",
        "inactive" => "未运行",
        "failed" => "失败",
        "activating" => "启动中",
        "deactivating" => "停止中",
        _ => state
    };

    private static double ClampPercent(double? value) => Math.Clamp(value ?? 0, 0, 100);
    private static string FormatPercent(double? value) => value is null ? "--" : $"{value:0.#}%";

    private static string MetricColor(double? value, double warning) =>
        value is null ? "#7B8D94"
        : value >= warning ? "#E05C5C"
        : value >= warning - 15 ? "#E0AD57"
        : "#38BFC7";

    private static string FormatBytePair(long? used, long? total) =>
        used is null || total is null ? "容量未知" : $"{FormatBytes(used.Value)} / {FormatBytes(total.Value)}";

    private static string FormatRate(long? bytes) => bytes is null ? "--" : $"{FormatBytes(Math.Max(0, bytes.Value))}/s";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatServerUptime(long? seconds)
    {
        if (seconds is null) return "--";
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds.Value));
        return value.TotalDays >= 1 ? $"{(int)value.TotalDays}天 {value.Hours}小时" : $"{(int)value.TotalHours}小时 {value.Minutes}分";
    }

    private void Window_Closed(object? sender, EventArgs e) => StopServerMonitoring();
}
