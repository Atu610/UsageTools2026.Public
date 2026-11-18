using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace UsageTools2026.Services;

/// <summary>
/// Locates the Claude Desktop profile, resolves the organization id, and returns the decrypted
/// claude.ai cookies. While Claude Desktop runs it holds an EXCLUSIVE lock on its cookie database,
/// so this uses a Volume Shadow Copy (VSS) snapshot to read a consistent copy (requires admin).
///
/// SECURITY: cookies live only in memory here; nothing is written to disk. Callers must never
/// persist or log cookie values.
/// </summary>
public static class ClaudeProfileAccess
{
    private static readonly Regex OrganizationIdRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    public sealed record CookieSet(
        string OrganizationId,
        IReadOnlyList<KeyValuePair<string, string>> Cookies);

    /// <summary>Returns the org id + decrypted cookies, or null with a safe failure reason.</summary>
    public static CookieSet? TryGetCookies(out string failure)
    {
        failure = string.Empty;

        var profileRoot = FindProfileRoot();
        if (profileRoot is null)
        {
            failure = "Claude Desktop profile was not found.";
            return null;
        }

        var organizationId = FindOrganizationId(profileRoot);
        if (organizationId is null)
        {
            failure = "Claude organization id was not found.";
            return null;
        }

        // Read cookie DB + Local State, using a VSS snapshot when the live files are locked.
        string cookiesDbPath;
        string localStatePath;
        string? snapshotLink = null;
        ManagementObject? shadow = null;

        try
        {
            var liveCookies = Path.Combine(profileRoot, "Network", "Cookies");
            if (!File.Exists(liveCookies))
            {
                failure = "cookie_database_missing";
                return null;
            }
            if (new FileInfo(liveCookies).Length == 0)
            {
                failure = "cookie_database_empty";
                return null;
            }
            if (!IsLocked(liveCookies))
            {
                cookiesDbPath = liveCookies;
                localStatePath = Path.Combine(profileRoot, "Local State");
            }
            else if (TryCreateSnapshotPaths(profileRoot, out cookiesDbPath, out localStatePath, out snapshotLink, out shadow, out var snapFailure))
            {
                // snapshot paths assigned
            }
            else
            {
                failure = snapFailure;
                return null;
            }

            var databaseFailure = ValidateCookieDatabase(cookiesDbPath);
            if (databaseFailure is not null)
            {
                failure = databaseFailure;
                return null;
            }
            var masterKey = ReadChromiumMasterKey(localStatePath);
            if (masterKey is null)
            {
                failure = "Claude cookie master key could not be read.";
                return null;
            }

            var cookies = ReadAndDecryptCookies(cookiesDbPath, masterKey);
            if (cookies.Count == 0)
            {
                failure = "No usable claude.ai cookies were found.";
                return null;
            }

            return new CookieSet(organizationId, cookies);
        }
        catch (SqliteException ex)
        {
            failure = $"cookie_sqlite_error code={ex.SqliteErrorCode} extended={ex.SqliteExtendedErrorCode}";
            return null;
        }
        catch (Exception ex)
        {
            failure = "Cookie read failed: " + ex.GetType().Name + ".";
            return null;
        }
        finally
        {
            if (snapshotLink is not null)
            {
                RunCmd($"rmdir \"{snapshotLink}\"");
            }

            if (shadow is not null)
            {
                try { shadow.Delete(); } catch { /* snapshot cleanup is best-effort */ }
                shadow.Dispose();
            }
        }
    }

    public static bool IsCookieDatabaseLocked()
    {
        var profileRoot = FindProfileRoot();
        return profileRoot is not null && IsLocked(Path.Combine(profileRoot, "Network", "Cookies"));
    }

    internal static string? ValidateCookieDatabase(string path)
    {
        if (!File.Exists(path)) return "cookie_database_missing";
        if (new FileInfo(path).Length == 0) return "cookie_database_empty";
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cookies'";
        return Convert.ToInt64(command.ExecuteScalar()) == 1 ? null : "cookie_table_missing";
    }

    private static bool IsLocked(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // ---- VSS snapshot -------------------------------------------------------

    private static bool TryCreateSnapshotPaths(
        string profileRoot,
        out string cookiesDbPath,
        out string localStatePath,
        out string? snapshotLink,
        out ManagementObject? shadow,
        out string failure)
    {
        cookiesDbPath = string.Empty;
        localStatePath = string.Empty;
        snapshotLink = null;
        shadow = null;
        failure = string.Empty;

        try
        {
            var physical = ResolvePhysicalProfile(profileRoot);
            var root = Path.GetPathRoot(physical);
            if (string.IsNullOrEmpty(root))
            {
                failure = "Could not resolve Claude profile volume for VSS.";
                return false;
            }

            using var shadowClass = new ManagementClass("Win32_ShadowCopy");
            using var inParams = shadowClass.GetMethodParameters("Create");
            inParams["Volume"] = root;
            inParams["Context"] = "ClientAccessible";
            using var outParams = shadowClass.InvokeMethod("Create", inParams, null);

            var returnValue = Convert.ToInt32(outParams["ReturnValue"]);
            var shadowId = outParams["ShadowID"] as string;
            if (returnValue != 0 || string.IsNullOrEmpty(shadowId))
            {
                failure = $"VSS snapshot failed (code {returnValue}). Run UsageTools as administrator.";
                return false;
            }

            shadow = new ManagementObject(new ManagementPath($"Win32_ShadowCopy.ID=\"{shadowId}\""));
            shadow.Get();
            var deviceObject = shadow["DeviceObject"] as string;
            if (string.IsNullOrEmpty(deviceObject))
            {
                failure = "VSS snapshot device was not available.";
                return false;
            }

            var link = Path.Combine(Path.GetTempPath(), "ut-vss-" + Guid.NewGuid().ToString("N"));
            RunCmd($"mklink /d \"{link}\" \"{deviceObject}\\\"");
            snapshotLink = link;

            var relative = physical[root.Length..].TrimStart('\\', '/');
            var snapProfile = Path.Combine(link, relative);
            cookiesDbPath = Path.Combine(snapProfile, "Network", "Cookies");
            localStatePath = Path.Combine(snapProfile, "Local State");

            if (!File.Exists(cookiesDbPath))
            {
                failure = "Cookie DB not present in VSS snapshot.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = "VSS snapshot error: " + ex.GetType().Name + ". Run UsageTools as administrator.";
            return false;
        }
    }

    private static string ResolvePhysicalProfile(string profileRoot)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(profileRoot, returnFinalTarget: true);
            if (target is not null && Directory.Exists(target.FullName))
            {
                return target.FullName;
            }
        }
        catch
        {
            // Not a standard reparse point; fall through to the known MSIX physical path.
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            try
            {
                foreach (var pkg in Directory.EnumerateDirectories(packagesRoot, "Claude_*"))
                {
                    var candidate = Path.Combine(pkg, "LocalCache", "Roaming", "Claude");
                    if (File.Exists(Path.Combine(candidate, "Network", "Cookies")))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // ignore inaccessible package folders
            }
        }

        return profileRoot;
    }

    private static void RunCmd(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit(5000);
        }
        catch
        {
            // best-effort
        }
    }

    // ---- profile / org resolution ------------------------------------------

    public static string? FindProfileRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new List<string>
        {
            Path.Combine(appData, "Claude"),
            Path.Combine(localAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude")
        };

        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            try
            {
                candidates.AddRange(
                    Directory.EnumerateDirectories(packagesRoot, "Claude_*")
                        .Select(path => Path.Combine(path, "LocalCache", "Roaming", "Claude")));
            }
            catch
            {
                // ignore
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(path => Directory.Exists(path) &&
                                    File.Exists(Path.Combine(path, "Network", "Cookies")));
    }

    public static string? FindOrganizationId(string profileRoot)
    {
        foreach (var relativePath in new[] { "extensions-blocklist.json", "config.json", "bridge-state.json" })
        {
            var match = FindOrganizationIdInFile(Path.Combine(profileRoot, relativePath));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? FindOrganizationIdInFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            var matches = OrganizationIdRegex.Matches(text);
            if (matches.Count == 0)
            {
                return null;
            }

            var apiIndex = text.IndexOf("/api/organizations/", StringComparison.OrdinalIgnoreCase);
            if (apiIndex >= 0)
            {
                foreach (Match match in matches)
                {
                    if (match.Index >= apiIndex)
                    {
                        return match.Value.ToLowerInvariant();
                    }
                }
            }

            return matches[0].Value.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    // ---- cookie decryption -------------------------------------------------

    private static List<KeyValuePair<string, string>> ReadAndDecryptCookies(string databasePath, byte[] masterKey)
    {
        var result = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, value, encrypted_value
            FROM cookies
            WHERE host_key LIKE '%claude.ai%'
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(name) || name.Contains(';') || name.Contains('='))
            {
                continue;
            }

            var plainValue = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var value = !string.IsNullOrEmpty(plainValue)
                ? plainValue
                : DecryptCookieValue(ReadBlob(reader, 2), masterKey);

            if (!string.IsNullOrEmpty(value) && IsAscii(value) && seen.Add(name))
            {
                result.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return result;
    }

    private static byte[]? ReadChromiumMasterKey(string localStatePath)
    {
        try
        {
            if (!File.Exists(localStatePath))
            {
                return null;
            }

            using var stream = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyElement) ||
                encryptedKeyElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var encryptedKey = Convert.FromBase64String(encryptedKeyElement.GetString() ?? string.Empty);
            if (encryptedKey.Length > 5 &&
                encryptedKey[0] == (byte)'D' && encryptedKey[1] == (byte)'P' && encryptedKey[2] == (byte)'A' &&
                encryptedKey[3] == (byte)'P' && encryptedKey[4] == (byte)'I')
            {
                encryptedKey = encryptedKey[5..];
            }

            return ProtectedData.Unprotect(encryptedKey, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadBlob(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return Array.Empty<byte>();
        }

        var length = reader.GetBytes(ordinal, 0, null, 0, 0);
        var buffer = new byte[length];
        reader.GetBytes(ordinal, 0, buffer, 0, buffer.Length);
        return buffer;
    }

    private static string? DecryptCookieValue(byte[] encryptedValue, byte[]? masterKey)
    {
        if (encryptedValue.Length == 0)
        {
            return null;
        }

        if (masterKey is not null &&
            encryptedValue.Length > 31 &&
            encryptedValue[0] == (byte)'v' &&
            encryptedValue[1] is (byte)'1' or (byte)'2')
        {
            try
            {
                var nonce = encryptedValue.AsSpan(3, 12);
                var cipherText = encryptedValue.AsSpan(15, encryptedValue.Length - 31);
                var tag = encryptedValue.AsSpan(encryptedValue.Length - 16, 16);
                var plainText = new byte[cipherText.Length];

                using var aes = new AesGcm(masterKey, 16);
                aes.Decrypt(nonce, cipherText, tag, plainText);
                return StripDomainHashPrefix(plainText);
            }
            catch
            {
                // fall through to legacy DPAPI
            }
        }

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encryptedValue, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }

    // Chromium M127+ prepends a 32-byte SHA-256 domain hash to the cookie plaintext.
    private static string StripDomainHashPrefix(byte[] plain)
    {
        if (plain.Length > 32)
        {
            var stripped = Encoding.UTF8.GetString(plain, 32, plain.Length - 32);
            if (IsAscii(stripped))
            {
                return stripped;
            }
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }
}
