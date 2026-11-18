using System.Diagnostics;
using Microsoft.Win32;

namespace UsageTools2026.Services;

/// <summary>
/// Manages "launch at logon". Because UsageTools requires administrator rights (VSS), a plain
/// HKCU\Run entry would trip UAC on every logon. Instead we register a Scheduled Task with
/// "highest privileges", which starts the app elevated at logon WITHOUT a UAC prompt.
/// </summary>
public static class StartupService
{
    private const string TaskName = "UsageTools2026.Public";
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "UsageTools2026.Public";

    public static bool IsLaunchAtStartupEnabled()
    {
        return RunSchtasks(new[] { "/Query", "/TN", TaskName }) == 0;
    }

    public static bool ToggleLaunchAtStartup()
    {
        RemoveLegacyRunKey();

        if (IsLaunchAtStartupEnabled())
        {
            RunSchtasks(new[] { "/Delete", "/TN", TaskName, "/F" });
            return false;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return false;
        }

        // /RL HIGHEST => run elevated; /SC ONLOGON => at this user's logon. The quoted /TR value
        // keeps the path intact even with spaces.
        var exit = RunSchtasks(new[]
        {
            "/Create", "/TN", TaskName,
            "/TR", $"\"{exe}\"",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F"
        });

        return exit == 0;
    }

    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            if (key?.GetValue(LegacyValueName) is not null)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // best-effort cleanup of the old startup mechanism
        }
    }

    private static int RunSchtasks(string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
