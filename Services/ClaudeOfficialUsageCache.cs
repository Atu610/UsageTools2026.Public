using System.IO;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

/// <summary>
/// Persists ONLY the official Claude plan percentages, their reset/sync times, and the outcome of
/// the most recent sync attempt. Never stores cookies, session tokens, or any authorization material.
/// </summary>
public sealed record ClaudeOfficialUsageCache(
    double? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    double? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    DateTimeOffset SyncedAt,
    DateTimeOffset? LastAttemptAt = null,
    bool LastAttemptOk = false,
    string? LastAttemptDetail = null,
    string? PlanType = null,
    double? FableUsedPercent = null,
    DateTimeOffset? FableResetsAt = null,
    string? AccountKey = null,
    ClaudeOfficialDetails? Details = null)
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageTools2026.Public",
        "claude-official-usage.json");

    public bool HasPercent => PrimaryUsedPercent is not null || SecondaryUsedPercent is not null;

    internal ClaudeOfficialUsageCache? ForAccount(string? accountKey, DateTimeOffset now)
    {
        if (!UsageFreshness.SameAccount(AccountKey, accountKey))
            return null;
        return this with
        {
            PrimaryUsedPercent = UsageFreshness.ClaudeWindow(PrimaryUsedPercent, PrimaryResetsAt, SyncedAt, now, TimeSpan.FromHours(5)),
            SecondaryUsedPercent = UsageFreshness.ClaudeWindow(SecondaryUsedPercent, SecondaryResetsAt, SyncedAt, now, TimeSpan.FromDays(7)),
            FableUsedPercent = UsageFreshness.ClaudeWindow(FableUsedPercent, FableResetsAt, SyncedAt, now, TimeSpan.FromDays(7)),
            Details = Details is null || SyncedAt > now.AddMinutes(1) || now - SyncedAt >= TimeSpan.FromDays(7) ? null : Details with
            {
                Pools = Details.Pools.Select(pool => pool with { Percent = UsageFreshness.ClaudeWindow(pool.Percent, pool.ResetsAt, SyncedAt, now,
                    pool.Label == "5H" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7)) }).ToArray()
            }
        };
    }

    internal static string? CurrentAccountKey()
    {
        var profile = ClaudeProfileAccess.FindProfileRoot();
        return profile is null ? null : UsageFreshness.AccountKey("claude",
            ClaudeProfileAccess.FindOrganizationId(profile));
    }

    public static ClaudeOfficialUsageCache? Load()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            using var stream = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ClaudeOfficialUsageCache>(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteText(CachePath, json);
        }
        catch
        {
            // Cache persistence is best-effort; a failed write only means the next run cannot show a
            // "last synced" value, never a crash or fake data.
        }
    }
}
