using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace UsageTools2026.Services;

public static class DisplayFormatService
{
    public static string FormatWindowLabel(int? windowMinutes, string fallback)
    {
        if (windowMinutes is null || windowMinutes <= 0)
        {
            return fallback;
        }

        var minutes = windowMinutes.Value;
        if (minutes % (24 * 60) == 0)
        {
            return $"{minutes / (24 * 60)}d";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
    }

    public static WpfBrush PercentBrush(double percent)
    {
        return percent switch
        {
            >= 80 => Gradient("#FF4A4A", "#8B0000", (percent - 80) / 20),
            >= 60 => Gradient("#C891FF", "#5D2A9D", (percent - 60) / 20),
            >= 40 => Gradient("#FFC36A", "#C96500", (percent - 40) / 20),
            >= 20 => Gradient("#64F58A", "#15803D", (percent - 20) / 20),
            _ => Gradient("#8EEAFF", "#1BA6FF", percent / 20)
        };
    }

    private static WpfBrush Brush(string color) => new WpfSolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(color));

    public static WpfBrush UsageBarBrush(double percent) => PercentBrush(percent);

    private static WpfBrush Gradient(string start, string end, double ratio)
    {
        var a = (WpfColor)WpfColorConverter.ConvertFromString(start);
        var b = (WpfColor)WpfColorConverter.ConvertFromString(end);
        var t = Math.Max(0, Math.Min(1, ratio));

        return new WpfSolidColorBrush(WpfColor.FromRgb(
            (byte)(a.R + ((b.R - a.R) * t)),
            (byte)(a.G + ((b.G - a.G) * t)),
            (byte)(a.B + ((b.B - a.B) * t))));
    }

    public static string FormatBytes(ulong bytes)
    {
        return FormatBytes((double)bytes);
    }

    public static string FormatCompactBytes(ulong bytes)
    {
        var value = bytes / 1024d / 1024d / 1024d;
        return value >= 1 ? $"{value:0.#}G" : $"{bytes / 1024d / 1024d:0.#}M";
    }

    public static string FormatRate(double bytesPerSecond)
    {
        return $"{FormatBytes(bytesPerSecond)}/s";
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
