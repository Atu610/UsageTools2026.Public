using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

/// <summary>
/// A tiny local HTTP server that serves a phone-optimized usage dashboard.
///
/// - Same Wi-Fi: open http://&lt;pc-lan-ip&gt;:&lt;port&gt;/?key=&lt;key&gt; on the phone.
/// - Away: expose this server via Cloudflare Tunnel / Tailscale (data stays on the PC, nothing
///   is pushed to any cloud storage).
///
/// Protected by a per-machine random access key. It only ever exposes usage PERCENTAGES and
/// system metrics — never cookies, tokens, or any Claude/Codex credential.
/// </summary>
public sealed class MobileServerService
{
    private readonly int _port;
    private readonly string _accessKey;
    private HttpListener? _listener;
    private Thread? _worker;
    private volatile string _payloadJson = "{}";

    public MobileServerService(int port, string accessKey)
    {
        _port = port;
        _accessKey = accessKey;
    }

    public bool IsRunning { get; private set; }

    public void UpdateSnapshot(UsageSnapshot snapshot, SystemUsage system)
    {
        _payloadJson = BuildJson(snapshot, system);
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            EnsureFirewallRule();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            IsRunning = true;

            _worker = new Thread(Loop) { IsBackground = true, Name = "UsageTools-MobileServer" };
            _worker.Start();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            DiagnosticsService.Write("Mobile server failed to start: " + ex.Message);
        }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
    }

    private void Loop()
    {
        while (IsRunning && _listener is not null)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch
            {
                break;
            }

            try
            {
                Handle(context);
            }
            catch
            {
                // never let one bad request kill the server loop
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";
        var key = request.QueryString["key"] ?? string.Empty;

        // Static page itself is harmless (no data); the data API requires the key.
        if (path is "/" or "/index.html")
        {
            WriteText(response, 200, "text/html; charset=utf-8", PageHtml);
            return;
        }

        if (path == "/api/usage")
        {
            if (!IsAuthorized(key))
            {
                WriteText(response, 401, "application/json", "{\"error\":\"unauthorized\"}");
                return;
            }

            WriteText(response, 200, "application/json; charset=utf-8", _payloadJson);
            return;
        }

        WriteText(response, 404, "text/plain", "not found");
    }

    private bool IsAuthorized(string key)
    {
        return !string.IsNullOrEmpty(_accessKey) &&
               string.Equals(key, _accessKey, StringComparison.Ordinal);
    }

    private static void WriteText(HttpListenerResponse response, int status, string contentType, string body)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.Headers["Cache-Control"] = "no-store";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // client may have disconnected
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { /* ignore */ }
        }
    }

    private static string BuildJson(UsageSnapshot snapshot, SystemUsage system)
    {
        static string? Reset(DateTimeOffset? value) =>
            value?.LocalDateTime.ToString("MM/dd HH:mm");

        var codex = snapshot.Codex;
        var claude = snapshot.Claude;

        var payload = new
        {
            updatedAt = snapshot.LastUpdatedLocal?.LocalDateTime.ToString("MM/dd HH:mm:ss"),
            codex = new
            {
                has = codex.HasData,
                stale = codex.IsStale,
                activeWork = snapshot.CodexWorkWindows.Count > 0,
                primaryLabel = DisplayFormatService.FormatWindowLabel(codex.PrimaryWindowMinutes, "5h"),
                secondaryLabel = DisplayFormatService.FormatWindowLabel(codex.SecondaryWindowMinutes, "7d"),
                fiveHour = codex.PrimaryUsedPercent,
                sevenDay = codex.SecondaryUsedPercent,
                fiveReset = Reset(codex.PrimaryResetsAt),
                sevenReset = Reset(codex.SecondaryResetsAt)
            },
            claude = new
            {
                has = claude.HasLimitData,
                state = claude.SyncState.ToString(),
                fiveHour = claude.PrimaryUsedPercent,
                sevenDay = claude.SecondaryUsedPercent,
                fiveReset = Reset(claude.PrimaryResetsAt),
                sevenReset = Reset(claude.SecondaryResetsAt),
                syncedAt = claude.OfficialSyncedAt?.LocalDateTime.ToString("MM/dd HH:mm")
            },
            system = new
            {
                cpu = Math.Round(system.CpuPercent),
                ram = Math.Round(system.RamPercent)
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private void EnsureFirewallRule()
    {
        try
        {
            const string ruleName = "UsageTools2026 Mobile";
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={_port}");
        }
        catch
        {
            // If the firewall rule cannot be added, same-Wi-Fi access may be blocked, but the
            // server still runs (and works over loopback / tunnels).
        }
    }

    private static void RunNetsh(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(4000);
        }
        catch
        {
            // best-effort
        }
    }

    // Phone dashboard. Static, self-contained, dark theme, Codex blue / Claude orange.
    // Reads ?key= from its own URL and polls /api/usage every 8s.
    private const string PageHtml = """
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<title>UsageTools 2026</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body { margin:0; padding:16px; font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;
         background:#0a0e12; color:#eef2f4; }
  h1 { font-size:15px; font-weight:600; color:#8b97a0; margin:0 0 14px; display:flex; justify-content:space-between; align-items:center; }
  .dot { width:9px; height:9px; border-radius:50%; display:inline-block; margin-left:8px; }
  .card { background:#141a21; border:1px solid #232d36; border-radius:14px; padding:16px; margin-bottom:14px; }
  .name { font-size:19px; font-weight:800; display:flex; align-items:center; }
  .codex { color:#4f9df7; } .claude { color:#e8843d; }
  .row { margin-top:14px; }
  .lbl { display:flex; justify-content:space-between; font-size:13px; font-weight:700; }
  .lbl .muted { color:#7f8a92; font-weight:600; }
  .pct { font-weight:800; }
  .bar { height:8px; border-radius:5px; background:#27313a; margin-top:6px; overflow:hidden; }
  .fill { height:100%; border-radius:5px; transition:width .4s; }
  .meta { font-size:11px; color:#7f8a92; margin-top:5px; text-align:right; }
  .sys { display:flex; gap:10px; }
  .sys .b { flex:1; background:#141a21; border:1px solid #232d36; border-radius:12px; padding:12px; text-align:center; }
  .sys .v { font-size:22px; font-weight:800; } .sys .k { font-size:11px; color:#7f8a92; margin-top:2px; }
  .foot { text-align:center; color:#5a646c; font-size:11px; margin-top:10px; }
  .warn { color:#e8843d; font-size:12px; margin-top:8px; }
</style>
</head>
<body>
  <h1><span>UsageTools 2026</span><span id="ago">—</span></h1>
  <div class="card">
    <div class="name codex">Codex<span class="dot" id="cxDot"></span></div>
    <div class="row" id="cx5row"><div class="lbl"><span class="muted" id="cx5l">Session 5h</span><span class="pct" id="cx5p">—</span></div>
      <div class="bar"><div class="fill" id="cx5b" style="width:0%;background:#4f9df7"></div></div>
      <div class="meta" id="cx5r"></div></div>
    <div class="row" id="cx7row"><div class="lbl"><span class="muted" id="cx7l">本週 7d</span><span class="pct" id="cx7p">—</span></div>
      <div class="bar"><div class="fill" id="cx7b" style="width:0%;background:#4f9df7"></div></div>
      <div class="meta" id="cx7r"></div></div>
  </div>
  <div class="card">
    <div class="name claude">Claude Code<span class="dot" id="clDot"></span></div>
    <div id="clBody">
      <div class="row"><div class="lbl"><span class="muted">Session 5h</span><span class="pct" id="cl5p">—</span></div>
        <div class="bar"><div class="fill" id="cl5b" style="width:0%;background:#e8843d"></div></div>
        <div class="meta" id="cl5r"></div></div>
      <div class="row"><div class="lbl"><span class="muted">本週 7d</span><span class="pct" id="cl7p">—</span></div>
        <div class="bar"><div class="fill" id="cl7b" style="width:0%;background:#e8843d"></div></div>
        <div class="meta" id="cl7r"></div></div>
    </div>
    <div class="warn" id="clWarn" style="display:none"></div>
  </div>
  <div class="sys">
    <div class="b"><div class="v" id="cpu">—</div><div class="k">CPU</div></div>
    <div class="b"><div class="v" id="ram">—</div><div class="k">RAM</div></div>
  </div>
  <div class="foot" id="foot">連線中…</div>
<script>
  var KEY = new URLSearchParams(location.search).get('key') || '';
  function col(p){ return p>=90?'#ff5555':p>=70?'#f3b05b':p<0?'#7f8a92':(p>=0?null:'#7f8a92'); }
  function setLimit(pPrefix, val, reset, color, activeWork, isCodex){
    var p=document.getElementById(pPrefix+'p'), b=document.getElementById(pPrefix+'b'), r=document.getElementById(pPrefix+'r');
    if(val===null||val===undefined){ p.textContent='—'; b.style.width='0%'; r.textContent=''; return; }
    var v=Math.round(val*10)/10;
    p.textContent=v+'% 已用';
    b.style.width=Math.max(0,Math.min(100,val))+'%';
    var c=val>=90?'#ff5555':val>=70?'#f3b05b':color; b.style.background=c; p.style.color=c;
    if(isCodex && val>=99.95){
      r.textContent=activeWork
        ? '\u5df2\u9054\u4e0a\u9650 \u00b7 \u7576\u524d\u56de\u5408\u53ef\u80fd\u7e7c\u7e8c'+(reset?' \u00b7 \u91cd\u7f6e '+reset:'')
        : '\u5df2\u9054\u4e0a\u9650 \u00b7 \u7b49\u5f85\u91cd\u7f6e'+(reset?' '+reset:'');
    } else {
      r.textContent=reset?('\u91cd\u7f6e '+reset):'';
    }
  }
  function dot(el, ok){ el.style.background = ok?'#35e66b':'#f2f5f4'; }
  function load(){
    fetch('/api/usage?key='+encodeURIComponent(KEY),{cache:'no-store'}).then(function(r){
      if(!r.ok) throw new Error(r.status); return r.json();
    }).then(function(d){
      dot(document.getElementById('cxDot'), d.codex.has);
      document.getElementById('cx5row').style.display=d.codex.fiveHour==null?'none':'block';
      document.getElementById('cx7row').style.display=d.codex.sevenDay==null?'none':'block';
      document.getElementById('cx5l').textContent='Session '+(d.codex.primaryLabel||'5h');
      document.getElementById('cx7l').textContent='\u672c\u9031 '+(d.codex.secondaryLabel||'7d');
      setLimit('cx5', d.codex.fiveHour, d.codex.fiveReset, '#4f9df7', d.codex.activeWork, true);
      setLimit('cx7', d.codex.sevenDay, d.codex.sevenReset, '#4f9df7', d.codex.activeWork, true);
      dot(document.getElementById('clDot'), d.claude.has);
      var body=document.getElementById('clBody'), warn=document.getElementById('clWarn');
      if(d.claude.has){
        body.style.display='block';
        setLimit('cl5', d.claude.fiveHour, d.claude.fiveReset, '#e8843d');
        setLimit('cl7', d.claude.sevenDay, d.claude.sevenReset, '#e8843d');
        warn.style.display = d.claude.state==='CachedSyncFailed' ? 'block':'none';
        warn.textContent = '官方同步於 '+(d.claude.syncedAt||'')+'，最近一次嘗試失敗';
      } else {
        body.style.display='none';
        warn.style.display='block';
        warn.textContent='官方 Usage 尚未同步';
      }
      document.getElementById('cpu').textContent=(d.system.cpu)+'%';
      document.getElementById('ram').textContent=(d.system.ram)+'%';
      document.getElementById('ago').textContent=d.updatedAt||'';
      document.getElementById('foot').textContent='更新於 '+(d.updatedAt||'—');
    }).catch(function(e){
      document.getElementById('foot').textContent = (e.message==='401') ? '密鑰錯誤' : '連線失敗，重試中…';
    });
  }
  load(); setInterval(load, 8000);
  document.addEventListener('visibilitychange', function(){ if(!document.hidden) load(); });
</script>
</body>
</html>
""";
}
