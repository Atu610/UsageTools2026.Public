using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public sealed class CodexResetCreditReader
{
    private const string CreditsEndpoint = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageTools2026.Public",
        "codex-reset-credits.json");

    private static readonly object Gate = new();
    private static CodexResetCreditSummary? _cached;
    private static long _lastAttemptTick;
    private static bool _cacheLoaded;
    private static bool _attempted;

    public CodexResetCreditSummary Read()
    {
        lock (Gate)
        {
            if (!_cacheLoaded)
            {
                _cached = LoadCache();
                _cacheLoaded = true;
            }

            if (ShouldUseCache(_attempted, Environment.TickCount64 - _lastAttemptTick))
            {
                return RemoveExpired(_cached ?? CodexResetCreditSummary.Empty);
            }
            _attempted = true;
            _lastAttemptTick = Environment.TickCount64;
        }

        try
        {
            var summary = Fetch();
            lock (Gate)
            {
                _cached = summary;
                _lastAttemptTick = Environment.TickCount64;
                SaveCache(summary);
                return summary;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write("Codex reset credit sync failed: " + ex.GetType().Name + ".");
            lock (Gate)
            {
                _lastAttemptTick = Environment.TickCount64;
                var fallback = RemoveExpired(_cached ?? CodexResetCreditSummary.Empty);
                _cached = fallback with { IsStale = true };
                return _cached;
            }
        }
    }

    private static CodexResetCreditSummary Fetch()
    {
        var authPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "auth.json");
        using var authStream = new FileStream(
            authPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var authDocument = JsonDocument.Parse(authStream);
        if (!authDocument.RootElement.TryGetProperty("tokens", out var tokens))
        {
            throw new InvalidDataException("Codex auth tokens are missing.");
        }

        var accessToken = tokens.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        var accountId = tokens.TryGetProperty("account_id", out var accountIdElement)
            ? accountIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidDataException("Codex access token is missing.");
        }

        using var handler = new WinHttpHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Get, CreditsEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");

        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var responseStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(responseStream);
        return ParseResponse(document.RootElement);
    }

    internal static bool ShouldUseCache(bool attempted, long elapsedMilliseconds) =>
        attempted && elapsedMilliseconds >= 0 && elapsedMilliseconds < RefreshInterval.TotalMilliseconds;

    internal static CodexResetCreditSummary ParseResponse(JsonElement root)
    {
        var container = GetCreditContainer(root);
        var credits = ParseCredits(container);
        var availableCount = ReadAvailableCount(container) ?? credits.Count;
        return new CodexResetCreditSummary(credits, DateTimeOffset.Now, false, availableCount);
    }

    private static JsonElement GetCreditContainer(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        var source = root;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            source = result;
        }

        if (source.TryGetProperty("rateLimitResetCredits", out var current) &&
            current.ValueKind == JsonValueKind.Object)
        {
            return current;
        }

        if (source.TryGetProperty("rate_limit_reset_credits", out var legacy) &&
            legacy.ValueKind == JsonValueKind.Object)
        {
            return legacy;
        }

        return source;
    }

    private static int? ReadAvailableCount(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("available_count", out var legacy) && legacy.TryGetInt32(out var legacyCount))
        {
            return Math.Max(0, legacyCount);
        }

        return root.TryGetProperty("availableCount", out var current) && current.TryGetInt32(out var currentCount)
            ? Math.Max(0, currentCount)
            : null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? ReadExpiry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            (!element.TryGetProperty("expires_at", out var value) &&
             !element.TryGetProperty("expiresAt", out value)))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
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

        return null;
    }

    private static IReadOnlyList<CodexResetCredit> ParseCredits(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var creditsElement) ||
            creditsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CodexResetCredit>();
        }

        var now = DateTimeOffset.Now;
        var credits = new List<CodexResetCredit>();
        foreach (var element in creditsElement.EnumerateArray())
        {
            var status = ReadString(element, "status");
            var resetType = ReadString(element, "reset_type") ?? ReadString(element, "resetType");
            var normalizedResetType = resetType?.Replace("_", string.Empty, StringComparison.Ordinal);
            if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(normalizedResetType, "codexRateLimits", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = ReadString(element, "id");
            var title = ReadString(element, "title");
            var expiresAt = ReadExpiry(element);
            if (string.IsNullOrWhiteSpace(id) ||
                expiresAt is null ||
                expiresAt <= now)
            {
                continue;
            }

            credits.Add(new CodexResetCredit(
                id,
                string.IsNullOrWhiteSpace(title) ? "Codex usage reset" : title.Trim(),
                expiresAt.Value));
        }

        return credits.OrderBy(credit => credit.ExpiresAt).ToArray();
    }

    private static CodexResetCreditSummary? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            using var stream = new FileStream(
                CachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var syncedAt = root.TryGetProperty("syncedAt", out var syncedElement) &&
                           DateTimeOffset.TryParse(syncedElement.GetString(), out var parsedSyncedAt)
                ? parsedSyncedAt
                : (DateTimeOffset?)null;
            var container = GetCreditContainer(root);
            var credits = ParseCredits(container);
            var availableCount = ReadAvailableCount(container) ?? credits.Count;
            return RemoveExpired(new CodexResetCreditSummary(credits, syncedAt, true, availableCount));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(CodexResetCreditSummary summary)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            using var stream = new FileStream(CachePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            if (summary.SyncedAt is { } syncedAt)
            {
                writer.WriteString("syncedAt", syncedAt);
            }
            else
            {
                writer.WriteNull("syncedAt");
            }
            writer.WriteNumber("available_count", summary.AvailableCount);
            writer.WriteStartArray("credits");
            foreach (var credit in summary.Credits)
            {
                writer.WriteStartObject();
                writer.WriteString("id", credit.Id);
                writer.WriteString("title", credit.Title);
                writer.WriteString("status", "available");
                writer.WriteString("reset_type", "codex_rate_limits");
                writer.WriteString("expires_at", credit.ExpiresAt);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        catch
        {
            // A cache failure must never interrupt the usage refresh loop.
        }
    }

    private static CodexResetCreditSummary RemoveExpired(CodexResetCreditSummary summary)
    {
        var active = summary.Credits
            .Where(credit => credit.ExpiresAt > DateTimeOffset.Now)
            .OrderBy(credit => credit.ExpiresAt)
            .ToArray();
        var removedCount = summary.Credits.Count - active.Length;
        int? reportedCount = summary.ReportedAvailableCount is { } count
            ? Math.Max(0, count - removedCount)
            : null;
        return summary with { Credits = active, ReportedAvailableCount = reportedCount };
    }
}
