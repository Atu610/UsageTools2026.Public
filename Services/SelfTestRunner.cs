using System.IO;
using System.Security.Cryptography;

namespace UsageTools2026.Services;

internal static class SelfTestRunner
{
    internal static int Run(string suite, Func<string> test, string[] args)
    {
        var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
        var isolated = Path.Combine(Path.GetTempPath(), "UsageTools2026-tests", runId);
        var index = Array.IndexOf(args, "--selftest-output");
        var output = index >= 0 && index + 1 < args.Length ? args[index + 1]
            : Path.Combine(isolated, suite + ".txt");
        try
        {
            Directory.CreateDirectory(isolated);
            AppSettingsService.UseTestStore(Path.Combine(isolated, "settings.json"));
            DiagnosticsService.TestLogDirectory = isolated;
            string body;
            try
            {
                body = args.Contains("--selftest-inject-failure") ? "FAIL injected_failure" : test();
            }
            catch (Exception ex)
            {
                body = "FAIL exception " + ex.GetType().Name;
            }
            var passed = IsPassing(body);
            var assembly = typeof(SelfTestRunner).Assembly;
            using var stream = File.OpenRead(assembly.Location);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            AtomicFile.WriteText(output,
                $"RUN_ID={runId}\nSUITE={suite}\nUTC={DateTimeOffset.UtcNow:O}\n" +
                $"VERSION={assembly.GetName().Version}\nASSEMBLY_SHA256={hash}\n" +
                body + $"\nEXIT_CODE={(passed ? 0 : 1)}\n");
            return passed ? 0 : 1;
        }
        catch
        {
            // A missing report is a failed run, never a successful silent exit.
            return 2;
        }
    }

    internal static bool IsPassing(string body) =>
        body.Split('\n').Any(line => line.TrimStart().StartsWith("PASS ", StringComparison.Ordinal)) &&
        !body.Split('\n').Any(line => line.TrimStart().StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)) &&
        (body.Contains("ALL PASS", StringComparison.Ordinal) || body.Contains("SUMMARY passed=True", StringComparison.Ordinal));
}
