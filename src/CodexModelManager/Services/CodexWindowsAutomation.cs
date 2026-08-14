using System.Diagnostics;
using System.Windows.Automation;

namespace CodexModelManager.Services;

/// <summary>
/// Uses the accessibility tree exposed by the ordinary Codex desktop window.
/// This operates the same visible model menu as the user and never reads or
/// writes the Codex chat database.
/// </summary>
internal static class CodexWindowsAutomation
{
    private static readonly (string Label, string Model)[] KnownModels =
    {
        ("5.6 Sol", "gpt-5.6-sol"),
        ("5.6 Terra", "gpt-5.6-terra"),
        ("5.6 Luna", "gpt-5.6-luna"),
        ("5.4 Mini", "gpt-5.4-mini"),
        ("5.3 Codex Spark", "gpt-5.3-codex-spark"),
        ("5.4", "gpt-5.4")
    };

    public static Task<CodexDesktopState> ReadStateAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadState(cancellationToken), cancellationToken);

    private static CodexDesktopState ReadState(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = FindSession();
            if (session is null)
            {
                return new CodexDesktopState(
                    false,
                    false,
                    null,
                    "没有找到普通 Codex 的当前任务模型按钮");
            }

            var running = HasVisibleStopButton(session.Root);
            var model = NormalizeModelLabel(session.Trigger.Current.Name);
            return new CodexDesktopState(
                true,
                running,
                model,
                running
                    ? "普通 Codex 正在回答"
                    : string.IsNullOrWhiteSpace(model) ? "当前没有打开任务" : $"当前任务：{model}",
                HostProcessId: session.ProcessId,
                TaskIdentity: string.IsNullOrWhiteSpace(session.WindowTitle) ? null : session.WindowTitle);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException
                                   or InvalidOperationException
                                   or UnauthorizedAccessException)
        {
            return new CodexDesktopState(false, false, null, $"普通 Codex 暂时不可读：{ex.Message}");
        }
    }

    private static WindowSession? FindSession()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                var root = AutomationElement.FromHandle(process.MainWindowHandle);
                var trigger = FindVisibleElement(
                    root,
                    element => element.Current.ControlType == ControlType.Button
                               && LooksLikeModelTrigger(element));
                if (trigger is not null)
                    return new WindowSession(root, trigger, process.Id, process.MainWindowTitle);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException
                                       or InvalidOperationException
                                       or UnauthorizedAccessException)
            {
                // Another ChatGPT-family window may be closing. Try the next one.
            }
            finally
            {
                process.Dispose();
            }
        }
        return null;
    }

    private static AutomationElement? FindVisibleElement(
        AutomationElement root,
        Func<AutomationElement, bool> predicate)
    {
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            try
            {
                if (!element.Current.IsOffscreen && predicate(element)) return element;
            }
            catch (ElementNotAvailableException)
            {
                // Accessibility trees are live; a disappearing menu item is normal.
            }
        }
        return null;
    }

    private static bool LooksLikeModelTrigger(AutomationElement element)
    {
        var name = element.Current.Name.Trim();
        if (OpenCodexClient.IsInternalRouteAlias(name))
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
        return KnownModels.Any(model => name.StartsWith(model.Label, StringComparison.OrdinalIgnoreCase))
               && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
    }

    private static bool HasVisibleStopButton(AutomationElement root) =>
        FindVisibleElement(
            root,
            element => element.Current.ControlType == ControlType.Button
                       && (string.Equals(element.Current.Name.Trim(), "停止", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(element.Current.Name.Trim(), "Stop", StringComparison.OrdinalIgnoreCase))) is not null;

    private static string? NormalizeModelLabel(string? label)
    {
        var normalized = label?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (OpenCodexClient.IsInternalRouteAlias(normalized))
            return normalized;
        foreach (var model in KnownModels)
        {
            if (normalized.StartsWith(model.Label, StringComparison.OrdinalIgnoreCase))
                return model.Model;
        }
        return normalized;
    }

    private sealed record WindowSession(
        AutomationElement Root,
        AutomationElement Trigger,
        int ProcessId,
        string WindowTitle);
}
