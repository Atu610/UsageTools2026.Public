using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace UsageTools2026.Services;

/// <summary>
/// Fetches the official Claude usage JSON using an embedded WebView2 (real Chromium engine).
/// The decrypted Claude cookies are injected into the WebView2 cookie store, then the usage
/// endpoint is navigated to. Because this is a genuine Chromium engine, Cloudflare's bot
/// challenge ("Just a moment...") is solved automatically — a plain HttpClient cannot pass it.
///
/// MUST be called on the WPF UI (STA) thread.
/// </summary>
public static class ClaudeWebViewUsageFetcher
{
    private static Window? _loginWindow;
    public static bool IsLoginOpen => _loginWindow is not null;

    public static async Task<bool> ShowLoginAsync()
    {
        if (_loginWindow is not null) { _loginWindow.Activate(); return false; }
        var done = new TaskCompletionSource<bool>();
        var window = new Window { Title = "Claude - 登入後同步用量", Width = Math.Min(900, SystemParameters.WorkArea.Width),
            Height = Math.Min(760, SystemParameters.WorkArea.Height), WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _loginWindow = window;
        var panel = new System.Windows.Controls.DockPanel();
        var finish = new System.Windows.Controls.Button { Content = "完成登入並同步", Height = 38, IsEnabled = false };
        System.Windows.Controls.DockPanel.SetDock(finish, System.Windows.Controls.Dock.Bottom);
        var view = new WebView2();
        panel.Children.Add(finish);
        panel.Children.Add(view);
        window.Content = panel;
        var confirmed = false;
        finish.Click += (_, _) => { confirmed = true; window.Close(); };
        window.Closed += (_, _) => { view.Dispose(); _loginWindow = null; done.TrySetResult(confirmed); };
        window.Show();
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "UsageTools2026-Public-WebView2"));
            if (_loginWindow != window) return false;
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.CoreWebView2.Navigate("https://claude.ai/settings/usage");
            finish.IsEnabled = true;
        }
        catch
        {
            if (_loginWindow == window)
            {
                System.Windows.MessageBox.Show(window, "無法開啟 Claude 登入視窗，請稍後重試。", "UsageTools2026.Public");
                window.Close();
            }
        }
        return await done.Task;
    }

    public sealed record FetchResult(string UsageJson, string? OrganizationJson, string? ModelsJson = null, string? ModelsDiagnostic = null,
        string? Failure = null);

    public static async Task<FetchResult?> TryFetchUsageJsonAsync(ClaudeProfileAccess.CookieSet cookieSet, bool includeModels = false,
        CancellationToken cancellationToken = default)
    {
        // A unique query prevents Chromium's disk cache from replaying an older usage response.
        // Anthropic may still publish accounting updates in batches; this only removes client cache.
        // cedar_ember=1 adds the official limit-reset grants to the same read-only response.
        var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = $"https://claude.ai/api/organizations/{cookieSet.OrganizationId}/usage?cedar_ember=1&ut={cacheBuster}";

        var window = new Window
        {
            Width = 480,
            Height = 360,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Visibility = Visibility.Hidden
        };

        var webview = new WebView2();
        window.Content = webview;
        window.Show();

        try
        {
            var dataFolder = Path.Combine(Path.GetTempPath(), "UsageTools2026-Public-WebView2");
            Directory.CreateDirectory(dataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder).WaitAsync(cancellationToken);
            await webview.EnsureCoreWebView2Async(environment).WaitAsync(cancellationToken);

            var core = webview.CoreWebView2;
            var status = 0;
            var challenge = false;
            core.WebResourceResponseReceived += (_, e) =>
            {
                if (e.Request.Uri.StartsWith(url, StringComparison.Ordinal)) status = e.Response.StatusCode;
            };
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Inject the decrypted Claude cookies so claude.ai treats this as the logged-in session.
            foreach (var pair in cookieSet.Cookies)
            {
                try
                {
                    var cookie = core.CookieManager.CreateCookie(pair.Key, pair.Value, ".claude.ai", "/");
                    cookie.IsSecure = true;
                    core.CookieManager.AddOrUpdateCookie(cookie);
                }
                catch
                {
                    // Skip any cookie the engine rejects; the auth cookie set still applies.
                }
            }

            if (cookieSet.Cookies.Count == 0)
            {
                var stored = await core.CookieManager.GetCookiesAsync("https://claude.ai").WaitAsync(cancellationToken);
                var activeOrg = stored.FirstOrDefault(c => c.Name == "lastActiveOrg")?.Value;
                if (!string.IsNullOrEmpty(activeOrg) && !string.Equals(activeOrg, cookieSet.OrganizationId, StringComparison.OrdinalIgnoreCase))
                    return new FetchResult("", null, Failure: "webview_account_mismatch");
            }

            core.Navigate(url);

            // Poll the document body until the usage JSON (or an auth error) appears, leaving time
            // for Cloudflare's challenge to run. ~20s budget at 500ms intervals.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(500, cancellationToken);

                string raw;
                try
                {
                    raw = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''").WaitAsync(cancellationToken);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(raw) || raw == "null")
                {
                    continue;
                }

                string? text;
                try
                {
                    text = JsonSerializer.Deserialize<string>(raw);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                challenge |= text.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase);
                if (status == 401 || core.Source.Contains("/login", StringComparison.OrdinalIgnoreCase))
                    return new FetchResult("", null, Failure: "webview_not_logged_in http=" + status);

                if (status == 200 && (text.Contains("five_hour") || text.Contains("seven_day")))
                {
                    var organizationUrl = $"https://claude.ai/api/organizations/{cookieSet.OrganizationId}";
                    var organizationJson = await TryFetchTextAsync(core, organizationUrl);
                    var models = includeModels ? await TryFetchModelsAsync(core, cookieSet.OrganizationId) : (Json: (string?)null, Diagnostic: "cached");
                    return new FetchResult(text, organizationJson, models.Json, models.Diagnostic);
                }
            }

            return new FetchResult("", null, Failure: challenge ? "webview_cloudflare http=" + status :
                status == 403 ? "webview_forbidden http=403" : "webview_no_usage http=" + status);
        }
        finally
        {
            try { window.Close(); } catch { /* ignore */ }
            try { webview.Dispose(); } catch { /* ignore */ }
        }
    }

    // /api/account also carries personal profile data, so the page script keeps only the model list
    // of the synced organization; nothing else leaves the WebView.
    internal const string ModelsRequestScript = """
        window.__ut2026Models = undefined;
        (() => {
          const org = __ORG__, kind = x => Array.isArray(x) ? 'array' : x === null ? 'null' : typeof x;
          const keys = x => x && typeof x === 'object' ? Object.keys(x).filter(k => /^[A-Za-z_]{1,60}$/.test(k)).slice(0, 20).join(',') : '';
          fetch('https://claude.ai/api/account', { credentials: 'include', cache: 'no-store' })
            .then(async r => {
              if (!r.ok) return { models: null, diagnostic: 'http=' + r.status };
              const a = await r.json();
              const memberships = a && (Array.isArray(a.memberships) ? a.memberships : a.account && a.account.memberships);
              const m = Array.isArray(memberships) ? memberships.find(x => x && x.organization && x.organization.uuid === org) : null;
              const config = m && m.organization.claude_ai_bootstrap_models_config;
              const c = Array.isArray(config) ? config : null;
              return {
                models: c ? c.slice(0, 40).filter(x => x && typeof x === 'object').map(x => ({ model: x.model, name: x.name, inactive: !!x.inactive, overflow: !!x.overflow })) : null,
                diagnostic: 'http=200 account=' + kind(a && a.account) + ' memberships=' + kind(memberships) +
                  ' rootMemberships=' + kind(a && a.memberships) + ' matched=' + !!m + ' config=' + kind(config) + ' configKeys=' + keys(config)
              };
            }).catch(() => ({ models: null, diagnostic: 'request_or_json_failed' }))
            .then(result => { window.__ut2026Models = JSON.stringify(result); });
        })();
        """;

    private static async Task<(string? Json, string Diagnostic)> TryFetchModelsAsync(CoreWebView2 core, string organizationId)
    {
        try
        {
            var startScript = ModelsRequestScript.Replace("__ORG__", JsonSerializer.Serialize(organizationId), StringComparison.Ordinal);
            await core.ExecuteScriptAsync(startScript);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                var raw = await core.ExecuteScriptAsync(
                    "window.__ut2026Models === undefined ? null : window.__ut2026Models");
                if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                {
                    continue;
                }

                var text = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrWhiteSpace(text)) return (null, "empty_result");
                using var result = JsonDocument.Parse(text);
                var models = result.RootElement.GetProperty("models");
                return (models.ValueKind == JsonValueKind.Array ? models.GetRawText() : null,
                    result.RootElement.GetProperty("diagnostic").GetString() ?? "unknown");
            }

            return (null, "timeout");
        }
        catch
        {
            return (null, "webview_script_failed");
        }
    }

    // ExecuteScriptAsync does NOT await JS promises (a pending fetch serializes as "{}"), so the
    // fetch result is stored on a window global and polled until it resolves.
    private static async Task<string?> TryFetchTextAsync(CoreWebView2 core, string url)
    {
        try
        {
            var startScript =
                "window.__ut2026FetchResult = undefined;" +
                $"fetch({JsonSerializer.Serialize(url)}, {{ credentials: 'include', cache: 'no-store', headers: {{ 'Cache-Control': 'no-cache', 'Pragma': 'no-cache' }} }})" +
                ".then(r => r.ok ? r.text() : '')" +
                ".then(t => { window.__ut2026FetchResult = t; })" +
                ".catch(() => { window.__ut2026FetchResult = ''; });";
            await core.ExecuteScriptAsync(startScript);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                var raw = await core.ExecuteScriptAsync(
                    "window.__ut2026FetchResult === undefined ? null : window.__ut2026FetchResult");
                if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                {
                    continue;
                }

                var text = JsonSerializer.Deserialize<string>(raw);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
