using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace CodexModelManager.Services;

/// <summary>
/// Uses the accessibility tree exposed by the ordinary Codex desktop window.
/// This operates the same visible model menu as the user and never reads or
/// writes the Codex chat database.
/// </summary>
internal static class CodexWindowsAutomation
{
    private const long MaxModelCacheBytes = 8 * 1024 * 1024;

    private static readonly (string Label, string Model)[] FallbackModels =
    {
        ("5.6 Sol", "gpt-5.6-sol"),
        ("5.6 Terra", "gpt-5.6-terra"),
        ("5.6 Luna", "gpt-5.6-luna"),
        ("5.5", "gpt-5.5"),
        ("5.4 Mini", "gpt-5.4-mini"),
        ("5.3 Codex Spark", "gpt-5.3-codex-spark"),
        ("5.4", "gpt-5.4")
    };

    public static Task<CodexDesktopState> ReadStateAsync(
        string? modelsCachePath,
        CancellationToken cancellationToken) =>
        Task.Run(() => ReadState(modelsCachePath, cancellationToken), cancellationToken);

    private static CodexDesktopState ReadState(
        string? modelsCachePath,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var knownModels = LoadKnownModels(modelsCachePath);
            var session = FindSession(knownModels);
            if (session is null)
            {
                return new CodexDesktopState(
                    false,
                    false,
                    null,
                    "没有找到普通 Codex 的当前任务模型按钮");
            }

            var running = HasVisibleStopButton(session.Root);
            var model = NormalizeModelLabel(session.Trigger.Current.Name, knownModels);
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

    private static WindowSession? FindSession(IReadOnlyList<ModelLabel> knownModels)
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
                               && LooksLikeModelTrigger(element, knownModels));
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

    private static bool LooksLikeModelTrigger(
        AutomationElement element,
        IReadOnlyList<ModelLabel> knownModels)
    {
        var name = element.Current.Name.Trim();
        if (TryReadInternalAlias(name, out _))
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
        var comparable = NormalizeForComparison(name);
        return knownModels.Any(model => StartsWithModelLabel(comparable, model.Label))
               && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
    }

    private static bool HasVisibleStopButton(AutomationElement root) =>
        FindVisibleElement(
            root,
            element => element.Current.ControlType == ControlType.Button
                       && (string.Equals(element.Current.Name.Trim(), "停止", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(element.Current.Name.Trim(), "Stop", StringComparison.OrdinalIgnoreCase))) is not null;

    internal static string? ResolveModelLabel(string? label, string? modelsCachePath) =>
        NormalizeModelLabel(label, LoadKnownModels(modelsCachePath));

    private static string? NormalizeModelLabel(
        string? label,
        IReadOnlyList<ModelLabel> knownModels)
    {
        var normalized = label?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (TryReadInternalAlias(normalized, out var alias)) return alias;

        var comparable = NormalizeForComparison(normalized);
        foreach (var model in knownModels)
        {
            if (StartsWithModelLabel(comparable, model.Label))
                return model.Model;
        }
        return normalized;
    }

    private static IReadOnlyList<ModelLabel> LoadKnownModels(string? modelsCachePath)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fallback in FallbackModels)
            AddModel(models, fallback.Label, fallback.Model);

        if (string.IsNullOrWhiteSpace(modelsCachePath)) return SortModels(models);
        try
        {
            using var stream = new FileStream(
                Path.GetFullPath(modelsCachePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > MaxModelCacheBytes) return SortModels(models);
            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("models", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
                return SortModels(models);

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("slug", out var slugValue)
                    || slugValue.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("display_name", out var displayValue)
                    || displayValue.ValueKind != JsonValueKind.String)
                    continue;
                AddModel(models, displayValue.GetString(), slugValue.GetString());
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            // A partially refreshed or user-edited cache must not break desktop detection.
        }
        return SortModels(models);
    }

    private static void AddModel(
        IDictionary<string, string> models,
        string? displayName,
        string? slug)
    {
        if (string.IsNullOrWhiteSpace(displayName)
            || string.IsNullOrWhiteSpace(slug)
            || displayName.Length > 256
            || slug.Length > 256
            || displayName.Any(char.IsControl)
            || slug.Any(character => !(char.IsLetterOrDigit(character)
                                       || character is '-' or '_' or '.' or '/')))
            return;
        var label = NormalizeForComparison(displayName);
        if (!string.IsNullOrWhiteSpace(label)) models[label] = slug.Trim();
    }

    private static IReadOnlyList<ModelLabel> SortModels(IDictionary<string, string> models) =>
        models.Select(item => new ModelLabel(item.Key, item.Value))
            .OrderByDescending(item => item.Label.Length)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeForComparison(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("GPT-", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        normalized = normalized.Replace('-', ' ').Replace('_', ' ');
        return string.Join(
                ' ',
                normalized.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }

    private static bool StartsWithModelLabel(string candidate, string modelLabel) =>
        candidate.Equals(modelLabel, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(modelLabel + " ", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadInternalAlias(string label, out string alias)
    {
        alias = label.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return OpenCodexClient.IsInternalRouteAlias(alias);
    }

    private sealed record ModelLabel(string Label, string Model);

    private sealed record WindowSession(
        AutomationElement Root,
        AutomationElement Trigger,
        int ProcessId,
        string WindowTitle);
}
