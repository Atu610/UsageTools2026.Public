using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

/// <summary>
/// Parses the optional official Claude details verified on 2026-11-11 (see
/// Reports/CLAUDE_OFFICIAL_EVIDENCE.md): limits[], weekly_scoped_shares[], cedar_ember grants,
/// spend/extra_usage, organization capabilities and the account model list. Missing sections
/// stay null; nothing is inferred from token counts or from other providers.
/// </summary>
internal static partial class ClaudeOfficialDetailsParser
{
    internal const int MaxPools = 8;
    internal const int MaxGrants = 20;
    internal const int MaxModels = 40;

    [GeneratedRegex(@"^[\p{L}\p{N} ._\-]{1,40}$")]
    private static partial Regex SafeNamePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._\-]{0,63}$")]
    private static partial Regex SafeIdPattern();

    internal static string? SafeName(string? value)
    {
        var text = value?.Trim();
        return text is { Length: > 0 } && SafeNamePattern().IsMatch(text) ? text : null;
    }

    internal static string CreditKindName(string? kind, bool english = false) => kind switch
    {
        "full" => english ? "full reset" : "完整重置券",
        "weekly" => english ? "weekly reset" : "週重置券",
        "five_hour" => english ? "5H reset" : "5H 重置券",
        "fable" => english ? "Fable reset" : "Fable 重置券",
        _ => english ? "reset" : "重置券"
    };

    internal static ClaudeOfficialDetails ParseDetails(JsonElement root, DateTimeOffset syncedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) return ClaudeOfficialDetails.Empty;
        return new ClaudeOfficialDetails(
            ParsePools(root),
            ParseResets(root, syncedAt),
            ExtraUsage: ParseExtraUsage(root));
    }

    internal static IReadOnlyList<ClaudeLimitPool> ParsePools(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            return Array.Empty<ClaudeLimitPool>();

        var allowances = new Dictionary<int, double>();
        if (root.TryGetProperty("weekly_scoped_shares", out var shares) && shares.ValueKind == JsonValueKind.Array)
        {
            foreach (var share in shares.EnumerateArray())
            {
                if (Int(share, "limit_index") is { } index && Number(share, "allowance_percent_of_weekly") is { } allowance &&
                    allowance is >= 0 and <= 100)
                    allowances[index] = allowance;
            }
        }

        var pools = new List<ClaudeLimitPool>();
        var position = 0;
        foreach (var item in limits.EnumerateArray())
        {
            var index = position++;
            if (item.ValueKind != JsonValueKind.Object || pools.Count >= MaxPools) continue;
            var kind = String(item, "kind") ?? "unknown";
            var group = String(item, "group");
            string? modelName = null;
            if (item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object)
            {
                if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                    modelName = SafeName(String(model, "display_name")) ?? (String(model, "display_name") is null ? null : "Model");
                if (modelName is null && scope.TryGetProperty("surface", out var surface))
                    modelName = surface.ValueKind == JsonValueKind.String ? SafeName(surface.GetString())
                        : surface.ValueKind == JsonValueKind.Object ? SafeName(String(surface, "display_name") ?? String(surface, "id")) : null;
            }

            var label = kind switch
            {
                "session" => "5H",
                "weekly_all" or "weekly_scoped" => "7D",
                _ => group switch { "session" => "5H", "weekly" => "7D", _ => "?" }
            };
            if (label == "?") continue;
            var percent = Number(item, "percent") ?? Number(item, "utilization");
            if (percent is { } value && (!double.IsFinite(value) || value < 0 || value > 100)) percent = null;
            pools.Add(new ClaudeLimitPool(kind, label, modelName, percent, Date(item, "resets_at"),
                String(item, "severity"), Bool(item, "is_active") == true,
                allowances.TryGetValue(index, out var share2) ? share2 : null));
        }
        return pools;
    }

    internal static ClaudeResetGrantSummary? ParseResets(JsonElement root, DateTimeOffset syncedAt)
    {
        if (!root.TryGetProperty("cedar_ember", out var ember) || ember.ValueKind != JsonValueKind.Object)
            return null;
        // An incomplete response is not an authoritative empty inventory.
        if (!ember.TryGetProperty("grants", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() > MaxGrants || list.EnumerateArray().Any(g => g.ValueKind != JsonValueKind.Object))
            return null;

        var grants = new List<ClaudeResetGrant>();
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var grant in list.EnumerateArray())
            {
                if (grant.ValueKind != JsonValueKind.Object || grants.Count >= MaxGrants) continue;
                var clears = grant.TryGetProperty("clears", out var clearList) && clearList.ValueKind == JsonValueKind.Array
                    ? clearList.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String)
                        .Select(c => c.GetString()!).Where(c => SafeIdPattern().IsMatch(c)).Take(8).ToArray()
                    : Array.Empty<string>();
                var rawId = String(grant, "id");
                var id = rawId is not null && SafeIdPattern().IsMatch(rawId) ? rawId : string.Empty;
                var label = String(grant, "label");
                var total = Int(grant, "resets_total");
                var left = Int(grant, "resets_left");
                grants.Add(new ClaudeResetGrant(
                    id,
                    Category(id, label, clears, Bool(grant, "gives_back_weekly") == true),
                    label is { Length: <= 160 } ? label : null,
                    total is >= 0 and <= 10000 ? total : null,
                    left is >= 0 and <= 10000 ? left : null,
                    Date(grant, "starts_at"),
                    Date(grant, "ends_at"),
                    clears,
                    Bool(grant, "usable_now") == true,
                    Bool(grant, "paused") == true,
                    String(grant, "unavailable_reason")));
            }
        }

        return new ClaudeResetGrantSummary(Bool(ember, "eligible"), grants, Date(ember, "cooldown_until"), syncedAt);
    }

    // The 2026-11 Fable grant reports type "full" yet does not clear seven_day, so classify by clears.
    internal static string Category(string id, string? label, IReadOnlyCollection<string> clears, bool givesBackWeekly)
    {
        var clearsWeekly = clears.Contains("seven_day");
        var clearsSession = clears.Contains("five_hour");
        var mentionsFable = id.Contains("fable", StringComparison.OrdinalIgnoreCase) ||
                            label?.Contains("fable", StringComparison.OrdinalIgnoreCase) == true;
        if (clearsWeekly) return clearsSession ? "full" : "weekly";
        if (mentionsFable || givesBackWeekly) return "fable";
        return clearsSession ? "five_hour" : "other";
    }

    internal static ClaudeExtraUsage? ParseExtraUsage(JsonElement root)
    {
        bool? enabled = null;
        decimal? used = null;
        decimal? limit = null;
        string? currency = null;
        var limitReached = false;

        if (root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object)
        {
            enabled = Bool(spend, "enabled");
            if (spend.TryGetProperty("used", out var usedElement)) (used, currency) = Money(usedElement);
            if (spend.TryGetProperty("limit", out var limitElement))
            {
                var (amount, limitCurrency) = Money(limitElement);
                limit = amount;
                currency ??= limitCurrency;
            }
        }

        if (root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            enabled ??= Bool(extra, "is_enabled");
            limitReached = Bool(extra, "spend_limit_reached") == true;
        }

        return enabled is null && used is null ? null : new ClaudeExtraUsage(enabled == true, used, limit, currency, limitReached);
    }

    private static (decimal? Amount, string? Currency) Money(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("amount_minor", out var minor) || minor.ValueKind != JsonValueKind.Number ||
            !minor.TryGetInt64(out var minorValue) || minorValue < 0)
            return (null, null);
        if (Int(element, "exponent") is not { } exponent || exponent is < 0 or > 6) return (null, null);
        var currency = String(element, "currency");
        return (minorValue / (decimal)Math.Pow(10, exponent),
            currency is { Length: 3 } && currency.All(char.IsAsciiLetterUpper) ? currency : null);
    }

    /// <summary>
    /// Returns a normalized raw plan id (claude_free/claude_pro/claude_max/claude_max_5x/claude_ultra/
    /// claude_team/claude_enterprise) plus its source. rate_limit_tier=default_claude_ai is shared by
    /// Free and Pro, so it never decides the plan on its own.
    /// </summary>
    internal static (string? Plan, string? Source) DetectPlan(JsonElement? usageRoot, string? organizationJson)
    {
        string? tier = null;
        string? capabilityPlan = null;
        string? analyticsPlan = null;
        if (!string.IsNullOrWhiteSpace(organizationJson))
        {
            try
            {
                using var document = JsonDocument.Parse(organizationJson);
                var org = document.RootElement;
                tier = String(org, "rate_limit_tier")?.ToLowerInvariant();
                analyticsPlan = String(org, "analytics_subscription_plan")?.ToLowerInvariant();
                if (org.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
                {
                    var values = capabilities.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String)
                        .Select(c => c.GetString()!.ToLowerInvariant())
                        .Where(v => v.StartsWith("claude_", StringComparison.Ordinal)).ToArray();
                    capabilityPlan = values.FirstOrDefault(v => v.Contains("enterprise", StringComparison.Ordinal))
                        ?? values.FirstOrDefault(v => v.Contains("team", StringComparison.Ordinal))
                        ?? values.FirstOrDefault(v => v.Contains("ultra", StringComparison.Ordinal))
                        ?? values.FirstOrDefault(v => v.Contains("max", StringComparison.Ordinal))
                        ?? values.FirstOrDefault(v => v.Contains("pro", StringComparison.Ordinal));
                }
            }
            catch (JsonException)
            {
                // Optional metadata; usage values stay valid without it.
            }
        }

        string? eventTier = null;
        if (usageRoot is { ValueKind: JsonValueKind.Object } usage &&
            usage.TryGetProperty("cedar_ember", out var ember) && ember.ValueKind == JsonValueKind.Object &&
            ember.TryGetProperty("event_props", out var props) && props.ValueKind == JsonValueKind.Object)
            eventTier = String(props, "tier")?.ToLowerInvariant();

        foreach (var (candidate, source) in new[] { (capabilityPlan, "capabilities"), (analyticsPlan, "analytics_subscription_plan"), (eventTier, "cedar_ember.tier") })
        {
            var plan = Classify(candidate, tier);
            if (plan is not null) return (plan, source);
        }

        // Only a tier that itself names a Max level is decisive; default_claude_ai is ambiguous.
        var tierPlan = tier is not null && (tier.Contains("max", StringComparison.Ordinal) || tier.Contains("ultra", StringComparison.Ordinal))
            ? Classify(tier, tier) : null;
        return tierPlan is null ? (null, null) : (tierPlan, "rate_limit_tier");
    }

    private static string? Classify(string? value, string? tier)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains("enterprise", StringComparison.Ordinal)) return "claude_enterprise";
        if (value.Contains("team", StringComparison.Ordinal)) return "claude_team";
        if (value.Contains("ultra", StringComparison.Ordinal) || value.Contains("20x", StringComparison.Ordinal)) return "claude_ultra";
        if (value.Contains("max", StringComparison.Ordinal))
        {
            if (value.Contains("5x", StringComparison.Ordinal)) return "claude_max_5x";
            if (tier?.Contains("20x", StringComparison.Ordinal) == true || tier?.Contains("ultra", StringComparison.Ordinal) == true) return "claude_ultra";
            if (tier?.Contains("5x", StringComparison.Ordinal) == true) return "claude_max_5x";
            return "claude_max";
        }
        if (value.Contains("pro", StringComparison.Ordinal)) return "claude_pro";
        if (value.Contains("free", StringComparison.Ordinal)) return "claude_free";
        return null;
    }

    /// <summary>Parses the model list produced in-page by the WebView fetcher (no account data).</summary>
    internal static IReadOnlyList<ClaudeModelOption>? ParseModels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var models = new List<ClaudeModelOption>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (models.Count >= MaxModels || item.ValueKind != JsonValueKind.Object) continue;
                var id = String(item, "model");
                var name = SafeName(String(item, "name"));
                if (id is null || !SafeIdPattern().IsMatch(id) || !id.StartsWith("claude-", StringComparison.Ordinal)) continue;
                if (models.Any(model => model.Id == id)) continue;
                models.Add(new ClaudeModelOption(id, name ?? id, Bool(item, "inactive") == true, Bool(item, "overflow") == true));
            }
            return models;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        String(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
