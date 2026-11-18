using System.IO;
using System.Windows.Threading;

namespace UsageTools2026.Services;

public static class DiagnosticsService
{
    private static readonly object SyncRoot = new();
    internal static string? TestLogDirectory { get; set; }

    public static void Install()
    {
        System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Write("Application started.");
    }

    public static void Write(string message)
    {
        try
        {
            var dir = TestLogDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsageTools2026.Public",
                "logs");
            Directory.CreateDirectory(dir);

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(Path.Combine(dir, "app.log"), line);
            }
        }
        catch
        {
            // Logging must never take down the tray utility.
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write("Dispatcher exception: " + e.Exception);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Write("Unhandled exception: " + e.ExceptionObject);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write("Task exception: " + e.Exception);
    }
}
