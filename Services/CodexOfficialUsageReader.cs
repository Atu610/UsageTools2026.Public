using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace UsageTools2026.Services;

/// <summary>
/// Reads Codex's OFFICIAL account usage from ChatGPT's backend, matching the "remaining usage"
/// panel. Codex stores an OAuth bearer token in ~/.codex/auth.json, so this is a simple
/// authenticated GET — no cookies, no Cloudflare, no VSS needed.
///
/// The main account bucket lives in <c>rate_limit.primary_window/secondary_window</c>; per-model
/// buckets (e.g. GPT-5.3-Codex-Spark) sit under <c>additional_rate_limits</c> and are ignored, since
/// the local JSONL only ever records whichever model was last used (which made Codex show a stray 0%).
/// </summary>
public sealed class CodexOfficialUsageReader
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage?supports_rewardless_invites=true";
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageTools2026.Public",
        "codex-official-usage.json");

    private static readonly object Gate = new();
    private static CodexOfficialUsage? _cached;
    private static long _cachedAtTick;
    private static bool _cacheLoaded;
    private static PendingWindowSample? _pendingPrimary;
    private static PendingWindowSample? _pendingSecondary;

    private sealed record PendingWindowSample(
        double? Percent,
        int? WindowMinutes,
        DateTimeOffset? ResetsAt);

    public sealed record CodexOfficialUsage(
        double? PrimaryPercent,
        int? PrimaryWindowMinutes,
        DateTimeOffset? PrimaryResetsAt,
        double? SecondaryPercent,
        int? SecondaryWindowMinutes,
        DateTimeOffset? SecondaryResetsAt,
        string? PlanType = null,
        DateTimeOffset? SyncedAt = null,
        DateTimeOffset? LastAttemptAt = null,
        bool? LastAttemptOk = null,
        string? AccountKey = null,
        Models.CodexCreditBalance? Credits = null);

    public CodexOfficialUsage? TryRead(out string? failure)
    {
        failure = null;

        string? accountKey = null;
        try
        {
            var authPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "auth.json");

            if (!File.Exists(authPath))
            {
                failure = "Codex auth.json not found.";
                return null;
            }

            string? accessToken;
            string? accountId;
            using (var authStream = new FileStream(authPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var authDoc = JsonDocument.Parse(authStream))
            {
                if (!authDoc.RootElement.TryGetProperty("tokens", out var tokens))
                {
                    failure = "Codex tokens missing.";
                    return null;
                }

                accessToken = tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                accountId = tokens.TryGetProperty("account_id", out var ac) ? ac.GetString() : null;
            }

            accountKey = UsageFreshness.AccountKey("codex", accountId);
            if (accountKey is null)
            {
                failure = "Codex account identity unavailable.";
                return null;
            }
            lock (Gate)
            {
                if (!_cacheLoaded)
                {
                    _cached = LoadCache();
                    _cacheLoaded = true;
                }
                if (!UsageFreshness.SameAccount(_cached?.AccountKey, accountKey))
                {
                    _cached = null;
                    _pendingPrimary = null;
                    _pendingSecondary = null;
                }
                _cached = ValidFallback(_cached);
                if (_cached is not null && Environment.TickCount64 - _cachedAtTick < 30_000)
                    return _cached;
            }

            if (string.IsNullOrEmpty(accessToken))
            {
                failure = "Codex access token missing.";
                return CachedFallback();
            }

            // ChatGPT's backend sits behind Cloudflare, which 403s the default SocketsHttpHandler's
            // TLS fingerprint. WinHttpHandler uses the OS WinHTTP stack (like the browser / PowerShell),
            // which Cloudflare lets through — so no WebView2 is needed here.
            using var handler = new WinHttpHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            if (!string.IsNullOrEmpty(accountId))
            {
                request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            }
            request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

            // WinHttpHandler only supports async send; block on it (we run on a background thread).
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                failure = $"Codex usage API returned {(int)response.StatusCode}.";
                return CachedFallback();
            }

            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(stream);
            var responseRoot = UnwrapResult(document.RootElement);
            if (!TryGetMainRateLimit(document.RootElement, out var rateLimit))
            {
                failure = "Codex usage response had no main rate-limit bucket.";
                return CachedFallback();
            }

            var primary = ReadWindow(
                rateLimit,
                300,
                "primary_window",
                "primary",
                "five_hour",
                "fiveHour",
                "five_hour_window",
                "fiveHourWindow");
            var secondary = ReadWindow(
                rateLimit,
                10080,
                "secondary_window",
                "secondary",
                "seven_day",
                "sevenDay",
                "weekly",
                "weekly_window",
                "weeklyWindow");
            if (primary.Percent is null && secondary.Percent is null)
            {
                failure = "Codex usage response had no windows.";
                return CachedFallback();
            }

            var syncedAt = DateTimeOffset.Now;
            var result = new CodexOfficialUsage(
                primary.Percent, primary.WindowMinutes, primary.ResetsAt,
                secondary.Percent, secondary.WindowMinutes, secondary.ResetsAt,
                ReadString(responseRoot, "plan_type") ?? ReadString(responseRoot, "planType"),
                syncedAt,
                syncedAt,
                true,
                accountKey,
                ParseCredits(responseRoot));

            lock (Gate)
            {
                var observed = result;
                result = Stabilize(_cached, result, out var stabilized);
                if (stabilized)
                {
                    DiagnosticsService.Write(
                        $"Codex official lower sample held: " +
                        $"observed primary={observed.PrimaryPercent:0.#}%/{observed.PrimaryWindowMinutes}m " +
                        $"reset={observed.PrimaryResetsAt:O}, secondary={observed.SecondaryPercent:0.#}%/" +
                        $"{observed.SecondaryWindowMinutes}m reset={observed.SecondaryResetsAt:O}; " +
                        $"accepted primary={result.PrimaryPercent:0.#}%, secondary={result.SecondaryPercent:0.#}%.");
                }

                _cached = result;
                _cachedAtTick = Environment.TickCount64;
                SaveCache(result);
            }

            return result;
        }
        catch (Exception ex)
        {
            failure = "Codex official sync failed: " + ex.GetType().Name + ".";
            return accountKey is null ? null : CachedFallback();
        }
    }

    private static CodexOfficialUsage? CachedFallback()
    {
        lock (Gate)
        {
            var fallback = ValidFallback(_cached);
            if (fallback is not null)
            {
                fallback = fallback with
                {
                    LastAttemptAt = DateTimeOffset.Now,
                    LastAttemptOk = false
                };
                _cached = fallback;
                _cachedAtTick = Environment.TickCount64;
                SaveCache(fallback);
            }

            return fallback;
        }
    }

    internal static Models.CodexCreditBalance? ParseCredits(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Object) return null;
        if (credits.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True)
            return new(null, true);
        if (!credits.TryGetProperty("balance", out var balance)) return null;
        decimal value;
        var valid = balance.ValueKind == JsonValueKind.Number ? balance.TryGetDecimal(out value)
            : decimal.TryParse(balance.ValueKind == JsonValueKind.String ? balance.GetString() : null,
                System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out value);
        return valid && value is >= 0 and <= 1_000_000_000_000m ? new(value) : null;
    }

    internal static CodexOfficialUsage Stabilize(
        CodexOfficialUsage? previous,
        CodexOfficialUsage current,
        out bool stabilized)
    {
        stabilized = false;
        if (previous is null)
        {
            return current;
        }

        var primary = StabilizeWindow(
            previous.PrimaryPercent,
            previous.PrimaryWindowMinutes,
            previous.PrimaryResetsAt,
            current.PrimaryPercent,
            current.PrimaryWindowMinutes,
            current.PrimaryResetsAt,
            ref _pendingPrimary);
        var secondary = StabilizeWindow(
            previous.SecondaryPercent,
            previous.SecondaryWindowMinutes,
            previous.SecondaryResetsAt,
            current.SecondaryPercent,
            current.SecondaryWindowMinutes,
            current.SecondaryResetsAt,
            ref _pendingSecondary);

        stabilized = primary.Stabilized || secondary.Stabilized;
        return current with
        {
            PrimaryPercent = primary.Percent, PrimaryWindowMinutes = primary.WindowMinutes, PrimaryResetsAt = primary.ResetsAt,
            SecondaryPercent = secondary.Percent, SecondaryWindowMinutes = secondary.WindowMinutes, SecondaryResetsAt = secondary.ResetsAt,
            PlanType = current.PlanType ?? previous.PlanType
        };
    }

    private static (double? Percent, int? WindowMinutes, DateTimeOffset? ResetsAt, bool Stabilized) StabilizeWindow(
        double? previousPercent,
        int? previousWindowMinutes,
        DateTimeOffset? previousResetsAt,
        double? currentPercent,
        int? currentWindowMinutes,
        DateTimeOffset? currentResetsAt,
        ref PendingWindowSample? pending)
    {
        var previousStillValid = previousResetsAt is not null && previousResetsAt > DateTimeOffset.Now;

        if (previousStillValid && currentPercent is null)
        {
            if (pending is { Percent: null })
            {
                pending = null;
                return (null, currentWindowMinutes, currentResetsAt, false);
            }

            pending = new PendingWindowSample(null, currentWindowMinutes, currentResetsAt);
            return (previousPercent, previousWindowMinutes, previousResetsAt, previousPercent is not null);
        }

        var sameDuration = previousWindowMinutes is null || currentWindowMinutes is null ||
                           previousWindowMinutes == currentWindowMinutes;
        var sameReset = IsSameReset(previousResetsAt, currentResetsAt);
        var sharpDrop = previousPercent is not null && currentPercent is not null &&
                        currentPercent.Value + 2 < previousPercent.Value;

        // A single lower response can be a transient model bucket. Hold it once, but accept a
        // second consistent sample so a legitimate reset or a corrected 100% can never stick.
        if (previousStillValid && sharpDrop && sameDuration && sameReset)
        {
            if (pending is not null && PendingMatches(pending, currentPercent!.Value, currentWindowMinutes, currentResetsAt))
            {
                pending = null;
                return (currentPercent, currentWindowMinutes ?? previousWindowMinutes, currentResetsAt ?? previousResetsAt, false);
            }

            pending = new PendingWindowSample(currentPercent!.Value, currentWindowMinutes, currentResetsAt);
            return (previousPercent, previousWindowMinutes, previousResetsAt, true);
        }

        pending = null;
        if (currentPercent is null)
        {
            return (null, currentWindowMinutes, currentResetsAt, false);
        }

        return (
            currentPercent,
            currentWindowMinutes ?? previousWindowMinutes,
            currentResetsAt ?? previousResetsAt,
            false);
    }

    private static bool PendingMatches(
        PendingWindowSample pending,
        double currentPercent,
        int? currentWindowMinutes,
        DateTimeOffset? currentResetsAt)
    {
        var samePercentRange = pending.Percent is { } pendingPercent &&
                               Math.Abs(pendingPercent - currentPercent) <= 3;
        var sameDuration = pending.WindowMinutes is null || currentWindowMinutes is null ||
                           pending.WindowMinutes == currentWindowMinutes;
        return samePercentRange && sameDuration && IsSameReset(pending.ResetsAt, currentResetsAt);
    }

    private static bool IsSameReset(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return Math.Abs((first.Value - second.Value).TotalMinutes) <= 5;
    }

    private static CodexOfficialUsage? ValidFallback(CodexOfficialUsage? cached)
    {
        if (cached is null || !UsageFreshness.UsableTime(cached.SyncedAt, DateTimeOffset.Now))
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var primaryPresent = UsageFreshness.Window(cached.PrimaryPercent, cached.PrimaryResetsAt, now) is not null;
        var secondaryPresent = UsageFreshness.Window(cached.SecondaryPercent, cached.SecondaryResetsAt, now) is not null;
        if (!primaryPresent && !secondaryPresent)
        {
            return null;
        }

        return new CodexOfficialUsage(
            primaryPresent ? cached.PrimaryPercent : null,
            primaryPresent ? cached.PrimaryWindowMinutes : null,
            primaryPresent ? cached.PrimaryResetsAt : null,
            secondaryPresent ? cached.SecondaryPercent : null,
            secondaryPresent ? cached.SecondaryWindowMinutes : null,
            secondaryPresent ? cached.SecondaryResetsAt : null,
            cached.PlanType,
            cached.SyncedAt,
            cached.LastAttemptAt,
            cached.LastAttemptOk,
            cached.AccountKey,
            cached.Credits);
    }

    private static CodexOfficialUsage? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var cached = JsonSerializer.Deserialize<CodexOfficialUsage>(File.ReadAllText(CachePath));
            return ValidFallback(cached);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(CodexOfficialUsage usage)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(directory);
            AtomicFile.WriteText(CachePath, JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A cache write failure must not interrupt usage refresh.
        }
    }

    private static bool TryGetMainRateLimit(JsonElement root, out JsonElement rateLimit)
    {
        rateLimit = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var source = UnwrapResult(root);

        if (TryGetObject(source, "rate_limit", out rateLimit))
        {
            return true;
        }

        if ((TryGetObject(source, "rateLimitsByLimitId", out var buckets) ||
             TryGetObject(source, "rate_limits_by_limit_id", out buckets)) &&
            buckets.TryGetProperty("codex", out var mainBucket) &&
            mainBucket.ValueKind == JsonValueKind.Object)
        {
            rateLimit = mainBucket;
            return true;
        }

        return TryGetObject(source, "rateLimits", out rateLimit) ||
               TryGetObject(source, "rate_limits", out rateLimit);
    }

    private static JsonElement UnwrapResult(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("result", out var result) &&
        result.ValueKind == JsonValueKind.Object
            ? result
            : root;

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    private static (double? Percent, int? WindowMinutes, DateTimeOffset? ResetsAt) ReadWindow(
        JsonElement rateLimit,
        int? inferredWindowMinutes,
        params string[] names)
    {
        JsonElement window = default;
        var found = false;
        foreach (var name in names)
        {
            if (rateLimit.TryGetProperty(name, out window) && window.ValueKind == JsonValueKind.Object)
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return (null, null, null);
        }

        var percent = ReadDouble(window, "used_percent") ??
                      ReadDouble(window, "usedPercent") ??
                      ReadDouble(window, "usage_percent") ??
                      ReadDouble(window, "usagePercent") ??
                      ReadDouble(window, "utilization") ??
                      ReadDouble(window, "percent");
        int? windowMinutes = null;
        if (ReadDouble(window, "limit_window_seconds") is { } windowSeconds)
        {
            windowMinutes = (int)Math.Round(windowSeconds / 60.0);
        }
        else if (ReadDouble(window, "windowDurationMins") is { } currentMinutes)
        {
            windowMinutes = (int)Math.Round(currentMinutes);
        }
        else if (ReadDouble(window, "window_minutes") is { } legacyMinutes)
        {
            windowMinutes = (int)Math.Round(legacyMinutes);
        }

        windowMinutes ??= inferredWindowMinutes;

        var resetsAt = ReadTimestamp(window, "reset_at") ??
                       ReadTimestamp(window, "resetsAt") ??
                       ReadTimestamp(window, "resets_at") ??
                       ReadTimestamp(window, "resetAt") ??
                       ReadRelativeSeconds(window, "reset_after_seconds") ??
                       ReadRelativeSeconds(window, "resetAfterSeconds");

        return (percent, windowMinutes, resetsAt);
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var result)
            ? result
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var unixSeconds) &&
            unixSeconds >= 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return value.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? ReadRelativeSeconds(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var seconds) ||
            seconds < 0)
        {
            return null;
        }

        return DateTimeOffset.Now.AddSeconds(seconds);
    }
}
