using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public sealed class UpdateService : IDisposable
{
    private const int MaxManifestBytes = 128 * 1024;
    private const long MaxPackageBytes = 1024L * 1024 * 1024;
    private const int DownloadBufferBytes = 128 * 1024;
    private readonly HttpClient _client;

    public UpdateService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("UsageTools2026-Updater/2026.7");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public bool IsConfigured => UpdateConfiguration.IsConfigured;

    public bool LastCheckSucceeded { get; private set; }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        LastCheckSucceeded = false;
        if (!UpdateConfiguration.IsConfigured)
        {
            return null;
        }

        try
        {
            var feedUri = new Uri(UpdateConfiguration.FeedUrl, UriKind.Absolute);
            using var response = await _client.GetAsync(
                feedUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaxManifestBytes)
            {
                throw new InvalidDataException("Update manifest exceeded the size limit.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxManifestBytes)
            {
                throw new InvalidDataException("Update manifest exceeded the size limit.");
            }

            var envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(bytes, JsonOptions())
                ?? throw new InvalidDataException("Update manifest was empty.");
            var payloadBytes = Convert.FromBase64String(envelope.Payload);
            var signatureBytes = Convert.FromBase64String(envelope.Signature);
            VerifySignature(payloadBytes, signatureBytes);

            var payload = JsonSerializer.Deserialize<UpdatePayload>(payloadBytes, JsonOptions())
                ?? throw new InvalidDataException("Update payload was empty.");
            var update = ValidatePayload(payload, feedUri);
            LastCheckSucceeded = true;
            return update.Version > CurrentVersion() ? update : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write($"Update check failed: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    public async Task<string> DownloadPackageAsync(
        UpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!UpdateConfiguration.IsConfigured)
        {
            throw new InvalidOperationException("The update service is not configured.");
        }

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsageTools2026.Public",
            "Updates",
            update.Version.ToString());
        Directory.CreateDirectory(updateRoot);

        var finalPath = Path.Combine(updateRoot, $"UsageTools2026-{update.Version}.zip");
        var partialPath = finalPath + ".partial";
        if (File.Exists(finalPath))
        {
            if (await VerifyPackageAsync(finalPath, update, cancellationToken))
            {
                progress?.Report(new UpdateDownloadProgress(update.PackageSize, update.PackageSize));
                return finalPath;
            }

            File.Delete(finalPath);
        }

        TryDeleteFile(partialPath);
        try
        {
            using var response = await _client.GetAsync(
                update.PackageUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != update.PackageSize)
            {
                throw new InvalidDataException("Update package size did not match the signed manifest.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                DownloadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferBytes);
            long received = 0;
            long nextReportAt = 0;
            try
            {
                progress?.Report(new UpdateDownloadProgress(0, update.PackageSize));
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, DownloadBufferBytes), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > update.PackageSize || received > MaxPackageBytes)
                    {
                        throw new InvalidDataException("Update package exceeded the signed size.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (received >= nextReportAt || received == update.PackageSize)
                    {
                        progress?.Report(new UpdateDownloadProgress(received, update.PackageSize));
                        nextReportAt = received + 512 * 1024;
                    }
                }

                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (received != update.PackageSize)
            {
                throw new InvalidDataException("Update package download was incomplete.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(update.PackageSha256)))
            {
                throw new CryptographicException("Update package SHA-256 did not match the signed manifest.");
            }

            destination.Close();
            File.Move(partialPath, finalPath, overwrite: true);
            progress?.Report(new UpdateDownloadProgress(update.PackageSize, update.PackageSize));
            return finalPath;
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }
    }

    public static UpdateInfo CreatePreview() => new(
        new Version(2026, 7, 17),
        DateTimeOffset.Now,
        new Uri("https://updates.invalid/usagetools/UsageTools2026-2026.7.17.zip"),
        new string('A', 64),
        85L * 1024 * 1024,
        "Codex usage windows now follow the current official quota response.\n\n" +
        "Added a signed update feed, download notification, and rollback-ready update layout.",
        null,
        false);

    public void Dispose() => _client.Dispose();

    private static UpdateInfo ValidatePayload(UpdatePayload payload, Uri feedUri)
    {
        if (payload.SchemaVersion != 1 || !string.Equals(payload.AppId, "UsageTools2026.Public", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update payload identity was invalid.");
        }

        if (!string.Equals(payload.Channel, UpdateConfiguration.Channel, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update channel did not match.");
        }

        if (!Version.TryParse(payload.Version, out var version))
        {
            throw new InvalidDataException("Update version was invalid.");
        }

        if (!Uri.TryCreate(payload.PackageUrl, UriKind.Absolute, out var packageUri) ||
            packageUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(packageUri.Host, feedUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update package URL was not allowed.");
        }

        if (payload.PackageSize <= 0 || payload.PackageSize > MaxPackageBytes)
        {
            throw new InvalidDataException("Update package size was invalid.");
        }

        if (payload.PackageSha256.Length != 64 || payload.PackageSha256.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException("Update package hash was invalid.");
        }

        Version? minimumVersion = null;
        if (!string.IsNullOrWhiteSpace(payload.MinimumVersion) &&
            !Version.TryParse(payload.MinimumVersion, out minimumVersion))
        {
            throw new InvalidDataException("Minimum supported version was invalid.");
        }

        return new UpdateInfo(
            version,
            payload.PublishedAtUtc,
            packageUri,
            payload.PackageSha256.ToUpperInvariant(),
            payload.PackageSize,
            (payload.ReleaseNotes ?? string.Empty).Trim()[..Math.Min((payload.ReleaseNotes ?? string.Empty).Trim().Length, 8000)],
            minimumVersion,
            payload.Mandatory);
    }

    private static void VerifySignature(byte[] payload, byte[] signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(UpdateConfiguration.RsaPublicKeyPem);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new CryptographicException("Update manifest signature was invalid.");
        }
    }

    private static async Task<bool> VerifyPackageAsync(
        string path,
        UpdateInfo update,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != update.PackageSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DownloadBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(
            actualHash,
            Convert.FromHexString(update.PackageSha256));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The next download will retry with a fresh staging path.
        }
    }

    private static Version CurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record SignedUpdateEnvelope(string Payload, string Signature);

    private sealed record UpdatePayload(
        int SchemaVersion,
        string AppId,
        string Channel,
        string Version,
        DateTimeOffset PublishedAtUtc,
        string PackageUrl,
        string PackageSha256,
        long PackageSize,
        string? ReleaseNotes,
        string? MinimumVersion,
        bool Mandatory);
}
