using System.IO;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public static class StageBSelfTest
{
    public static string Run()
    {
        var results = new List<string>();
        var failed = 0;

        Check(results, ref failed, "parser_missing_duplicate_invalid_date", TestParser());
        Check(results, ref failed, "parser_malformed_json", TestMalformedJson());
        Check(results, ref failed, "official_codex_html_parser", TestOfficialCodexHtmlParser());
        Check(results, ref failed, "official_claude_html_parser", TestOfficialClaudeHtmlParser());
        Check(results, ref failed, "atomic_state_roundtrip", TestAtomicStateRoundTrip());
        Check(results, ref failed, "report_html_encode_and_privacy", TestReportPrivacy());
        Check(results, ref failed, "report_unknown_not_guessed", TestUnknownValues());
        Check(results, ref failed, "provider_data_settings_backward_compatible", TestProviderDataSettings());
        Check(results, ref failed, "window_topmost_setting_backward_compatible", TestWindowTopmostSetting());
        Check(results, ref failed, "translation_parser_and_atomic_cache", TestTranslationParserAndAtomicCache());

        results.Add($"SUMMARY passed={failed == 0} checks={results.Count} failed={failed}");
        return string.Join(Environment.NewLine, results);
    }

    private static bool TestParser()
    {
        const string fixture = """
        {
          "schemaVersion": 1,
          "sourceLabel": "offline",
          "isOfflineFixture": true,
          "releases": [
            {
              "provider": "codex",
              "id": "same-id",
              "version": "1.0",
              "surface": "desktop",
              "publishedAt": "not-a-date",
              "title": "First",
              "summary": "kept",
              "sourceUrl": "https://learn.chatgpt.com/docs/changelog"
            },
            {
              "provider": "codex",
              "id": "same-id",
              "version": "2.0",
              "title": "Duplicate"
            },
            {
              "provider": "claude-code",
              "id": "claude-1",
              "version": "3.0",
              "title": "Claude",
              "summary": "valid",
              "sourceUrl": "https://malicious.example/update"
            },
            {
              "provider": "unknown-ai",
              "id": "unknown-1",
              "version": "1",
              "title": "Unknown"
            },
            {
              "provider": "codex",
              "id": "missing-version",
              "title": "Missing"
            }
          ]
        }
        """;

        var snapshot = AiChangelogService.ParseFixture(fixture);
        var codex = snapshot.ForProvider(AiProvider.Codex).SingleOrDefault();
        var claude = snapshot.ForProvider(AiProvider.ClaudeCode).SingleOrDefault();
        return snapshot.Releases.Count == 2 &&
               codex is { Version: "1.0", PublishedAt: null } &&
               codex.SourceUri?.Host == "learn.chatgpt.com" &&
               claude is not null &&
               claude.SourceUri is null;
    }

    private static bool TestMalformedJson()
    {
        var snapshot = AiChangelogService.ParseFixture("{not-json");
        return snapshot.Releases.Count == 0 &&
               snapshot.SourceLabel == "UNKNOWN / 未提供";
    }

    private static bool TestOfficialCodexHtmlParser()
    {
        const string html = """
        <li id="codex-2026-08-25-browser" class="scroll-mt-28">
          <div>
            <time class="text-sm text-secondary">2026-08-25</time>
            <h3 class="heading-xl"><span>Browser extensions, site tools, and cloud sign-in</span><button>copy</button></h3>
          </div>
          <article class="prose-content">
            <ul><li><p><strong>More browsers:</strong> Use the ChatGPT browser extension in Microsoft Edge, Brave, Opera, and Vivaldi.</p></li></ul>
            <script>ignore()</script>
          </article>
        </li>
        <li id="codex-2026-08-25-browser"><article>
          <time>bad date</time><h3><span>Duplicate</span><button>copy</button></h3>
        </article></li>
        """;

        var release = AiChangelogService.ParseOfficialCodexHtml(html).SingleOrDefault();
        return release is { Provider: AiProvider.Codex, Version: "未提供", PublishedAt: not null } &&
               release.SourceUri?.Host == "learn.chatgpt.com" &&
               release.Summary.Contains("More browsers", StringComparison.Ordinal) &&
               !release.Summary.Contains("官方未提供摘要", StringComparison.Ordinal) &&
               !release.Summary.Contains("ignore", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TestOfficialClaudeHtmlParser()
    {
        const string html = """
        <article>
          <h3>July 14, 2026</h3>
          <p><b>Claude Code desktop model update</b></p>
          <p>Claude Code adds safer model selection for coding work.</p>
          <h3>July 10, 2026</h3>
          <p><b>Unrelated billing notice</b></p><p>General account information only.</p>
        </article>
        """;

        var release = AiChangelogService.ParseOfficialClaudeHtml(html).SingleOrDefault();
        return release is { Provider: AiProvider.ClaudeCode, PublishedAt: not null } &&
               release.SourceUri?.Host == "support.claude.com" &&
               release.Title.Contains("Claude Code", StringComparison.Ordinal);
    }

    private static bool TestAtomicStateRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageTools2026-stage-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "ai-changelog-state.json");
            var first = new AiChangelogStateDocument
            {
                ReadReleaseIds = ["release-a"]
            };
            AiChangelogService.SaveStateAtomic(path, first);

            var second = new AiChangelogStateDocument
            {
                ReadReleaseIds = ["release-a", "release-b"],
                LastReadAt = DateTimeOffset.UtcNow
            };
            AiChangelogService.SaveStateAtomic(path, second);

            var parsed = JsonSerializer.Deserialize<AiChangelogStateDocument>(File.ReadAllText(path));
            return parsed?.ReadReleaseIds.SequenceEqual(["release-a", "release-b"]) == true &&
                   Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Length == 0;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool TestReportPrivacy()
    {
        var maliciousProject = "<script>alert('x')</script> D:\\Private\\Client\\secret.txt dev@example.com token=very-secret-value";
        var work = new CodexWorkWindow(
            maliciousProject,
            "session",
            100,
            1000,
            DateTimeOffset.Now,
            "gpt-test",
            "High",
            "Authorization: Bearer abcdefghijklmnopqrstuvwxyz");
        var snapshot = new UsageSnapshot(
            DateTimeOffset.Now,
            new CodexUsage(10, 300, DateTimeOffset.Now.AddHours(1), 20, 10080, DateTimeOffset.Now.AddDays(1), false, true),
            new ClaudeUsage(true, DateTimeOffset.Now, "cookie=session-secret dev@example.com", 5, 6, true),
            new TokenTotals(1, 2, 3, 4, 10, 1),
            [new ProjectUsage(maliciousProject, 10)],
            [work],
            [],
            CodexResetCreditSummary.Empty);
        var release = new AiReleaseInfo(
            AiProvider.Codex,
            "malicious",
            "1",
            "desktop",
            "<img src=x onerror=alert(1)>",
            maliciousProject,
            null,
            null);
        var context = new ReportContext(
            snapshot,
            new AppSettings(),
            new AiChangelogSnapshot([release], "offline", true, DateTimeOffset.Now),
            new HashSet<string>(),
            "1.0",
            null,
            DateTimeOffset.Now);

        var html = ReportService.BuildHtml(context);
        return !html.Contains("<script>", StringComparison.OrdinalIgnoreCase) &&
               !html.Contains("<img src=x", StringComparison.OrdinalIgnoreCase) &&
               html.Contains("&lt;script&gt;", StringComparison.Ordinal) &&
               !html.Contains("D:\\Private", StringComparison.OrdinalIgnoreCase) &&
               !html.Contains("dev@example.com", StringComparison.OrdinalIgnoreCase) &&
               !html.Contains("very-secret-value", StringComparison.Ordinal) &&
               !html.Contains("abcdefghijklmnopqrstuvwxyz", StringComparison.Ordinal);
    }

    private static bool TestUnknownValues()
    {
        var context = new ReportContext(
            UsageSnapshot.Empty,
            new AppSettings(),
            AiChangelogSnapshot.Empty,
            new HashSet<string>(),
            "1.0",
            null,
            DateTimeOffset.Now);
        var html = ReportService.BuildHtml(context);
        return html.Contains("UNKNOWN / 未提供", StringComparison.Ordinal) &&
               !html.Contains(">0%<", StringComparison.Ordinal);
    }

    private static bool TestProviderDataSettings()
    {
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}");
        var disabled = JsonSerializer.Deserialize<AppSettings>(
            "{\"CodexDataEnabled\":false,\"ClaudeDataEnabled\":false}");
        return defaults is { CodexDataEnabled: true, ClaudeDataEnabled: true } &&
               disabled is { CodexDataEnabled: false, ClaudeDataEnabled: false };
    }

    private static bool TestWindowTopmostSetting()
    {
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}");
        var disabled = JsonSerializer.Deserialize<AppSettings>("{\"KeepWindowOnTop\":false}");
        return defaults is { KeepWindowOnTop: true } &&
               disabled is { KeepWindowOnTop: false };
    }

    private static bool TestTranslationParserAndAtomicCache()
    {
        const string response = "[[[\"瀏覽器擴充功能\",\"Browser extensions\",null,null,10],[\"與雲端登入\",\"and cloud sign-in\",null,null,10]],null,\"en\"]";
        var parsed = PublicReleaseTranslationService.ParseTranslationResponse(response);
        var root = Path.Combine(Path.GetTempPath(), "UsageTools2026-translation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "translations.json");
            var cache = new TranslationCacheDocument
            {
                Translations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fixture"] = parsed ?? string.Empty
                }
            };
            PublicReleaseTranslationService.SaveCacheAtomic(path, cache);
            var roundTrip = JsonSerializer.Deserialize<TranslationCacheDocument>(File.ReadAllText(path));
            return parsed == "瀏覽器擴充功能與雲端登入" &&
                   roundTrip?.Translations.GetValueOrDefault("fixture") == parsed &&
                   Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Length == 0;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void Check(List<string> results, ref int failed, string name, bool passed)
    {
        if (!passed)
        {
            failed++;
        }

        results.Add($"{(passed ? "PASS" : "FAIL")} {name}");
    }
}
