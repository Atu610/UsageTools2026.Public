using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageToolsReleaseTool;

internal static class Program
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("Expected command: keygen or publish.");
            }

            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());
            return command switch
            {
                "keygen" => GenerateKey(options),
                "publish" => Publish(options),
                "verify" => Verify(options),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int GenerateKey(IReadOnlyDictionary<string, string?> options)
    {
        var privatePath = Path.GetFullPath(Required(options, "--private"));
        var publicPath = Path.GetFullPath(Required(options, "--public"));
        var force = options.ContainsKey("--force");
        if (!force && (File.Exists(privatePath) || File.Exists(publicPath)))
        {
            throw new IOException("Refusing to overwrite an existing key without --force.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        using var rsa = RSA.Create(4096);
        File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem(), Utf8);
        File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem(), Utf8);
        Console.WriteLine($"PrivateKey={privatePath}");
        Console.WriteLine($"PublicKey={publicPath}");
        Console.WriteLine("WARNING: Never upload or distribute the private key.");
        return 0;
    }

    private static int Publish(IReadOnlyDictionary<string, string?> options)
    {
        var releaseDirectory = ExistingDirectory(Required(options, "--release-dir"));
        var privateKeyPath = ExistingFile(Required(options, "--private-key"));
        var outputDirectory = Path.GetFullPath(Required(options, "--output-dir"));
        var versionText = Required(options, "--version");
        var baseUrl = Required(options, "--base-url");
        var minimumText = Optional(options, "--minimum");
        if (!Version.TryParse(versionText, out var version))
        {
            throw new ArgumentException("Version must be numeric, for example 2026.7.14.");
        }

        if (!string.IsNullOrWhiteSpace(minimumText) && !Version.TryParse(minimumText, out _))
        {
            throw new ArgumentException("Minimum version was invalid.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Base URL must be absolute HTTPS.");
        }

        foreach (var required in new[] { "UsageTools2026.exe", "UsageTools2026.dll", "UsageToolsUpdater.exe" })
        {
            if (!File.Exists(Path.Combine(releaseDirectory, required)))
            {
                throw new InvalidDataException($"Release directory is incomplete: {required}");
            }
        }

        var notesPath = Optional(options, "--notes");
        var notes = string.IsNullOrWhiteSpace(notesPath)
            ? "此版本包含穩定性、安全性與使用體驗改善。"
            : File.ReadAllText(ExistingFile(notesPath), Encoding.UTF8).Trim();
        if (notes.Length > 8000)
        {
            throw new InvalidDataException("Release notes must not exceed 8000 characters.");
        }

        Directory.CreateDirectory(outputDirectory);
        var packageName = $"UsageTools2026-{version}-win-x64.zip";
        var packagePath = Path.Combine(outputDirectory, packageName);
        var manifestPath = Path.Combine(outputDirectory, "stable.json");
        File.Delete(packagePath);
        ZipFile.CreateFromDirectory(releaseDirectory, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);

        var packageInfo = new FileInfo(packagePath);
        using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var packageHash = Convert.ToHexString(SHA256.HashData(packageStream));
        var packageUri = new Uri(baseUri.ToString().TrimEnd('/') + "/" + packageName);
        var payload = new UpdatePayload(
            1,
            "UsageTools2026.Public",
            "stable",
            version.ToString(),
            DateTimeOffset.UtcNow,
            packageUri.ToString(),
            packageHash,
            packageInfo.Length,
            notes,
            string.IsNullOrWhiteSpace(minimumText) ? null : minimumText,
            options.ContainsKey("--mandatory"));
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions());

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (!rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new CryptographicException("Signing self-verification failed.");
        }

        var envelope = new SignedEnvelope(
            Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(signature));
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(envelope, JsonOptions()), Utf8);
        File.WriteAllText(
            packagePath + ".sha256.txt",
            packageHash.ToLowerInvariant() + "  " + packageName + Environment.NewLine,
            Utf8);

        Console.WriteLine($"Manifest={manifestPath}");
        Console.WriteLine($"Package={packagePath}");
        Console.WriteLine($"PackageSize={packageInfo.Length}");
        Console.WriteLine($"PackageSha256={packageHash}");
        return 0;
    }

    private static int Verify(IReadOnlyDictionary<string, string?> options)
    {
        var manifestPath = ExistingFile(Required(options, "--manifest"));
        var publicKeyPath = ExistingFile(Required(options, "--public-key"));
        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length is <= 0 or > 128 * 1024)
        {
            throw new InvalidDataException("Manifest size was invalid.");
        }

        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(File.ReadAllText(manifestPath, Encoding.UTF8), JsonOptions())
            ?? throw new InvalidDataException("Manifest envelope was empty.");
        var payloadBytes = Convert.FromBase64String(envelope.Payload);
        var signature = Convert.FromBase64String(envelope.Signature);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(publicKeyPath));
        if (!rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new CryptographicException("Manifest signature verification failed.");
        }

        var payload = JsonSerializer.Deserialize<UpdatePayload>(payloadBytes, JsonOptions())
            ?? throw new InvalidDataException("Manifest payload was empty.");
        if (payload.SchemaVersion != 1 || payload.AppId != "UsageTools2026.Public" || payload.Channel != "stable" ||
            !Uri.TryCreate(payload.PackageUrl, UriKind.Absolute, out var packageUri) ||
            packageUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Manifest payload identity was invalid.");
        }

        var packageName = Path.GetFileName(packageUri.LocalPath);
        var packagePath = ExistingFile(Path.Combine(Path.GetDirectoryName(manifestPath)!, packageName));
        var packageInfo = new FileInfo(packagePath);
        if (packageInfo.Length != payload.PackageSize)
        {
            throw new InvalidDataException("Package size did not match the signed payload.");
        }

        using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var actualHash = SHA256.HashData(packageStream);
        var expectedHash = Convert.FromHexString(payload.PackageSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new CryptographicException("Package SHA-256 did not match the signed payload.");
        }

        Console.WriteLine("Verification=OK");
        Console.WriteLine($"Version={payload.Version}");
        Console.WriteLine($"Package={packagePath}");
        Console.WriteLine($"PackageSha256={payload.PackageSha256}");
        return 0;
    }

    private static Dictionary<string, string?> ParseOptions(IReadOnlyList<string> args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid option: {name}");
            }

            if (name is "--force" or "--mandatory")
            {
                result.Add(name, null);
                continue;
            }

            if (++i >= args.Count)
            {
                throw new ArgumentException($"Missing value for {name}.");
            }

            result.Add(name, args[i]);
        }

        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing option: {name}");

    private static string? Optional(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) ? value : null;

    private static string ExistingDirectory(string path) =>
        Directory.Exists(Path.GetFullPath(path))
            ? Path.GetFullPath(path)
            : throw new DirectoryNotFoundException(path);

    private static string ExistingFile(string path) =>
        File.Exists(Path.GetFullPath(path))
            ? Path.GetFullPath(path)
            : throw new FileNotFoundException("File was not found.", path);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record SignedEnvelope(string Payload, string Signature);

    private sealed record UpdatePayload(
        int SchemaVersion,
        string AppId,
        string Channel,
        string Version,
        DateTimeOffset PublishedAtUtc,
        string PackageUrl,
        string PackageSha256,
        long PackageSize,
        string ReleaseNotes,
        string? MinimumVersion,
        bool Mandatory);
}
