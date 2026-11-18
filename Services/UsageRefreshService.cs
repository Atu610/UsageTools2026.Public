using UsageTools2026.Models;

namespace UsageTools2026.Services;

public sealed class UsageRefreshService
{
    private readonly CodexUsageReader _codexReader = new();
    private readonly ClaudeUsageReader _claudeReader = new();
    private readonly CodexResetCreditReader _resetCreditReader = new();

    public UsageSnapshot LoadSnapshot()
    {
        var settings = AppSettingsService.Load();
        var codex = settings.CodexDataEnabled
            ? _codexReader.Read()
            : CodexReadResult.Empty;
        var claude = settings.ClaudeDataEnabled
            ? _claudeReader.Read()
            : ClaudeUsage.Missing;
        var claudeWindows = settings.ClaudeDataEnabled
            ? _claudeReader.ReadWorkWindows()
            : Array.Empty<CodexWorkWindow>();
        var resetCredits = settings.CodexDataEnabled
            ? _resetCreditReader.Read()
            : CodexResetCreditSummary.Empty;

        return new UsageSnapshot(
            codex.LastUpdatedLocal,
            codex.Usage,
            claude,
            codex.Today,
            codex.TopProjects,
            codex.WorkWindows,
            claudeWindows,
            resetCredits);
    }
}
