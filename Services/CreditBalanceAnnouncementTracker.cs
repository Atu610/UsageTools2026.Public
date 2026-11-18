using UsageTools2026.Models;

namespace UsageTools2026.Services;

// In-memory only: balance is not a notification archive or a reset voucher.
internal sealed class CreditBalanceAnnouncementTracker
{
    private string? _account;
    private string? _display;
    private DateTimeOffset _lastShown;

    internal MiniUsageAnnouncement? Observe(CodexUsage usage, DateTimeOffset now, bool force = false)
    {
        if (usage.IsStale || !usage.HasData || usage.Credits is null ||
            UsageFreshness.Evaluate(true, true, usage.OfficialSyncedAt, false, now) != UsageDataState.Fresh)
            return null;
        if (_account != usage.AccountKey)
        {
            _account = usage.AccountKey;
            _display = null;
        }
        var display = usage.Credits.Display;
        if (!force && (_display == display || _display is not null && now - _lastShown < TimeSpan.FromSeconds(60)))
            return null;
        _display = display;
        _lastShown = now;
        return new("Codex", "Credits", MiniUsageAnnouncementKind.CreditBalanceChanged, 0, 1, false,
            Balance: usage.Credits);
    }
}
