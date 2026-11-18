using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace UsageTools2026.Services;

public enum AppLanguage
{
    ZhTw,
    En
}

public enum AppTheme
{
    DeepNight,
    Day
}

public sealed class AppSettings
{
    public AppLanguage Language { get; set; } = AppLanguage.ZhTw;

    public AppTheme Theme { get; set; } = AppTheme.DeepNight;

    public string ThemeId { get; set; } = string.Empty;

    public bool HasMainWindowPosition { get; set; }

    public double MainWindowLeft { get; set; }

    public double MainWindowTop { get; set; }

    public double MainBackgroundOpacity { get; set; } = 0.9;

    public bool HasMiniWindowPosition { get; set; }

    public double MiniWindowLeft { get; set; }

    public double MiniWindowTop { get; set; }

    // Mobile web view: a small local HTTP server lets a phone (same Wi-Fi, or via a tunnel) view
    // the usage dashboard. Protected by a per-machine random access key. No cookies/tokens exposed.
    public bool MobileServerEnabled { get; set; } = false;

    public bool PersonalMobileEnabled { get; set; } = false;

    public bool CodexDataEnabled { get; set; } = true;

    public string? CodexConfirmedPlan { get; set; }

    public string? CodexConfirmedPlanAccountKey { get; set; }

    public bool ClaudeDataEnabled { get; set; } = true;

    public bool KeepWindowOnTop { get; set; } = true;

    public int MobilePort { get; set; } = 8770;

    public string MobileAccessKey { get; set; } = string.Empty;

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
}

public static class AppSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageTools2026.Public",
        "settings.json");

    private static SettingsStore _store = new(SettingsPath);

    public static AppSettings Load() => _store.Load();
    public static void Save(AppSettings settings) => _store.Save(settings);

    internal static void UseTestStore(string path) => _store = new SettingsStore(path);
}

internal sealed class SettingsStore(string path)
{
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<AppSettings, JsonObject> _baselines = new();
    private readonly string _mutexName = "Local\\UsageToolsSettings-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));

    public AppSettings Load()
    {
        lock (_gate)
        {
            var settings = ReadValid()?.Deserialize<AppSettings>() ?? new AppSettings();
            _baselines.Add(settings, Snapshot(settings));
            return settings;
        }
    }

    private JsonObject? ReadValid()
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var node = JsonNode.Parse(stream) as JsonObject;
                if (node?.Deserialize<AppSettings>() is not null) return node;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return null;
    }

    private static JsonObject Snapshot(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings)!.AsObject();

    public bool Save(AppSettings settings)
    {
        lock (_gate)
        {
            using var mutex = new Mutex(false, _mutexName);
            var held = false;
            try
            {
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("Settings lock timed out.");

                var current = Snapshot(settings);
                var baseline = _baselines.TryGetValue(settings, out var known) ? known : Snapshot(new AppSettings());
                var disk = ReadValid();
                if (disk is null && (File.Exists(path) || File.Exists(path + ".bak")))
                    throw new IOException("No valid settings copy; preserving damaged files.");
                var merged = disk?.DeepClone().AsObject() ?? Snapshot(new AppSettings());
                // Each window owns only its changes, not the entire old snapshot it loaded.
                foreach (var property in current)
                    if (!JsonNode.DeepEquals(property.Value, baseline[property.Key]))
                        merged[property.Key] = property.Value?.DeepClone();

                var options = new JsonSerializerOptions { WriteIndented = true };
                if (disk is not null) AtomicFile.WriteText(path + ".bak", disk.ToJsonString(options));
                AtomicFile.WriteText(path, merged.ToJsonString(options));
                if (disk is null) AtomicFile.WriteText(path + ".bak", merged.ToJsonString(options));
                _baselines.Remove(settings);
                _baselines.Add(settings, current);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsService.Write("Settings save failed: " + ex.GetType().Name);
                return false;
            }
            finally
            {
                if (held) mutex.ReleaseMutex();
            }
        }
    }
}
