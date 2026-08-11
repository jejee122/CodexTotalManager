using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexModelManager.Services;

namespace CodexModelManager;

public partial class MainWindow
{
    internal async Task<DetachedUiStressReport> RunDetachedUiStressAsync(
        int cycles,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeMode.IsDetachedUi || RuntimeMode.AllowsExternalStatusConnections)
            throw new InvalidOperationException("界面压力测试只允许在完全断外网的独立模式运行。");
        if (cycles is < 1 or > 20_000)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        DisableDetachedActionButtons();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var managedBefore = GC.GetTotalMemory(true);
        var workingSetBefore = process.WorkingSet64;
        var handlesBefore = process.HandleCount;
        var gdiHandlesBefore = GetGuiResources(process.Handle, 0);
        var userHandlesBefore = GetGuiResources(process.Handle, 1);
        var timer = Stopwatch.StartNew();
        long maxCycleLatencyMs = 0;
        var checkpoints = new List<DetachedUiStressCheckpoint>();

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cycleTimer = Stopwatch.StartNew();
            ShowHomePage();
            ShowAccountsPage();
            ShowSubagentsPage();
            ShowTokenPage();
            ShowThemesPage();
            ShowServicesPage();
            ShowServersPage();
            await Dispatcher.Yield(DispatcherPriority.Background);
            cycleTimer.Stop();
            maxCycleLatencyMs = Math.Max(maxCycleLatencyMs, cycleTimer.ElapsedMilliseconds);
            var completed = cycle + 1;
            if (completed is 100 or 1_000 || completed % 2_500 == 0 || completed == cycles)
            {
                process.Refresh();
                checkpoints.Add(new DetachedUiStressCheckpoint(
                    completed,
                    GC.GetTotalMemory(false),
                    process.WorkingSet64,
                    process.HandleCount,
                    GetGuiResources(process.Handle, 0),
                    GetGuiResources(process.Handle, 1)));
            }
        }

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        timer.Stop();
        DisableDetachedActionButtons();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();

        var allowed = DetachedAllowedButtons();
        var enabledActionButtons = VisualDescendants<Button>(this)
            .Count(button => !allowed.Contains(button) && button.IsEnabled);
        return new DetachedUiStressReport(
            Marker: "DETACHED_UI_STRESS_OK",
            Cycles: cycles,
            PageTransitions: checked(cycles * 7),
            ElapsedMs: timer.ElapsedMilliseconds,
            MaxCycleLatencyMs: maxCycleLatencyMs,
            ManagedBefore: managedBefore,
            ManagedAfter: GC.GetTotalMemory(true),
            WorkingSetBefore: workingSetBefore,
            WorkingSetAfter: process.WorkingSet64,
            HandlesBefore: handlesBefore,
            HandlesAfter: process.HandleCount,
            GdiHandlesBefore: gdiHandlesBefore,
            GdiHandlesAfter: GetGuiResources(process.Handle, 0),
            UserHandlesBefore: userHandlesBefore,
            UserHandlesAfter: GetGuiResources(process.Handle, 1),
            EnabledActionButtonCount: enabledActionButtons,
            LocalServiceRows: _localServices.Count,
            ServerMonitorRunning: _serverTimer?.IsEnabled == true,
            ExpectedServerCount: _services.Dashboard.ExpectedServerCount,
            ExternalStatusConnectionsAllowed: RuntimeMode.AllowsExternalStatusConnections,
            FakeCodexHome: Environment.GetEnvironmentVariable("CMM_SANDBOX_CODEX_HOME") ?? string.Empty,
            RuntimeRoot: Environment.GetEnvironmentVariable("CMM_RUNTIME_ROOT") ?? string.Empty,
            Checkpoints: checkpoints);
    }

    private HashSet<Button> DetachedAllowedButtons() => new()
    {
        HomeNavButton,
        AccountsNavButton,
        SubagentsNavButton,
        TokenNavButton,
        ThemesNavButton,
        ServicesNavButton,
        ServersNavButton,
        MinimizeWindowButton,
        MaximizeWindowButton,
        CloseWindowButton,
        HomeToggleCodexConnectionButton,
        ToggleCodexConnectionButton,
        ServerCheckButton
    };

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr processHandle, int flags);
}

internal sealed record DetachedUiStressCheckpoint(
    int Cycles,
    long ManagedMemory,
    long WorkingSet,
    int Handles,
    int GdiHandles,
    int UserHandles);

internal sealed record DetachedUiStressReport(
    string Marker,
    int Cycles,
    int PageTransitions,
    long ElapsedMs,
    long MaxCycleLatencyMs,
    long ManagedBefore,
    long ManagedAfter,
    long WorkingSetBefore,
    long WorkingSetAfter,
    int HandlesBefore,
    int HandlesAfter,
    int GdiHandlesBefore,
    int GdiHandlesAfter,
    int UserHandlesBefore,
    int UserHandlesAfter,
    int EnabledActionButtonCount,
    int LocalServiceRows,
    bool ServerMonitorRunning,
    int ExpectedServerCount,
    bool ExternalStatusConnectionsAllowed,
    string FakeCodexHome,
    string RuntimeRoot,
    IReadOnlyList<DetachedUiStressCheckpoint> Checkpoints);
