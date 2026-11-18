using System.Diagnostics;
using System.IO;
using System.Text.Json;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

internal static class UpdateInstallerService
{
    private const string UpdaterFileName = "UsageToolsUpdater.exe";
    private const string HealthArgument = "--update-health-check";

    internal static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, UpdaterFileName));

    internal static void Start(UpdateInfo update, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(update);
        var sourceUpdater = Path.Combine(AppContext.BaseDirectory, UpdaterFileName);
        if (!File.Exists(sourceUpdater))
        {
            throw new FileNotFoundException("The external updater was not included in this build.", sourceUpdater);
        }

        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new FileNotFoundException("The verified update package was not found.", fullPackagePath);
        }

        var launcherRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsageTools2026.Public",
            "Updater",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(launcherRoot);
        var updaterPath = Path.Combine(launcherRoot, UpdaterFileName);
        File.Copy(sourceUpdater, updaterPath, overwrite: false);

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = launcherRoot,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)));
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(fullPackagePath);
        startInfo.ArgumentList.Add("--sha256");
        startInfo.ArgumentList.Add(update.PackageSha256);
        startInfo.ArgumentList.Add("--size");
        startInfo.ArgumentList.Add(update.PackageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(update.Version.ToString());

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Windows could not start the external updater.");
        }
    }

    internal static void TryWriteHealthMarker(IReadOnlyList<string> arguments)
    {
        try
        {
            var marker = ArgumentValue(arguments, HealthArgument);
            if (string.IsNullOrWhiteSpace(marker))
            {
                return;
            }

            var healthRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsageTools2026.Public",
                "UpdateHealth"));
            var fullMarker = Path.GetFullPath(marker);
            var allowedPrefix = Path.TrimEndingDirectorySeparator(healthRoot) + Path.DirectorySeparatorChar;
            if (!fullMarker.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(fullMarker), ".ok", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticsService.Write("Rejected an invalid update health marker path.");
                return;
            }

            Directory.CreateDirectory(healthRoot);
            File.WriteAllText(fullMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write($"Update health marker failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    internal static string? ConsumeFailureMessage()
    {
        var resultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsageTools2026.Public",
            "update-result.json");
        try
        {
            if (!File.Exists(resultPath))
            {
                return null;
            }

            var file = new FileInfo(resultPath);
            if (file.Length is <= 0 or > 16 * 1024)
            {
                File.Delete(resultPath);
                return "上次更新結果檔格式異常，程式已維持目前可用版本。";
            }

            var result = JsonSerializer.Deserialize<UpdateInstallResult>(File.ReadAllText(resultPath));
            File.Delete(resultPath);
            return result is { Success: false } ? result.Message : null;
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write($"Update result read failed: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    private static string? ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    private sealed record UpdateInstallResult(bool Success, string Message, string Version, DateTimeOffset TimestampUtc);
}
