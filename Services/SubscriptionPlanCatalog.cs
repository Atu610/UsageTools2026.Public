namespace UsageTools2026.Services;

public enum SubscriptionProvider
{
    Codex,
    Claude
}

public sealed record SubscriptionPlanOption(string Id, string NameZh, string NameEn);

public static class SubscriptionPlanCatalog
{
    private static readonly IReadOnlyList<SubscriptionPlanOption> CodexPlans =
    [
        new("codex-free", "Free", "Free"),
        new("codex-go", "Go", "Go"),
        new("codex-plus", "Plus", "Plus"),
        new("codex-pro", "Pro（級別未提供）", "Pro (tier unavailable)"),
        new("codex-pro-usd100", "Pro · US$100/月", "Pro · US$100/mo"),
        new("codex-pro-usd200", "Pro · US$200/月", "Pro · US$200/mo"),
        new("codex-pro-usd500", "Pro · US$500/月", "Pro · US$500/mo"),
        new("codex-business", "Business", "Business"),
        new("codex-enterprise", "Enterprise", "Enterprise"),
        new("codex-edu", "Edu", "Edu")
    ];

    private static readonly IReadOnlyList<SubscriptionPlanOption> ClaudePlans =
    [
        new("claude-free", "Free", "Free"),
        new("claude-pro", "Pro", "Pro"),
        new("claude-max", "Max／Ultra（級別未提供）", "Max/Ultra (tier unavailable)"),
        new("claude-max-5x", "Max 5x", "Max 5x"),
        new("claude-ultra", "Ultra（原 Max 20x）", "Ultra (formerly Max 20x)"),
        new("claude-team", "Team", "Team"),
        new("claude-enterprise", "Enterprise", "Enterprise")
    ];

    public static IReadOnlyList<SubscriptionPlanOption> GetPlans(SubscriptionProvider provider) =>
        provider == SubscriptionProvider.Codex ? CodexPlans : ClaudePlans;

    public static string? Normalize(SubscriptionProvider provider, string? rawPlan) =>
        provider == SubscriptionProvider.Codex ? NormalizeCodex(rawPlan) : NormalizeClaude(rawPlan);

    internal static string? ResolveCodexPlan(string? rawPlan, string? accountKey, AppSettings settings)
    {
        // An explicitly confirmed local tier applies only to the same account and generic Pro.
        if (!string.Equals(rawPlan?.Trim(), "pro", StringComparison.OrdinalIgnoreCase) ||
            !UsageFreshness.SameAccount(accountKey, settings.CodexConfirmedPlanAccountKey)) return rawPlan;
        return settings.CodexConfirmedPlan is "pro-usd100-confirmed" or "pro-usd200-confirmed" or "pro-usd500-confirmed"
            ? settings.CodexConfirmedPlan : rawPlan;
    }

    public static string CurrentLabel(SubscriptionProvider provider, string? rawPlan, bool english = false)
    {
        var id = Normalize(provider, rawPlan);
        var option = GetPlans(provider).FirstOrDefault(p => p.Id == id);
        var name = option is null ? (english ? "Unknown plan" : "方案尚未提供") : english ? option.NameEn : option.NameZh;
        if (rawPlan?.EndsWith("-confirmed", StringComparison.Ordinal) == true)
            name += english ? " (user confirmed)" : "（使用者確認）";
        return name;
    }

    private static string? NormalizeCodex(string? rawPlan)
    {
        var value = NormalizeRaw(rawPlan);
        if (value is null) return null;
        if (ContainsAny(value, "education", "edu")) return "codex-edu";
        if (value.Contains("enterprise", StringComparison.Ordinal)) return "codex-enterprise";
        if (ContainsAny(value, "business", "team")) return "codex-business";
        foreach (var price in new[] { "100", "200", "500" })
            if (value == "pro-usd" + price || value == "pro-usd" + price + "-confirmed" || value == "pro us$" + price + "/mo") return "codex-pro-usd" + price;
        // Match whole tier labels: 25x must not match the 5x suffix.
        var compact = value.Replace("-", "").Replace("_", "").Replace(" ", "").Replace("×", "x");
        var tier = compact switch
        {
            "25x" or "pro25" or "pro25x" or "codexpro25x" or "chatgptpro25x" => "25x",
            "20x" or "pro20" or "pro20x" or "codexpro20x" or "chatgptpro20x" => "20x",
            "10x" or "pro10" or "pro10x" or "codexpro10x" or "chatgptpro10x" => "10x",
            "5x" or "pro5" or "pro5x" or "codexpro5x" or "chatgptpro5x" or "prolite" => "5x",
            _ => null
        };
        if (tier is not null) return "codex-pro-" + tier;
        if (value.Contains("pro", StringComparison.Ordinal)) return "codex-pro";
        if (value.Contains("plus", StringComparison.Ordinal)) return "codex-plus";
        if (value.Contains("go", StringComparison.Ordinal)) return "codex-go";
        if (value.Contains("free", StringComparison.Ordinal)) return "codex-free";
        return null;
    }

    private static string? NormalizeClaude(string? rawPlan)
    {
        var value = NormalizeRaw(rawPlan);
        if (value is null) return null;
        if (value.Contains("enterprise", StringComparison.Ordinal)) return "claude-enterprise";
        if (ContainsAny(value, "team", "business")) return "claude-team";
        // Official 2026-11 rename: "The Max 20x plan is now called the Ultra plan".
        if (ContainsAny(value, "ultra", "20x", "max20", "max_20", "max-20")) return "claude-ultra";
        if (ContainsAny(value, "5x", "max5", "max_5", "max-5")) return "claude-max-5x";
        // A generic Max without a tier must not be guessed as 5x or Ultra.
        if (value.Contains("max", StringComparison.Ordinal)) return "claude-max";
        // default_claude_ai is shared by Free and Pro organizations, so it is not a plan.
        if (value.Equals("default_claude_ai", StringComparison.Ordinal)) return null;
        if (value.Contains("pro", StringComparison.Ordinal)) return "claude-pro";
        if (value.Contains("free", StringComparison.Ordinal)) return "claude-free";
        return null;
    }

    private static string? NormalizeRaw(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
}
