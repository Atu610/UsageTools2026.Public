using System.IO;
using System.Text.Json;

namespace UsageTools2026.Services;

/// <summary>
/// Writes a privacy-safe schema report of the official Claude usage/organization JSON so the real
/// plan/Fable field names can be identified. The report contains ONLY property paths and their
/// JsonValueKind, plus short whitelisted plan/limit-style values (e.g. "plan_type = max_5x",
/// "name = Fable", "utilization = 41"). It NEVER contains the full JSON, cookies, session tokens,
/// authorization data, account IDs, or email addresses.
/// </summary>
public static class ClaudeSchemaDiagnostics
{
    private const int MaxLines = 400;
    private const int MaxValueLength = 48;

    private static readonly string[] SensitiveNameParts =
    [
        "cookie", "token", "authorization", "auth", "session", "secret", "credential",
        "email", "uuid", "account", "user", "member", "phone", "address"
    ];

    private static readonly string[] PlanLikeNames =
    [
        "plan", "plan_type", "subscription_type", "rate_limit_tier", "billing_plan",
        "tier", "slug", "type", "name", "limit_name", "label", "id", "kind", "model"
    ];

    private static readonly string[] NumericValueNames =
    [
        "utilization", "used_percent", "usedpercent", "percent", "remaining", "limit"
    ];

    private static readonly string[] ValueKeywords =
    [
        "fable", "opus", "sonnet", "haiku", "claude", "max", "pro", "free", "team",
        "enterprise", "five_hour", "seven_day", "session", "weekly", "overall", "default"
    ];

    private static string ReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageTools2026.Public",
        "logs",
        "claude-schema.log");

    /// <summary>Best-effort; a diagnostics failure must never break the sync itself.</summary>
    public static void TryWriteReport(string usageJson, string? organizationJson)
    {
        try
        {
            var lines = new List<string>
            {
                $"=== Claude schema diagnostic {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                "(property paths + JsonValueKind only; values shown only for whitelisted plan/limit fields)"
            };

            AppendDocument(lines, "usage", usageJson);
            if (!string.IsNullOrWhiteSpace(organizationJson))
            {
                AppendDocument(lines, "org", organizationJson);
            }
            else
            {
                lines.Add("[org] <no organization JSON returned>");
            }

            var directory = Path.GetDirectoryName(ReportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(ReportPath, lines);
            DiagnosticsService.Write($"Claude schema diagnostic written ({lines.Count} lines): {ReportPath}");
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static void AppendDocument(List<string> lines, string label, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(lines, label, string.Empty, document.RootElement);
        }
        catch (Exception ex)
        {
            lines.Add($"[{label}] <parse failed: {ex.GetType().Name}>");
        }
    }

    private static void Walk(List<string> lines, string label, string path, JsonElement element)
    {
        if (lines.Count >= MaxLines)
        {
            if (lines.Count == MaxLines)
            {
                lines.Add("... (truncated)");
            }

            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (path.Length > 0)
                {
                    lines.Add($"[{label}] {path} : Object");
                }

                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    Walk(lines, label, childPath, property.Value);
                }

                break;

            case JsonValueKind.Array:
                lines.Add($"[{label}] {path} : Array({element.GetArrayLength()})");
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(lines, label, $"{path}[{index}]", item);
                    index++;
                }

                break;

            default:
                var leafName = LeafName(path);
                var valueText = DescribeLeafValue(leafName, element);
                lines.Add(valueText is null
                    ? $"[{label}] {path} : {element.ValueKind}"
                    : $"[{label}] {path} : {element.ValueKind} = {valueText}");
                break;
        }
    }

    private static string LeafName(string path)
    {
        var start = path.LastIndexOf('.') + 1;
        var name = path[start..];
        var bracket = name.IndexOf('[');
        return bracket >= 0 ? name[..bracket] : name;
    }

    // Returns a short displayable value ONLY when the field name and value both look like
    // non-sensitive plan/limit metadata; otherwise null so only the kind is logged.
    private static string? DescribeLeafValue(string name, JsonElement element)
    {
        var lowerName = name.ToLowerInvariant();
        if (SensitiveNameParts.Any(lowerName.Contains))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return NumericValueNames.Any(lowerName.Contains) && element.TryGetDouble(out var number)
                ? number.ToString("0.##")
                : null;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean().ToString();
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxValueLength ||
            value.Contains('@') ||
            HasLongDigitRun(value) ||
            !PlanLikeNames.Any(candidate => lowerName == candidate || lowerName.EndsWith("_" + candidate, StringComparison.Ordinal)))
        {
            return null;
        }

        var lowerValue = value.ToLowerInvariant();
        return ValueKeywords.Any(lowerValue.Contains) ? value : null;
    }

    private static bool HasLongDigitRun(string value)
    {
        var run = 0;
        foreach (var ch in value)
        {
            run = char.IsDigit(ch) ? run + 1 : 0;
            if (run >= 8)
            {
                return true;
            }
        }

        return false;
    }
}
