using System.Diagnostics;

namespace CodexModelManager.Services;

internal static class CodexDesktopProcessDetector
{
    private static readonly HashSet<string> KnownProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ChatGPT",
        "Codex"
    };

    public static bool IsRunning()
    {
        try
        {
            var currentId = Environment.ProcessId;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == currentId || process.HasExited) continue;
                    if (KnownProcessNames.Contains(process.ProcessName)) return true;
                }
            }
            return false;
        }
        catch
        {
            // A failed safety check must never be interpreted as "Codex is not running".
            return true;
        }
    }
}
