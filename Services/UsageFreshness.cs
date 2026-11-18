using System.Security.Cryptography;
using System.Text;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public enum UsageDataState { Fresh, Cached, Stale, Unavailable, Disabled }

public static class UsageFreshness
{
    internal static readonly TimeSpan FreshAge = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan MaximumCacheAge = TimeSpan.FromHours(24);

    internal static string? AccountKey(string provider, string? identity) =>
        string.IsNullOrWhiteSpace(identity) ? null : Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(provider + ":" + identity.Trim())));

    internal static bool SameAccount(string? cached, string? current) =>
        !string.IsNullOrEmpty(current) && string.Equals(cached, current, StringComparison.Ordinal);

    internal static bool UsableTime(DateTimeOffset? synced, DateTimeOffset now) =>
        synced is { } time && time <= now.AddMinutes(1) && now - time <= MaximumCacheAge;

    internal static double? Window(double? percent, DateTimeOffset? reset, DateTimeOffset now) =>
        percent is { } value && double.IsFinite(value) && value >= 0 && value <= 100 &&
        (reset is null || reset > now) ? value : null;

    internal static double? ClaudeWindow(double? percent, DateTimeOffset? reset,
        DateTimeOffset? synced, DateTimeOffset now, TimeSpan maximumAge) =>
        synced is { } time && time <= now.AddMinutes(1) && now - time < maximumAge
            ? Window(percent, reset, now) : null;

    public static string ClaudeCacheLabel(ClaudeUsage usage, bool chinese) =>
        usage.OfficialSyncedAt is { } time
            ? chinese ? $"快取 {time.LocalDateTime:MM/dd HH:mm} · 同步失敗"
                : $"Cached {time.LocalDateTime:MM/dd HH:mm} · sync failed"
            : chinese ? "未同步" : "Not synced";

    public static UsageDataState Evaluate(bool enabled, bool hasData, DateTimeOffset? synced,
        bool failed, DateTimeOffset now)
    {
        if (!enabled) return UsageDataState.Disabled;
        if (!hasData) return UsageDataState.Unavailable;
        if (synced is null || !UsableTime(synced, now) || now - synced > FreshAge)
            return UsageDataState.Stale;
        return failed ? UsageDataState.Cached : UsageDataState.Fresh;
    }

    public static UsageDataState State(CodexUsage usage, bool enabled = true) =>
        Evaluate(enabled, usage.HasData, usage.OfficialSyncedAt, usage.IsStale, DateTimeOffset.Now);

    public static UsageDataState State(ClaudeUsage usage, bool enabled = true) =>
        Evaluate(enabled, usage.HasLimitData, usage.OfficialSyncedAt,
            usage.SyncState != ClaudeSyncState.LiveSynced, DateTimeOffset.Now);

    public static string Color(UsageDataState state) => state switch
    {
        UsageDataState.Fresh => "#35E66B",
        UsageDataState.Cached or UsageDataState.Stale => "#FFBD59",
        UsageDataState.Disabled => "#88949C",
        _ => "#FF4A4A"
    };

    public static string Describe(UsageDataState state, DateTimeOffset? synced, bool chinese)
    {
        var label = state switch
        {
            UsageDataState.Fresh => chinese ? "官方資料已同步" : "Official data synced",
            UsageDataState.Cached => chinese ? "同步失敗，顯示快取" : "Sync failed; cached data",
            UsageDataState.Stale => chinese ? "資料已過期，等待同步" : "Stale data; awaiting sync",
            UsageDataState.Disabled => chinese ? "資料讀取已關閉" : "Data reading disabled",
            _ => chinese ? "官方資料未提供" : "Official data unavailable"
        };
        if (synced is not { } time) return label;
        var minutes = Math.Max(0, (int)(DateTimeOffset.Now - time).TotalMinutes);
        return label + (chinese
            ? $"\n上次同步 {time.LocalDateTime:yyyy/MM/dd HH:mm:ss}（{minutes} 分鐘前）"
            : $"\nLast synced {time.LocalDateTime:yyyy/MM/dd HH:mm:ss} ({minutes} min ago)");
    }
}
