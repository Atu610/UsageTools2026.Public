using System.IO;
using System.Diagnostics;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

/// <summary>
/// Produces the Claude usage snapshot shown in the UI. This is the lightweight, synchronous read
/// path used on every poll: it only reads the locally-cached official percentages written by
/// <see cref="ClaudeOfficialSyncService"/> and reports an honest sync state. The heavy work
/// (VSS cookie read + WebView2 fetch) happens out-of-band in the sync service.
/// </summary>
public sealed class ClaudeUsageReader
{
    private int _lastLoggedWorkCount = -1;

    // Active Claude working windows (CLI sessions) and their live context-window fill, read from
    // ~/.claude/projects/**/*.jsonl. Reuses CodexWorkWindow as a generic work-window record.
    public IReadOnlyList<CodexWorkWindow> ReadWorkWindows()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");

        if (!Directory.Exists(root))
        {
            return Array.Empty<CodexWorkWindow>();
        }

        var cut = DateTimeOffset.Now.AddHours(-4);
        var list = new List<CodexWorkWindow>();

        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cut.LocalDateTime)
                {
                    continue;
                }

                var (used, cwd, timestamp, model, effort, workName) = ReadLastAssistantContext(file);
                if (used <= 0)
                {
                    continue;
                }

                // Claude models default to 200K context, with a 1M beta; infer which from usage.
                var window = used > 200_000 ? 1_000_000 : 200_000;
                list.Add(new CodexWorkWindow(
                    ShortenCwd(cwd),
                    ExtractSessionId(info.Name),
                    used,
                    window,
                    timestamp ?? new DateTimeOffset(info.LastWriteTime),
                    model,
                    effort,
                    workName));
            }
            catch
            {
                // skip locked or malformed session files
            }
        }

        if (_lastLoggedWorkCount != list.Count)
        {
            _lastLoggedWorkCount = list.Count;
            DiagnosticsService.Write($"Claude work windows: {list.Count} active within 4h.");
        }

        return list
            .OrderByDescending(window => window.LastActivity)
            .Take(20)
            .ToList();
    }

    private static (long Used, string? Cwd, DateTimeOffset? Timestamp, string? Model, string? Effort, string? WorkName) ReadLastAssistantContext(string path)
    {
        long used = 0;
        string? cwd = null;
        DateTimeOffset? timestamp = null;
        string? model = null;
        string? effort = null;
        string? customTitle = null;
        string? slug = null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.Contains("\"usage\"", StringComparison.Ordinal) &&
                !line.Contains("\"effort\"", StringComparison.Ordinal) &&
                !line.Contains("\"reasoning_effort\"", StringComparison.Ordinal) &&
                !line.Contains("\"customTitle\"", StringComparison.Ordinal) &&
                !line.Contains("\"slug\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var rootElement = document.RootElement;

                var currentCustomTitle = GetString(rootElement, "customTitle");
                if (!string.IsNullOrWhiteSpace(currentCustomTitle))
                {
                    customTitle = currentCustomTitle.Trim();
                }

                var currentSlug = GetString(rootElement, "slug");
                if (!string.IsNullOrWhiteSpace(currentSlug))
                {
                    slug = currentSlug.Trim();
                }

                if (cwd is null &&
                    rootElement.TryGetProperty("cwd", out var cwdElement) &&
                    cwdElement.ValueKind == JsonValueKind.String)
                {
                    cwd = cwdElement.GetString();
                }

                effort = GetString(rootElement, "effort")
                    ?? GetString(rootElement, "reasoning_effort")
                    ?? effort;

                if (!rootElement.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var currentModel = GetString(message, "model");
                if (!string.IsNullOrWhiteSpace(currentModel) &&
                    !string.Equals(currentModel, "<synthetic>", StringComparison.OrdinalIgnoreCase))
                {
                    model = currentModel;
                }

                effort = GetString(message, "effort")
                    ?? GetString(message, "reasoning_effort")
                    ?? effort;

                if (!message.TryGetProperty("usage", out var usage))
                {
                    continue;
                }

                var current = GetLong(usage, "input_tokens")
                    + GetLong(usage, "cache_creation_input_tokens")
                    + GetLong(usage, "cache_read_input_tokens");
                if (current > 0)
                {
                    used = current;
                    if (rootElement.TryGetProperty("timestamp", out var ts) &&
                        ts.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(ts.GetString(), out var parsed))
                    {
                        timestamp = parsed;
                    }
                }
            }
            catch
            {
                // tolerate partially-written lines while Claude is active
            }
        }

        return (used, cwd, timestamp, model, effort, customTitle ?? slug);
    }

    private static long GetLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ShortenCwd(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "(unknown)";
        }

        try
        {
            return new DirectoryInfo(cwd).Name;
        }
        catch
        {
            return cwd;
        }
    }

    private static string ExtractSessionId(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.Length >= 8 ? name[..8] : name;
    }

    public ClaudeUsage Read()
    {
        var profileRoot = ClaudeProfileAccess.FindProfileRoot();
        var isInstalled = profileRoot is not null || IsClaudeCodeInstalled();

        if (!isInstalled)
        {
            return ClaudeUsage.Missing;
        }

        var cache = ClaudeOfficialUsageCache.Load()?.ForAccount(
            ClaudeOfficialUsageCache.CurrentAccountKey(), DateTimeOffset.Now);
        if (cache is not null)
        {
            // We have a real official value. If the latest sync attempt failed, say so honestly but
            // keep showing the last good numbers.
            var attemptFailed = cache.LastAttemptAt is not null && !cache.LastAttemptOk;
            var stale = DateTimeOffset.Now - cache.SyncedAt > UsageFreshness.FreshAge;
            var state = attemptFailed || stale ? ClaudeSyncState.CachedSyncFailed : ClaudeSyncState.LiveSynced;
            var detail = attemptFailed
                ? $"Official usage synced {cache.SyncedAt.LocalDateTime:MM/dd HH:mm}; last attempt failed ({cache.LastAttemptDetail})."
                : $"Official usage synced {cache.SyncedAt.LocalDateTime:MM/dd HH:mm}.";

            return new ClaudeUsage(
                true,
                cache.SyncedAt,
                detail,
                cache.PrimaryUsedPercent,
                cache.SecondaryUsedPercent,
                cache.HasPercent,
                state,
                cache.SyncedAt,
                cache.PrimaryResetsAt,
                cache.SecondaryResetsAt,
                cache.PlanType,
                cache.FableUsedPercent,
                cache.FableResetsAt,
                cache.Details,
                cache.AccountKey);
        }

        // Installed but no official percentage has been synced yet. Never fake 0%.
        var reason = cache?.LastAttemptDetail is { Length: > 0 } d
            ? $"Official usage not synced yet (last attempt: {d})."
            : "Official usage has not been synced yet.";

        return new ClaudeUsage(
            true,
            null,
            reason,
            null,
            null,
            false,
            ClaudeSyncState.NeverSynced);
    }

    private static bool IsClaudeCodeInstalled()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(Path.Combine(userProfile, ".claude")) ||
                   File.Exists(Path.Combine(userProfile, ".local", "bin", "claude.exe")) ||
                   IsClaudeProcessRunning();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsClaudeProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("claude").Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
