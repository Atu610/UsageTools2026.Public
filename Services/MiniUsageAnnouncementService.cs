using System.IO;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public enum MiniUsageAnnouncementKind
{
    UsageMilestone,
    LowRemaining,
    UsageReset,
    ResetCreditsAdded,
    CreditBalanceChanged
}

public sealed record MiniUsageAnnouncement(
    string Provider,
    string WindowLabel,
    MiniUsageAnnouncementKind Kind,
    int RemainingPercent,
    int RepeatCount,
    bool IsAlarm,
    int AlertThreshold = 0,
    int CreditCount = 0,
    string DeliveryId = "",
    DateTimeOffset? OccurredAt = null,
    int AddedCreditCount = 0,
    string? Plan = null,
    DateTimeOffset? ResetsAt = null,
    DateTimeOffset? CreditExpiresAt = null,
    DateTimeOffset[]? CreditExpirations = null,
    bool PushCompleted = false,
    CodexCreditBalance? Balance = null,
    string? Pool = null,
    string? CreditKind = null)
{
    /// <summary>Window plus optional model pool, e.g. "7D" or "7D Fable".</summary>
    public string QuotaLabel => string.IsNullOrEmpty(Pool) ? WindowLabel : WindowLabel + " " + Pool;
}

public sealed class MiniUsageAnnouncementService
{
    private static readonly int[] LowRemainingThresholds = [20, 15, 10, 5];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly bool _outbox;
    private MiniUsageAnnouncementState _state;

    public MiniUsageAnnouncementService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsageTools2026.Public",
            "mini-usage-announcements.json"))
    {
    }

    internal MiniUsageAnnouncementService(string statePath, bool outbox = false)
    {
        _statePath = statePath;
        _outbox = outbox;
        _state = LoadState(statePath);
        PrunePending();
        // Rewrite legacy state without its old History property; keep only unsent work and baselines.
        if (File.Exists(statePath)) SaveState();
    }

    public IReadOnlyList<MiniUsageAnnouncement> Observe(UsageSnapshot snapshot, bool allowAnnouncements = true)
    {
        lock (_gate)
        {
            var announcements = new List<MiniUsageAnnouncement>();
            var dirty = PrunePending();

            if (UsageFreshness.State(snapshot.Codex) == UsageDataState.Fresh)
            {
                var windows = new List<UsageWindow>();
                AddWindow(windows, snapshot.Codex.PrimaryUsedPercent, snapshot.Codex.PrimaryWindowMinutes,
                    snapshot.Codex.PrimaryResetsAt);
                AddWindow(windows, snapshot.Codex.SecondaryUsedPercent, snapshot.Codex.SecondaryWindowMinutes,
                    snapshot.Codex.SecondaryResetsAt);
                windows.Sort((left, right) => (left.Minutes ?? int.MaxValue).CompareTo(right.Minutes ?? int.MaxValue));
                for (var index = 0; index < windows.Count; index++)
                {
                    var label = WindowLabel(windows[index].Minutes, index == 0 ? "5H" : "7D");
                    dirty |= ObserveWindow("Codex", label, windows[index], allowAnnouncements, announcements);
                }
            }

            if (UsageFreshness.State(snapshot.Claude) == UsageDataState.Fresh)
            {
                dirty |= ObserveWindow(
                    "Claude Code",
                    "5H",
                    new UsageWindow(snapshot.Claude.PrimaryUsedPercent, 300, snapshot.Claude.PrimaryResetsAt),
                    allowAnnouncements,
                    announcements);
                dirty |= ObserveWindow(
                    "Claude Code",
                    "7D",
                    new UsageWindow(snapshot.Claude.SecondaryUsedPercent, 10080, snapshot.Claude.SecondaryResetsAt),
                    allowAnnouncements,
                    announcements);
                // Model-scoped pools (e.g. weekly Fable) are separate quotas with their own keys.
                foreach (var pool in (snapshot.Claude.Details?.Pools ?? []).Where(p => p.IsModelScoped)
                             .GroupBy(p => p.Label + ":" + p.ModelName, StringComparer.Ordinal).Select(g => g.First()))
                {
                    dirty |= ObserveWindow(
                        "Claude Code",
                        pool.Label,
                        new UsageWindow(pool.Percent, pool.Label == "5H" ? 300 : 10080, pool.ResetsAt),
                        allowAnnouncements,
                        announcements,
                        ClaudeOfficialDetailsParser.SafeName(pool.ModelName));
                }
                dirty |= ObserveClaudeGrants(snapshot.Claude, allowAnnouncements, announcements);
            }

            dirty |= ObserveResetCredits(snapshot.CodexResetCredits, allowAnnouncements, announcements);
            for (var i = 0; i < announcements.Count; i++)
            {
                var source = announcements[i];
                var isCodex = source.Provider == "Codex";
                var expirations = source.Kind != MiniUsageAnnouncementKind.ResetCreditsAdded ? null : isCodex
                    ? snapshot.CodexResetCredits.Credits.Where(c => c.ExpiresAt > DateTimeOffset.UtcNow).OrderBy(c => c.ExpiresAt).Take(100).Select(c => c.ExpiresAt).ToArray()
                    : ClaudeGrantExpirations(snapshot.Claude, DateTimeOffset.UtcNow);
                var item = source with { DeliveryId = Guid.NewGuid().ToString("N"), OccurredAt = DateTimeOffset.UtcNow,
                    Plan = isCodex ? PersonalMobileService.SafePlan(snapshot.Codex.PlanType) : PersonalMobileService.SafeClaudePlan(snapshot.Claude.PlanType),
                    ResetsAt = _state.Windows.GetValueOrDefault(WindowKey(source.Provider, source.WindowLabel, source.Pool))?.ResetsAt,
                    CreditExpiresAt = expirations is { Length: > 0 } ? expirations.Min() : null,
                    CreditExpirations = expirations };
                announcements[i] = item;
                if (!_outbox) _state.Pending.RemoveAll(old => old.Provider == item.Provider && old.WindowLabel == item.WindowLabel && old.Pool == item.Pool);
                _state.Pending.Add(item);
                if (_state.Pending.Count > 100) _state.Pending.RemoveRange(0, _state.Pending.Count - 100);
                dirty = true;
            }
            if (dirty)
            {
                SaveState();
            }

            return announcements;
        }
    }

    public IReadOnlyList<MiniUsageAnnouncement> Pending()
    {
        lock (_gate)
        {
            if (PrunePending()) SaveState();
            return _state.Pending.ToArray();
        }
    }

    private bool PrunePending()
    {
        var now = DateTimeOffset.UtcNow;
        return _state.Pending.RemoveAll(item => item.OccurredAt is null || item.OccurredAt < now.AddHours(-24) || item.OccurredAt > now.AddMinutes(1)) > 0;
    }

    public void Acknowledge(MiniUsageAnnouncement item)
    {
        lock (_gate)
        {
            if (_state.Pending.RemoveAll(p => p.DeliveryId == item.DeliveryId) > 0) SaveState();
        }
    }

    public void MarkPushCompleted(MiniUsageAnnouncement item)
    {
        lock (_gate)
        {
            var index = _state.Pending.FindIndex(p => p.DeliveryId == item.DeliveryId);
            if (index >= 0 && !_state.Pending[index].PushCompleted)
            {
                _state.Pending[index] = _state.Pending[index] with { PushCompleted = true };
                SaveState();
            }
        }
    }

    public IReadOnlyList<MiniUsageAnnouncement> Current(UsageSnapshot snapshot)
    {
        lock (_gate)
        {
            return _state.Windows.Where(pair => pair.Key.StartsWith("Codex:", StringComparison.Ordinal)
                    ? UsageFreshness.State(snapshot.Codex) == UsageDataState.Fresh
                    : UsageFreshness.State(snapshot.Claude) == UsageDataState.Fresh)
                .Where(pair => pair.Value.LastUsedPercent is not null &&
                    (pair.Value.ResetsAt is null || pair.Value.ResetsAt > DateTimeOffset.Now))
                .Select(pair =>
                {
                    var parts = pair.Key.Split(':');
                    var pool = parts.Length > 2 ? parts[2] : null;
                    var remaining = (int)Math.Round(100 - pair.Value.LastUsedPercent!.Value);
                    return remaining <= 20 ? CreateLowRemaining(parts[0], parts[1], remaining, pool)
                        : new MiniUsageAnnouncement(parts[0], parts[1], MiniUsageAnnouncementKind.UsageMilestone, remaining, 3, false, Pool: pool);
                }).ToArray();
        }
    }

    private bool ObserveWindow(
        string provider,
        string windowLabel,
        UsageWindow current,
        bool allowAnnouncements,
        ICollection<MiniUsageAnnouncement> output,
        string? pool = null)
    {
        if (current.UsedPercent is not { } rawUsed || !double.IsFinite(rawUsed) || rawUsed is < 0 or > 100)
        {
            return false;
        }

        var used = Math.Clamp(rawUsed, 0, 100);
        var bucket = Math.Clamp((int)Math.Floor((used + 0.0001) / 5.0) * 5, 0, 100);
        var remaining = Math.Clamp((int)Math.Round(100 - used, MidpointRounding.AwayFromZero), 0, 100);
        var key = WindowKey(provider, windowLabel, pool);
        if (!_state.Windows.TryGetValue(key, out var state))
        {
            state = new MiniUsageWindowState
            {
                Initialized = true,
                LastBucket = bucket,
                LastUsedPercent = used,
                ResetsAt = current.ResetsAt
            };
            MarkReachedLowThresholds(state, remaining);
            _state.Windows[key] = state;
            if (allowAnnouncements && remaining <= 20)
            {
                output.Add(CreateLowRemaining(provider, windowLabel, remaining, pool));
            }
            return true;
        }

        var oldUsed = state.LastUsedPercent;
        var dirty = false;
        var resetDetected = oldUsed is { } previousUsed &&
                            IsUsageReset(previousUsed, used, state.ResetsAt, current.ResetsAt);
        if (resetDetected)
        {
            state.LastBucket = bucket;
            state.LowRemainingThresholds.Clear();
            dirty = true;
            if (allowAnnouncements)
            {
                output.Add(new MiniUsageAnnouncement(
                    provider,
                    windowLabel,
                    MiniUsageAnnouncementKind.UsageReset,
                    remaining,
                    5,
                    false,
                    Pool: pool));
            }
        }
        else if (bucket > state.LastBucket)
        {
            var newlyReached = LowRemainingThresholds
                .Where(threshold => remaining <= threshold && !state.LowRemainingThresholds.Contains(threshold))
                .ToArray();
            MarkReachedLowThresholds(state, remaining);
            if (allowAnnouncements)
            {
                output.Add(newlyReached.Length > 0
                    ? CreateLowRemaining(provider, windowLabel, remaining, pool)
                    : new MiniUsageAnnouncement(
                        provider,
                        windowLabel,
                        MiniUsageAnnouncementKind.UsageMilestone,
                        remaining,
                        3,
                        false,
                        Pool: pool));
            }
            state.LastBucket = bucket;
            dirty = true;
        }

        if (state.ResetsAt != current.ResetsAt)
        {
            dirty = true;
        }
        state.LastUsedPercent = used;
        state.ResetsAt = current.ResetsAt;
        state.Initialized = true;
        return dirty;
    }

    private bool ObserveResetCredits(
        CodexResetCreditSummary summary,
        bool allowAnnouncements,
        ICollection<MiniUsageAnnouncement> output)
    {
        if (summary.IsStale || summary.SyncedAt is null)
        {
            return false;
        }

        var currentIds = summary.Credits
            .Where(credit => credit.ExpiresAt > DateTimeOffset.Now)
            .Select(credit => credit.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var currentCount = summary.AvailableCount;
        if (!_state.CreditsInitialized)
        {
            _state.CreditsInitialized = true;
            _state.CreditIds = currentIds;
            _state.CreditCount = currentCount;
            return true;
        }

        var knownIds = _state.CreditIds.ToHashSet(StringComparer.Ordinal);
        var newIdCount = currentIds.Count(id => !knownIds.Contains(id));
        var addedCount = Math.Max(newIdCount, Math.Max(0, currentCount - _state.CreditCount));
        var dirty = currentCount != _state.CreditCount ||
                    !currentIds.SequenceEqual(_state.CreditIds, StringComparer.Ordinal);
        _state.CreditIds = currentIds;
        _state.CreditCount = currentCount;
        if (allowAnnouncements && addedCount > 0)
        {
            output.Add(new MiniUsageAnnouncement(
                "Codex",
                string.Empty,
                MiniUsageAnnouncementKind.ResetCreditsAdded,
                0,
                5,
                false,
                CreditCount: currentCount,
                AddedCreditCount: addedCount));
        }
        return dirty;
    }

    private static MiniUsageAnnouncement CreateLowRemaining(string provider, string windowLabel, int remaining, string? pool = null)
    {
        var threshold = LowRemainingThresholds
            .Where(value => remaining <= value)
            .DefaultIfEmpty(20)
            .Min();
        return new MiniUsageAnnouncement(
            provider,
            windowLabel,
            MiniUsageAnnouncementKind.LowRemaining,
            remaining,
            3,
            true,
            threshold,
            Pool: pool);
    }

    private static string WindowKey(string provider, string windowLabel, string? pool) =>
        string.IsNullOrEmpty(pool) ? provider + ":" + windowLabel : provider + ":" + windowLabel + ":" + pool;

    // One expiry per remaining reset, so two grants ending at the same minute stay two entries.
    internal static DateTimeOffset[] ClaudeGrantExpirations(ClaudeUsage claude, DateTimeOffset now) =>
        (claude.Details?.Resets?.Active(now) ?? [])
            .Where(grant => grant.EndsAt is not null && grant.Left is > 0)
            .SelectMany(grant => Enumerable.Repeat(grant.EndsAt!.Value, Math.Min(grant.Left!.Value, 100)))
            .OrderBy(at => at).Take(100).ToArray();

    // First sight of an account's grants (or an account switch) is a silent baseline, never "new".
    private bool ObserveClaudeGrants(ClaudeUsage claude, bool allowAnnouncements, ICollection<MiniUsageAnnouncement> output)
    {
        if (claude.Details?.Resets is not { } summary || string.IsNullOrEmpty(claude.AccountKey)) return false;
        var now = DateTimeOffset.UtcNow;
        // Keep the last known baseline while counts or stable identities are unavailable.
        if (summary.Active(now).Any(grant => grant.Left is null || string.IsNullOrEmpty(grant.Id))) return false;
        var current = summary.Active(now).Where(grant => grant.Left is > 0)
            .GroupBy(grant => grant.Id + "|" + grant.EndsAt?.ToUniversalTime().ToString("O"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(grant => grant.Left!.Value), StringComparer.Ordinal);
        var available = summary.AvailableCount(now);
        if (!string.Equals(_state.ClaudeGrantAccount, claude.AccountKey, StringComparison.Ordinal))
        {
            _state.ClaudeGrantAccount = claude.AccountKey;
            _state.ClaudeGrants = current;
            return true;
        }

        var added = 0;
        string? kind = null;
        foreach (var (key, left) in current)
        {
            var previous = _state.ClaudeGrants.GetValueOrDefault(key);
            if (left <= previous) continue;
            added += left - previous;
            var category = summary.Grants.First(grant => grant.Id + "|" + grant.EndsAt?.ToUniversalTime().ToString("O") == key).Category;
            kind = kind is null || kind == category ? category : "mixed";
        }

        var dirty = current.Count != _state.ClaudeGrants.Count ||
                    current.Any(pair => _state.ClaudeGrants.GetValueOrDefault(pair.Key) != pair.Value);
        _state.ClaudeGrants = current;
        if (allowAnnouncements && added > 0)
        {
            output.Add(new MiniUsageAnnouncement(
                "Claude Code",
                string.Empty,
                MiniUsageAnnouncementKind.ResetCreditsAdded,
                0,
                5,
                false,
                CreditCount: available ?? current.Values.Sum(),
                AddedCreditCount: added,
                CreditKind: kind == "mixed" ? null : kind));
        }
        return dirty;
    }

    private static void MarkReachedLowThresholds(MiniUsageWindowState state, int remaining)
    {
        foreach (var threshold in LowRemainingThresholds.Where(value => remaining <= value))
        {
            if (!state.LowRemainingThresholds.Contains(threshold))
            {
                state.LowRemainingThresholds.Add(threshold);
            }
        }
    }

    private static bool IsUsageReset(
        double previousUsed,
        double currentUsed,
        DateTimeOffset? previousReset,
        DateTimeOffset? currentReset)
    {
        var drop = previousUsed - currentUsed;
        if (drop <= 0)
        {
            return false;
        }

        var resetAdvanced = previousReset is { } oldReset && currentReset is { } newReset &&
                            newReset > oldReset.AddMinutes(1);
        var scheduledResetReached = previousReset is { } scheduled &&
                                    scheduled <= DateTimeOffset.Now.AddMinutes(2);
        return resetAdvanced || (scheduledResetReached && currentUsed <= 1);
    }

    private static void AddWindow(
        ICollection<UsageWindow> windows,
        double? usedPercent,
        int? minutes,
        DateTimeOffset? resetsAt)
    {
        if (usedPercent is not null)
        {
            windows.Add(new UsageWindow(usedPercent, minutes, resetsAt));
        }
    }

    private static string WindowLabel(int? minutes, string fallback) =>
        DisplayFormatService.FormatWindowLabel(minutes, fallback).ToUpperInvariant();

    private static MiniUsageAnnouncementState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new MiniUsageAnnouncementState();
            }
            return JsonSerializer.Deserialize<MiniUsageAnnouncementState>(File.ReadAllText(path), JsonOptions)
                   ?? new MiniUsageAnnouncementState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticsService.Write("Mini usage announcement state load failed: " + ex.GetType().Name);
            return new MiniUsageAnnouncementState();
        }
    }

    private void SaveState()
    {
        try
        {
            AtomicFile.WriteText(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticsService.Write("Mini usage announcement state save failed: " + ex.GetType().Name);
        }
    }

    private sealed record UsageWindow(double? UsedPercent, int? Minutes, DateTimeOffset? ResetsAt);
}

public sealed class MiniUsageAnnouncementState
{
    public List<MiniUsageAnnouncement> Pending { get; set; } = [];
    public Dictionary<string, MiniUsageWindowState> Windows { get; set; } = new(StringComparer.Ordinal);
    public bool CreditsInitialized { get; set; }
    public List<string> CreditIds { get; set; } = [];
    public int CreditCount { get; set; }
    public string? ClaudeGrantAccount { get; set; }
    public Dictionary<string, int> ClaudeGrants { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MiniUsageWindowState
{
    public bool Initialized { get; set; }
    public int LastBucket { get; set; }
    public double? LastUsedPercent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public List<int> LowRemainingThresholds { get; set; } = [];
}
