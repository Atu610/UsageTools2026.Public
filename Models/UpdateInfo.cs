namespace UsageTools2026.Models;

public sealed record UpdateInfo(
    Version Version,
    DateTimeOffset PublishedAtUtc,
    Uri PackageUri,
    string PackageSha256,
    long PackageSize,
    string ReleaseNotes,
    Version? MinimumVersion,
    bool Mandatory);

public sealed record UpdateDownloadProgress(
    long BytesReceived,
    long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0d, 100d);
}
