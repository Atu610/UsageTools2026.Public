using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace UsageTools2026.Models;

public sealed record UsageSnapshot(
    DateTimeOffset? LastUpdatedLocal,
    CodexUsage Codex,
    ClaudeUsage Claude,
    TokenTotals Today,
    IReadOnlyList<ProjectUsage> TopProjects,
    IReadOnlyList<CodexWorkWindow> CodexWorkWindows,
    IReadOnlyList<CodexWorkWindow> ClaudeWorkWindows,
    CodexResetCreditSummary CodexResetCredits)
{
    public static UsageSnapshot Empty { get; } = new(
        null,
        CodexUsage.Empty,
        ClaudeUsage.Missing,
        TokenTotals.Empty,
        Array.Empty<ProjectUsage>(),
        Array.Empty<CodexWorkWindow>(),
        Array.Empty<CodexWorkWindow>(),
        CodexResetCreditSummary.Empty);
}

public sealed record CodexResetCredit(
    string Id,
    string Title,
    DateTimeOffset ExpiresAt);

public sealed record CodexResetCreditSummary(
    IReadOnlyList<CodexResetCredit> Credits,
    DateTimeOffset? SyncedAt,
    bool IsStale,
    int? ReportedAvailableCount = null)
{
    public static CodexResetCreditSummary Empty { get; } = new(
        Array.Empty<CodexResetCredit>(),
        null,
        false);

    public int AvailableCount => Math.Max(0, ReportedAvailableCount ?? Credits.Count);

    public CodexResetCredit? Earliest => Credits
        .Where(credit => credit.ExpiresAt > DateTimeOffset.Now)
        .OrderBy(credit => credit.ExpiresAt)
        .FirstOrDefault();

    public bool HasExpiringCredit(TimeSpan threshold)
    {
        var earliest = Earliest;
        return earliest is not null && earliest.ExpiresAt - DateTimeOffset.Now <= threshold;
    }
}

/// <summary>One active Codex working window (CLI session) and its context-window fill.</summary>
public sealed record CodexWorkWindow(
    string Project,
    string SessionId,
    long UsedTokens,
    long ContextWindow,
    DateTimeOffset? LastActivity,
    string? Model = null,
    string? Effort = null,
    string? WorkName = null)
{
    public double Percent => ContextWindow > 0
        ? Math.Clamp((double)UsedTokens / ContextWindow * 100.0, 0, 100)
        : 0;
}

public sealed record CodexUsage(
    double? PrimaryUsedPercent,
    int? PrimaryWindowMinutes,
    DateTimeOffset? PrimaryResetsAt,
    double? SecondaryUsedPercent,
    int? SecondaryWindowMinutes,
    DateTimeOffset? SecondaryResetsAt,
    bool IsStale,
    bool HasData,
    string? PlanType = null,
    DateTimeOffset? OfficialSyncedAt = null,
    CodexCreditBalance? Credits = null,
    string? AccountKey = null)
{
    public static CodexUsage Empty { get; } = new(null, null, null, null, null, null, false, false);

    public string StatusLabel
    {
        get
        {
            if (!HasData)
            {
                return "WAIT";
            }

            if (IsStale)
            {
                return "STALE";
            }

            var max = Math.Max(PrimaryUsedPercent ?? 0, SecondaryUsedPercent ?? 0);
            return max >= 90 ? "HIGH" : max >= 70 ? "WARN" : "OK";
        }
    }

    public WpfBrush StatusBrush
    {
        get
        {
            if (!HasData)
            {
                return WpfBrushes.DimGray;
            }

            if (IsStale)
            {
                return WpfBrushes.DarkSlateGray;
            }

            var max = Math.Max(PrimaryUsedPercent ?? 0, SecondaryUsedPercent ?? 0);
            return max >= 90 ? WpfBrushes.Firebrick : max >= 70 ? WpfBrushes.DarkGoldenrod : WpfBrushes.SeaGreen;
        }
    }
}

public sealed record CodexCreditBalance(decimal? Balance, bool Unlimited = false)
{
    public string Display => Unlimited ? "不限額" : Balance is { } value
        ? decimal.Floor(value).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "未提供";
}

public enum ClaudeSyncState
{
    /// <summary>Claude Code / Desktop is not installed or detectable.</summary>
    NotInstalled,

    /// <summary>Detected, but the official plan percentage has never been synced yet.</summary>
    NeverSynced,

    /// <summary>Official usage was just fetched live (Claude Desktop was closed, cookie readable).</summary>
    LiveSynced,

    /// <summary>Cookie DB is locked (Claude Desktop running); showing the last cached official value.</summary>
    CachedLocked,

    /// <summary>Cookie was readable but the sync failed; showing the last cached official value if any.</summary>
    CachedSyncFailed
}

public sealed record ClaudeUsage(
    bool HasStatusFile,
    DateTimeOffset? LastUpdatedLocal,
    string StatusDetail,
    double? PrimaryUsedPercent,
    double? SecondaryUsedPercent,
    bool HasLimitData,
    ClaudeSyncState SyncState = ClaudeSyncState.NotInstalled,
    DateTimeOffset? OfficialSyncedAt = null,
    DateTimeOffset? PrimaryResetsAt = null,
    DateTimeOffset? SecondaryResetsAt = null,
    string? PlanType = null,
    double? FableUsedPercent = null,
    DateTimeOffset? FableResetsAt = null,
    ClaudeOfficialDetails? Details = null,
    string? AccountKey = null)
{
    public static ClaudeUsage Missing { get; } = new(false, null, "Claude status hook is not installed yet.", null, null, false, ClaudeSyncState.NotInstalled);

    public string StatusLabel => HasStatusFile ? "OK" : "MISS";

    public WpfBrush StatusBrush => HasStatusFile ? WpfBrushes.SeaGreen : WpfBrushes.DimGray;
}

/// <summary>
/// Optional official Claude details read from claude.ai usage?cedar_ember=1 and /api/account.
/// Every member may be null/empty when the official source did not provide it; unknown is never 0.
/// </summary>
public sealed record ClaudeOfficialDetails(
    IReadOnlyList<ClaudeLimitPool> Pools,
    ClaudeResetGrantSummary? Resets = null,
    IReadOnlyList<ClaudeModelOption>? Models = null,
    DateTimeOffset? ModelsSyncedAt = null,
    ClaudeExtraUsage? ExtraUsage = null,
    string? PlanSource = null)
{
    public static ClaudeOfficialDetails Empty { get; } = new(Array.Empty<ClaudeLimitPool>());
}

/// <summary>One official limit entry. Kind is session, weekly_all or weekly_scoped.</summary>
public sealed record ClaudeLimitPool(
    string Kind,
    string Label,
    string? ModelName,
    double? Percent,
    DateTimeOffset? ResetsAt,
    string? Severity = null,
    bool IsActive = false,
    double? AllowancePercentOfWeekly = null)
{
    public bool IsModelScoped => !string.IsNullOrEmpty(ModelName);
}

/// <summary>Category is derived from the official clears list: full, weekly, five_hour, fable or other.</summary>
public sealed record ClaudeResetGrant(
    string Id,
    string Category,
    string? Label,
    int? Total,
    int? Left,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyList<string> Clears,
    bool UsableNow,
    bool Paused,
    string? UnavailableReason);

public sealed record ClaudeResetGrantSummary(
    bool? Eligible,
    IReadOnlyList<ClaudeResetGrant> Grants,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset SyncedAt)
{
    public IEnumerable<ClaudeResetGrant> Active(DateTimeOffset now) =>
        Grants.Where(grant => grant.EndsAt is null || grant.EndsAt > now);

    /// <summary>Null when any active grant does not state how many resets are left.</summary>
    public int? AvailableCount(DateTimeOffset now)
    {
        var active = Active(now).ToArray();
        return active.Any(grant => grant.Left is null) ? null : active.Sum(grant => Math.Max(0, grant.Left!.Value));
    }
}

public sealed record ClaudeModelOption(string Id, string Name, bool Inactive, bool Overflow);

/// <summary>Usage credits are a separate money unit and are never mixed with reset counts.</summary>
public sealed record ClaudeExtraUsage(bool Enabled, decimal? Used, decimal? MonthlyLimit, string? Currency, bool SpendLimitReached);

public sealed record TokenTotals(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    int SessionCount)
{
    public static TokenTotals Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public sealed record ProjectUsage(string Name, long TotalTokens);

public sealed record SystemUsage(
    double CpuPercent,
    double RamPercent,
    ulong TotalMemoryBytes,
    ulong AvailableMemoryBytes,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond)
{
    public static SystemUsage Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
