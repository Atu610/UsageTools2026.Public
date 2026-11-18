using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public sealed class AiChangelogService
{
    private const string FixtureResourceSuffix = "Fixtures.ai-changelog-offline.json";
    private const string CodexChangelogUrl = "https://learn.chatgpt.com/docs/changelog";
    private const string ClaudeReleaseNotesUrl = "https://support.claude.com/en/articles/12138966-release-notes";
    private const int MaximumReleasesPerProvider = 12;
    private static readonly HttpClient PublicHttpClient = CreatePublicHttpClient();
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _statePath;
    private AiChangelogStateDocument _state;

    public AiChangelogService(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsageTools2026.Public",
            "ai-changelog-state.json");
        _state = LoadState(_statePath);
    }

    public IReadOnlySet<string> ReadReleaseIds =>
        new HashSet<string>(_state.ReadReleaseIds, StringComparer.Ordinal);

    public AiChangelogSnapshot LoadCachedOrOfflineFixture() =>
        _state.CachedReleases.Count > 0 ? SnapshotFromCache() : LoadOfflineFixture();

    public AiChangelogSnapshot LoadOfflineFixture()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(FixtureResourceSuffix, StringComparison.Ordinal));
            if (resourceName is null)
            {
                return SnapshotFromCache();
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return SnapshotFromCache();
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var snapshot = ParseFixture(reader.ReadToEnd());
            _state.CachedReleases = snapshot.Releases.ToList();
            _state.CachedSourceLabel = snapshot.SourceLabel;
            _state.CachedIsOfflineFixture = true;
            _state.CachedAt = snapshot.LoadedAt;
            SaveState();
            return snapshot;
        }
        catch
        {
            return SnapshotFromCache();
        }
    }

    public int GetUnreadCount(AiChangelogSnapshot snapshot, AiProvider provider)
    {
        var read = new HashSet<string>(_state.ReadReleaseIds, StringComparer.Ordinal);
        return snapshot.ForProvider(provider).Count(release => !read.Contains(release.Id));
    }

    public void MarkProviderRead(AiChangelogSnapshot snapshot, AiProvider provider)
    {
        var changed = false;
        foreach (var release in snapshot.ForProvider(provider))
        {
            if (!_state.ReadReleaseIds.Contains(release.Id, StringComparer.Ordinal))
            {
                _state.ReadReleaseIds.Add(release.Id);
                changed = true;
            }
        }

        if (changed)
        {
            _state.LastReadAt = DateTimeOffset.UtcNow;
            SaveState();
        }
    }

    /// <summary>
    /// Reads only the public, allow-listed release-note pages. It never uses account cookies,
    /// usage endpoints, or private provider data. A failed provider keeps the previous cache.
    /// </summary>
    public async Task<AiChangelogSnapshot> RefreshOfficialAsync(CancellationToken cancellationToken = default)
    {
        var codexTask = FetchCodexAsync(cancellationToken);
        var claudeTask = FetchClaudeAsync(cancellationToken);
        await Task.WhenAll(codexTask, claudeTask);

        var codex = await codexTask;
        var claude = await claudeTask;
        if (!codex.Succeeded && !claude.Succeeded)
        {
            return SnapshotFromCache();
        }

        var cached = SnapshotFromCache();
        var releases = new List<AiReleaseInfo>();
        releases.AddRange(codex.Succeeded ? codex.Releases : cached.ForProvider(AiProvider.Codex));
        releases.AddRange(claude.Succeeded ? claude.Releases : cached.ForProvider(AiProvider.ClaudeCode));

        var snapshot = new AiChangelogSnapshot(
            releases
                .GroupBy(release => $"{release.Provider}:{release.Id}", StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(release => release.PublishedAt ?? DateTimeOffset.MinValue)
                .ThenBy(release => release.Provider)
                .ToArray(),
            codex.Succeeded && claude.Succeeded
                ? "公開官方來源"
                : "公開官方來源（部分使用本機快取）",
            false,
            DateTimeOffset.UtcNow);

        _state.CachedReleases = snapshot.Releases.ToList();
        _state.CachedSourceLabel = snapshot.SourceLabel;
        _state.CachedIsOfflineFixture = false;
        _state.CachedAt = snapshot.LoadedAt;
        SaveState();
        return snapshot;
    }

    internal static AiChangelogSnapshot ParseFixture(string json)
    {
        try
        {
            return ParseFixtureCore(json);
        }
        catch (JsonException)
        {
            return new AiChangelogSnapshot(
                Array.Empty<AiReleaseInfo>(),
                "UNKNOWN / 未提供",
                true,
                DateTimeOffset.UtcNow);
        }
    }

    private static AiChangelogSnapshot ParseFixtureCore(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var sourceLabel = ReadText(root, "sourceLabel", 100) ?? "UNKNOWN / 未提供";
        var isOffline = !root.TryGetProperty("isOfflineFixture", out var offlineElement) ||
                        offlineElement.ValueKind != JsonValueKind.False;
        var releases = new List<AiReleaseInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("releases", out var releaseArray) &&
            releaseArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in releaseArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryReadProvider(item, out var provider))
                {
                    continue;
                }

                var id = ReadText(item, "id", 120);
                var version = ReadText(item, "version", 80);
                var title = ReadText(item, "title", 180);
                if (id is null || version is null || title is null)
                {
                    continue;
                }

                var dedupeKey = $"{provider}:{id}";
                if (!seen.Add(dedupeKey))
                {
                    continue;
                }

                var surface = ReadText(item, "surface", 100) ?? "UNKNOWN / 未提供";
                var summary = ReadText(item, "summary", 900) ?? "UNKNOWN / 未提供";
                var publishedAt = ReadDate(item, "publishedAt");
                var sourceUri = ReadOfficialSourceUri(item, "sourceUrl");
                releases.Add(new AiReleaseInfo(
                    provider,
                    id,
                    version,
                    surface,
                    title,
                    summary,
                    publishedAt,
                    sourceUri));
            }
        }

        return new AiChangelogSnapshot(
            releases
                .OrderByDescending(release => release.PublishedAt ?? DateTimeOffset.MinValue)
                .ThenBy(release => release.Provider)
                .ToArray(),
            sourceLabel,
            isOffline,
            DateTimeOffset.UtcNow);
    }

    private AiChangelogSnapshot SnapshotFromCache()
    {
        if (_state.CachedReleases.Count == 0)
        {
            return AiChangelogSnapshot.Empty;
        }

        return new AiChangelogSnapshot(
            _state.CachedReleases.ToArray(),
            string.IsNullOrWhiteSpace(_state.CachedSourceLabel)
                ? "本機快取"
                : _state.CachedSourceLabel,
            _state.CachedIsOfflineFixture,
            _state.CachedAt ?? DateTimeOffset.UtcNow);
    }

    private void SaveState() => SaveStateAtomic(_statePath, _state);

    private static AiChangelogStateDocument LoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AiChangelogStateDocument();
            }

            var state = JsonSerializer.Deserialize<AiChangelogStateDocument>(File.ReadAllText(path));
            if (state is null || state.SchemaVersion is < 1 or > 2)
            {
                return new AiChangelogStateDocument();
            }

            if (state.SchemaVersion == 1)
            {
                state.SchemaVersion = 2;
                state.CachedIsOfflineFixture = true;
            }

            state.ReadReleaseIds = state.ReadReleaseIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            state.CachedReleases ??= [];
            return state;
        }
        catch
        {
            return new AiChangelogStateDocument();
        }
    }

    internal static void SaveStateAtomic(string path, AiChangelogStateDocument state)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("AI changelog state path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = path + ".bak";

        try
        {
            var json = JsonSerializer.Serialize(state, StateJsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
                catch (IOException)
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? ReadText(JsonElement parent, string propertyName, int maximumLength)
    {
        if (!parent.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static bool TryReadProvider(JsonElement item, out AiProvider provider)
    {
        var raw = ReadText(item, "provider", 40);
        if (string.Equals(raw, "codex", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.Codex;
            return true;
        }

        if (string.Equals(raw, "claude-code", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "claudecode", StringComparison.OrdinalIgnoreCase))
        {
            provider = AiProvider.ClaudeCode;
            return true;
        }

        provider = default;
        return false;
    }

    private static DateTimeOffset? ReadDate(JsonElement item, string propertyName)
    {
        var raw = ReadText(item, propertyName, 80);
        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static Uri? ReadOfficialSourceUri(JsonElement item, string propertyName)
    {
        var raw = ReadText(item, propertyName, 500);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return uri.Host.ToLowerInvariant() switch
        {
            "learn.chatgpt.com" => uri,
            "developers.openai.com" => uri,
            "help.openai.com" => uri,
            "docs.anthropic.com" => uri,
            "support.claude.com" => uri,
            _ => null
        };
    }

    internal static IReadOnlyList<AiReleaseInfo> ParseOfficialCodexHtml(string html)
    {
        const string entryPattern = "<li\\s+id=\\\"(?<id>codex-[^\\\"]+)\\\"(?<body>.*?)</article>\\s*</li>";
        var releases = new List<AiReleaseInfo>();
        foreach (Match match in Regex.Matches(html, entryPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var id = match.Groups["id"].Value;
            var body = match.Groups["body"].Value;
            var date = ReadHtmlDate(body, "<time[^>]*>(?<value>.*?)</time>");
            var title = ReadHtmlText(body, "<h3[^>]*>.*?<span>(?<value>.*?)</span>\\s*<button", 180);
            if (date is null || title is null)
            {
                continue;
            }

            var summary = ReadHtmlText(body, "<article[^>]*>(?<value>.*)$", 900) ?? "官方未提供摘要";
            var versionMatch = Regex.Match(title, @"\b\d{1,4}\.\d{1,4}(?:\.\d{1,4})?\b");
            releases.Add(new AiReleaseInfo(
                AiProvider.Codex,
                id,
                versionMatch.Success ? versionMatch.Value : "未提供",
                id.Contains("-app", StringComparison.OrdinalIgnoreCase) ? "ChatGPT 桌面版" : "Codex 官方更新",
                title,
                summary,
                date,
                new Uri(CodexChangelogUrl + "#" + id)));
        }

        return NormalizeOfficialReleases(releases, AiProvider.Codex);
    }

    internal static IReadOnlyList<AiReleaseInfo> ParseOfficialClaudeHtml(string html)
    {
        const string entryPattern = "<h3[^>]*>(?<date>.*?)</h3>(?<body>.*?)(?=<h3[^>]*>|</article>)";
        var releases = new List<AiReleaseInfo>();
        var ordinal = 0;
        foreach (Match match in Regex.Matches(html, entryPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var date = TryParsePublicDate(ToPlainText(match.Groups["date"].Value));
            var body = match.Groups["body"].Value;
            var title = ReadHtmlText(body, "<p[^>]*>\\s*<b>(?<value>.*?)</b>", 180)
                        ?? ReadHtmlText(body, "<p[^>]*>(?<value>.*?)</p>", 180);
            var summary = ReadHtmlText(body, "<p[^>]*>(?<value>.*?)</p>", 900);
            if (date is null || title is null || summary is null || !IsClaudeCodeRelevant(title + " " + summary))
            {
                continue;
            }

            ordinal++;
            releases.Add(new AiReleaseInfo(
                AiProvider.ClaudeCode,
                $"claude-{date.Value:yyyy-MM-dd}-{ordinal:D2}",
                "官方更新",
                "Claude Code / Claude 官方更新",
                title,
                summary,
                date,
                new Uri(ClaudeReleaseNotesUrl)));
        }

        return NormalizeOfficialReleases(releases, AiProvider.ClaudeCode);
    }

    private async Task<ProviderFetchResult> FetchCodexAsync(CancellationToken cancellationToken)
    {
        try
        {
            var releases = ParseOfficialCodexHtml(await DownloadPublicHtmlAsync(CodexChangelogUrl, cancellationToken));
            return releases.Count > 0 ? ProviderFetchResult.Success(releases) : ProviderFetchResult.Failure;
        }
        catch
        {
            return ProviderFetchResult.Failure;
        }
    }

    private async Task<ProviderFetchResult> FetchClaudeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var releases = ParseOfficialClaudeHtml(await DownloadPublicHtmlAsync(ClaudeReleaseNotesUrl, cancellationToken));
            return releases.Count > 0 ? ProviderFetchResult.Success(releases) : ProviderFetchResult.Failure;
        }
        catch
        {
            return ProviderFetchResult.Failure;
        }
    }

    private static async Task<string> DownloadPublicHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html");
        request.Headers.UserAgent.ParseAdd("UsageTools2026-AIChangelog/1.0");
        using var response = await PublicHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (html.Length is 0 or > 2_000_000)
        {
            throw new InvalidDataException("Official changelog response size is invalid.");
        }

        return html;
    }

    private static IReadOnlyList<AiReleaseInfo> NormalizeOfficialReleases(IEnumerable<AiReleaseInfo> releases, AiProvider provider)
    {
        return releases
            .Where(release => release.Provider == provider && release.SourceUri is not null &&
                              release.Title.Length > 0 && release.Summary.Length > 0)
            .GroupBy(release => release.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(release => release.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(MaximumReleasesPerProvider)
            .ToArray();
    }

    private static DateTimeOffset? ReadHtmlDate(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? TryParsePublicDate(ToPlainText(match.Groups["value"].Value)) : null;
    }

    private static string? ReadHtmlText(string html, string pattern, int maximumLength)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? NormalizePublicText(ToPlainText(match.Groups["value"].Value), maximumLength) : null;
    }

    private static DateTimeOffset? TryParsePublicDate(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string ToPlainText(string html)
    {
        var withoutExecutableBlocks = Regex.Replace(
            html,
            "<(?:script|style)\\b[^>]*>.*?</(?:script|style)\\s*>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return WebUtility.HtmlDecode(Regex.Replace(withoutExecutableBlocks, "<[^>]+>", " ", RegexOptions.Singleline));
    }

    private static string? NormalizePublicText(string value, int maximumLength)
    {
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        normalized = Regex.Replace(normalized, "[\\u0000-\\u001F\\u007F]", string.Empty);
        normalized = Regex.Replace(normalized, "<[^>]+>", string.Empty, RegexOptions.Singleline);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static bool IsClaudeCodeRelevant(string value) =>
        Regex.IsMatch(value, "claude code|claude desktop|model|opus|sonnet|fable|mythos|coding", RegexOptions.IgnoreCase);

    private static HttpClient CreatePublicHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private sealed record ProviderFetchResult(bool Succeeded, IReadOnlyList<AiReleaseInfo> Releases)
    {
        public static ProviderFetchResult Failure { get; } = new(false, Array.Empty<AiReleaseInfo>());

        public static ProviderFetchResult Success(IReadOnlyList<AiReleaseInfo> releases) => new(true, releases);
    }
}

internal sealed class AiChangelogStateDocument
{
    public int SchemaVersion { get; set; } = 2;

    public List<string> ReadReleaseIds { get; set; } = [];

    public List<AiReleaseInfo> CachedReleases { get; set; } = [];

    public string CachedSourceLabel { get; set; } = string.Empty;

    public bool CachedIsOfflineFixture { get; set; } = true;

    public DateTimeOffset? CachedAt { get; set; }

    public DateTimeOffset? LastReadAt { get; set; }
}
