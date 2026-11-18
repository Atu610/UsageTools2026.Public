namespace UsageTools2026.Services;

public enum BarColorMode
{
    /// <summary>Progress bars colored by usage % (red/amber/green) — the original behavior.</summary>
    ByUsage,

    /// <summary>Progress bars use a single theme color.</summary>
    ThemeSolid,

    /// <summary>Progress bars use a multi-stop theme gradient.</summary>
    ThemeGradient
}

/// <summary>
/// One UI theme: the 14 brush colors, font, and progress-bar coloring. Pure data (hex strings),
/// no WPF dependency. Consumed by MainWindow.ApplyTheme / MiniStatusWindow.ApplyTheme.
/// </summary>
public sealed record ThemeDefinition
{
    public required string Id { get; init; }
    public required string NameZh { get; init; }
    public required string NameEn { get; init; }

    public required string Shell { get; init; }
    public required string Panel { get; init; }
    public required string Card { get; init; }
    public required string Track { get; init; }
    public required string Border { get; init; }
    public required string Outer { get; init; }
    public required string Text { get; init; }
    public required string Muted { get; init; }
    public required string Button { get; init; }
    public required string PrimaryButton { get; init; }

    public string Accent { get; init; } = "#78F0F4";
    public string Success { get; init; } = "#4EF06F";
    public string Warning { get; init; } = "#F3B05B";
    public string Danger { get; init; } = "#FF5555";

    public string Font { get; init; } = "Segoe UI";
    public string CodexAccent { get; init; } = "#4F9DF7";
    public string ClaudeAccent { get; init; } = "#E8843D";

    public BarColorMode BarMode { get; init; } = BarColorMode.ByUsage;
    public string BarColor { get; init; } = "#4F9DF7";
    public string[] BarGradient { get; init; } = System.Array.Empty<string>();
    public string VisualStyle { get; init; } = "default";
}

public static class ThemeCatalog
{
    public const string DefaultId = "deep-night";

    public static readonly IReadOnlyList<ThemeDefinition> Themes = new[]
    {
        new ThemeDefinition
        {
            Id = "deep-night", NameZh = "深夜", NameEn = "Deep Night",
            Shell = "#070906", Panel = "#10151B", Card = "#151B22", Track = "#27313A",
            Border = "#2F3A46", Outer = "#30414E", Text = "#F2F5F4", Muted = "#7F8A92",
            Button = "#242B34", PrimaryButton = "#1C5962",
            Accent = "#78F0F4", Success = "#4EF06F", Warning = "#F3B05B", Danger = "#FF5555",
            CodexAccent = "#4F9DF7", ClaudeAccent = "#E8843D",
            BarMode = BarColorMode.ByUsage,
            VisualStyle = "deep-night"
        },
        new ThemeDefinition
        {
            Id = "day", NameZh = "白天", NameEn = "Day",
            Shell = "#D7E0E2", Panel = "#F3F7F6", Card = "#FFFFFF", Track = "#D9E0E3",
            Border = "#C4CED4", Outer = "#9EB0B8", Text = "#18212A", Muted = "#64717B",
            Button = "#E8EEF1", PrimaryButton = "#D9F3F4",
            Accent = "#2A8FBC", Success = "#218B46", Warning = "#B86A0B", Danger = "#C73434",
            CodexAccent = "#166DCC", ClaudeAccent = "#B85520",
            BarMode = BarColorMode.ByUsage,
            VisualStyle = "day"
        },
        new ThemeDefinition
        {
            Id = "classic", NameZh = "經典", NameEn = "Classic",
            Shell = "#03100F", Panel = "#08201F", Card = "#0D2A2A", Track = "#183D40",
            Border = "#23555B", Outer = "#34727A", Text = "#EAFBF8", Muted = "#81A6A6",
            Button = "#153536", PrimaryButton = "#0B5E63", Accent = "#31E4D4",
            Success = "#49DB99", Warning = "#CFA24B", Danger = "#F05A5A",
            CodexAccent = "#2FB7D7", ClaudeAccent = "#4FE6C6",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#0E6E86", "#22D7C8", "#7DFFD6" },
            VisualStyle = "classic"
        },
        new ThemeDefinition
        {
            Id = "aquarium", NameZh = "水族箱", NameEn = "Aquarium",
            Shell = "#021225", Panel = "#06233B", Card = "#082D4A", Track = "#0B4860",
            Border = "#157C98", Outer = "#1DA8B8", Text = "#E4F8FF", Muted = "#84BED2",
            Button = "#0A3B58", PrimaryButton = "#086F86", Accent = "#5CF2FF",
            Success = "#64F6B6", Warning = "#FFD178", Danger = "#FF6B83",
            CodexAccent = "#5FCBFF", ClaudeAccent = "#2EF3C0",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#0663A6", "#18E0D0", "#9AF8FF" },
            VisualStyle = "aquarium"
        },
        new ThemeDefinition
        {
            Id = "cloud_observation", NameZh = "天衡星盤", NameEn = "Celestial Balance",
            Shell = "#05060C", Panel = "#0B0A12", Card = "#12101A", Track = "#211B27",
            Border = "#8B6A3A", Outer = "#D7AA5D", Text = "#F3DEB0", Muted = "#B89A68",
            Button = "#18131D", PrimaryButton = "#332318", Accent = "#F0C46A",
            Success = "#D8B864", Warning = "#F3AA42", Danger = "#C75A45",
            CodexAccent = "#E8C878", ClaudeAccent = "#B99254",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#5B3A16", "#C99A43", "#FFE5A3", "#B6792F" },
            VisualStyle = "celestial"
        },
        new ThemeDefinition
        {
            Id = "black_hole", NameZh = "黑洞", NameEn = "Black Hole",
            Shell = "#000000", Panel = "#050505", Card = "#0A0706", Track = "#201614",
            Border = "#3A241C", Outer = "#6A2D17", Text = "#F7EEE8", Muted = "#A47C6B",
            Button = "#17100F", PrimaryButton = "#4A190B", Accent = "#FF6A1A",
            Success = "#FFC65A", Warning = "#FF8B32", Danger = "#FF3333",
            CodexAccent = "#FFB14A", ClaudeAccent = "#FF4E18",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#140000", "#FF3B00", "#FFB545", "#FFF3A0" },
            VisualStyle = "black-hole"
        },
        new ThemeDefinition
        {
            Id = "prism_arcade", NameZh = "稜鏡街機", NameEn = "Prism Arcade",
            Shell = "#090015", Panel = "#120326", Card = "#1B0737", Track = "#2F1548",
            Border = "#6931B6", Outer = "#17E6FF", Text = "#FFF7FF", Muted = "#BDA7FF",
            Button = "#29104A", PrimaryButton = "#2B0F68", Accent = "#00F5FF",
            Success = "#46FF70", Warning = "#FFE646", Danger = "#FF3CB7",
            CodexAccent = "#00E7FF", ClaudeAccent = "#FF46D8",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#FF2DD6", "#7B4DFF", "#00E7FF", "#49FF73", "#FFE646" },
            VisualStyle = "prism"
        },
        new ThemeDefinition
        {
            Id = "matrix", NameZh = "經典版", NameEn = "Classic Lineage",
            Shell = "#0B0C10", Panel = "#14161C", Card = "#1B1D24", Track = "#080A0E",
            Border = "#665338", Outer = "#B28B4D", Text = "#EEE5D5", Muted = "#A79C88",
            Button = "#242129", PrimaryButton = "#55351F", Accent = "#D0AA63",
            Success = "#7CB65B", Warning = "#D89942", Danger = "#D74332",
            Font = "Segoe UI",
            CodexAccent = "#78AEE8", ClaudeAccent = "#E1844B",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#501114", "#A51E24", "#E2483C", "#F17A5D" },
            VisualStyle = "dragon-lair"
        },
        new ThemeDefinition
        {
            Id = "newspaper", NameZh = "報紙", NameEn = "Newspaper",
            Shell = "#E8E1D0", Panel = "#F5F0E5", Card = "#FFFDF4", Track = "#D4CCBB",
            Border = "#2C2C2C", Outer = "#111111", Text = "#161616", Muted = "#5C5C5C",
            Button = "#ECE5D7", PrimaryButton = "#D8D0C0", Accent = "#111111",
            Success = "#222222", Warning = "#555555", Danger = "#000000",
            Font = "Georgia",
            CodexAccent = "#111111", ClaudeAccent = "#3D3D3D",
            BarMode = BarColorMode.ThemeSolid,
            BarColor = "#111111",
            VisualStyle = "newspaper"
        },
        new ThemeDefinition
        {
            Id = "win95", NameZh = "Windows 95", NameEn = "Win95",
            Shell = "#008080", Panel = "#C0C0C0", Card = "#C0C0C0", Track = "#808080",
            Border = "#404040", Outer = "#FFFFFF", Text = "#000000", Muted = "#303030",
            Button = "#C0C0C0", PrimaryButton = "#000080", Accent = "#000080",
            Success = "#008000", Warning = "#808000", Danger = "#800000",
            CodexAccent = "#000080", ClaudeAccent = "#000080",
            BarMode = BarColorMode.ThemeSolid,
            BarColor = "#000080",
            VisualStyle = "win95"
        },
        new ThemeDefinition
        {
            Id = "world_cup", NameZh = "賽博朋克", NameEn = "Cyberpunk",
            Shell = "#05000F", Panel = "#0B0821", Card = "#13102A", Track = "#23143A",
            Border = "#FF2BD6", Outer = "#00E7FF", Text = "#F7F8FF", Muted = "#A7B4FF",
            Button = "#20133B", PrimaryButton = "#3A145C", Accent = "#00E7FF",
            Success = "#49FF73", Warning = "#FFE646", Danger = "#FF2D55",
            CodexAccent = "#00E7FF", ClaudeAccent = "#FF2BD6",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#FF2BD6", "#7B4DFF", "#00E7FF", "#FFE646" },
            VisualStyle = "cyberpunk"
        },
        new ThemeDefinition
        {
            Id = "cartoon_platformer", NameZh = "卡通平台", NameEn = "Cartoon Platform",
            Shell = "#E91417", Panel = "#FFD43A", Card = "#FFF9E6", Track = "#1B1B1B",
            Border = "#111111", Outer = "#111111", Text = "#111111", Muted = "#4A3423",
            Button = "#FFE66D", PrimaryButton = "#E91B21", Accent = "#168EF2",
            Success = "#22A83A", Warning = "#FFD928", Danger = "#E91B21",
            Font = "Segoe UI",
            CodexAccent = "#168EF2", ClaudeAccent = "#E91B21",
            BarMode = BarColorMode.ThemeGradient,
            BarGradient = new[] { "#900000", "#E91B21", "#FF6A4D" },
            VisualStyle = "cartoon-platformer"
        }
    };

    public static ThemeDefinition Resolve(string? id)
    {
        if (string.Equals(id, "dark_fantasy", StringComparison.OrdinalIgnoreCase))
        {
            id = "cartoon_platformer";
        }

        if (!string.IsNullOrEmpty(id))
        {
            foreach (var theme in Themes)
            {
                if (string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return theme;
                }
            }
        }

        return Themes[0];
    }
}
