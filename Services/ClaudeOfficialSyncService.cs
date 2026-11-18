using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

/// <summary>
/// Orchestrates a full official-usage sync: decrypt Claude cookies (via VSS snapshot when locked),
/// fetch the usage JSON through an embedded WebView2 (passing Cloudflare), parse the official
/// percentages, and persist them to <see cref="ClaudeOfficialUsageCache"/>.
///
/// MUST run on the WPF UI (STA) thread because of WebView2. Returns a safe one-line diagnostic
/// (no cookies/tokens). Heavy operation — call it on a throttled schedule, not every poll.
/// </summary>
public sealed class ClaudeOfficialSyncService
{
    private bool _isSyncing;

    private static readonly TimeSpan CookieReadTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WebViewFetchTimeout = TimeSpan.FromSeconds(32);
    private static readonly TimeSpan ModelRefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ModelFailureBackoff = TimeSpan.FromMinutes(30);
    private DateTimeOffset _nextModelAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAttempt;
    private int _failures;

    public bool IsSyncing => _isSyncing;
    public void RetryAfterLogin() { _nextAttempt = DateTimeOffset.MinValue; _nextModelAttempt = DateTimeOffset.MinValue; }

    public async Task<string> SyncAsync()
    {
        if (_isSyncing)
        {
            return "sync already in progress";
        }
        if (DateTimeOffset.Now < _nextAttempt || ClaudeWebViewUsageFetcher.IsLoginOpen) return "backoff";

        _isSyncing = true;
        try
        {
            var cookieRead = await ReadCookiesAsync(CookieReadTimeout);
            var cookieSet = cookieRead.CookieSet;
            if (cookieSet is null)
            {
                var profile = ClaudeProfileAccess.FindProfileRoot();
                var org = profile is null ? null : ClaudeProfileAccess.FindOrganizationId(profile);
                if (org is null)
                {
                    RecordFailure("desktop_organization_unavailable; " + cookieRead.Failure);
                    return "desktop_organization_unavailable";
                }
                cookieSet = new ClaudeProfileAccess.CookieSet(org, Array.Empty<KeyValuePair<string, string>>());
            }

            var accountKey = UsageFreshness.AccountKey("claude", cookieSet.OrganizationId);
            var previous = ClaudeOfficialUsageCache.Load();
            var previousDetails = previous is not null && UsageFreshness.SameAccount(previous.AccountKey, accountKey)
                ? previous.Details : null;
            var needModels = DateTimeOffset.Now >= _nextModelAttempt &&
                             (previousDetails?.ModelsSyncedAt is not { } modelsAt ||
                              DateTimeOffset.Now - modelsAt > ModelRefreshInterval || DateTimeOffset.Now < modelsAt);

            ClaudeWebViewUsageFetcher.FetchResult? fetched;
            using var fetchTimeout = new CancellationTokenSource(WebViewFetchTimeout);
            try
            {
                fetched = await ClaudeWebViewUsageFetcher.TryFetchUsageJsonAsync(cookieSet, needModels, fetchTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                RecordFailure("Claude WebView2 usage fetch timed out.");
                return "webview: timed out";
            }

            if (fetched is null || string.IsNullOrWhiteSpace(fetched.UsageJson))
            {
                var reason = fetched?.Failure ?? "webview_no_usage";
                RecordFailure(cookieRead.CookieSet is null ? cookieRead.Failure + "; " + reason : reason);
                return reason;
            }

            var now = DateTimeOffset.Now;
            var parsed = ParseUsage(fetched.UsageJson, fetched.OrganizationJson, now);
            var models = ClaudeOfficialDetailsParser.ParseModels(fetched.ModelsJson);
            if (needModels) DiagnosticsService.Write("Claude model fetch: " + fetched.ModelsDiagnostic + $" parsed={models?.Count.ToString() ?? "unknown"}");
            if (needModels && models is null) _nextModelAttempt = now + ModelFailureBackoff;
            var details = models is not null
                ? parsed.Details with { Models = models, ModelsSyncedAt = now }
                : parsed.Details with { Models = previousDetails?.Models, ModelsSyncedAt = previousDetails?.ModelsSyncedAt };

            // Until the real plan/Fable fields are identified, capture a privacy-safe schema report
            // (paths + kinds only). Once both fields parse, this stops writing automatically.
            if (parsed.PlanType is null || parsed.Fable.Percent is null)
            {
                ClaudeSchemaDiagnostics.TryWriteReport(fetched.UsageJson, fetched.OrganizationJson);
            }

            if (!parsed.Ok)
            {
                RecordFailure("Usage JSON had no plan percentages.");
                return "parse: no percentages";
            }

            new ClaudeOfficialUsageCache(
                parsed.Five.Percent, parsed.Five.ResetsAt,
                parsed.Seven.Percent, parsed.Seven.ResetsAt,
                now,
                now,
                true,
                "OK",
                parsed.PlanType,
                parsed.Fable.Percent,
                parsed.Fable.ResetsAt,
                accountKey,
                details).Save();
            _failures = 0;
            _nextAttempt = DateTimeOffset.MinValue;

            var fableText = parsed.Fable.Percent is { } fable
                ? $" Fable={fable:0.#}%"
                : string.Empty;
            var resetText = details.Resets?.AvailableCount(now) is { } count ? $" resets={count}" : string.Empty;
            var modelText = details.Models is { } list ? $" models={list.Count}" : string.Empty;
            return $"OK 5h={parsed.Five.Percent?.ToString("0.#") ?? "?"}% " +
                   $"7d={parsed.Seven.Percent?.ToString("0.#") ?? "?"}%{fableText}{resetText}{modelText}";
        }
        catch (Exception ex)
        {
            RecordFailure(ex.GetType().Name);
            return "sync error: " + ex.GetType().Name;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private static async Task<CookieReadResult> ReadCookiesAsync(TimeSpan timeout)
    {
        var task = Task.Run(() =>
        {
            var cookieSet = ClaudeProfileAccess.TryGetCookies(out var failure);
            return new CookieReadResult(cookieSet, failure, false);
        });

        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(completed, task))
        {
            return new CookieReadResult(null, "Claude cookie/profile read timed out.", true);
        }

        return await task;
    }

    // Preserve any previously-synced percentages but record that the latest attempt failed, so the
    // UI can honestly show "last synced HH:mm, last attempt failed".
    private void RecordFailure(string detail)
    {
        _failures = Math.Min(_failures + 1, 5);
        _nextAttempt = DateTimeOffset.Now.AddMinutes(Math.Min(30, 2 * Math.Pow(2, _failures - 1)));
        var existing = ClaudeOfficialUsageCache.Load();
        if (existing is not null && existing.HasPercent)
        {
            new ClaudeOfficialUsageCache(
                existing.PrimaryUsedPercent, existing.PrimaryResetsAt,
                existing.SecondaryUsedPercent, existing.SecondaryResetsAt,
                existing.SyncedAt,
                DateTimeOffset.Now,
                false,
                detail,
                existing.PlanType,
                existing.FableUsedPercent,
                existing.FableResetsAt,
                existing.AccountKey,
                existing.Details).Save();
        }
    }

    internal static ParsedUsage ParseUsage(string usageJson, string? organizationJson, DateTimeOffset? syncedAt = null)
    {
        try
        {
            using var document = JsonDocument.Parse(usageJson);
            var root = document.RootElement;
            var five = ReadLimit(root, "five_hour");
            var seven = ReadLimit(root, "seven_day");
            var fable = FindScopedLimit(root, "fable");
            if (fable.Percent is null)
            {
                fable = FindNamedLimit(root, "fable");
            }
            var (planType, planSource) = ClaudeOfficialDetailsParser.DetectPlan(root, organizationJson);
            if (planType is null)
            {
                planType = AmbiguousTierFilter(FindPlanType(root));
                if (planType is null && !string.IsNullOrWhiteSpace(organizationJson))
                {
                    try
                    {
                        using var organizationDocument = JsonDocument.Parse(organizationJson);
                        planType = AmbiguousTierFilter(FindPlanType(organizationDocument.RootElement));
                    }
                    catch
                    {
                        // The usage values are still valid when optional organization metadata changes.
                    }
                }
                planSource = planType is null ? null : "legacy-field";
            }

            var details = ClaudeOfficialDetailsParser.ParseDetails(root, syncedAt ?? DateTimeOffset.Now) with { PlanSource = planSource };
            return new ParsedUsage(
                five,
                seven,
                fable,
                planType,
                five.Percent is not null || seven.Percent is not null,
                details);
        }
        catch
        {
            var empty = new Limit(null, null);
            return new ParsedUsage(empty, empty, empty, null, false, ClaudeOfficialDetails.Empty);
        }
    }

    // default_claude_ai is reported for both Free and Pro organizations (verified 2026-10-02 and
    // 2026-11-11), so on its own it is not evidence of a plan.
    private static string? AmbiguousTierFilter(string? value) =>
        string.Equals(value?.Trim(), "default_claude_ai", StringComparison.OrdinalIgnoreCase) ? null : value;

    private static Limit ReadLimit(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(property, out var limit) ||
            limit.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new Limit(null, null);
        }

        return ReadLimitElement(limit);
    }

    private static Limit ReadLimitElement(JsonElement limit)
    {
        double? percent = null;
        DateTimeOffset? resetsAt = null;

        if (limit.TryGetProperty("utilization", out var utilization) &&
            utilization.ValueKind == JsonValueKind.Number &&
            utilization.TryGetDouble(out var utilizationValue))
        {
            percent = utilizationValue;
        }
        else if (limit.TryGetProperty("used_percent", out var usedPercent) &&
                 usedPercent.ValueKind == JsonValueKind.Number &&
                 usedPercent.TryGetDouble(out var usedPercentValue))
        {
            percent = usedPercentValue;
        }
        else if (limit.TryGetProperty("usedPercent", out var currentUsedPercent) &&
                 currentUsedPercent.ValueKind == JsonValueKind.Number &&
                 currentUsedPercent.TryGetDouble(out var currentUsedPercentValue))
        {
            percent = currentUsedPercentValue;
        }
        else if (limit.TryGetProperty("percent", out var percentElement) &&
                 percentElement.ValueKind == JsonValueKind.Number &&
                 percentElement.TryGetDouble(out var percentValue))
        {
            percent = percentValue;
        }

        if (limit.TryGetProperty("resets_at", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetElement.GetString(), out var parsedReset))
        {
            resetsAt = parsedReset;
        }
        else if (limit.TryGetProperty("reset_at", out var legacyResetElement) &&
                 legacyResetElement.ValueKind == JsonValueKind.String &&
                 DateTimeOffset.TryParse(legacyResetElement.GetString(), out var parsedLegacyReset))
        {
            resetsAt = parsedLegacyReset;
        }

        return new Limit(percent, resetsAt);
    }

    // Official schema (verified 2026-07-15): the Fable percentage lives in the root "limits" array
    // as { kind: "weekly_scoped", percent, resets_at, scope: { model: { display_name: "Fable" } } }.
    // The name marker sits two levels below the percentage, so match any nested string in the entry.
    private static Limit FindScopedLimit(JsonElement root, string namePart)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("limits", out var limits) ||
            limits.ValueKind != JsonValueKind.Array)
        {
            return new Limit(null, null);
        }

        foreach (var item in limits.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !ContainsStringDeep(item, namePart, 4))
            {
                continue;
            }

            var candidate = ReadLimitElement(item);
            if (candidate.Percent is not null)
            {
                return candidate;
            }
        }

        return new Limit(null, null);
    }

    private static bool ContainsStringDeep(JsonElement element, string namePart, int depth)
    {
        if (depth < 0)
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String =>
                element.GetString()?.Contains(namePart, StringComparison.OrdinalIgnoreCase) == true,
            JsonValueKind.Object =>
                element.EnumerateObject().Any(property => ContainsStringDeep(property.Value, namePart, depth - 1)),
            JsonValueKind.Array =>
                element.EnumerateArray().Any(item => ContainsStringDeep(item, namePart, depth - 1)),
            _ => false
        };
    }

    private static Limit FindNamedLimit(JsonElement element, string namePart)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var describesRequestedLimit = element.EnumerateObject().Any(property =>
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString()?.Contains(namePart, StringComparison.OrdinalIgnoreCase) == true);
            if (describesRequestedLimit)
            {
                var current = ReadLimitElement(element);
                if (current.Percent is not null)
                {
                    return current;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    var candidate = ReadLimitElement(property.Value);
                    if (candidate.Percent is not null)
                    {
                        return candidate;
                    }
                }

                var nested = FindNamedLimit(property.Value, namePart);
                if (nested.Percent is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindNamedLimit(item, namePart);
                if (nested.Percent is not null)
                {
                    return nested;
                }
            }
        }

        return new Limit(null, null);
    }

    private static string? FindPlanType(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsPlanProperty(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                if (IsPlanProperty(property.Name) &&
                    property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var value = FindPlanScalar(property.Value);
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindPlanType(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindPlanType(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? FindPlanScalar(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("slug", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("type", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("value", StringComparison.OrdinalIgnoreCase)))
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindPlanScalar(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindPlanScalar(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool IsPlanProperty(string name) =>
        name.Equals("rate_limit_tier", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("subscription_type", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("plan_type", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("billing_plan", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("plan", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("tier", StringComparison.OrdinalIgnoreCase);

    internal sealed record Limit(double? Percent, DateTimeOffset? ResetsAt);
    private sealed record CookieReadResult(ClaudeProfileAccess.CookieSet? CookieSet, string Failure, bool TimedOut);
    internal sealed record ParsedUsage(
        Limit Five,
        Limit Seven,
        Limit Fable,
        string? PlanType,
        bool Ok,
        ClaudeOfficialDetails Details);
}
