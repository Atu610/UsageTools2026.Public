using System.IO;
using System.Text.Json;

namespace UsageTools2026.Services;

public sealed record CodexResetReminderState(string CreditId, DateTimeOffset RemindedAt);

public static class CodexResetReminderStateService
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageTools2026.Public",
        "codex-reset-reminder.json");

    public static CodexResetReminderState? Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            using var stream = new FileStream(
                StatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var creditId = root.TryGetProperty("creditId", out var idElement)
                ? idElement.GetString()
                : null;
            var remindedAtText = root.TryGetProperty("remindedAt", out var remindedElement)
                ? remindedElement.GetString()
                : null;
            return !string.IsNullOrWhiteSpace(creditId) &&
                   DateTimeOffset.TryParse(remindedAtText, out var remindedAt)
                ? new CodexResetReminderState(creditId, remindedAt)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(CodexResetReminderState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            using var stream = new FileStream(StatePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("creditId", state.CreditId);
            writer.WriteString("remindedAt", state.RemindedAt);
            writer.WriteEndObject();
        }
        catch
        {
            // Reminder persistence is best-effort; it must not break the monitor.
        }
    }
}
