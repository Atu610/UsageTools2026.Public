using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public sealed record PublicReleaseTranslation(string Title, string Summary);

public sealed class PublicReleaseTranslationService
{
    private const string TranslationEndpoint =
        "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-TW&dt=t";
    private const int MaximumCacheEntries = 512;
    private static readonly HttpClient TranslationClient = CreateHttpClient();
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };

    private readonly string _cachePath;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private TranslationCacheDocument _cache;

    public PublicReleaseTranslationService(string? cachePath = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsageTools2026.Public",
            "ai-changelog-translations.json");
        _cache = LoadCache(_cachePath);
    }

    public async Task<PublicReleaseTranslation?> TranslateReleaseAsync(
        AiReleaseInfo release,
        CancellationToken cancellationToken = default)
    {
        var titleTask = TranslateTextAsync(release.Title, cancellationToken);
        var summaryTask = TranslateTextAsync(release.Summary, cancellationToken);
        await Task.WhenAll(titleTask, summaryTask);

        var title = await titleTask;
        var summary = await summaryTask;
        if (title is null && summary is null)
        {
            return null;
        }

        return new PublicReleaseTranslation(title ?? release.Title, summary ?? release.Summary);
    }

    private async Task<string?> TranslateTextAsync(string source, CancellationToken cancellationToken)
    {
        var normalized = Normalize(source);
        if (normalized.Length == 0 || LooksLikeChinese(normalized))
        {
            return normalized.Length == 0 ? null : normalized;
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("zh-TW\n" + normalized)));
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.Translations.TryGetValue(key, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }
        finally
        {
            _cacheGate.Release();
        }

        var translated = await RequestTranslationAsync(normalized, cancellationToken);
        if (string.IsNullOrWhiteSpace(translated))
        {
            return null;
        }

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            _cache.Translations[key] = translated;
            while (_cache.Translations.Count > MaximumCacheEntries)
            {
                _cache.Translations.Remove(_cache.Translations.Keys.First());
            }

            SaveCacheAtomic(_cachePath, _cache);
        }
        finally
        {
            _cacheGate.Release();
        }

        return translated;
    }

    private static async Task<string?> RequestTranslationAsync(
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TranslationEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["q"] = source
                })
            };
            request.Headers.UserAgent.ParseAdd("UsageTools2026-PublicChangelogTranslation/1.0");
            using var response = await TranslationClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return json.Length is > 0 and <= 128_000 ? ParseTranslationResponse(json) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ParseTranslationResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0 ||
            document.RootElement[0].ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var segment in document.RootElement[0].EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Array ||
                segment.GetArrayLength() == 0 ||
                segment[0].ValueKind != JsonValueKind.String)
            {
                continue;
            }

            builder.Append(segment[0].GetString());
        }

        return Normalize(builder.ToString()) is { Length: > 0 } translated ? translated : null;
    }

    private static TranslationCacheDocument LoadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new TranslationCacheDocument();
            }

            var cache = JsonSerializer.Deserialize<TranslationCacheDocument>(File.ReadAllText(path));
            if (cache is not { SchemaVersion: 1 })
            {
                return new TranslationCacheDocument();
            }

            cache.Translations ??= new Dictionary<string, string>(StringComparer.Ordinal);
            return cache;
        }
        catch
        {
            return new TranslationCacheDocument();
        }
    }

    internal static void SaveCacheAtomic(string path, TranslationCacheDocument cache)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Translation cache path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(cache, CacheJsonOptions),
                new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Normalize(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool LooksLikeChinese(string value)
    {
        var han = Regex.Matches(value, "[\\u3400-\\u9FFF]").Count;
        var latin = Regex.Matches(value, "[A-Za-z]").Count;
        return han >= 4 && han >= latin / 2;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }
}

internal sealed class TranslationCacheDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("translations")]
    public Dictionary<string, string> Translations { get; set; } = new(StringComparer.Ordinal);
}
