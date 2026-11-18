using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageToolsUpdater;

internal static class Program
{
    private const long MaxPackageBytes = 1024L * 1024 * 1024;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 25_000;
    private static readonly string StateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageTools2026.Public");
    private static readonly string LogPath = Path.Combine(StateRoot, "updater.log");
    private static readonly string ResultPath = Path.Combine(StateRoot, "update-result.json");

    private static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(StateRoot);
        TryDelete(ResultPath);
        Options? options = null;
        try
        {
            options = Options.Parse(args);
            var installed = await ApplyAsync(options);
            return installed ? 0 : 2;
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex.GetType().Name} - {ex.Message}");
            WriteResult(false, "更新未完成，已保留或還原上一個可用版本。", options?.Version ?? "unknown");
            if (options is not null)
            {
                TryRestoreAndLaunch(options);
            }

            return 1;
        }
    }

    private static async Task<bool> ApplyAsync(Options options)
    {
        var target = NormalizeTarget(options.TargetDirectory);
        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidOperationException("The application directory has no writable parent.");
        var targetName = Path.GetFileName(target);
        var operationId = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(parent, $".{targetName}.stage-{operationId}");
        var pendingBackup = Path.Combine(parent, $".{targetName}.backup-{operationId}");
        var previousBackup = target + ".previous";
        var healthRoot = Path.Combine(StateRoot, "UpdateHealth");
        var healthMarker = Path.Combine(healthRoot, operationId + ".ok");

        Log($"Starting update to {options.Version}.");
        await VerifyPackageAsync(options);
        ExtractPackage(options.PackagePath, stage);
        ValidateStagedApplication(stage, options.Version);
        WaitForParentExit(options.ParentProcessId);

        var targetSwapped = false;
        try
        {
            Directory.Move(target, pendingBackup);
            try
            {
                Directory.Move(stage, target);
                targetSwapped = true;
            }
            catch
            {
                Directory.Move(pendingBackup, target);
                throw;
            }

            try
            {
                Directory.CreateDirectory(healthRoot);
                TryDelete(healthMarker);
                var newProcess = StartApplication(target, "--update-health-check", healthMarker);
                if (!WaitForHealth(newProcess, healthMarker, TimeSpan.FromSeconds(45)))
                {
                    TryStop(newProcess);
                    TryDelete(healthMarker);
                    RollBack(target, pendingBackup, operationId);
                    targetSwapped = false;
                    WriteResult(false, "新版啟動健康檢查未通過，已自動回復上一版。", options.Version);
                    StartApplication(target);
                    return false;
                }
            }
            catch
            {
                TryDelete(healthMarker);
                if (targetSwapped && Directory.Exists(pendingBackup))
                {
                    Log("The updated application could not start; restoring the previous version.");
                    RollBack(target, pendingBackup, operationId);
                    targetSwapped = false;
                }

                throw;
            }

            TryDelete(healthMarker);
            try
            {
                SafeDeleteDirectory(previousBackup, parent);
                Directory.Move(pendingBackup, previousBackup);
            }
            catch (Exception backupEx)
            {
                Log($"Previous-version retention warning: {backupEx.GetType().Name} - {backupEx.Message}");
            }

            TryDelete(options.PackagePath);
            Log("Update completed and passed startup health verification.");
        }
        finally
        {
            SafeDeleteDirectory(stage, parent);
        }

        return true;
    }

    private static async Task VerifyPackageAsync(Options options)
    {
        var file = new FileInfo(options.PackagePath);
        if (!file.Exists || file.Length != options.PackageSize || file.Length > MaxPackageBytes)
        {
            throw new InvalidDataException("The update package size was invalid.");
        }

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream);
        var expected = Convert.FromHexString(options.PackageSha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new CryptographicException("The update package hash was invalid.");
        }
    }

    private static void ExtractPackage(string packagePath, string stage)
    {
        Directory.CreateDirectory(stage);
        var stagePrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stage)) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is <= 0 or > MaxArchiveEntries)
        {
            throw new InvalidDataException("The update archive entry count was invalid.");
        }

        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length is 0 or > 512 || name.StartsWith('/') || name.Contains(':'))
            {
                throw new InvalidDataException("The update archive contained an unsafe path.");
            }

            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException("The update archive attempted path traversal.");
            }

            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
            {
                throw new InvalidDataException("Symbolic links are not allowed in update archives.");
            }

            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaxExtractedBytes)
            {
                throw new InvalidDataException("The extracted update exceeded the size limit.");
            }

            var outputPath = Path.GetFullPath(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!outputPath.StartsWith(stagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update archive attempted path traversal.");
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var input = entry.Open();
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
            if (new FileInfo(outputPath).Length != entry.Length)
            {
                throw new InvalidDataException("An extracted update file was incomplete.");
            }
        }
    }

    private static void ValidateStagedApplication(string stage, string expectedVersion)
    {
        var executable = Path.Combine(stage, "UsageTools2026.exe");
        if (!File.Exists(executable))
        {
            throw new InvalidDataException("The update did not contain UsageTools2026.exe.");
        }

        var actualText = FileVersionInfo.GetVersionInfo(executable).FileVersion;
        var normalized = actualText?.Split('-', '+')[0];
        if (!Version.TryParse(normalized, out var actual) ||
            !Version.TryParse(expectedVersion, out var expected) ||
            actual.Major != expected.Major ||
            actual.Minor != expected.Minor ||
            actual.Build != expected.Build)
        {
            throw new InvalidDataException($"The staged application version was invalid ({actualText ?? "missing"}).");
        }
    }

    private static void WaitForParentExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                throw new TimeoutException("UsageTools2026 did not close in time.");
            }
        }
        catch (ArgumentException)
        {
            // The parent already exited.
        }
    }

    private static bool WaitForHealth(Process process, string marker, TimeSpan timeout)
    {
        var expires = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < expires)
        {
            if (File.Exists(marker))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            Thread.Sleep(250);
        }

        return File.Exists(marker);
    }

    private static void RollBack(string target, string pendingBackup, string operationId)
    {
        var parent = Directory.GetParent(target)?.FullName
            ?? throw new InvalidOperationException("The application directory has no parent for rollback.");
        var failed = Path.Combine(parent, $".{Path.GetFileName(target)}.failed-{operationId}");
        Directory.Move(target, failed);
        try
        {
            Directory.Move(pendingBackup, target);
        }
        catch
        {
            Directory.Move(failed, target);
            throw;
        }

        SafeDeleteDirectory(failed, parent);
        Log("The previous version was restored.");
    }

    private static Process StartApplication(string target, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(target, "UsageTools2026.exe"),
            WorkingDirectory = target,
            UseShellExecute = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("The updated application could not start.");
    }

    private static void TryRestoreAndLaunch(Options options)
    {
        try
        {
            try
            {
                using var parentProcess = Process.GetProcessById(options.ParentProcessId);
                if (!parentProcess.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                // The original process has already exited and may need relaunching.
            }

            var target = NormalizeTarget(options.TargetDirectory);
            var parent = Directory.GetParent(target)?.FullName;
            if (!Directory.Exists(target) && parent is not null)
            {
                var backup = Directory.GetDirectories(parent, $".{Path.GetFileName(target)}.backup-*")
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (backup is not null)
                {
                    Directory.Move(backup, target);
                }
            }

            if (File.Exists(Path.Combine(target, "UsageTools2026.exe")))
            {
                StartApplication(target);
            }
        }
        catch (Exception ex)
        {
            Log($"Emergency restore failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private static string NormalizeTarget(string target)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!Directory.Exists(full) || string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The application directory was invalid.");
        }

        if (!File.Exists(Path.Combine(full, "UsageTools2026.exe")))
        {
            throw new InvalidOperationException("The target was not a UsageTools2026 installation.");
        }

        return full;
    }

    private static void SafeDeleteDirectory(string path, string allowedParent)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parentPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedParent)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside the update parent.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private static void TryDelete(string path)
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
            // Cleanup is best-effort; validation never trusts an existing partial file.
        }
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Rollback will still be attempted after the health timeout.
        }
    }

    private static void WriteResult(bool success, string message, string version)
    {
        try
        {
            var payload = new UpdateInstallResult(success, message, version, DateTimeOffset.UtcNow);
            File.WriteAllText(
                ResultPath,
                JsonSerializer.Serialize(payload, UpdaterJsonContext.Default.UpdateInstallResult));
        }
        catch
        {
            // Result reporting must not interfere with rollback.
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging is best-effort.
        }
    }

    private sealed record Options(
        int ParentProcessId,
        string TargetDirectory,
        string PackagePath,
        string PackageSha256,
        long PackageSize,
        string Version)
    {
        internal static Options Parse(IReadOnlyList<string> args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Count; i += 2)
            {
                if (i + 1 >= args.Count || !args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Updater arguments were invalid.");
                }

                values.Add(args[i], args[i + 1]);
            }

            if (!int.TryParse(Required(values, "--parent-pid"), out var parentPid) || parentPid <= 0 ||
                !long.TryParse(Required(values, "--size"), out var packageSize) || packageSize <= 0 ||
                packageSize > MaxPackageBytes)
            {
                throw new ArgumentException("Updater numeric arguments were invalid.");
            }

            var sha256 = Required(values, "--sha256").ToUpperInvariant();
            if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new ArgumentException("Updater hash argument was invalid.");
            }

            var version = Required(values, "--version");
            if (!System.Version.TryParse(version, out _))
            {
                throw new ArgumentException("Updater version argument was invalid.");
            }

            return new Options(
                parentPid,
                Path.GetFullPath(Required(values, "--target")),
                Path.GetFullPath(Required(values, "--package")),
                sha256,
                packageSize,
                version);
        }

        private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"Missing updater argument: {name}");
    }
}

internal sealed record UpdateInstallResult(bool Success, string Message, string Version, DateTimeOffset TimestampUtc);

[JsonSerializable(typeof(UpdateInstallResult))]
internal partial class UpdaterJsonContext : JsonSerializerContext;
