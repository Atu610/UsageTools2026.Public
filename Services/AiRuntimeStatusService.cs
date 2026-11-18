using System.Diagnostics;

namespace UsageTools2026.Services;

public readonly record struct AiRuntimeStatus(bool CodexRunning, bool ClaudeRunning);

public static class AiRuntimeStatusService
{
    public static AiRuntimeStatus Read() => new(
        IsAnyProcessRunning("codex", "codex-code-mode-host"),
        IsAnyProcessRunning("claude"));

    private static bool IsAnyProcessRunning(params string[] processNames)
    {
        try
        {
            foreach (var processName in processNames)
            {
                var processes = Process.GetProcessesByName(processName);
                try
                {
                    if (processes.Length > 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
        }
        catch
        {
            // A denied process query should not make a running companion disappear.
            return true;
        }

        return false;
    }
}
