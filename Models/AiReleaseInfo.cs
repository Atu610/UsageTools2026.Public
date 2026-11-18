using UsageTools2026.Services;

namespace UsageTools2026.Models;

public enum AiProvider
{
    Codex,
    ClaudeCode
}

public sealed record AiReleaseInfo(
    AiProvider Provider,
    string Id,
    string Version,
    string Surface,
    string Title,
    string Summary,
    DateTimeOffset? PublishedAt,
    Uri? SourceUri);

public sealed record AiChangelogSnapshot(
    IReadOnlyList<AiReleaseInfo> Releases,
    string SourceLabel,
    bool IsOfflineFixture,
    DateTimeOffset LoadedAt)
{
    public static AiChangelogSnapshot Empty { get; } = new(
        Array.Empty<AiReleaseInfo>(),
        "UNKNOWN / 未提供",
        true,
        DateTimeOffset.MinValue);

    public IReadOnlyList<AiReleaseInfo> ForProvider(AiProvider provider) =>
        Releases
            .Where(release => release.Provider == provider)
            .OrderByDescending(release => release.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record ReportContext(
    UsageSnapshot Snapshot,
    AppSettings Settings,
    AiChangelogSnapshot AiChangelog,
    IReadOnlySet<string> ReadAiReleaseIds,
    string ApplicationVersion,
    UpdateInfo? AvailableApplicationUpdate,
    DateTimeOffset GeneratedAt);
