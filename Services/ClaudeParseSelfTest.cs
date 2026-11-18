using UsageTools2026.Models;

namespace UsageTools2026.Services;

/// <summary>
/// Offline parse fixtures for <see cref="ClaudeOfficialSyncService.ParseUsage"/>, run via
/// "UsageTools2026.exe --parse-selftest". Covers the three required shapes (root-level fable
/// object, additional-limits array entry, nested plan object) plus a negative case proving that
/// Fable/plan are never invented from 5h/7d data alone.
/// </summary>
internal static class ClaudeParseSelfTest
{
    public static string Run()
    {
        var results = new List<string>();
        var clock = DateTimeOffset.Now;
        var old = new ClaudeOfficialUsageCache(0, null, 84, clock.AddDays(2), clock.AddHours(-11),
            clock, false, "cookie_database_empty", AccountKey: "fixture");
        var filtered = old.ForAccount("fixture", clock);
        Check(results, "11-hour cache expires 5H without resets_at but retains weekly cache",
            filtered?.PrimaryUsedPercent is null && filtered?.SecondaryUsedPercent == 84 && !filtered.LastAttemptOk, "age and failure preserved");
        Check(results, "reset boundary expires cached quota",
            (old with { SyncedAt = clock.AddMinutes(-1), PrimaryResetsAt = clock.AddSeconds(-1) }).ForAccount("fixture", clock)?.PrimaryUsedPercent is null, "no invented zero");
        Check(results, "weekly cache expires after seven days",
            (old with { SyncedAt = clock.AddDays(-8) }).ForAccount("fixture", clock)?.SecondaryUsedPercent is null, "age bounded");
        var fixtureRoot = System.IO.Path.Combine(AppContext.BaseDirectory, "cookie-fixture");
        System.IO.Directory.CreateDirectory(fixtureRoot);
        var fixturePath = System.IO.Path.Combine(fixtureRoot, "Cookies");
        try
        {
            System.IO.File.WriteAllBytes(fixturePath, []);
            Check(results, "empty cookie database classified before SQLite",
                ClaudeProfileAccess.ValidateCookieDatabase(fixturePath) == "cookie_database_empty", "read only validation");
            SQLitePCL.Batteries_V2.Init();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = fixturePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE fixture(value INTEGER)";
                command.ExecuteNonQuery();
            }
            Check(results, "SQLite without cookies table classified",
                ClaudeProfileAccess.ValidateCookieDatabase(fixturePath) == "cookie_table_missing", "no exception-only diagnostic");
        }
        finally
        {
            System.IO.File.Delete(fixturePath);
            System.IO.Directory.Delete(fixtureRoot);
        }

        const string rootFableUsage = """
            {
              "five_hour": { "utilization": 46, "resets_at": "2026-07-15T10:00:00Z" },
              "seven_day": { "utilization": 13, "resets_at": "2026-07-20T10:00:00Z" },
              "fable": { "utilization": 21, "resets_at": "2026-07-16T10:00:00Z" }
            }
            """;
        var rootFable = ClaudeOfficialSyncService.ParseUsage(rootFableUsage, null);
        Check(results, "root-level fable object",
            rootFable.Ok &&
            rootFable.Five.Percent == 46 &&
            rootFable.Seven.Percent == 13 &&
            rootFable.Fable.Percent == 21 &&
            rootFable.Fable.ResetsAt is not null,
            $"5h={rootFable.Five.Percent} 7d={rootFable.Seven.Percent} fable={rootFable.Fable.Percent}");

        const string additionalLimitsUsage = """
            {
              "five_hour": { "utilization": 46, "resets_at": "2026-07-15T10:00:00Z" },
              "seven_day": { "utilization": 13, "resets_at": "2026-07-20T10:00:00Z" },
              "additional_limits": [
                { "name": "Sonnet", "utilization": 9 },
                { "name": "Fable", "utilization": 41, "resets_at": "2026-07-17T10:00:00Z" }
              ]
            }
            """;
        var additionalLimits = ClaudeOfficialSyncService.ParseUsage(additionalLimitsUsage, null);
        Check(results, "additional-limits array entry named Fable",
            additionalLimits.Ok && additionalLimits.Fable.Percent == 41,
            $"fable={additionalLimits.Fable.Percent}");

        const string plainUsage = """
            {
              "five_hour": { "utilization": 46, "resets_at": "2026-07-15T10:00:00Z" },
              "seven_day": { "utilization": 13, "resets_at": "2026-07-20T10:00:00Z" }
            }
            """;
        const string nestedPlanOrganization = """
            {
              "rate_limit_tier": { "slug": "default_claude_max_5x" }
            }
            """;
        var nestedPlan = ClaudeOfficialSyncService.ParseUsage(plainUsage, nestedPlanOrganization);
        var normalized = SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Claude, nestedPlan.PlanType);
        Check(results, "nested plan object (rate_limit_tier.slug)",
            nestedPlan.Ok &&
            nestedPlan.PlanType == "default_claude_max_5x" &&
            normalized == "claude-max-5x",
            $"planType={nestedPlan.PlanType ?? "<null>"} normalized={normalized ?? "<null>"}");

        // 2026-10-02 (Free org) and 2026-11-11 (Pro org) both reported default_claude_ai.
        const string ambiguousOrganization = """
            {
              "rate_limit_tier": "default_claude_ai"
            }
            """;
        var ambiguousPlan = ClaudeOfficialSyncService.ParseUsage(plainUsage, ambiguousOrganization);
        Check(results, "default_claude_ai alone is not treated as Pro",
            ambiguousPlan.Ok &&
            ambiguousPlan.PlanType is null &&
            SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Claude, "default_claude_ai") is null,
            $"planType={ambiguousPlan.PlanType ?? "<null>"}");

        const string proOrganization = """
            { "capabilities": ["chat", "claude_pro"], "rate_limit_tier": "default_claude_ai", "analytics_subscription_plan": "claude_pro" }
            """;
        var proPlan = ClaudeOfficialSyncService.ParseUsage(plainUsage, proOrganization);
        Check(results, "capabilities claude_pro maps to Pro (official source)",
            proPlan.PlanType == "claude_pro" && proPlan.Details.PlanSource == "capabilities" &&
            PersonalMobileService.SafeClaudePlan(proPlan.PlanType) == "Pro",
            $"planType={proPlan.PlanType ?? "<null>"} source={proPlan.Details.PlanSource ?? "<null>"}");

        const string freeOrganization = """
            { "capabilities": ["chat"], "rate_limit_tier": "default_claude_ai", "analytics_subscription_plan": "claude_free" }
            """;
        var freePlan = ClaudeOfficialSyncService.ParseUsage(plainUsage, freeOrganization);
        Check(results, "free organization with the same tier stays Free",
            freePlan.PlanType == "claude_free" && PersonalMobileService.SafeClaudePlan(freePlan.PlanType) == "Free",
            $"planType={freePlan.PlanType ?? "<null>"}");

        var maxTiers = new (string Org, string Expected)[]
        {
            ("""{ "capabilities": ["chat","claude_max"], "rate_limit_tier": "default_claude_max_5x" }""", "Max 5x"),
            ("""{ "capabilities": ["chat","claude_max"], "rate_limit_tier": "default_claude_max_20x" }""", "Ultra"),
            ("""{ "capabilities": ["chat","claude_max"] }""", "Max"),
            ("""{ "capabilities": ["chat","claude_ultra"] }""", "Ultra")
        };
        foreach (var (org, expected) in maxTiers)
        {
            var parsed = ClaudeOfficialSyncService.ParseUsage(plainUsage, org);
            var label = PersonalMobileService.SafeClaudePlan(parsed.PlanType);
            Check(results, $"Max family tier -> {expected} (generic Max is not guessed)", label == expected,
                $"planType={parsed.PlanType ?? "<null>"} label={label ?? "<null>"}");
        }
        Check(results, "plan catalog: generic max, 20x alias and Ultra",
            SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Claude, "max") == "claude-max" &&
            SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Claude, "Max 20x") == "claude-ultra" &&
            SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Claude, "claude_max_5x") == "claude-max-5x" &&
            SubscriptionPlanCatalog.CurrentLabel(SubscriptionProvider.Claude, "max").Contains("級別未提供"),
            SubscriptionPlanCatalog.CurrentLabel(SubscriptionProvider.Claude, "max"));

        RunDetailsCases(results);

        const string officialLimitsUsage = """
            {
              "five_hour": { "utilization": 6, "resets_at": "2026-07-15T12:00:00Z" },
              "seven_day": { "utilization": 14, "resets_at": "2026-07-20T12:00:00Z" },
              "limits": [
                { "kind": "session", "percent": 6, "resets_at": "2026-07-15T12:00:00Z", "scope": null },
                { "kind": "weekly_all", "percent": 14, "resets_at": "2026-07-20T12:00:00Z", "scope": null },
                {
                  "kind": "weekly_scoped",
                  "percent": 12,
                  "resets_at": "2026-07-20T12:00:00Z",
                  "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null }
                }
              ]
            }
            """;
        var officialLimits = ClaudeOfficialSyncService.ParseUsage(officialLimitsUsage, null);
        Check(results, "official limits array (weekly_scoped scope.model.display_name)",
            officialLimits.Ok &&
            officialLimits.Five.Percent == 6 &&
            officialLimits.Seven.Percent == 14 &&
            officialLimits.Fable.Percent == 12 &&
            officialLimits.Fable.ResetsAt is not null,
            $"5h={officialLimits.Five.Percent} 7d={officialLimits.Seven.Percent} fable={officialLimits.Fable.Percent}");

        const string limitReachedUsage = """
            {
              "five_hour": { "utilization": 100, "resets_at": "2026-08-11T12:30:00Z" },
              "seven_day": { "utilization": 100, "resets_at": "2026-08-18T12:30:00Z" },
              "limits": [
                { "kind": "session", "percent": 100, "resets_at": "2026-08-11T12:30:00Z" },
                { "kind": "weekly_all", "percent": 100, "resets_at": "2026-08-18T12:30:00Z" }
              ]
            }
            """;
        var limitReached = ClaudeOfficialSyncService.ParseUsage(limitReachedUsage, null);
        Check(results, "Claude 5h/7d 100% limit reached still parses as valid data",
            limitReached.Ok &&
            limitReached.Five.Percent == 100 &&
            limitReached.Seven.Percent == 100,
            $"5h={limitReached.Five.Percent} 7d={limitReached.Seven.Percent}");

        var noFable = ClaudeOfficialSyncService.ParseUsage(plainUsage, null);
        Check(results, "no plan/Fable fields -> stays null (never guessed)",
            noFable.Ok && noFable.PlanType is null && noFable.Fable.Percent is null,
            $"planType={noFable.PlanType ?? "<null>"} fable={noFable.Fable.Percent?.ToString() ?? "<null>"}");

        var failed = results.Count(line => line.StartsWith("FAIL", StringComparison.Ordinal));
        results.Add(string.Empty);
        results.Add(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
        return string.Join(Environment.NewLine, results);
    }

    // FIXTURE: shape copied from a synthetic claude.ai schema fixture;
    // percentages kept, identifiers replaced, no account data. Not a live verification.
    internal const string RealShapeUsage = """
        {
          "five_hour": { "utilization": 16, "resets_at": "2026-11-11T06:20:00.277781+00:00", "limit_dollars": null, "locked_reason": null },
          "seven_day": { "utilization": 3, "resets_at": "2026-11-16T18:00:00.277798+00:00", "limit_dollars": null, "locked_reason": null },
          "seven_day_opus": null, "seven_day_sonnet": null,
          "cedar_ember": {
            "eligible": true, "ineligible_reason": null, "at_limit": false, "exhausted": [],
            "grants": [
              { "id": "launch-2610-fable-week", "label": "New model launch: one Fable reset for Pro and Team Standard, launch week only",
                "resets_total": 1, "resets_left": 1, "starts_at": "2026-11-09T18:00:00+00:00", "ends_at": "2026-11-17T07:59:00+00:00",
                "clears": ["five_hour", "seven_day_overage_included"], "paused": false, "usable_now": true, "use_requires_limit": false,
                "type": "full", "unavailable_reason": null, "gives_back_weekly": true },
              { "id": "full-reset-2611", "label": "One full limit reset (November 2026)",
                "resets_total": 1, "resets_left": 1, "starts_at": "2026-11-10T21:30:00+00:00", "ends_at": "2026-12-10T22:00:00+00:00",
                "clears": ["five_hour", "seven_day", "seven_day_overage_included"], "paused": false, "usable_now": true,
                "type": "full", "unavailable_reason": null, "gives_back_weekly": false }
            ],
            "next_grant_id": "launch-2610-fable-week", "weekly_resets_at": "2026-11-16T18:00:00+00:00", "cooldown_until": null,
            "event_props": { "surface": "claude_ai", "tier": "claude_pro" }
          },
          "extra_usage": { "is_enabled": false, "monthly_limit": null, "used_credits": null, "currency": null, "spend_limit_reached": false },
          "limits": [
            { "kind": "session", "group": "session", "percent": 16, "severity": "normal", "resets_at": "2026-11-11T06:20:00.277781+00:00", "scope": null, "is_active": true },
            { "kind": "weekly_all", "group": "weekly", "percent": 3, "severity": "normal", "resets_at": "2026-11-16T18:00:00.277798+00:00", "scope": null, "is_active": false },
            { "kind": "weekly_scoped", "group": "weekly", "percent": 0, "severity": "normal", "resets_at": "2026-11-16T18:00:00+00:00",
              "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null }, "is_active": false }
          ],
          "spend": { "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 }, "limit": null, "enabled": false },
          "weekly_scoped_shares": [ { "limit_index": 2, "used_percent_of_weekly": 0, "allowance_percent_of_weekly": 50 } ]
        }
        """;

    private static void RunDetailsCases(List<string> results)
    {
        var now = DateTimeOffset.Parse("2026-11-11T03:00:00Z");
        var real = ClaudeOfficialSyncService.ParseUsage(RealShapeUsage, null, now);
        var details = real.Details;
        Check(results, "real-shape fixture: 5H/7D/Fable pools with 50% allowance",
            real.Ok && real.Five.Percent == 16 && real.Seven.Percent == 3 && real.Fable.Percent == 0 &&
            details.Pools.Count == 3 && details.Pools[0].Label == "5H" && details.Pools[1].Label == "7D" &&
            details.Pools[2] is { Label: "7D", ModelName: "Fable", AllowancePercentOfWeekly: 50 },
            string.Join(",", details.Pools.Select(p => $"{p.Label}/{p.ModelName}/{p.Percent}/{p.AllowancePercentOfWeekly}")));
        var grants = details.Resets?.Grants ?? [];
        Check(results, "real-shape fixture: Fable grant classified by clears, not by type",
            grants.Count == 2 && grants[0].Category == "fable" && grants[1].Category == "full" &&
            details.Resets!.AvailableCount(now) == 2 && details.Resets.Eligible == true,
            string.Join(",", grants.Select(g => $"{g.Id}:{g.Category}:{g.Left}:{g.EndsAt:O}")));
        Check(results, "real-shape fixture: plan from cedar_ember tier when org JSON missing",
            real.PlanType == "claude_pro" && real.Details.PlanSource == "cedar_ember.tier",
            $"planType={real.PlanType ?? "<null>"}");
        Check(results, "real-shape fixture: usage credits disabled, USD unit, separate from resets",
            details.ExtraUsage is { Enabled: false, Used: 0m, Currency: "USD", MonthlyLimit: null },
            $"{details.ExtraUsage}");

        var afterFableExpiry = DateTimeOffset.Parse("2026-11-17T08:00:00Z");
        Check(results, "expired grant leaves the available count (no own deadline math)",
            details.Resets!.AvailableCount(afterFableExpiry) == 1 &&
            details.Resets.Active(afterFableExpiry).Single().Category == "full",
            $"available={details.Resets.AvailableCount(afterFableExpiry)}");

        const string sameDayGrants = """
            { "five_hour": { "utilization": 1 }, "seven_day": { "utilization": 1 },
              "cedar_ember": { "eligible": true, "grants": [
                { "id": "a-1", "resets_left": 1, "resets_total": 1, "ends_at": "2027-03-09T07:59:00Z", "clears": ["five_hour","seven_day"] },
                { "id": "a-2", "resets_left": 2, "resets_total": 2, "ends_at": "2027-03-09T07:59:00Z", "clears": ["five_hour","seven_day"] },
                { "id": "a-3", "ends_at": null, "clears": ["five_hour"] } ] } }
            """;
        var same = ClaudeOfficialSyncService.ParseUsage(sameDayGrants, null, now);
        var claude = ClaudeUsage.Missing with { Details = same.Details };
        Check(results, "same-expiry grants are not merged; unknown left keeps count unknown",
            MiniUsageAnnouncementService.ClaudeGrantExpirations(claude, now).Length == 3 &&
            same.Details.Resets!.AvailableCount(now) is null &&
            same.Details.Resets.Grants[2] is { Category: "five_hour", EndsAt: null, Left: null },
            $"expirations={MiniUsageAnnouncementService.ClaudeGrantExpirations(claude, now).Length} available={same.Details.Resets?.AvailableCount(now)?.ToString() ?? "null"}");

        const string weeklyOnly = """
            { "seven_day": { "utilization": 40, "resets_at": "2026-11-16T18:00:00Z" }, "five_hour": null,
              "limits": [
                { "kind": "weekly_all", "group": "weekly", "percent": 40, "resets_at": "2026-11-16T18:00:00Z" },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 101, "scope": { "model": { "display_name": "Opus" } } },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 7, "scope": { "model": { "display_name": "Opus" } } },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 9, "scope": { "model": { "display_name": "<script>x" } } },
                { "kind": "mystery_bucket", "percent": 5 } ] }
            """;
        var weekly = ClaudeOfficialSyncService.ParseUsage(weeklyOnly, null, now);
        Check(results, "7D-only account: no phantom 5H; invalid percent -> unknown; unsafe names masked",
            weekly.Ok && weekly.Five.Percent is null && weekly.Details.Pools.All(p => p.Label == "7D") &&
            weekly.Details.Pools.Count == 4 && weekly.Details.Pools[1].Percent is null && weekly.Details.Pools[2].Percent == 7 &&
            weekly.Details.Pools[3].ModelName == "Model" && weekly.Details.Resets is null,
            string.Join(",", weekly.Details.Pools.Select(p => $"{p.Label}/{p.ModelName}/{p.Percent?.ToString() ?? "null"}")));

        const string modelsJson = """
            [ { "model": "claude-fable-5-2", "name": "Claude Fable 5.2", "inactive": false, "overflow": false },
              { "model": "claude-opus-5-5", "name": "Claude Opus 5.5" },
              { "model": "claude-opus-4-5-20251101", "name": "Claude Opus 4.5", "inactive": true },
              { "model": "claude-sonnet-4-6", "name": "Claude Sonnet 4.6", "overflow": true },
              { "model": "claude-opus-5-5", "name": "duplicate" },
              { "model": "not-a-claude-model", "name": "x" },
              { "model": "claude-x", "name": "C:\\Users\\private\\path" } ]
            """;
        foreach (var fragment in new[] { "{}", "{\"grants\":null}", "{\"grants\":[null]}", "{\"grants\":{}}" })
        {
            using var incomplete = System.Text.Json.JsonDocument.Parse("{\"cedar_ember\":" + fragment + "}");
            Check(results, "incomplete grant inventory stays unknown: " + fragment,
                ClaudeOfficialDetailsParser.ParseDetails(incomplete.RootElement, now).Resets is null, "not an empty inventory");
        }
        using (var empty = System.Text.Json.JsonDocument.Parse("""{"cedar_ember":{"grants":[]}}"""))
            Check(results, "explicit empty grant inventory is zero", ClaudeOfficialDetailsParser.ParseDetails(empty.RootElement, now).Resets?.AvailableCount(now) == 0, "explicit array");
        using (var noId = System.Text.Json.JsonDocument.Parse("""{"cedar_ember":{"grants":[{"resets_left":1}]}}"""))
            Check(results, "missing grant ID is not an ordinal identity", ClaudeOfficialDetailsParser.ParseDetails(noId.RootElement, now).Resets?.Grants[0].Id == "", "display only, no gift detection");
        foreach (var money in new[] { "{\"amount_minor\":\"100\",\"exponent\":2}", "{\"amount_minor\":100}" })
        {
            using var malformedMoney = System.Text.Json.JsonDocument.Parse("{\"spend\":{\"enabled\":true,\"used\":" + money + "}}");
            Check(results, "invalid money stays unknown: " + money,
                ClaudeOfficialDetailsParser.ParseDetails(malformedMoney.RootElement, now).ExtraUsage is { Enabled: true, Used: null }, "no exception or guessed exponent");
        }
        // Obfuscation guard: the cache must keep readable, stable JSON names in the protected build.
        var cache = new ClaudeOfficialUsageCache(16, now, 3, now, now, Details: details with
        {
            Models = [new ClaudeModelOption("claude-opus-5-5", "Claude Opus 5.5", false, false)], ModelsSyncedAt = now
        });
        var cacheJson = System.Text.Json.JsonSerializer.Serialize(cache);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ClaudeOfficialUsageCache>(cacheJson);
        Check(results, "cache JSON keeps contract names and round-trips (protected build guard)",
            new[] { "\"Details\"", "\"Pools\"", "\"ModelName\"", "\"AllowancePercentOfWeekly\"", "\"Resets\"", "\"Grants\"", "\"EndsAt\"",
                    "\"Category\"", "\"Models\"", "\"ExtraUsage\"", "\"PlanSource\"" }.All(cacheJson.Contains) &&
            roundTrip?.Details?.Resets?.Grants.Count == 2 && roundTrip.Details.Pools[2].ModelName == "Fable" &&
            roundTrip.Details.Models?[0].Id == "claude-opus-5-5",
            $"bytes={cacheJson.Length}");

        var models = ClaudeOfficialDetailsParser.ParseModels(modelsJson) ?? [];
        Check(results, "account models: inactive/overflow kept, duplicates and non-claude ids dropped, unsafe names replaced",
            models.Count == 5 && models[2].Inactive && models[3].Overflow && models[4].Name == "claude-x" &&
            ClaudeOfficialDetailsParser.ParseModels("not json") is null,
            string.Join(",", models.Select(m => m.Id)));
    }

    private static void Check(List<string> results, string caseName, bool passed, string detail)
    {
        results.Add($"{(passed ? "PASS" : "FAIL")} {caseName} ({detail})");
    }
}
