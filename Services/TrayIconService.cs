using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using UsageTools2026.Models;
using Brushes = System.Drawing.Brushes;

namespace UsageTools2026.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private Icon? _currentIcon;

    public TrayIconService(
        Action toggleWindow,
        Action showMini,
        Func<Task> refresh,
        Action report,
        Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("正常模式", null, (_, _) => toggleWindow());
        menu.Items.Add("簡易模式", null, (_, _) => showMini());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh", null, async (_, _) => await refresh());
        menu.Items.Add("HTML Report", null, (_, _) => report());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());

        _notifyIcon = new NotifyIcon
        {
            Text = "UsageTools 2026",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                toggleWindow();
            }
        };

        ApplySnapshot(UsageSnapshot.Empty);
    }

    public void ApplySnapshot(UsageSnapshot snapshot)
    {
        var primary = snapshot.Codex.PrimaryUsedPercent ?? 0;
        var secondary = snapshot.Codex.SecondaryUsedPercent ?? 0;
        var max = Math.Max(primary, secondary);
        var color = snapshot.Codex.HasData
            ? max >= 80 ? Color.FromArgb(255,115,115) : max >= 70 ? Color.FromArgb(233,182,93) : Color.FromArgb(141,241,196)
            : Color.DimGray;

        _currentIcon?.Dispose();
        _currentIcon = CreateIcon(color, max);
        _notifyIcon.Icon = _currentIcon;

        var limitState = max >= 99.95
            ? snapshot.CodexWorkWindows.Count > 0 ? " (active turn)" : " (limit reached)"
            : string.Empty;
        var windowParts = new List<string>();
        if (snapshot.Codex.PrimaryUsedPercent is { } primaryValue)
        {
            var label = DisplayFormatService.FormatWindowLabel(snapshot.Codex.PrimaryWindowMinutes, "5h");
            windowParts.Add($"{label} {primaryValue:0.#}%");
        }

        if (snapshot.Codex.SecondaryUsedPercent is { } secondaryValue)
        {
            var label = DisplayFormatService.FormatWindowLabel(snapshot.Codex.SecondaryWindowMinutes, "7d");
            windowParts.Add($"{label} {secondaryValue:0.#}%");
        }

        var codex = snapshot.Codex.HasData
            ? $"Codex {string.Join(" / ", windowParts)}{limitState}"
            : "Codex usage unavailable";

        var stale = snapshot.Codex.IsStale ? " (stale)" : string.Empty;
        _notifyIcon.Text = TruncateTooltip($"UsageTools 2026 - {codex}{stale}");
    }

    public void ShowNewResetCredits(int addedCount, int availableCount, string title = "Codex 新增重置券")
    {
        _notifyIcon.ShowBalloonTip(8000, title,
            addedCount > 0 ? $"新增 {addedCount} 張，目前可用 {availableCount} 張。" : $"目前可用 {availableCount} 張重置券。",
            ToolTipIcon.Info);
    }

    public void ShowResetCreditReminder(int availableCount, DateTimeOffset expiresAt)
    {
        _notifyIcon.ShowBalloonTip(
            8000,
            "Codex \u91cd\u7f6e\u984d\u5ea6\u5373\u5c07\u5230\u671f",
            $"\u6700\u65e9\u4e00\u7b46\u5c07\u65bc {expiresAt.LocalDateTime:MM/dd HH:mm} \u5230\u671f\uff0c\u76ee\u524d\u53ef\u7528 {availableCount} \u6b21\u3002",
            ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }

    private static Icon CreateIcon(Color color, double percent)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(32,36,38));
        using var shell = new System.Drawing.Drawing2D.GraphicsPath();
        shell.AddArc(0, 0, 8, 8, 180, 90);
        shell.AddArc(24, 0, 8, 8, 270, 90);
        shell.AddArc(24, 24, 8, 8, 0, 90);
        shell.AddArc(0, 24, 8, 8, 90, 90);
        shell.CloseFigure();
        graphics.FillPath(background, shell);
        using var arc = new Pen(color, 4);
        arc.StartCap = arc.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        graphics.DrawArc(arc, 6, 8, 20, 20, 180, 180);
        using var needle = new Pen(Color.White, 2.5f);
        needle.StartCap = needle.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        graphics.DrawLine(needle, 16, 20, 21, 15);
        graphics.FillEllipse(Brushes.White, 14, 18, 4, 4);

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static string TruncateTooltip(string text) => text.Length > 63 ? text[..63] : text;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
