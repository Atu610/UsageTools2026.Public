using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebPush;
using WebPush.Model;

namespace UsageTools2026.Services;

internal sealed class ResetNewsDeliveryService
{
    private DateTimeOffset _nextPoll;
    internal sealed record Post(string Id, DateTimeOffset At, string Kind, string Text, string? Chinese = null);
    internal sealed record Delivery(string Key, DateTimeOffset At);

    internal static Post[] ReadPosts(JsonElement feed, DateTimeOffset now)
    {
        if (!feed.TryGetProperty("stale", out var stale) || stale.ValueKind != JsonValueKind.False ||
            !feed.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 30)
            return [];
        var posts = new List<Post>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? Value(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var id = Value("id"); var kind = Value("kind"); var text = Value("text");
            if (id is null || !Regex.IsMatch(id, "^[0-9]{10,24}$") || kind is not ("regular" or "banked" or "scheduled" or "watch") ||
                text is null || text.Length > 16000 || Value("url") != "https://x.com/thsottiaux/status/" + id ||
                !DateTimeOffset.TryParse(Value("at"), out var at) || at < now.AddHours(-24) || at > now.AddMinutes(1)) continue;
            var zh = Value("zhText");
            var translated = Value("translationStatus") == "translated" && zh is { Length: > 0 and <= 16000 } && Regex.IsMatch(zh, "[\u3400-\u9FFF]");
            posts.Add(new(id, at, kind, text, translated ? zh : null));
        }
        return posts.DistinctBy(p => p.Id).OrderBy(p => p.At).ToArray();
    }

    internal static bool Eligible(JsonElement sub, Post post)
    {
        return sub.TryGetProperty("preferences", out var prefs) && prefs.TryGetProperty("resetNews", out var flag) &&
            flag.ValueKind == JsonValueKind.True && sub.TryGetProperty("newsSince", out var since) &&
            since.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(since.GetString(), out var at) && post.At > at;
    }

    internal static string DeliveryKey(JsonElement sub, Post post) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(sub.GetProperty("endpoint").GetString() + "|" + sub.GetProperty("keys").GetProperty("p256dh").GetString() + "|" +
            sub.GetProperty("keys").GetProperty("auth").GetString() + "|" + post.Id)));

    internal static JsonObject Message(Post post, string account)
    {
        var excerpt = string.Concat((post.Chinese ?? post.Text).EnumerateRunes().Take(140).Select(r => r.ToString()));
        var label = post.Kind switch { "banked" => "贈送重置券消息", "scheduled" => "重置預告", "watch" => "重置相關討論", _ => "額度重置消息" };
        return new JsonObject {
            ["title"] = "Tibo｜" + label,
            ["body"] = (post.Chinese is null ? "原文（翻譯暫不可用）：" : "自動翻譯：") + excerpt + "\n來源 Codex Resets；不代表帳戶已重置。",
            ["tag"] = "tibo-" + post.Id, ["newsId"] = post.Id, ["kind"] = "ResetNews",
            ["account"] = account, ["imageEnabled"] = false
        };
    }

    public async Task PollAsync(HttpClient http, PersonalMobileCredentials credentials, JsonElement subscriptions, string root)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextPoll) return;
        _nextPoll = now.AddMinutes(5);
        try
        {
            var opted = subscriptions.EnumerateArray().Where(sub => sub.TryGetProperty("preferences", out var prefs) &&
                prefs.TryGetProperty("resetNews", out var flag) && flag.ValueKind == JsonValueKind.True).Take(8).ToArray();
            if (opted.Length == 0) return;
            // Optional news work is bounded and must never change quota-sync success or its outbox.
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var request = PersonalMobileService.Request(HttpMethod.Get, "api.php?op=reset-news", credentials);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(524288, budget.Token).ConfigureAwait(false);
            using var feed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false));
            var posts = ReadPosts(feed.RootElement, now);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.PublicKey)));
            var path = Path.Combine(root, "news-delivery-" + identity + ".json");
            // Only hashes/timestamps for delivery deduplication; no message archive or push credentials.
            var delivered = File.Exists(path) ? JsonSerializer.Deserialize<List<Delivery>>(File.ReadAllText(path)) ?? [] : [];
            delivered.RemoveAll(item => item.At < now.AddDays(-7));
            var attempts = 0;
            foreach (var post in posts)
            foreach (var sub in opted)
            {
                if (!Eligible(sub, post)) continue;
                var endpoint = sub.GetProperty("endpoint").GetString()!;
                if (!PersonalMobileService.AllowedPushEndpoint(endpoint)) continue;
                var key = DeliveryKey(sub, post);
                if (delivered.Any(item => item.Key == key)) continue;
                if (++attempts > 4 || budget.IsCancellationRequested) return;
                try
                {
                    using var pushHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
                    using var push = new WebPushClient(pushHttp);
                    var subscription = new PushSubscription(endpoint, sub.GetProperty("keys").GetProperty("p256dh").GetString()!, sub.GetProperty("keys").GetProperty("auth").GetString()!);
                    await push.SendNotificationAsync(subscription, Message(post, credentials.AccountId ?? "legacy").ToJsonString(),
                        new WebPushOptions { VapidDetails = new VapidDetails(PersonalMobileService.ServiceUrl(credentials), credentials.PublicKey, credentials.PrivateKey),
                            ContentEncoding = ContentEncoding.Aes128gcm, Urgency = Urgency.Normal, TTL = 3600 }, budget.Token).ConfigureAwait(false);
                    delivered.Add(new(key, now));
                    Directory.CreateDirectory(root);
                    AtomicFile.WriteText(path, JsonSerializer.Serialize(delivered.TakeLast(500)));
                }
                catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                {
                    using var prune = PersonalMobileService.Request(HttpMethod.Delete, "api.php?op=subscriptions", credentials);
                    prune.Content = new StringContent(new JsonObject { ["endpoint"] = endpoint }.ToJsonString(), Encoding.UTF8, "application/json");
                    using var removed = await http.SendAsync(prune, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { DiagnosticsService.Write("Reset news push pending: " + ex.GetType().Name); }
            }
        }
        catch (Exception ex) { DiagnosticsService.Write("Reset news refresh skipped: " + ex.GetType().Name); }
    }

    internal static void RunTests(Action<string, bool> check)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new JsonObject { ["id"] = "2109873287853707513", ["at"] = now.ToString("O"), ["kind"] = "regular",
            ["text"] = "Synthetic reset announcement", ["url"] = "https://x.com/thsottiaux/status/2109873287853707513" };
        Post[] Read(bool stale, params JsonObject[] items)
        {
            using var doc = JsonDocument.Parse(new JsonObject { ["stale"] = stale, ["items"] = new JsonArray(items.Select(i => i.DeepClone()).ToArray()) }.ToJsonString());
            return ReadPosts(doc.RootElement, now);
        }
        var post = Read(false, item).Single();
        check("reset_news_stale_not_pushed", Read(true, item).Length == 0);
        check("reset_news_source_dedup", Read(false, item, item).Length == 1);
        var bad = (JsonObject)item.DeepClone(); bad["url"] = "https://evil.test/";
        check("reset_news_wrong_source_rejected", Read(false, bad).Length == 0);
        var old = (JsonObject)item.DeepClone(); old["at"] = now.AddDays(-2).ToString("O");
        check("reset_news_old_posts_not_pushed", Read(false, old).Length == 0);
        using var subscribed = JsonDocument.Parse(new JsonObject { ["preferences"] = new JsonObject { ["resetNews"] = true }, ["newsSince"] = now.AddSeconds(-1).ToString("O") }.ToJsonString());
        using var first = JsonDocument.Parse(new JsonObject { ["preferences"] = new JsonObject { ["resetNews"] = true }, ["newsSince"] = now.AddSeconds(1).ToString("O") }.ToJsonString());
        using var legacy = JsonDocument.Parse("{\"preferences\":{\"reset\":true}}");
        check("reset_news_explicit_opt_in", Eligible(subscribed.RootElement, post) && !Eligible(legacy.RootElement, post));
        check("reset_news_first_enable_skips_old", !Eligible(first.RootElement, post));
        var message = Message(post, new string('a', 64));
        check("reset_news_not_quota_confirmation", message["quota"] is null && message["body"]!.GetValue<string>().Contains("不代表") && message["newsId"]!.GetValue<string>() == post.Id);
        check("reset_news_account_scoped", message["account"]!.GetValue<string>() == new string('a', 64) && Message(post, "legacy")["account"]!.GetValue<string>() == "legacy");
        using var deviceA = JsonDocument.Parse("{\"endpoint\":\"https://web.push.apple.com/a\",\"keys\":{\"auth\":\"a\",\"p256dh\":\"p\"}}");
        using var reordered = JsonDocument.Parse("{\"keys\":{\"p256dh\":\"p\",\"auth\":\"a\"},\"endpoint\":\"https://web.push.apple.com/a\"}");
        using var deviceB = JsonDocument.Parse("{\"endpoint\":\"https://web.push.apple.com/b\",\"keys\":{\"auth\":\"a\",\"p256dh\":\"p\"}}");
        check("reset_news_delivery_key_stable", DeliveryKey(deviceA.RootElement, post) == DeliveryKey(reordered.RootElement, post));
        check("reset_news_delivery_per_device", DeliveryKey(deviceA.RootElement, post) != DeliveryKey(deviceB.RootElement, post));
        var translatedItem = (JsonObject)item.DeepClone(); translatedItem["zhText"] = "這是中文測試消息"; translatedItem["translationStatus"] = "translated";
        check("reset_news_chinese_push_preserves_source", Message(Read(false, translatedItem).Single(), "legacy")["body"]!.GetValue<string>().Contains("自動翻譯：這是中文測試消息"));
        translatedItem["kind"] = "scheduled";
        check("reset_news_scheduled_not_claimed_complete", Message(Read(false, translatedItem).Single(), "legacy")["title"]!.GetValue<string>().Contains("預告"));
        var state = JsonSerializer.Serialize(new List<Delivery> { new("fixture-hash", now) });
        var roundTrip = JsonSerializer.Deserialize<List<Delivery>>(state);
        check("reset_news_delivery_contract_survives_packaging", state.Contains("\"Key\":") && state.Contains("\"At\":") &&
            roundTrip is { Count: 1 } && roundTrip[0].Key == "fixture-hash" && roundTrip[0].At == now);
    }
}
