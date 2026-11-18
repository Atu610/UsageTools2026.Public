namespace UsageTools2026.Services;

internal static class UpdateConfiguration
{
    internal const string FeedUrl = "";
    internal const string RsaPublicKeyPem = "";
    internal const string Channel = "stable";

    // The signed manifest is only a few kilobytes. A 15-minute cadence keeps
    // releases visible promptly without creating meaningful client or host load.
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    internal static bool IsConfigured =>
        Uri.TryCreate(FeedUrl, UriKind.Absolute, out var feedUri) &&
        feedUri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(RsaPublicKeyPem);
}
