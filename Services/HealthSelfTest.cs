using System.IO;
using System.Text.Json.Nodes;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

internal static class HealthSelfTest
{
    internal static string Run()
    {
        var lines = new List<string>();
        void Check(string name, Func<bool> test)
        {
            try { lines.Add((test() ? "PASS " : "FAIL ") + name); }
            catch (Exception ex) { lines.Add("FAIL " + name + " " + ex.GetType().Name); }
        }
        var now = DateTimeOffset.UtcNow;
        Check("credit_string_numeric_zero_and_invalid", () =>
        {
            CodexCreditBalance? Parse(string json) { using var doc = System.Text.Json.JsonDocument.Parse(json); return CodexOfficialUsageReader.ParseCredits(doc.RootElement); }
            return Parse("{\"credits\":{\"balance\":\"61740.53\"}}")?.Balance == 61740.53m &&
                Parse("{\"credits\":{\"balance\":0}}")?.Balance == 0 &&
                Parse("{\"credits\":{\"balance\":-1}}") is null &&
                Parse("{\"credits\":{\"balance\":\"NaN\"}}") is null &&
                Parse("{\"credits\":{\"balance\":1000000000001}}") is null && Parse("{}") is null &&
                Parse("{\"credits\":{\"unlimited\":true}}")?.Unlimited == true;
        });
        Check("stabilize_100_preserves_account_and_balance", () =>
        {
            var before = new CodexOfficialUsageReader.CodexOfficialUsage(100, 10080, now.AddDays(1), null, null, null, "pro", now, now, true, "fixture-account", new(123m));
            var after = before with { Credits = new(122m) };
            for (var i = 0; i < 1000; i++) after = CodexOfficialUsageReader.Stabilize(before, after, out _);
            return after.AccountKey == before.AccountKey && after.Credits?.Balance == 122m && after.PrimaryPercent == 100;
        });
        Check("balance_initial_changed_coalesced_stale_and_account_switch", () =>
        {
            var tracker = new CreditBalanceAnnouncementTracker();
            var usage = CodexUsage.Empty with { HasData = true, IsStale = false, OfficialSyncedAt = now, AccountKey = "a", Credits = new(100.9m), PrimaryUsedPercent = 100 };
            if (tracker.Observe(usage, now)?.Balance?.Balance != 100.9m) return false;
            if (tracker.Observe(usage with { Credits = new(100.1m) }, now.AddSeconds(65)) is not null) return false;
            if (tracker.Observe(usage with { Credits = new(99m) }, now.AddSeconds(10)) is not null) return false;
            if (tracker.Observe(usage with { Credits = new(99m) }, now.AddSeconds(65))?.Balance?.Balance != 99m) return false;
            if (tracker.Observe(usage with { IsStale = true, Credits = new(0m) }, now.AddSeconds(130)) is not null) return false;
            return tracker.Observe(usage with { AccountKey = "b", Credits = new(0m) }, now.AddSeconds(130))?.Balance?.Balance == 0m;
        });
        Check("full_quota_does_not_repeat_1000_times", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", "full-quota.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Delete(path);
            try
            {
                var service = new MiniUsageAnnouncementService(path);
                service.Observe(MiniSnapshot(now, 99, 10, now.AddHours(1)));
                service.Observe(MiniSnapshot(now, 100, 10, now.AddHours(1)));
                var count = service.Pending().Count;
                for (var i = 0; i < 1000; i++)
                    if (service.Observe(MiniSnapshot(now, 100, 10, now.AddHours(1))).Count != 0) return false;
                return service.Pending().Count == count;
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        Check("fresh_green", () => UsageFreshness.Evaluate(true, true, now, false, now) == UsageDataState.Fresh);
        Check("failure_with_recent_data_amber", () => UsageFreshness.Evaluate(true, true, now, true, now) == UsageDataState.Cached);
        Check("old_success_not_green", () => UsageFreshness.Evaluate(true, true, now.AddMinutes(-4), false, now) == UsageDataState.Stale);
        Check("unknown_sync_time_not_green", () => UsageFreshness.Evaluate(true, true, null, false, now) == UsageDataState.Stale);
        Check("missing_not_green", () => UsageFreshness.Evaluate(true, false, now, false, now) == UsageDataState.Unavailable);
        Check("disabled_wins", () => UsageFreshness.Evaluate(false, true, now, false, now) == UsageDataState.Disabled);
        Check("reset_boundary_invalidates_window", () => UsageFreshness.Window(100, now, now) is null);
        Check("real_zero_preserved", () => UsageFreshness.Window(0, null, now) == 0);
        Check("unknown_not_zero", () => UsageFreshness.Window(null, null, now) is null);
        Check("invalid_percent_rejected", () => UsageFreshness.Window(double.NaN, null, now) is null && UsageFreshness.Window(101, null, now) is null);
        Check("clock_future_not_trusted", () => !UsageFreshness.UsableTime(now.AddHours(1), now));
        Check("cache_age_bounded", () => !UsageFreshness.UsableTime(now.AddHours(-25), now));
        var accountA = UsageFreshness.AccountKey("fixture", "account-a");
        var accountB = UsageFreshness.AccountKey("fixture", "account-b");
        var cache = new ClaudeOfficialUsageCache(null, null, 60, now.AddDays(1), now, now, true, AccountKey: accountA);
        Check("identity_hash_not_plaintext", () => accountA?.Length == 64 && !accountA.Contains("account-a"));
        Check("same_account_accepts_partial", () => cache.ForAccount(accountA, now) is { PrimaryUsedPercent: null, SecondaryUsedPercent: 60 });
        Check("different_account_rejected", () => cache.ForAccount(accountB, now) is null);
        Check("unknown_account_rejected", () => cache.ForAccount(null, now) is null);
        Check("legacy_unbound_cache_rejected", () => (cache with { AccountKey = null }).ForAccount(accountA, now) is null);
        Check("expired_primary_preserves_secondary", () => (cache with { PrimaryUsedPercent = 100, PrimaryResetsAt = now }).ForAccount(accountA, now) is { PrimaryUsedPercent: null, SecondaryUsedPercent: 60 });
        Check("missing_five_hour_parser", () => ClaudeOfficialSyncService.ParseUsage("{\"seven_day\":{\"utilization\":60}}", null) is { Five.Percent: null, Seven.Percent: 60 });
        Check("selftest_fails_closed", () => !SelfTestRunner.IsPassing("FAIL injected\nALL PASS") && !SelfTestRunner.IsPassing("") && SelfTestRunner.IsPassing("PASS sample\nALL PASS") && SelfTestRunner.IsPassing("PASS failure_case\nSUMMARY passed=True checks=1 failed=0"));
        Check("mini_usage_milestone_and_alarm", TestMiniUsageMilestoneAndAlarm);
        Check("mini_usage_reset_and_credit", TestMiniUsageResetAndCredit);
        Check("mini_usage_state_deduplicates_restart", TestMiniUsageStateDeduplication);
        Check("mini_pending_survives_until_acknowledged", TestMiniPendingDelivery);
        Check("notification_outbox_no_local_archive_and_24h_retention", TestNotificationHistory);
        Check("small_usage_reset_with_period_advance", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"), "reset.json");
            try
            {
                var service = new MiniUsageAnnouncementService(path);
                service.Observe(MiniSnapshot(now, 11, 10, now.AddHours(1)));
                var reset = service.Observe(MiniSnapshot(now, 0, 10, now.AddHours(6)));
                return reset.Count == 1 && reset[0].Kind == MiniUsageAnnouncementKind.UsageReset;
            }
            finally { if (File.Exists(path)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
        });
        Check("percent_drop_alone_not_official_reset", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"), "reset.json");
            try
            {
                var service = new MiniUsageAnnouncementService(path);
                service.Observe(MiniSnapshot(now, 80, 10, now.AddHours(1)));
                return !service.Observe(MiniSnapshot(now, 0, 10, now.AddHours(1))).Any(e => e.Kind == MiniUsageAnnouncementKind.UsageReset);
            }
            finally { if (File.Exists(path)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
        });
        Check("credit_startup_refresh_not_delayed_by_disk_cache", () => !CodexResetCreditReader.ShouldUseCache(false, 0));
        Check("credit_poll_interval_is_one_minute", () => CodexResetCreditReader.ShouldUseCache(true, 59_999) && !CodexResetCreditReader.ShouldUseCache(true, 60_000));
        Check("credit_parser_preserves_three_available_credits", () =>
        {
            var value = new JsonObject { ["available_count"] = 3, ["credits"] = new JsonArray() };
            for (var i = 0; i < 3; i++) value["credits"]!.AsArray().Add(new JsonObject {
                ["id"] = "fixture-" + i, ["status"] = "available", ["reset_type"] = "codex_rate_limits", ["expires_at"] = now.AddDays(i + 1).ToString("O") });
            using var doc = System.Text.Json.JsonDocument.Parse(value.ToJsonString());
            var summary = CodexResetCreditReader.ParseResponse(doc.RootElement);
            return summary.AvailableCount == 3 && summary.Credits.Count == 3 && !summary.IsStale;
        });
        Check("settings_merge_recovery", TestSettings);
        Check("settings_parallel_stores", TestParallelSettings);
        var failed = lines.Count(line => line.StartsWith("FAIL", StringComparison.Ordinal));
        lines.Add($"SUMMARY passed={failed == 0} checks={lines.Count} failed={failed}");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool TestSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        AtomicFile.WriteText(path, "{\"ThemeId\":\"legacy\",\"FutureField\":42}");
        var store = new SettingsStore(path);
        var ui = store.Load();
        var background = store.Load();
        var checkedAt = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        background.LastUpdateCheckUtc = checkedAt;
        if (!store.Save(background)) return false;
        ui.ThemeId = "classic";
        if (!store.Save(ui)) return false;
        var merged = JsonNode.Parse(File.ReadAllText(path))!;
        if (merged["FutureField"]?.GetValue<int>() != 42 || store.Load().LastUpdateCheckUtc != checkedAt) return false;
        ui.ClaudeDataEnabled = false;
        ui.KeepWindowOnTop = false;
        if (!store.Save(ui)) return false;
        if (store.Load().KeepWindowOnTop) return false;
        // Corrupt the isolated main file and verify the last-good copy is used and retained.
        File.WriteAllText(path, "{");
        var recovered = store.Load();
        if (recovered.ThemeId != "classic" || recovered.LastUpdateCheckUtc != checkedAt) return false;
        recovered.Language = AppLanguage.En;
        if (!store.Save(recovered) || store.Load().Language != AppLanguage.En) return false;
        return JsonNode.Parse(File.ReadAllText(path + ".bak"))?["ThemeId"]?.GetValue<string>() == "classic"
            && Directory.GetFiles(root, "*.tmp").Length == 0;
    }

    private static bool TestParallelSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var a = new SettingsStore(path);
        var b = new SettingsStore(path);
        var first = a.Load();
        var second = b.Load();
        first.ThemeId = "parallel";
        second.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        var one = Task.Run(() => a.Save(first));
        var two = Task.Run(() => b.Save(second));
        Task.WaitAll(one, two);
        var result = a.Load();
        return one.Result && two.Result && result.ThemeId == "parallel" && result.LastUpdateCheckUtc == second.LastUpdateCheckUtc;
    }

    private static bool TestMiniUsageMilestoneAndAlarm()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new MiniUsageAnnouncementService(Path.Combine(root, "mini-announcements.json"));
            var now = DateTimeOffset.Now;
            if (service.Observe(MiniSnapshot(now, codexFiveHour: 10, codexSevenDay: 79)).Count != 0)
            {
                return false;
            }

            var milestone = service.Observe(MiniSnapshot(now, codexFiveHour: 15, codexSevenDay: 79));
            if (milestone.Count != 1 ||
                milestone[0] is not { Kind: MiniUsageAnnouncementKind.UsageMilestone, RepeatCount: 3, RemainingPercent: 85, IsAlarm: false })
            {
                return false;
            }

            var alarm = service.Observe(MiniSnapshot(now, codexFiveHour: 15, codexSevenDay: 81));
            if (alarm.Count != 1 ||
                alarm[0] is not { Kind: MiniUsageAnnouncementKind.LowRemaining, RepeatCount: 3, IsAlarm: true, AlertThreshold: 20 })
            {
                return false;
            }

            foreach (var (used, threshold) in new[] { (86d, 15), (91d, 10), (96d, 5) })
            {
                var nextAlarm = service.Observe(MiniSnapshot(now, codexFiveHour: 15, codexSevenDay: used));
                if (nextAlarm.Count != 1 ||
                    nextAlarm[0] is not { Kind: MiniUsageAnnouncementKind.LowRemaining, RepeatCount: 3, IsAlarm: true } ||
                    nextAlarm[0].AlertThreshold != threshold)
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static bool TestMiniUsageResetAndCredit()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var statePath = Path.Combine(root, "mini-announcements.json");
            var service = new MiniUsageAnnouncementService(statePath);
            var now = DateTimeOffset.Now;
            var oldReset = now.AddMinutes(-1);
            var nextReset = now.AddHours(5);
            var firstCredit = new CodexResetCredit("credit-a", "Full reset", now.AddDays(2));
            service.Observe(MiniSnapshot(now, 86, 30, oldReset, now.AddDays(2), [firstCredit]));

            var reset = service.Observe(MiniSnapshot(now, 2, 30, nextReset, now.AddDays(2), [firstCredit]));
            if (reset.Count != 1 || reset[0] is not { Kind: MiniUsageAnnouncementKind.UsageReset, RepeatCount: 5 })
            {
                return false;
            }

            var secondCredit = new CodexResetCredit("credit-b", "Full reset", now.AddDays(3));
            var credit = service.Observe(MiniSnapshot(now, 2, 30, nextReset, now.AddDays(2), [firstCredit, secondCredit]));
            return credit.Count == 1 &&
                   credit[0] is { Kind: MiniUsageAnnouncementKind.ResetCreditsAdded, RepeatCount: 5, CreditCount: 2 };
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static bool TestMiniUsageStateDeduplication()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var statePath = Path.Combine(root, "mini-announcements.json");
            var now = DateTimeOffset.Now;
            var baseline = MiniSnapshot(now, codexFiveHour: 79, codexSevenDay: 30);
            var alarmSnapshot = MiniSnapshot(now, codexFiveHour: 81, codexSevenDay: 30);
            var service = new MiniUsageAnnouncementService(statePath);
            service.Observe(baseline);
            if (service.Observe(alarmSnapshot).Count != 1 || service.Observe(alarmSnapshot).Count != 0)
            {
                return false;
            }

            return new MiniUsageAnnouncementService(statePath).Observe(alarmSnapshot).Count == 0;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static bool TestMiniPendingDelivery()
    {
        var path = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"), "pending.json");
        var now = DateTimeOffset.Now;
        var service = new MiniUsageAnnouncementService(path);
        service.Observe(MiniSnapshot(now, 79, 30));
        service.Observe(MiniSnapshot(now, 80, 30));
        var restarted = new MiniUsageAnnouncementService(path);
        var pending = restarted.Pending();
        if (pending.Count != 1 || !pending[0].IsAlarm || string.IsNullOrEmpty(pending[0].DeliveryId)) return false;
        restarted.Observe(MiniSnapshot(now, 80, 30));
        if (restarted.Pending().Count != 1) return false;
        restarted.Acknowledge(pending[0]);
        var acknowledged = new MiniUsageAnnouncementService(path);
        return acknowledged.Pending().Count == 0 && acknowledged.Observe(MiniSnapshot(now, 80, 30)).Count == 0;
    }

    private static bool TestNotificationHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "history.json");
            var service = new MiniUsageAnnouncementService(path, outbox: true);
            var now = DateTimeOffset.UtcNow;
            var a = new CodexResetCredit("a", "fixture", now.AddDays(1));
            var b = new CodexResetCredit("b", "fixture", now.AddDays(2));
            var c = new CodexResetCredit("c", "fixture", now.AddDays(3));
            var baseline = MiniSnapshot(now, 10, 10, credits: [a, b]);
            service.Observe(baseline);
            var next = baseline with { Codex = CodexUsage.Empty, CodexResetCredits = new([a, b, c], now, false) };
            if (service.Observe(next with { CodexResetCredits = next.CodexResetCredits with { IsStale = true } }).Count != 0) return false;
            var events = service.Observe(next);
            if (events.Count != 1 || events[0].AddedCreditCount != 1 || events[0].CreditCount != 3 || events[0].OccurredAt is null) return false;
            if (events[0].CreditExpirations?.Length != 3 || events[0].CreditExpirations![2] != c.ExpiresAt) return false;
            if (!PersonalMobileService.CanDeliver(events[0], next, new AppSettings())) return false;
            if (PersonalMobileService.CanDeliver(events[0], next, new AppSettings { CodexDataEnabled = false })) return false;
            if (PersonalMobileService.CanDeliver(events[0], next with { CodexResetCredits = next.CodexResetCredits with { IsStale = true } }, new AppSettings())) return false;
            var restarted = new MiniUsageAnnouncementService(path, outbox: true);
            if (restarted.Pending().Count != 1 || restarted.Observe(next).Count != 0) return false;
            restarted.MarkPushCompleted(events[0]);
            restarted = new MiniUsageAnnouncementService(path, outbox: true);
            if (!restarted.Pending()[0].PushCompleted) return false;
            var message = PersonalMobileService.BuildNotification(events[0], next, now)["body"]!.GetValue<string>();
            if (!message.Contains("新增 1 張") || !message.Contains("可用 3 張")) return false;
            var payload = JsonNode.Parse(PersonalMobileService.BuildPayload(next, new AppSettings(), now.AddSeconds(1), restarted.Pending()))!;
            if (payload["notifications"]!.AsArray().Count != 1 || payload["resetCredits"]!["available"]!.GetValue<int>() != 3) return false;
            if (payload["resetCredits"]!["expirations"]!.AsArray().Count != 3 || payload["notifications"]![0]!["creditExpirations"]!.AsArray().Count != 3) return false;
            var disabled = JsonNode.Parse(PersonalMobileService.BuildPayload(next, new AppSettings { CodexDataEnabled = false }, now.AddSeconds(1), restarted.Pending()))!;
            if (disabled["notifications"]!.AsArray().Count != 0 || disabled["resetCredits"]!["available"] is not null) return false;
            restarted.Acknowledge(events[0]);
            if (File.ReadAllText(path).Contains("\"History\"") || new MiniUsageAnnouncementService(path, outbox: true).Pending().Count != 0) return false;
            for (var i = 0; i < 105; i++)
                restarted.Observe(next with { CodexResetCredits = new([new CodexResetCredit("fixture-" + i, "fixture", now.AddDays(1))], now, false) });
            if (restarted.Pending().Count != 100) return false;
            var legacy = JsonNode.Parse(File.ReadAllText(path))!;
            legacy["History"] = new JsonArray(new JsonObject { ["secret"] = "old-notice" });
            foreach (var item in legacy["Pending"]!.AsArray()) item!["OccurredAt"] = now.AddHours(-25).ToString("O");
            File.WriteAllText(path, legacy.ToJsonString());
            return new MiniUsageAnnouncementService(path, outbox: true).Pending().Count == 0 && !File.ReadAllText(path).Contains("old-notice");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static UsageSnapshot MiniSnapshot(
        DateTimeOffset now,
        double codexFiveHour,
        double codexSevenDay,
        DateTimeOffset? fiveHourReset = null,
        DateTimeOffset? sevenDayReset = null,
        IReadOnlyList<CodexResetCredit>? credits = null)
    {
        return new UsageSnapshot(
            now,
            new CodexUsage(
                codexFiveHour,
                300,
                fiveHourReset ?? now.AddHours(3),
                codexSevenDay,
                10080,
                sevenDayReset ?? now.AddDays(3),
                false,
                true,
                "plus",
                now),
            ClaudeUsage.Missing,
            TokenTotals.Empty,
            Array.Empty<ProjectUsage>(),
            Array.Empty<CodexWorkWindow>(),
            Array.Empty<CodexWorkWindow>(),
            new CodexResetCreditSummary(credits ?? Array.Empty<CodexResetCredit>(), now, false));
    }
}
