using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageTools2026.Models;
using WebPush;
using WebPush.Model;

namespace UsageTools2026.Services;

public sealed record PersonalMobileCredentials(string ReadToken, string WriteToken,
    string PublicKey, string PrivateKey, string? AccountId = null);
public sealed record MobileQuotaWindow(string Label, double? Used, string? ResetsAt);
public sealed record MobileQuotaProvider(string Name, bool Enabled, string? Plan, string State,
    string? SyncedAt, MobileQuotaWindow[] Windows);
public sealed record MobileQuotaPayload(int SchemaVersion, string SentAt, MobileQuotaProvider[] Providers,
    string ThemeId);

public sealed class PersonalMobileService : IDisposable
{
    internal const string BaseUrl = "https://usage.example.invalid/personal/";
    internal const string TeamBaseUrl = "https://usage.example.invalid/mobile/";
    public static bool IsConfigured => Uri.TryCreate(TeamBaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && !uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim PairingGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "UsageTools2026.Public", "personal-mobile");
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ResetNewsDeliveryService _resetNews = new();
    private readonly MiniUsageAnnouncementService _events = new(Path.Combine(Root, "events.json"), outbox: true);
    private DateTimeOffset _nextAttempt;
    private PersonalMobileCredentials? _credentials;
    public string Status { get; private set; } = "尚未連線";

    internal static PersonalMobileCredentials? ReadCredentials()
    {
        var path = Path.Combine(Root, "credentials.dat");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PersonalMobileCredentials>(ProtectedData.Unprotect(
            Convert.FromBase64String(File.ReadAllText(path)), null, DataProtectionScope.CurrentUser), Json);
    }

    private static string TokenHash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static PersonalMobileCredentials NewMemberCredentials()
    {
        var keys = VapidHelper.GenerateVapidKeys();
        var writer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            writer, keys.PublicKey, keys.PrivateKey, TokenHash(writer));
    }

    internal static string ServiceUrl(PersonalMobileCredentials credentials) =>
        credentials.AccountId is null ? BaseUrl : TeamBaseUrl;

    public static async Task<string> PreparePairingAsync()
    {
        if (!IsConfigured) throw new InvalidOperationException("Configure your own HTTPS mobile service before pairing.");
        await PairingGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Root);
            PersonalMobileCredentials credentials;
            // Cross-process lock prevents two instances from creating different identities.
            using (var fileLock = new FileStream(Path.Combine(Root, "pairing.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
            {
                credentials = ReadCredentials() ?? NewMemberCredentials();
                if (!File.Exists(Path.Combine(Root, "credentials.dat")))
                {
                    var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credentials, Json),
                        null, DataProtectionScope.CurrentUser);
                    AtomicFile.WriteText(Path.Combine(Root, "credentials.dat"), Convert.ToBase64String(encrypted));
                }
            }
            if (credentials.AccountId is not null)
            {
                if (credentials.AccountId != TokenHash(credentials.WriteToken))
                    throw new InvalidDataException("Mobile identity does not match its writer key.");
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                    { Timeout = TimeSpan.FromSeconds(15) };
                using var request = Request(HttpMethod.Post, "api.php?op=register", credentials);
                request.Content = new StringContent(new JsonObject {
                    ["readHash"] = TokenHash(credentials.ReadToken), ["publicKey"] = credentials.PublicKey
                }.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (result.RootElement.GetProperty("account").GetString() != credentials.AccountId)
                    throw new InvalidDataException("Unexpected mobile identity response.");
            }
            return WritePairingPage(credentials);
        }
        finally { PairingGate.Release(); }
    }

    internal static void Setup(string outputDirectory)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(outputDirectory);
        var credentials = ReadCredentials();
        if (credentials is null)
        {
            var keys = VapidHelper.GenerateVapidKeys();
            credentials = new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                keys.PublicKey, keys.PrivateKey);
            var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credentials, Json),
                null, DataProtectionScope.CurrentUser);
            AtomicFile.WriteText(Path.Combine(Root, "credentials.dat"), Convert.ToBase64String(encrypted));
            // Keep the secret file textual for atomic replacement; load decodes it below.
        }
        string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var config = new JsonObject { ["readHash"] = Hash(credentials.ReadToken),
            ["writeHash"] = Hash(credentials.WriteToken), ["publicKey"] = credentials.PublicKey }.ToJsonString();
        AtomicFile.WriteText(Path.Combine(outputDirectory, "config.php"), "<?php exit; ?>\n" + config);
        var bitmap = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/UsageTools.png"));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
            new System.Windows.Media.Imaging.TransformedBitmap(bitmap,
                new System.Windows.Media.ScaleTransform(192d / bitmap.PixelWidth, 192d / bitmap.PixelHeight))));
        using (var image = File.Create(Path.Combine(outputDirectory, "app-icon.png"))) encoder.Save(image);
        WritePairingPage(credentials);
    }

    internal static string RunTests()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = UsageSnapshot.Empty with {
            LastUpdatedLocal = now,
            Codex = new CodexUsage(38, 300, now.AddHours(2), 76, 10080, now.AddDays(3), false, true,
                "plus", now),
            Claude = ClaudeUsage.Missing with { HasLimitData = true, SyncState = ClaudeSyncState.LiveSynced,
                PrimaryUsedPercent = 0, SecondaryUsedPercent = 41, OfficialSyncedAt = now, PlanType = "pro",
                SecondaryResetsAt = now.AddDays(6), StatusDetail = "secret-token@example.com C:\\secret\\auth.json" },
            TopProjects = new[] { new ProjectUsage("private-project", 12345) }
        };
        var lines = new List<string>();
        void Check(string name, bool value) => lines.Add((value ? "PASS " : "FAIL ") + name);
        var payload = BuildPayload(snapshot, new AppSettings(), now);
        using var doc = JsonDocument.Parse(payload);
        var providers = doc.RootElement.GetProperty("providers");
        Check("mobile_schema_and_real_percent", providers[0].GetProperty("windows")[0].GetProperty("used").GetDouble() == 38);
        Check("mobile_zero_preserved", providers[1].GetProperty("windows")[0].GetProperty("used").GetDouble() == 0);
        Check("mobile_privacy_whitelist", !payload.Contains("secret") && !payload.Contains("private-project") && !payload.Contains("12345"));
        using var disabled = JsonDocument.Parse(BuildPayload(snapshot, new AppSettings { ClaudeDataEnabled = false }, now));
        Check("mobile_disabled_hides_usage", disabled.RootElement.GetProperty("providers")[1].GetProperty("windows")[0].GetProperty("used").ValueKind == JsonValueKind.Null);
        using var expired = JsonDocument.Parse(BuildPayload(snapshot with { Codex = snapshot.Codex with { PrimaryResetsAt = now } }, new AppSettings(), now));
        Check("mobile_expired_reset_unknown", expired.RootElement.GetProperty("providers")[0].GetProperty("windows")[0].GetProperty("used").ValueKind == JsonValueKind.Null);
        Check("mobile_endpoint_blocks_ssrf", !AllowedPushEndpoint("https://127.0.0.1/") && !AllowedPushEndpoint("https://fcm.googleapis.com.evil.test/") && !AllowedPushEndpoint("http://fcm.googleapis.com/") && AllowedPushEndpoint("https://web.push.apple.com/fixture"));
        Check("mobile_vapid_generation", VapidHelper.GenerateVapidKeys().PublicKey.Length == 87);
        Check("mobile_qr_png", QRCoder.PngByteQRCodeHelper.GetQRCode("https://example.com", QRCoder.QRCodeGenerator.ECCLevel.Q, 4)[0] == 137);
        var keys = VapidHelper.GenerateVapidKeys();
        using var push = new WebPushClient();
        using var pushRequest = push.GenerateRequestDetails(new PushSubscription("https://web.push.apple.com/fixture",
            keys.PublicKey, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_')),
            "fixture", new WebPushOptions { VapidDetails = new VapidDetails(BaseUrl, keys.PublicKey, keys.PrivateKey),
                ContentEncoding = ContentEncoding.Aes128gcm, TTL = 300 });
        Check("mobile_modern_push_encoding", pushRequest.Content!.Headers.ContentEncoding.Contains("aes128gcm") &&
            pushRequest.Headers.GetValues("TTL").Single() == "300" && pushRequest.Content.Headers.ContentLength > 86);
        using var weeklyOnly = JsonDocument.Parse(BuildPayload(snapshot with { Codex = snapshot.Codex with {
            PrimaryWindowMinutes = 10080, SecondaryUsedPercent = null, SecondaryResetsAt = null
        } }, new AppSettings(), now));
        var windows = weeklyOnly.RootElement.GetProperty("providers")[0].GetProperty("windows");
        Check("mobile_weekly_only_no_phantom_5h", windows.GetArrayLength() == 1 && windows[0].GetProperty("label").GetString() == "7D");
        using var pro = JsonDocument.Parse(BuildPayload(snapshot with { Codex = snapshot.Codex with { PlanType = "pro" } }, new AppSettings { ThemeId = "matrix" }, now));
        Check("mobile_pro_uses_actual_windows", pro.RootElement.GetProperty("providers")[0].GetProperty("windows").GetArrayLength() == 2);
        var detailed = BuildPayload(snapshot with { Codex = snapshot.Codex with { PlanType = "pro-5x" } }, new AppSettings(), now);
        using var legacyPayload = JsonDocument.Parse(LegacyPayload(detailed));
        Check("mobile_legacy_plan_fallback_preserves_windows", legacyPayload.RootElement.GetProperty("providers")[0].GetProperty("plan").GetString() == "Pro" &&
            legacyPayload.RootElement.GetProperty("providers")[0].GetProperty("windows").GetArrayLength() == 2 && detailed.Contains("Pro 5x"));
        Check("codex_pro25_exact_tier_not_5x", new[] { "Pro 25x", "pro-25x", "pro_25x", "codex-pro-25x", "pro25", "Pro 25×" }
            .All(plan => SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Codex, plan) == "codex-pro-25x" && SafePlan(plan) == "Pro 25x"));
        Check("codex_unspecified_pro_does_not_guess_multiplier", new[] { "pro", "pro-250x", "pro-50x", "pro-500" }
            .All(plan => SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Codex, plan) == "codex-pro" && SafePlan(plan) == "Pro"));
        Check("codex_existing_tiers_preserved", SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Codex, "prolite") == "codex-pro-5x" &&
            SafePlan("pro-10x") == "Pro 10x" && SafePlan("pro-20x") == "Pro 20x" && SafePlan("claude-max-20x") == "Max 20x");
        var confirmedPlan = new AppSettings { CodexConfirmedPlan = "pro-usd200-confirmed", CodexConfirmedPlanAccountKey = new string('a', 64) };
        Check("owner_price_confirmation_account_bound", SubscriptionPlanCatalog.ResolveCodexPlan("pro", new string('a', 64), confirmedPlan) == "pro-usd200-confirmed" &&
            SubscriptionPlanCatalog.ResolveCodexPlan("pro", new string('b', 64), confirmedPlan) == "pro" &&
            SubscriptionPlanCatalog.ResolveCodexPlan("pro", null, confirmedPlan) == "pro");
        Check("explicit_official_plan_overrides_local_confirmation", SubscriptionPlanCatalog.ResolveCodexPlan("pro-25x", new string('a', 64), confirmedPlan) == "pro-25x" &&
            SubscriptionPlanCatalog.ResolveCodexPlan("plus", new string('a', 64), confirmedPlan) == "plus");
        Check("plan_confirmation_not_uploaded", !BuildPayload(snapshot, confirmedPlan, now).Contains(new string('a', 64)));
        Check("priced_plan_survives_notification_reserialization", new[] { "100", "200", "500" }.All(price =>
            SafePlan(SafePlan("pro-usd" + price + "-confirmed")) == "Pro US$" + price + "/mo"));
        Check("legacy_multiplier_not_assumed_current", SubscriptionPlanCatalog.ResolveCodexPlan("pro", new string('a', 64),
            new AppSettings { CodexConfirmedPlan = "pro-20x", CodexConfirmedPlanAccountKey = new string('a', 64) }) == "pro");
        using var balancePayload = JsonDocument.Parse(BuildPayload(snapshot with { Codex = snapshot.Codex with {
            Credits = new(61740.53m), AccountKey = new string('a', 64), PlanType = "pro-usd200-confirmed" } }, new AppSettings(), now));
        Check("balance_separate_from_vouchers", balancePayload.RootElement.GetProperty("creditBalance").GetProperty("balance").GetDecimal() == 61740.53m &&
            balancePayload.RootElement.GetProperty("providers")[0].GetProperty("plan").GetString() == "Pro US$200/mo" &&
            balancePayload.RootElement.GetProperty("planConfirmed").GetBoolean() && !balancePayload.RootElement.GetRawText().Contains(new string('a', 64)));
        using var balanceDisabled = JsonDocument.Parse(BuildPayload(snapshot with { Codex = snapshot.Codex with { Credits = new(123m) } }, new AppSettings { CodexDataEnabled = false }, now));
        Check("disabled_balance_not_uploaded", balanceDisabled.RootElement.GetProperty("creditBalance").ValueKind == JsonValueKind.Null);
        var pro25Snapshot = snapshot with { Codex = snapshot.Codex with { PlanType = "pro_25x", PrimaryWindowMinutes = 10080, SecondaryUsedPercent = null, SecondaryResetsAt = null } };
        var pro25Notice = new MiniUsageAnnouncement("Codex", "7D", MiniUsageAnnouncementKind.LowRemaining, 20, 3, true) { Plan = "Pro 25x", OccurredAt = now, DeliveryId = new string('a', 32) };
        var pro25Json = BuildPayload(pro25Snapshot, new AppSettings(), now, [pro25Notice]);
        using var pro25 = JsonDocument.Parse(pro25Json);
        Check("mobile_pro25_weekly_only_and_event_plan", pro25.RootElement.GetProperty("providers")[0].GetProperty("plan").GetString() == "Pro 25x" &&
            pro25.RootElement.GetProperty("providers")[0].GetProperty("windows").GetArrayLength() == 1 &&
            pro25.RootElement.GetProperty("providers")[0].GetProperty("windows")[0].GetProperty("label").GetString() == "7D" &&
            pro25.RootElement.GetProperty("notifications")[0].GetProperty("plan").GetString() == "Pro 25x");
        using var legacy25 = JsonDocument.Parse(LegacyPayload(pro25Json));
        Check("mobile_pro25_old_server_fallback", legacy25.RootElement.GetProperty("providers")[0].GetProperty("plan").GetString() == "Pro" &&
            legacy25.RootElement.GetProperty("notifications")[0].GetProperty("plan").GetString() == "Pro");
        Check("mobile_theme_whitelist", pro.RootElement.GetProperty("themeId").GetString() == "matrix" &&
            !BuildPayload(snapshot, new AppSettings { ThemeId = "<script>secret</script>" }, now).Contains("secret"));
        var notice = BuildNotification(new MiniUsageAnnouncement("Codex", "5H", MiniUsageAnnouncementKind.LowRemaining, 20, 3, true), snapshot, now);
        Check("mobile_notification_uses_latest_quota", notice["body"]!.GetValue<string>().Contains("剩餘 62%\n🟥🟥🟥🟥🟥🟥⬜⬜⬜⬜"));
        Check("mobile_notification_title_has_latest_window_and_percent", notice["title"]!.GetValue<string>() == "Codex｜5H 剩餘 62%");
        foreach (var remaining in new[] { 0, 5, 20, 95, 100 })
        {
            var msg = BuildNotification(new MiniUsageAnnouncement("Codex", "5H", MiniUsageAnnouncementKind.UsageMilestone, remaining, 3, false),
                snapshot with { Codex = snapshot.Codex with { PrimaryUsedPercent = 100 - remaining } }, now)["body"]!.GetValue<string>();
            var red = msg.Split("🟥").Length - 1;
            var white = msg.Split("⬜").Length - 1;
            Check("mobile_colored_bar_" + remaining, red + white == 10 && (remaining == 0 ? red == 0 : remaining == 100 ? red == 10 : red is >= 1 and <= 9));
        }
        Check("mobile_notification_no_credentials", !notice.ToJsonString().Contains("secret") && !notice.ToJsonString().Contains("private-project"));
        Check("mobile_notification_missing_window_no_bar", BuildNotification(new MiniUsageAnnouncement("Codex", "1H", MiniUsageAnnouncementKind.LowRemaining, 20, 3, true), snapshot, now)["quota"] is null);
        Check("mobile_notification_unknown_no_bar", BuildNotification(new MiniUsageAnnouncement("Codex", "5H", MiniUsageAnnouncementKind.LowRemaining, 20, 3, true), snapshot with { Codex = snapshot.Codex with { PrimaryUsedPercent = null } }, now)["quota"] is null);
        var memberA = NewMemberCredentials();
        var memberB = NewMemberCredentials();
        Check("member_identity_is_writer_hash", memberA.AccountId == TokenHash(memberA.WriteToken));
        Check("members_have_independent_reader_writer_vapid", memberA.ReadToken != memberB.ReadToken &&
            memberA.WriteToken != memberB.WriteToken && memberA.PrivateKey != memberB.PrivateKey &&
            memberA.ReadToken != memberA.WriteToken);
        Check("member_uses_separate_service", ServiceUrl(memberA) == TeamBaseUrl);
        var legacy = JsonSerializer.Deserialize<PersonalMobileCredentials>(
            "{\"readToken\":\"read\",\"writeToken\":\"write\",\"publicKey\":\"public\",\"privateKey\":\"private\"}", Json)!;
        Check("legacy_pairing_json_backward_compatible", legacy.AccountId is null && ServiceUrl(legacy) == BaseUrl);
        using var memberRequest = Request(HttpMethod.Get, "api.php?op=subscriptions", memberA);
        Check("member_writer_scoped_header", memberRequest.Headers.GetValues("X-Usage-Account").Single() == memberA.AccountId &&
            memberRequest.Headers.Authorization?.Parameter == memberA.WriteToken);
        Check("writer_not_in_request_url", !memberRequest.RequestUri!.AbsoluteUri.Contains(memberA.WriteToken));
        Check("disabled_by_default_for_members", !new AppSettings().PersonalMobileEnabled);
        RunClaudeContractTests(Check, snapshot, now);
        ResetNewsDeliveryService.RunTests(Check);
        lines.Add("SUMMARY passed=" + !lines.Any(l => l.StartsWith("FAIL")) + " checks=" + lines.Count + " failed=" + lines.Count(l => l.StartsWith("FAIL")));
        return string.Join(Environment.NewLine, lines);
    }

    // FIXTURE-based checks for the optional Claude contract; no live account data is used.
    private static void RunClaudeContractTests(Action<string, bool> Check, UsageSnapshot baseSnapshot, DateTimeOffset now)
    {
        var parsed = ClaudeOfficialSyncService.ParseUsage(ClaudeParseSelfTest.RealShapeUsage, null, now);
        var fableEnds = now.AddDays(5);
        var details = parsed.Details with
        {
            Pools = parsed.Details.Pools.Select(p => p with { ResetsAt = now.AddDays(p.Label == "5H" ? 0.1 : 5) }).ToArray(),
            Resets = parsed.Details.Resets! with
            {
                Grants = parsed.Details.Resets.Grants.Select((g, i) => g with { EndsAt = i == 0 ? fableEnds : now.AddDays(29) }).ToArray()
            },
            Models = ClaudeOfficialDetailsParser.ParseModels("""[{"model":"claude-fable-5-2","name":"Claude Fable 5.2"},{"model":"claude-opus-5-5","name":"Claude Opus 5.5"},{"model":"claude-sonnet-4-6","name":"Claude Sonnet 4.6","overflow":true},{"model":"claude-opus-4-1","name":"Opus 4.1","inactive":true}]"""),
            ModelsSyncedAt = now
        };
        var accountA = new string('c', 64);
        var claude = baseSnapshot.Claude with { PlanType = parsed.PlanType, Details = details, AccountKey = accountA,
            PrimaryUsedPercent = 16, PrimaryResetsAt = now.AddHours(2), SecondaryUsedPercent = 3, SecondaryResetsAt = now.AddDays(5) };
        var snapshot = baseSnapshot with { Claude = claude };

        var json = BuildPayload(snapshot, new AppSettings(), now);
        using var doc = JsonDocument.Parse(json);
        var cd = doc.RootElement.GetProperty("claudeDetails");
        Check("claude_details_pools_resets_models_credits",
            cd.GetProperty("pools").GetArrayLength() == 3 &&
            cd.GetProperty("pools")[2].GetProperty("model").GetString() == "Fable" &&
            cd.GetProperty("pools")[2].GetProperty("allowance").GetDouble() == 50 &&
            cd.GetProperty("resets").GetProperty("available").GetInt32() == 2 &&
            cd.GetProperty("resets").GetProperty("grants").GetArrayLength() == 2 &&
            cd.GetProperty("resets").GetProperty("grants")[0].GetProperty("kind").GetString() == "fable" &&
            cd.GetProperty("models").GetProperty("count").GetInt32() == 3 &&
            cd.GetProperty("models").GetProperty("primary").GetArrayLength() == 2 &&
            cd.GetProperty("extraUsage").GetProperty("currency").GetString() == "USD" &&
            doc.RootElement.GetProperty("providers")[1].GetProperty("windows").GetArrayLength() == 2);
        Check("claude_details_no_labels_ids_or_account", !json.Contains("launch-2610") && !json.Contains("New model launch") &&
            !json.Contains(accountA) && !json.Contains("claude-fable-5-2"));
        Check("claude_details_hidden_when_disabled_or_unsynced",
            JsonDocument.Parse(BuildPayload(snapshot, new AppSettings { ClaudeDataEnabled = false }, now)).RootElement.GetProperty("claudeDetails").ValueKind == JsonValueKind.Null &&
            JsonDocument.Parse(BuildPayload(snapshot with { Claude = claude with { OfficialSyncedAt = now.AddDays(-8) } }, new AppSettings(), now)).RootElement.GetProperty("claudeDetails").ValueKind == JsonValueKind.Null);
        Check("claude_details_size_bounded", Encoding.UTF8.GetByteCount(json) < 15000);

        var ultra = BuildPayload(snapshot with { Claude = claude with { PlanType = "claude_ultra" } }, new AppSettings(), now);
        using var ultraLegacy = JsonDocument.Parse(LegacyPayload(ultra));
        Check("claude_ultra_plan_and_old_backend_fallback", ultra.Contains("\"plan\":\"Ultra\"") &&
            ultraLegacy.RootElement.GetProperty("providers")[1].GetProperty("plan").GetString() == "Max" &&
            !ultraLegacy.RootElement.TryGetProperty("claudeDetails", out _));

        var statePath = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", Guid.NewGuid().ToString("N"), "events.json");
        var events = new MiniUsageAnnouncementService(statePath, outbox: true);
        var first = events.Observe(snapshot);
        Check("claude_existing_grants_are_silent_baseline", first.All(item => item.Kind != MiniUsageAnnouncementKind.ResetCreditsAdded));
        var withGift = details with { Resets = details.Resets! with { Grants = details.Resets.Grants.Append(
            new ClaudeResetGrant("full-reset-2612", "full", null, 1, 1, now, now.AddDays(30), ["five_hour", "seven_day"], true, false, null)).ToArray() } };
        var giftSnapshot = snapshot with { Claude = claude with { Details = withGift } };
        var failedSnapshot = giftSnapshot with { Claude = giftSnapshot.Claude with
            { SyncState = ClaudeSyncState.CachedSyncFailed, PrimaryUsedPercent = 0, SecondaryUsedPercent = 0 } };
        Check("claude_failed_sync_never_announces_reset_or_gift", events.Observe(failedSnapshot)
            .All(item => item.Provider != "Claude Code"));
        using (var failedPayload = JsonDocument.Parse(BuildPayload(failedSnapshot, new AppSettings(), now)))
            Check("claude_failed_sync_payload_not_fresh", failedPayload.RootElement.GetProperty("providers")[1]
                .GetProperty("state").GetString() != "Fresh");
        var gift = events.Observe(giftSnapshot).Where(item => item.Kind == MiniUsageAnnouncementKind.ResetCreditsAdded).ToArray();
        var repeat = events.Observe(giftSnapshot).Where(item => item.Kind == MiniUsageAnnouncementKind.ResetCreditsAdded).ToArray();
        Check("claude_new_grant_announced_once_with_kind_and_five_repeats", gift.Length == 1 && repeat.Length == 0 &&
            gift[0] is { Provider: "Claude Code", AddedCreditCount: 1, CreditCount: 3, CreditKind: "full", RepeatCount: 5, WindowLabel: "" } &&
            gift[0].CreditExpirations?.Length == 3);
        var giftJson = BuildPayload(giftSnapshot, new AppSettings(), now, gift);
        using var giftDoc = JsonDocument.Parse(giftJson);
        var giftEvent = giftDoc.RootElement.GetProperty("notifications")[0];
        Check("claude_grant_event_serialized_and_stripped_for_old_backend",
            giftEvent.GetProperty("provider").GetString() == "claude" && giftEvent.GetProperty("creditKind").GetString() == "full" &&
            giftEvent.GetProperty("window").GetString() == "" &&
            JsonDocument.Parse(LegacyPayload(giftJson)).RootElement.GetProperty("notifications").GetArrayLength() == 0);
        var giftPush = BuildNotification(gift[0], giftSnapshot, now);
        Check("claude_grant_push_title_names_provider_and_kind", giftPush["title"]!.GetValue<string>() == "Claude Code｜新增 1 張完整重置券" &&
            giftPush["body"]!.GetValue<string>().Contains("目前可用 3 張") && giftPush["creditCount"]!.GetValue<int>() == 3);

        foreach (var partial in new ClaudeResetGrantSummary?[]
        {
            null,
            withGift.Resets! with { Grants = withGift.Resets!.Grants.Select(g => g with { Left = null }).ToArray() },
            withGift.Resets! with { Grants = withGift.Resets!.Grants.Select(g => g with { Id = "" }).ToArray() }
        })
        {
            var unknown = events.Observe(giftSnapshot with { Claude = giftSnapshot.Claude with { Details = withGift with { Resets = partial } } });
            var restored = events.Observe(giftSnapshot);
            Check("claude_partial_grants_preserve_baseline_" + (partial is null ? "missing" : partial.Grants[0].Left is null ? "count" : "identity"),
                unknown.Concat(restored).All(item => item.Kind != MiniUsageAnnouncementKind.ResetCreditsAdded));
        }

        var accountB = new string('d', 64);
        var switched = events.Observe(snapshot with { Claude = claude with { AccountKey = accountB, Details = withGift } })
            .Where(item => item.Kind == MiniUsageAnnouncementKind.ResetCreditsAdded).ToArray();
        Check("claude_account_switch_never_replays_grants", switched.Length == 0);

        var lowFable = details with { Pools = details.Pools.Select(p => p.IsModelScoped ? p with { Percent = 81 } : p).ToArray() };
        var fable = events.Observe(snapshot with { Claude = claude with { AccountKey = accountB, Details = lowFable } })
            .Where(item => item.Pool == "Fable").ToArray();
        Check("claude_fable_pool_low_remaining_separate_from_7d", fable.Length == 1 &&
            fable[0] is { Kind: MiniUsageAnnouncementKind.LowRemaining, WindowLabel: "7D", RemainingPercent: 19, RepeatCount: 3 } &&
            events.Pending().Count(item => item.Provider == "Claude Code" && item.WindowLabel == "7D") >= 1);
        var fablePush = BuildNotification(fable[0], snapshot with { Claude = claude with { Details = lowFable } }, now);
        Check("claude_pool_push_title_names_pool", fablePush["title"]!.GetValue<string>() == "Claude Code｜7D Fable 低額度 19%" &&
            fablePush["quota"]!["pool"]!.GetValue<string>() == "Fable");
        try { Directory.Delete(Path.GetDirectoryName(statePath)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string WritePairingPage(PersonalMobileCredentials credentials)
    {
        var url = ServiceUrl(credentials) + "#key=" +
            (credentials.AccountId is null ? "" : credentials.AccountId + ".") + credentials.ReadToken;
        var png = QRCoder.PngByteQRCodeHelper.GetQRCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q, 6);
        var path = Path.Combine(Root, "pairing.html");
        AtomicFile.WriteText(path, "<!doctype html><html lang=\"zh-Hant\"><meta charset=\"utf-8\">" +
            "<meta name=\"referrer\" content=\"no-referrer\"><title>手機配對</title>" +
            "<body style=\"font-family:Segoe UI,sans-serif;text-align:center;background:#10161b;color:#eee;padding:32px\">" +
            "<h1>UsageTools 個人手機版</h1><p>用手機相機掃描，加入主畫面後開啟通知。</p>" +
            "<img width=\"300\" height=\"300\" alt=\"個人配對碼\" src=\"data:image/png;base64," +
            Convert.ToBase64String(png) + "\"><p><a style=\"color:#58acff\" href=\"" +
            WebUtility.HtmlEncode(url) + "\">開啟個人看板</a></p>" +
            "<p>這個配對碼可讀取你的用量，請勿分享。電腦工具須保持開啟與連網。</p></body></html>");
        return path;
    }

    public static string? PairingPage()
    {
        var credentials = ReadCredentials();
        return credentials is null ? null : WritePairingPage(credentials);
    }

    internal static string BuildPayload(UsageSnapshot snapshot, AppSettings settings, DateTimeOffset now,
        IReadOnlyList<MiniUsageAnnouncement>? history = null)
    {
        var cx = snapshot.Codex;
        var cl = snapshot.Claude;
        MobileQuotaProvider Provider(string name, bool enabled, bool has, bool failed, DateTimeOffset? synced,
            string? plan, double? five, DateTimeOffset? fiveReset, double? seven, DateTimeOffset? sevenReset,
            string fiveLabel = "5H", string sevenLabel = "7D", bool claudePlan = false)
        {
            var usable = enabled && has && (claudePlan || UsageFreshness.UsableTime(synced, now));
            if (claudePlan)
            {
                five = UsageFreshness.ClaudeWindow(five, fiveReset, synced, now, TimeSpan.FromHours(5));
                seven = UsageFreshness.ClaudeWindow(seven, sevenReset, synced, now, TimeSpan.FromDays(7));
            }
            var windows = new[] {
                new MobileQuotaWindow(fiveLabel, usable ? UsageFreshness.Window(five, fiveReset, now) : null,
                    enabled ? fiveReset?.ToUniversalTime().ToString("O") : null),
                new MobileQuotaWindow(sevenLabel, usable ? UsageFreshness.Window(seven, sevenReset, now) : null,
                    enabled ? sevenReset?.ToUniversalTime().ToString("O") : null)
            };
            if (fiveLabel == sevenLabel)
                windows = new[] { seven is not null || sevenReset is not null ? windows[1] : windows[0] };
            if (!claudePlan && five is null && fiveReset is null && (seven is not null || sevenReset is not null))
                windows = windows.Where(w => w.Label != fiveLabel || fiveLabel == sevenLabel).ToArray();
            if (seven is null && sevenReset is null && (five is not null || fiveReset is not null))
                windows = windows.Where(w => w.Label != sevenLabel || fiveLabel == sevenLabel).ToArray();
            return new(name, enabled, enabled ? claudePlan ? SafeClaudePlan(plan) : SafePlan(plan) : null,
                UsageFreshness.Evaluate(enabled, has, synced, failed, now).ToString(),
                enabled ? synced?.ToUniversalTime().ToString("O") : null,
                windows);
        }
        var payload = JsonSerializer.Serialize(new MobileQuotaPayload(2, now.ToUniversalTime().ToString("O"),
            new[] {
                Provider("Codex", settings.CodexDataEnabled, cx.HasData, cx.IsStale, cx.OfficialSyncedAt,
                    cx.PlanType, cx.PrimaryUsedPercent, cx.PrimaryResetsAt, cx.SecondaryUsedPercent, cx.SecondaryResetsAt,
                    DisplayFormatService.FormatWindowLabel(cx.PrimaryWindowMinutes, "5H").ToUpperInvariant(),
                    DisplayFormatService.FormatWindowLabel(cx.SecondaryWindowMinutes, "7D").ToUpperInvariant()),
                Provider("Claude Code", settings.ClaudeDataEnabled, cl.HasLimitData,
                    cl.SyncState != ClaudeSyncState.LiveSynced, cl.OfficialSyncedAt, cl.PlanType,
                    cl.PrimaryUsedPercent, cl.PrimaryResetsAt, cl.SecondaryUsedPercent, cl.SecondaryResetsAt,
                    claudePlan: true)
            }, ThemeCatalog.Resolve(string.IsNullOrEmpty(settings.ThemeId)
                ? settings.Theme == AppTheme.Day ? "day" : "deep-night" : settings.ThemeId).Id), Json);
        var root = JsonNode.Parse(payload)!.AsObject();
        root["creditBalance"] = !settings.CodexDataEnabled || cx.Credits is null ? null : new JsonObject {
            ["balance"] = cx.Credits.Balance, ["unlimited"] = cx.Credits.Unlimited,
            ["syncedAt"] = cx.OfficialSyncedAt?.ToUniversalTime().ToString("O"),
            ["stale"] = UsageFreshness.Evaluate(true, cx.HasData, cx.OfficialSyncedAt, cx.IsStale, now) != UsageDataState.Fresh
        };
        root["planConfirmed"] = settings.CodexDataEnabled && cx.PlanType?.EndsWith("-confirmed", StringComparison.Ordinal) == true;
        root["claudeDetails"] = BuildClaudeDetails(cl, settings.ClaudeDataEnabled, now);
        var credits = snapshot.CodexResetCredits;
        root["resetCredits"] = new JsonObject {
            ["available"] = settings.CodexDataEnabled && credits.SyncedAt is not null ? credits.AvailableCount : null,
            ["syncedAt"] = settings.CodexDataEnabled ? credits.SyncedAt?.ToUniversalTime().ToString("O") : null,
            ["stale"] = !settings.CodexDataEnabled || credits.IsStale || credits.SyncedAt is null,
            ["expirations"] = JsonSerializer.SerializeToNode(settings.CodexDataEnabled
                ? credits.Credits.Where(c => c.ExpiresAt > now).OrderBy(c => c.ExpiresAt).Take(100).Select(c => c.ExpiresAt.ToUniversalTime().ToString("O")).ToArray() : [])
        };
        var notifications = new JsonArray();
        foreach (var item in (history ?? []).Where(item =>
            (item.Provider == "Codex" ? settings.CodexDataEnabled : item.Provider == "Claude Code" && settings.ClaudeDataEnabled) &&
            item.OccurredAt >= now.AddHours(-24) && item.OccurredAt <= now.AddMinutes(1)).Take(25))
        {
            notifications.Add(new JsonObject {
                ["id"] = item.DeliveryId, ["at"] = item.OccurredAt!.Value.ToUniversalTime().ToString("O"),
                ["provider"] = item.Provider == "Codex" ? "codex" : "claude", ["window"] = item.WindowLabel,
                ["kind"] = item.Kind.ToString(), ["remaining"] = item.RemainingPercent,
                ["creditCount"] = item.CreditCount, ["addedCount"] = item.AddedCreditCount,
                ["plan"] = item.Provider == "Codex" ? SafePlan(item.Plan) : SafeClaudePlan(item.Plan),
                ["pool"] = ClaudeOfficialDetailsParser.SafeName(item.Pool),
                ["creditKind"] = item.CreditKind,
                ["resetsAt"] = item.ResetsAt?.ToUniversalTime().ToString("O"),
                ["expiresAt"] = item.CreditExpiresAt?.ToUniversalTime().ToString("O"),
                ["creditExpirations"] = JsonSerializer.SerializeToNode((item.CreditExpirations ?? []).Take(100).Select(at => at.ToUniversalTime().ToString("O")).ToArray())
            });
        }
        root["notifications"] = notifications;
        return root.ToJsonString();
    }

    internal static string? SafePlan(string? plan)
    {
        var normalized = SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Codex, plan);
        if (normalized?.StartsWith("codex-pro-usd", StringComparison.Ordinal) == true)
            return "Pro US$" + normalized[13..] + "/mo";
        if (normalized?.StartsWith("codex-pro-", StringComparison.Ordinal) == true)
            return "Pro " + normalized[10..];
        if (normalized == "codex-pro") return "Pro";
        return plan?.Trim().ToLowerInvariant() switch
        {
        "free" => "Free", "go" => "Go", "edu" or "education" => "Edu", "plus" => "Plus", "pro" or "claude-pro" => "Pro",
        "pro-5x" or "pro 5x" => "Pro 5x", "pro-20x" or "pro 20x" => "Pro 20x",
        "max" => "Max", "claude-max-5x" or "max 5x" => "Max 5x", "claude-max-20x" or "max 20x" => "Max 20x",
        "ultra" or "claude-ultra" => "Ultra",
        "team" => "Team", "business" => "Business", "enterprise" => "Enterprise", _ => null
        };
    }

    internal static string? SafeClaudePlan(string? plan) =>
        SubscriptionPlanCatalog.Normalize(SubscriptionProvider.Claude, plan) switch
        {
            "claude-free" => "Free",
            "claude-pro" => "Pro",
            "claude-max" => "Max",
            "claude-max-5x" => "Max 5x",
            "claude-ultra" => "Ultra",
            "claude-team" => "Team",
            "claude-enterprise" => "Enterprise",
            _ => null
        };

    // Optional, additive Claude contract (schema documented in Reports/CLAUDE_DATA_CONTRACT.md).
    // Older backends ignore this key; it never carries account ids, labels, paths or raw JSON.
    internal static JsonObject? BuildClaudeDetails(ClaudeUsage claude, bool enabled, DateTimeOffset now)
    {
        if (!enabled || claude.Details is not { } details || !claude.HasLimitData ||
            claude.OfficialSyncedAt is not { } synced || synced > now.AddMinutes(1) || now - synced >= TimeSpan.FromDays(7)) return null;
        var pools = new JsonArray();
        foreach (var pool in details.Pools.Take(ClaudeOfficialDetailsParser.MaxPools))
        {
            pools.Add(new JsonObject
            {
                ["label"] = pool.Label,
                ["model"] = ClaudeOfficialDetailsParser.SafeName(pool.ModelName),
                ["used"] = UsageFreshness.ClaudeWindow(pool.Percent, pool.ResetsAt, synced, now,
                    pool.Label == "5H" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7)),
                ["resetsAt"] = pool.ResetsAt?.ToUniversalTime().ToString("O"),
                ["allowance"] = pool.AllowancePercentOfWeekly
            });
        }
        JsonObject? resets = null;
        if (details.Resets is { } summary)
        {
            var grants = new JsonArray();
            foreach (var grant in summary.Active(now).OrderBy(g => g.EndsAt ?? DateTimeOffset.MaxValue)
                         .Take(ClaudeOfficialDetailsParser.MaxGrants))
            {
                grants.Add(new JsonObject
                {
                    ["kind"] = grant.Category,
                    ["left"] = grant.Left,
                    ["total"] = grant.Total,
                    ["expiresAt"] = grant.EndsAt?.ToUniversalTime().ToString("O"),
                    ["usableNow"] = grant.UsableNow
                });
            }
            resets = new JsonObject
            {
                ["available"] = summary.AvailableCount(now),
                ["eligible"] = summary.Eligible,
                ["syncedAt"] = summary.SyncedAt.ToUniversalTime().ToString("O"),
                ["grants"] = grants
            };
        }
        JsonObject? models = null;
        if (details.Models is { } list)
        {
            var visible = list.Where(model => !model.Inactive).ToArray();
            models = new JsonObject
            {
                ["count"] = visible.Length,
                ["primary"] = JsonSerializer.SerializeToNode(visible.Where(model => !model.Overflow)
                    .Select(model => ClaudeOfficialDetailsParser.SafeName(model.Name)).OfType<string>().Take(12).ToArray()),
                ["syncedAt"] = details.ModelsSyncedAt?.ToUniversalTime().ToString("O")
            };
        }
        JsonObject? extra = details.ExtraUsage is { } usage ? new JsonObject
        {
            ["enabled"] = usage.Enabled,
            ["used"] = usage.Used,
            ["limit"] = usage.MonthlyLimit,
            ["currency"] = usage.Currency,
            ["limitReached"] = usage.SpendLimitReached
        } : null;
        return new JsonObject
        {
            ["syncedAt"] = claude.OfficialSyncedAt?.ToUniversalTime().ToString("O"),
            ["pools"] = pools,
            ["resets"] = resets,
            ["models"] = models,
            ["extraUsage"] = extra
        };
    }

    internal static string LegacyPayload(string payload)
    {
        var root = JsonNode.Parse(payload)!;
        foreach (var provider in root["providers"]!.AsArray().Concat(root["notifications"]?.AsArray() ?? new JsonArray()))
        {
            var plan = provider!["plan"]?.GetValue<string>();
            if (plan?.StartsWith("Pro ", StringComparison.Ordinal) == true) provider["plan"] = "Pro";
            if (plan?.StartsWith("Max ", StringComparison.Ordinal) == true || plan == "Ultra") provider["plan"] = "Max";
        }
        // Older backends only accept Codex reset-credit events; Claude grant events stay local there.
        if (root["notifications"] is JsonArray events)
        {
            foreach (var item in events.Where(e => e?["kind"]?.GetValue<string>() == nameof(MiniUsageAnnouncementKind.ResetCreditsAdded) &&
                                                   e["provider"]?.GetValue<string>() == "claude").ToArray())
                events.Remove(item);
        }
        root.AsObject().Remove("claudeDetails");
        return root.ToJsonString();
    }

    internal static JsonObject BuildNotification(MiniUsageAnnouncement item, UsageSnapshot snapshot, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(BuildPayload(snapshot, new AppSettings(), now));
        var provider = doc.RootElement.GetProperty("providers").EnumerateArray().FirstOrDefault(p => p.GetProperty("name").GetString() == item.Provider);
        var window = default(JsonElement);
        if (string.IsNullOrEmpty(item.Pool))
        {
            window = provider.ValueKind == JsonValueKind.Object ? provider.GetProperty("windows").EnumerateArray()
                .FirstOrDefault(w => w.GetProperty("label").GetString() == item.WindowLabel) : default;
        }
        else if (doc.RootElement.TryGetProperty("claudeDetails", out var claudeDetails) && claudeDetails.ValueKind == JsonValueKind.Object)
        {
            window = claudeDetails.GetProperty("pools").EnumerateArray().FirstOrDefault(p =>
                p.GetProperty("label").GetString() == item.WindowLabel && p.GetProperty("model").ValueKind == JsonValueKind.String &&
                p.GetProperty("model").GetString() == item.Pool);
        }
        var isClaude = item.Provider != "Codex";
        var claudeCredits = isClaude ? snapshot.Claude.Details?.Resets?.AvailableCount(now) : null;
        var creditName = ClaudeOfficialDetailsParser.CreditKindName(item.CreditKind);
        var title = item.Kind == MiniUsageAnnouncementKind.ResetCreditsAdded
            ? (isClaude ? "Claude Code｜新增 " + item.AddedCreditCount + " 張" + creditName : "Codex｜新增 " + item.AddedCreditCount + " 張重置券") :
            item.Provider + "｜" + item.QuotaLabel + (item.Kind == MiniUsageAnnouncementKind.UsageReset ? " 額度已重置" : " 用量提醒");
        var body = "最新額度尚未提供";
        JsonObject? quota = null;
        if (item.Kind == MiniUsageAnnouncementKind.ResetCreditsAdded)
        {
            var available = isClaude ? claudeCredits?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未提供"
                : snapshot.CodexResetCredits.AvailableCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            body = (item.AddedCreditCount > 0 ? "新增 " + item.AddedCreditCount + " 張；" : "") + "目前可用 " + available + " 張重置券";
            var dates = (item.CreditExpirations ?? (item.CreditExpiresAt is { } expires ? [expires] : []));
            foreach (var (date, index) in dates.Take(3).Select((date, index) => (date, index)))
                body += "\n第 " + (index + 1) + " 張 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(date, "Taipei Standard Time").ToString("MM/dd HH:mm") + " 到期";
            if (item.CreditCount > dates.Take(3).Count()) body += "\n其餘券請開啟通知詳情";
        }
        else if (window.ValueKind == JsonValueKind.Object && window.GetProperty("used").ValueKind == JsonValueKind.Number && window.GetProperty("used").TryGetDouble(out var used))
        {
            var remaining = Math.Clamp((int)Math.Round(100 - used), 0, 100);
            if (item.Kind != MiniUsageAnnouncementKind.UsageReset)
                title = item.Provider + "｜" + item.QuotaLabel + (item.Kind == MiniUsageAnnouncementKind.LowRemaining && remaining <= 20 ? " 低額度 " : " 剩餘 ") + remaining + "%";
            var filled = Math.Clamp((int)Math.Round(remaining / 10.0, MidpointRounding.AwayFromZero), remaining == 0 ? 0 : 1, remaining == 100 ? 10 : 9);
            body = "剩餘 " + remaining + "%\n" + string.Concat(Enumerable.Repeat("🟥", filled)) + string.Concat(Enumerable.Repeat("⬜", 10 - filled));
            var reset = window.GetProperty("resetsAt").GetString();
            if (DateTimeOffset.TryParse(reset, out var at))
                body += "\n重置 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(at, "Taipei Standard Time").ToString("MM/dd HH:mm");
            if (DateTimeOffset.TryParse(provider.GetProperty("syncedAt").GetString(), out var synced))
                body += " · 資料 " + TimeZoneInfo.ConvertTimeBySystemTimeZoneId(synced, "Taipei Standard Time").ToString("HH:mm");
            quota = new JsonObject { ["remaining"] = remaining, ["provider"] = item.Provider, ["window"] = item.WindowLabel,
                ["pool"] = ClaudeOfficialDetailsParser.SafeName(item.Pool) };
        }
        body = (item.Plan ?? "方案待確認") + " · " + body;
        return new JsonObject { ["title"] = title, ["body"] = body, ["tag"] = item.DeliveryId,
            ["kind"] = item.Kind.ToString(),
            ["creditCount"] = isClaude ? claudeCredits ?? item.CreditCount : snapshot.CodexResetCredits.AvailableCount,
            ["addedCount"] = item.AddedCreditCount,
            ["provider"] = item.Provider == "Codex" ? "codex" : "claude", ["quota"] = quota };
    }

    public async Task PublishAsync(UsageSnapshot snapshot)
    {
        if (!IsConfigured) { Status = "Mobile service is not configured"; return; }
        if (!AppSettingsService.Load().PersonalMobileEnabled || DateTimeOffset.UtcNow < _nextAttempt ||
            !await _gate.WaitAsync(0)) return;
        try
        {
            _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
            _credentials ??= ReadCredentials();
            if (_credentials is null) { Status = "尚未配對"; return; }
            var settings = AppSettingsService.Load();
            var filtered = snapshot with {
                Codex = settings.CodexDataEnabled ? snapshot.Codex : CodexUsage.Empty,
                Claude = settings.ClaudeDataEnabled ? snapshot.Claude : ClaudeUsage.Missing,
                CodexResetCredits = settings.CodexDataEnabled ? snapshot.CodexResetCredits : CodexResetCreditSummary.Empty
            };
            // Persist events before network I/O; failed uploads must not erase the transition.
            _events.Observe(filtered);
            foreach (var item in _events.Pending().Where(item => item.Provider == "Codex" ? !settings.CodexDataEnabled : !settings.ClaudeDataEnabled))
                _events.Acknowledge(item);
            var batch = _events.Pending().Where(item => item.Provider != "Claude Code" ||
                UsageFreshness.State(filtered.Claude) == UsageDataState.Fresh).OrderBy(item => item.PushCompleted).Take(25).ToArray();
            var payload = BuildPayload(filtered, settings, DateTimeOffset.UtcNow, batch);
            while (Encoding.UTF8.GetByteCount(payload) > 15000 && batch.Length > 1)
            {
                batch = batch.Take(batch.Length - 1).ToArray();
                payload = BuildPayload(filtered, settings, DateTimeOffset.UtcNow, batch);
            }
            using var request = Request(HttpMethod.Post, "api.php", _credentials);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            var receiptText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest && LegacyPayload(payload) != payload)
            {
                using var retry = Request(HttpMethod.Post, "api.php", _credentials);
                retry.Content = new StringContent(LegacyPayload(payload), Encoding.UTF8, "application/json");
                using var retried = await _http.SendAsync(retry).ConfigureAwait(false);
                retried.EnsureSuccessStatusCode();
                receiptText = await retried.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            else response.EnsureSuccessStatusCode();
            using var receipt = JsonDocument.Parse(receiptText);
            var historyAccepted = receipt.RootElement.TryGetProperty("historyAccepted", out var accepted) && accepted.ValueKind == JsonValueKind.True;
            Status = historyAccepted ? "用量與通知紀錄已同步 " + DateTime.Now.ToString("HH:mm:ss") : "用量已同步；舊手機服務，通知歷史待更新";
            using var subscriptionRequest = Request(HttpMethod.Get, "api.php?op=subscriptions", _credentials);
            using var subscriptionResponse = await _http.SendAsync(subscriptionRequest).ConfigureAwait(false);
            subscriptionResponse.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await subscriptionResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            foreach (var item in batch)
            {
                if (item.PushCompleted) { if (historyAccepted) _events.Acknowledge(item); continue; }
                if (!CanDeliver(item, filtered, settings)) continue;
                var complete = true;
                foreach (var sub in doc.RootElement.GetProperty("subscriptions").EnumerateArray())
                {
                    if (!Wants(sub, item)) continue;
                    var endpoint = sub.GetProperty("endpoint").GetString()!;
                    if (!AllowedPushEndpoint(endpoint)) continue;
                    try
                    {
                        using var pushHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                            { Timeout = TimeSpan.FromSeconds(10) };
                        using var push = new WebPushClient(pushHttp);
                        var subscription = new PushSubscription(endpoint, sub.GetProperty("keys").GetProperty("p256dh").GetString()!,
                            sub.GetProperty("keys").GetProperty("auth").GetString()!);
                        var message = BuildNotification(item, filtered, DateTimeOffset.UtcNow);
                        if (_credentials.AccountId is not null) message["account"] = _credentials.AccountId;
                        message["imageEnabled"] = !sub.GetProperty("preferences").TryGetProperty("images", out var images) || images.ValueKind == JsonValueKind.True;
                        await push.SendNotificationAsync(subscription, message.ToJsonString(),
                            new WebPushOptions { VapidDetails = new VapidDetails(ServiceUrl(_credentials), _credentials.PublicKey, _credentials.PrivateKey),
                                ContentEncoding = ContentEncoding.Aes128gcm, Urgency = Urgency.High, TTL = 300 }).ConfigureAwait(false);
                    }
                    catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                    {
                        using var prune = Request(HttpMethod.Delete, "api.php?op=subscriptions", _credentials);
                        prune.Content = new StringContent(new JsonObject { ["endpoint"] = endpoint }.ToJsonString(), Encoding.UTF8, "application/json");
                        using var removed = await _http.SendAsync(prune).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        complete = false;
                        DiagnosticsService.Write("Mobile push failed: " + ex.GetType().Name +
                            (ex is WebPushException error ? " HTTP " + (int)error.StatusCode : "") + ". Unsent work retained up to 24h.");
                    }
                }
                if (complete) { if (historyAccepted) _events.Acknowledge(item); else _events.MarkPushCompleted(item); }
                else Status = historyAccepted ? "通知紀錄已同步，推播待重試" : "舊手機服務，推播待重試；歷史待更新";
            }
            await _resetNews.PollAsync(_http, _credentials, doc.RootElement.GetProperty("subscriptions"), Root).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status = "同步失敗，稍後重試";
            DiagnosticsService.Write("Personal mobile sync failed: " + ex.GetType().Name);
        }
        finally { _gate.Release(); }
    }

    internal static bool AllowedPushEndpoint(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host == "fcm.googleapis.com" || uri.Host == "updates.push.services.mozilla.com" ||
         uri.Host == "web.push.apple.com" || uri.Host.EndsWith(".push.apple.com", StringComparison.Ordinal) ||
         uri.Host == "wns.windows.com" || uri.Host.EndsWith(".notify.windows.com", StringComparison.Ordinal));

    internal static bool CanDeliver(MiniUsageAnnouncement item, UsageSnapshot snapshot, AppSettings settings)
    {
        if (item.Provider == "Codex")
            return settings.CodexDataEnabled && (item.Kind == MiniUsageAnnouncementKind.ResetCreditsAdded
                ? !snapshot.CodexResetCredits.IsStale && snapshot.CodexResetCredits.SyncedAt is not null
                : UsageFreshness.State(snapshot.Codex) == UsageDataState.Fresh);
        return item.Provider == "Claude Code" && settings.ClaudeDataEnabled && UsageFreshness.State(snapshot.Claude) == UsageDataState.Fresh;
    }

    private static bool Wants(JsonElement sub, MiniUsageAnnouncement item)
    {
        var p = sub.GetProperty("preferences");
        bool Flag(string name) => p.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        if (!Flag(item.Provider == "Codex" ? "codex" : "claude")) return false;
        return item.Kind switch {
            MiniUsageAnnouncementKind.UsageMilestone => Flag("milestones"),
            MiniUsageAnnouncementKind.LowRemaining => Flag("low"), _ => Flag("reset")
        };
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path, PersonalMobileCredentials credentials)
    {
        var request = new HttpRequestMessage(method, ServiceUrl(credentials) + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.WriteToken);
        if (credentials.AccountId is not null) request.Headers.Add("X-Usage-Account", credentials.AccountId);
        return request;
    }
    public void Dispose() => _http.Dispose();
}
