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

    public static Task<CodexAliasSwitchResult> EnsureAliasAsync(
        string alias,
        CancellationToken cancellationToken) =>
        Task.Run(() => EnsureAlias(alias, cancellationToken), cancellationToken);

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

    private static CodexAliasSwitchResult EnsureAlias(
        string alias,
        CancellationToken cancellationToken)
    {
        WindowSession? session = null;
        ExpandCollapsePattern? topMenu = null;
        ExpandCollapsePattern? modelMenu = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = FindSession();
            if (session is null)
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Unavailable,
                    "没有找到普通 Codex 的当前任务模型按钮。");
            }

            var before = NormalizeModelLabel(session.Trigger.Current.Name);
            if (HasVisibleStopButton(session.Root))
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Busy,
                    "Codex 正在回答。等回答结束后再点一次模型。",
                    before);
            }
            if (SameModel(before, alias))
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Success,
                    "当前任务已经接到固定入口。",
                    before);
            }

            topMenu = GetExpandCollapse(session.Trigger);
            if (topMenu is null)
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    "普通 Codex 的模型按钮不支持自动展开。",
                    before);
            }
            if (topMenu.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                topMenu.Expand();

            var modelSubmenuElement = WaitForElement(
                () => FindVisibleElement(
                    session.Root,
                    element => element.Current.ControlType == ControlType.MenuItem
                               && (element.Current.Name.StartsWith("模型 ", StringComparison.OrdinalIgnoreCase)
                                   || element.Current.Name.StartsWith("Model ", StringComparison.OrdinalIgnoreCase))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            if (modelSubmenuElement is null)
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    "普通 Codex 没有打开模型列表。",
                    before);
            }

            modelMenu = GetExpandCollapse(modelSubmenuElement);
            if (modelMenu is null)
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    "普通 Codex 的模型列表不能自动展开。",
                    before);
            }
            if (modelMenu.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                modelMenu.Expand();

            var aliasItem = WaitForElement(
                () => FindVisibleElement(
                    session.Root,
                    element => element.Current.ControlType == ControlType.MenuItem
                               && element.Current.IsEnabled
                               && string.Equals(element.Current.Name.Trim(), alias, StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            if (aliasItem is null)
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.NeedsRestart,
                    $"普通 Codex 的模型列表还没有出现 {alias}。",
                    before);
            }
            if (!aliasItem.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObject)
                || invokeObject is not InvokePattern invoke)
            {
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    $"{alias} 当前不可点击。",
                    before);
            }

            invoke.Invoke();
            var switched = WaitUntil(
                () =>
                {
                    var refreshed = FindSession();
                    return refreshed is not null
                           && SameModel(NormalizeModelLabel(refreshed.Trigger.Current.Name), alias);
                },
                TimeSpan.FromSeconds(4),
                cancellationToken);
            if (!switched)
            {
                var after = FindSession();
                return new CodexAliasSwitchResult(
                    CodexAliasSwitchStatus.Failed,
                    "普通 Codex 没有确认当前任务的模型切换。",
                    after is null ? before : NormalizeModelLabel(after.Trigger.Current.Name));
            }

            return new CodexAliasSwitchResult(
                CodexAliasSwitchStatus.Success,
                "当前任务已经通过普通 Codex 的真实模型菜单接到固定入口。",
                alias);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException
                                   or InvalidOperationException
                                   or UnauthorizedAccessException)
        {
            return new CodexAliasSwitchResult(
                CodexAliasSwitchStatus.Failed,
                $"普通 Codex 模型菜单操作失败：{ex.Message}");
        }
        finally
        {
            TryCollapse(modelMenu);
            TryCollapse(topMenu);
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

    private static ExpandCollapsePattern? GetExpandCollapse(AutomationElement element) =>
        element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern)
        && pattern is ExpandCollapsePattern expandCollapse
            ? expandCollapse
            : null;

    private static AutomationElement? WaitForElement(
        Func<AutomationElement?> read,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = read();
            if (element is not null) return element;
            Thread.Sleep(100);
        }
        return null;
    }

    private static bool WaitUntil(
        Func<bool> read,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read()) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private static void TryCollapse(ExpandCollapsePattern? pattern)
    {
        if (pattern is null) return;
        try
        {
            if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                pattern.Collapse();
        }
        catch
        {
            // Selecting a menu item normally destroys the popup and its pattern.
        }
    }

    private static bool SameModel(string? left, string right) =>
        string.Equals(left?.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed record WindowSession(
        AutomationElement Root,
        AutomationElement Trigger,
        int ProcessId,
        string WindowTitle);
}
