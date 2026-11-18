namespace UsageToolsUpdateTestApp;

internal static class Program
{
    private static int Main(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        File.WriteAllText(
            Path.Combine(baseDirectory, "started-pid.txt"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var healthIndex = Array.FindIndex(
            args,
            value => string.Equals(value, "--update-health-check", StringComparison.OrdinalIgnoreCase));
        if (healthIndex >= 0 && healthIndex + 1 < args.Length)
        {
            if (File.Exists(Path.Combine(baseDirectory, "fail-health.marker")))
            {
                return 23;
            }

            var marker = Path.GetFullPath(args[healthIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }

        Thread.Sleep(TimeSpan.FromMinutes(2));
        return 0;
    }
}
