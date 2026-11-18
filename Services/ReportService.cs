using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public static class ReportService
{
    private const string Unknown = "UNKNOWN / 未提供";

    private static readonly Regex EmailRegex = new(
        @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsPathRegex = new(
        @"\b[A-Z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnixPathRegex = new(
        @"/(?:home|Users|var|tmp|opt)/[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveAssignmentRegex = new(
        @"(?i)\b(authorization|access[_-]?token|refresh[_-]?token|token|cookie|secret|api[_-]?key|password)\s*[:=]\s*[^\s<]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerRegex = new(
        @"(?i)\bbearer\s+[A-Z0-9._~+/\-=]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void OpenLatestReport(ReportContext context)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "UsageTools2026.Public",
            "Reports");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"usage-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        File.WriteAllText(path, BuildHtml(context), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    internal static string BuildHtml(ReportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var snapshot = context.Snapshot;
        var projects = BuildProjectItems(snapshot.TopProjects);
        var codexWork = BuildWorkItems(snapshot.CodexWorkWindows, "codex");
        var claudeWork = BuildWorkItems(snapshot.ClaudeWorkWindows, "claude");
        var codexUsageCards = BuildCodexUsageCards(snapshot.Codex);
        var resetCredits = BuildResetCredits(snapshot.CodexResetCredits);
        var aiUpdates = BuildAiUpdates(context);
        var appUpdate = BuildApplicationUpdate(context);

        return $$"""
<!doctype html>
<html lang="zh-Hant">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>UsageTools2026 Report</title>
  <style>
    :root { color-scheme: dark; }
    * { box-sizing: border-box; }
    body { margin: 0; font-family: "Segoe UI", "Microsoft JhengHei", sans-serif; background: #0d1217; color: #eaf0f4; }
    main { max-width: 1120px; margin: 34px auto; padding: 0 22px 36px; }
    h1 { margin: 0; font-size: 28px; }
    h2 { margin: 0 0 14px; font-size: 19px; }
    h3 { margin: 0 0 10px; font-size: 15px; }
    p { line-height: 1.55; }
    .muted { color: #94a4af; }
    .eyebrow { margin-bottom: 7px; color: #63b3ff; font-size: 12px; font-weight: 700; text-transform: uppercase; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(185px, 1fr)); gap: 12px; margin-top: 18px; }
    .two { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; margin-top: 12px; }
    .card { border: 1px solid #2d3c47; border-radius: 8px; background: #151d24; padding: 17px; }
    .value { margin-top: 7px; font-size: 24px; font-weight: 700; overflow-wrap: anywhere; }
    .compact { font-size: 16px; }
    ul { list-style: none; padding: 0; margin: 10px 0 0; }
    li { display: flex; justify-content: space-between; gap: 14px; padding: 9px 0; border-bottom: 1px solid #26343e; }
    li:last-child { border-bottom: 0; }
    li span { min-width: 0; overflow-wrap: anywhere; }
    .work, .release { padding: 11px 0; border-bottom: 1px solid #26343e; }
    .work:last-child, .release:last-child { border-bottom: 0; padding-bottom: 0; }
    .work:first-child, .release:first-child { padding-top: 0; }
    .work-head, .release-head { display: flex; justify-content: space-between; gap: 14px; align-items: baseline; }
    .work-head strong, .release-head strong { min-width: 0; overflow-wrap: anywhere; }
    .bar { height: 6px; margin: 8px 0 7px; overflow: hidden; background: #27343d; border-radius: 3px; }
    .bar span { display: block; height: 100%; border-radius: inherit; }
    .codex .bar span { background: #4f9df7; }
    .claude .bar span { background: #e8843d; }
    .meta { color: #94a4af; font-size: 12px; line-height: 1.55; overflow-wrap: anywhere; }
    .pill { display: inline-block; margin: 3px 5px 3px 0; padding: 3px 7px; border: 1px solid #3a4c59; border-radius: 4px; color: #c9d5dc; font-size: 11px; }
    .warning { color: #ffd178; }
    .note { margin: 10px 0 0; color: #94a4af; font-size: 12px; }
    @media (max-width: 760px) { .two { grid-template-columns: 1fr; } }
  </style>
</head>
<body>
  <main>
    <div class="eyebrow">本機診斷報告</div>
    <h1>UsageTools2026 Report</h1>
    <div class="muted">產生時間 {{E(context.GeneratedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss"))}} · 版本 {{E(context.ApplicationVersion)}}</div>

    <section class="grid">
      <div class="card"><div class="muted">今日 Codex Tokens</div><div class="value">{{snapshot.Today.TotalTokens:N0}}</div></div>
      {{codexUsageCards}}
      {{BuildUsageCard("Claude Code", "5h", snapshot.Claude.PrimaryUsedPercent, snapshot.Claude.PrimaryResetsAt)}}
      {{BuildUsageCard("Claude Code", "7d", snapshot.Claude.SecondaryUsedPercent, snapshot.Claude.SecondaryResetsAt)}}
      {{BuildUsageCard("Claude Code", "Fable 5", snapshot.Claude.FableUsedPercent, snapshot.Claude.FableResetsAt)}}
    </section>

    <section class="two">
      <div class="card">
        <h2>方案與同步</h2>
        <p><strong>Codex：</strong>{{E(PlanName(SubscriptionProvider.Codex, snapshot.Codex.PlanType))}}</p>
        <p><strong>Claude Code：</strong>{{E(PlanName(SubscriptionProvider.Claude, snapshot.Claude.PlanType))}}</p>
        <p class="meta">Codex 狀態：{{E(BuildCodexStatus(snapshot))}}</p>
        <p class="meta">Claude 狀態：{{E(snapshot.Claude.StatusDetail)}}</p>
        <p class="meta">Claude 官方同步：{{E(FormatDate(snapshot.Claude.OfficialSyncedAt))}}</p>
      </div>
      <div class="card">
        <h2>Codex 重置額度</h2>
        {{resetCredits}}
      </div>
    </section>

    <section class="card" style="margin-top: 12px;">
      <h2>今日專案用量</h2>
      <ul>{{projects}}</ul>
      <p class="note">Claude Code 官方資料目前未提供可靠的每日分專案 token 總計，因此不推算、不補值。</p>
    </section>

    <section class="two">
      <div class="card codex">
        <h2>目前 Codex 工作</h2>
        {{codexWork}}
      </div>
      <div class="card claude">
        <h2>目前 Claude Code 工作</h2>
        {{claudeWork}}
      </div>
    </section>

    <section class="card" style="margin-top: 12px;">
      <h2>UsageTools2026 版本</h2>
      {{appUpdate}}
    </section>

    <section class="card" style="margin-top: 12px;">
      <h2>AI 更新資訊</h2>
      {{aiUpdates}}
      <p class="note">Stage B 僅使用內嵌離線 fixture，未連線至 OpenAI、Anthropic、OVH 或正式 feed。</p>
    </section>

    <section class="card" style="margin-top: 12px;">
      <h2>資料範圍與限制</h2>
      <ul>
        <li><span>Codex 最近 token 事件</span><strong>{{E(FormatDate(snapshot.LastUpdatedLocal))}}</strong></li>
        <li><span>Codex 專案資料</span><strong>{{E(snapshot.TopProjects.Count > 0 ? "本機事件彙整" : Unknown)}}</strong></li>
        <li><span>Codex 用量資料</span><strong>{{E(UsageFreshness.Describe(UsageFreshness.State(snapshot.Codex, context.Settings.CodexDataEnabled), snapshot.Codex.OfficialSyncedAt, true))}}</strong></li>
        <li><span>Claude 用量資料</span><strong>{{E(UsageFreshness.Describe(UsageFreshness.State(snapshot.Claude, context.Settings.ClaudeDataEnabled), snapshot.Claude.OfficialSyncedAt, true))}}</strong></li>
        <li><span>AI 更新來源</span><strong>{{E(context.AiChangelog.SourceLabel)}}</strong></li>
      </ul>
      <p class="note">查不到的方案、模型、重置時間或更新內容一律顯示 UNKNOWN／未提供；報告不記錄 Cookie、Session Token、Authorization 或完整帳號資料。</p>
    </section>
  </main>
</body>
</html>
""";
    }

    internal static string SanitizeForReport(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unknown;
        }

        var sanitized = value.Replace('\0', ' ').Trim();
        sanitized = EmailRegex.Replace(sanitized, "[Email 已隱藏]");
        sanitized = BearerRegex.Replace(sanitized, "Bearer [敏感資訊已隱藏]");
        sanitized = SensitiveAssignmentRegex.Replace(sanitized, "$1=[敏感資訊已隱藏]");
        sanitized = WindowsPathRegex.Replace(sanitized, "[本機路徑已隱藏]");
        sanitized = UnixPathRegex.Replace(sanitized, "[本機路徑已隱藏]");
        return sanitized;
    }

    private static string BuildUsageCard(
        string provider,
        string window,
        double? usedPercent,
        DateTimeOffset? resetsAt)
    {
        var value = FormatPercent(usedPercent);
        var reset = FormatDate(resetsAt);
        return $"<div class=\"card\"><div class=\"muted\">{E(provider)} {E(window)} 已用</div>" +
               $"<div class=\"value\">{E(value)}</div><div class=\"meta\">重置：{E(reset)}</div></div>";
    }

    private static string BuildCodexUsageCards(CodexUsage codex)
    {
        var cards = new List<string>();
        if (codex.PrimaryUsedPercent is not null)
        {
            cards.Add(BuildUsageCard(
                "Codex",
                DisplayFormatService.FormatWindowLabel(codex.PrimaryWindowMinutes, "5h"),
                codex.PrimaryUsedPercent,
                codex.PrimaryResetsAt));
        }

        if (codex.SecondaryUsedPercent is not null)
        {
            cards.Add(BuildUsageCard(
                "Codex",
                DisplayFormatService.FormatWindowLabel(codex.SecondaryWindowMinutes, "7d"),
                codex.SecondaryUsedPercent,
                codex.SecondaryResetsAt));
        }

        return cards.Count > 0
            ? string.Join(Environment.NewLine, cards)
            : BuildUsageCard("Codex", Unknown, null, null);
    }

    private static string BuildProjectItems(IReadOnlyList<ProjectUsage> projects)
    {
        if (projects.Count == 0)
        {
            return $"<li><span>{E(Unknown)}</span><strong>-</strong></li>";
        }

        return string.Join(Environment.NewLine, projects.Select(project =>
            $"<li><span>{E(project.Name)}</span><strong>{project.TotalTokens:N0}</strong></li>"));
    }

    private static string BuildWorkItems(IReadOnlyList<CodexWorkWindow> windows, string providerClass)
    {
        if (windows.Count == 0)
        {
            return $"<p class=\"muted\">{E(Unknown)}</p>";
        }

        return string.Join(Environment.NewLine, windows.Take(20).Select(window =>
        {
            var workName = string.IsNullOrWhiteSpace(window.WorkName) ? Unknown : window.WorkName;
            var project = string.IsNullOrWhiteSpace(window.Project) ? Unknown : window.Project;
            var model = string.IsNullOrWhiteSpace(window.Model) ? Unknown : window.Model;
            var effort = string.IsNullOrWhiteSpace(window.Effort) ? Unknown : window.Effort;
            var percent = Math.Clamp(window.Percent, 0, 100);
            var percentCss = ((int)Math.Round(percent)).ToString(CultureInfo.InvariantCulture);

            return $"""
              <div class="work {providerClass}">
                <div class="work-head"><strong>{E(project)} · {E(workName)}</strong><span>{percent:0}%</span></div>
                <div class="bar"><span style="width: {percentCss}%"></span></div>
                <div class="meta">Context {E(FormatTokens(window.UsedTokens))} / {E(FormatTokens(window.ContextWindow))}<br>模型 {E(model)} · Effort {E(effort)}<br>最近活動 {E(FormatDate(window.LastActivity))}</div>
              </div>
              """;
        }));
    }

    private static string BuildResetCredits(CodexResetCreditSummary summary)
    {
        var hasOfficialCount = summary.SyncedAt is not null ||
                               summary.ReportedAvailableCount is not null ||
                               summary.Credits.Count > 0;
        if (!hasOfficialCount)
        {
            return $"<p class=\"muted\">{E(Unknown)}</p>";
        }

        var items = summary.Credits.Count == 0
            ? $"<li><span>到期明細</span><strong>{E(Unknown)}</strong></li>"
            : string.Join(Environment.NewLine, summary.Credits
                .OrderBy(credit => credit.ExpiresAt)
                .Select(credit =>
                    $"<li><span>{E(credit.Title)}</span><strong>{E(credit.ExpiresAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm"))}</strong></li>"));

        return $"""
          <div class="value compact">可用 {summary.AvailableCount} 次</div>
          <div class="meta">同步：{E(FormatDate(summary.SyncedAt))} · {(summary.IsStale ? "本機快取" : "官方資料")}</div>
          <ul>{items}</ul>
          """;
    }

    private static string BuildAiUpdates(ReportContext context)
    {
        if (context.AiChangelog.Releases.Count == 0)
        {
            return $"<p class=\"muted\">{E(Unknown)}</p>";
        }

        return string.Join(Environment.NewLine, context.AiChangelog.Releases.Select(release =>
        {
            var isUnread = !context.ReadAiReleaseIds.Contains(release.Id);
            return $"""
              <div class="release">
                <div class="release-head"><strong>{E(ProviderName(release.Provider))} · {E(release.Title)}</strong><span class="pill">{(isUnread ? "未讀" : "已讀")}</span></div>
                <div class="meta">版本 {E(release.Version)} · {E(release.Surface)} · {E(FormatDate(release.PublishedAt))}</div>
                <p>{E(release.Summary)}</p>
              </div>
              """;
        }));
    }

    private static string BuildApplicationUpdate(ReportContext context)
    {
        if (context.AvailableApplicationUpdate is not { } update)
        {
            return $"""
              <p><strong>目前版本：</strong>{E(context.ApplicationVersion)}</p>
              <p><strong>可用更新：</strong>{E(Unknown)}</p>
              <p class="meta">正式更新歷史尚未提供結構化本機紀錄。</p>
              """;
        }

        return $"""
          <p><strong>目前版本：</strong>{E(context.ApplicationVersion)}</p>
          <p><strong>可用版本：</strong>{E(update.Version.ToString())}</p>
          <p><strong>發布時間：</strong>{E(FormatDate(update.PublishedAtUtc))}</p>
          <p class="meta">{E(update.ReleaseNotes)}</p>
          """;
    }

    private static string PlanName(SubscriptionProvider provider, string? rawPlan)
    {
        var normalized = SubscriptionPlanCatalog.Normalize(provider, rawPlan);
        if (normalized is null)
        {
            return Unknown;
        }

        return SubscriptionPlanCatalog.GetPlans(provider)
                   .FirstOrDefault(plan => string.Equals(plan.Id, normalized, StringComparison.Ordinal))
                   ?.NameZh
               ?? Unknown;
    }

    private static string BuildCodexStatus(UsageSnapshot snapshot)
    {
        if (!snapshot.Codex.HasData)
        {
            return Unknown;
        }

        var max = Math.Max(snapshot.Codex.PrimaryUsedPercent ?? 0, snapshot.Codex.SecondaryUsedPercent ?? 0);
        if (max >= 99.95)
        {
            return snapshot.CodexWorkWindows.Count > 0
                ? "已達上限；偵測到執行中回合，可能在 fair-use 範圍內繼續"
                : "已達上限；等待重置";
        }

        return snapshot.Codex.IsStale ? "STALE / 顯示上次有效資料" : snapshot.Codex.StatusLabel;
    }

    private static string ProviderName(AiProvider provider) =>
        provider == AiProvider.Codex ? "Codex" : "Claude Code";

    private static string FormatPercent(double? percent) =>
        percent is null ? Unknown : $"{Math.Clamp(percent.Value, 0, 100):0.#}%";

    private static string FormatDate(DateTimeOffset? value) =>
        value is null || value == DateTimeOffset.MinValue
            ? Unknown
            : value.Value.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss");

    private static string FormatTokens(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:0}k" : tokens.ToString("N0");

    private static string E(string? value) =>
        WebUtility.HtmlEncode(SanitizeForReport(value));
}
