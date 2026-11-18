using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UsageTools2026.Services;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;

namespace UsageTools2026;

public partial class AboutWindow : Window
{
    public AboutWindow(ThemeDefinition theme, AppLanguage language)
    {
        InitializeComponent();
        FontFamily = new System.Windows.Media.FontFamily(theme.Font);
        ApplyTheme(theme);
        ApplyLanguage(language);
    }

    private void ApplyTheme(ThemeDefinition theme)
    {
        SetBrush("ShellBrush", theme.Shell);
        SetBrush("PanelBrush", theme.Panel);
        SetBrush("BorderBrush", theme.Border);
        SetBrush("OuterBorderBrush", theme.Outer);
        SetBrush("TextBrush", theme.Text);
        SetBrush("MutedBrush", theme.Muted);
        SetBrush("ButtonBrush", theme.Button);
        SetBrush("AccentBrush", theme.Accent);
        SetBrush("CodexAccentBrush", theme.CodexAccent);
        SetBrush("ClaudeAccentBrush", theme.ClaudeAccent);
    }

    private void ApplyLanguage(AppLanguage language)
    {
        var zh = language == AppLanguage.ZhTw;
        Title = zh ? "關於 UsageTools2026" : "About UsageTools2026";
        WindowTitleText.Text = Title;
        ProductTitleText.Text = "UsageTools2026.Public";
        ProductSubtitleText.Text = zh ? "AI 使用量與工作視窗監控工具" : "AI usage and work-window monitor";
        DescriptionText.Text = zh
            ? "UsageTools2026 是專為 Codex 與 Claude Code 打造的 Windows 桌面監控工具，整合 Session／本週用量、上下文視窗、AI 模型與 Effort、專案 Token 統計及系統資源狀態。"
            : "UsageTools2026 is a Windows desktop monitor built for Codex and Claude Code. It brings session and weekly usage, context windows, AI model and effort, project token totals, and system status into one compact view.";
        AuthorLabelText.Text = zh ? "參與人員" : "Project team";
        AuthorValueText.Text = "EricsWei、Atu";
        AiLabelText.Text = zh ? "協作 AI" : "AI collaborators";
        AiValueText.Text = "Claude Code · OpenAI Codex";
        PlatformLabelText.Text = zh ? "技術平台" : "Platform";
        PlatformValueText.Text = "C# · .NET 10 · WPF · Windows";
        CopyrightText.Text = "Copyright 2026 EricsWei · UsageTools2026";
        CloseButton.Content = zh ? "關閉" : "Close";
    }

    private void SetBrush(string key, string color)
    {
        Resources[key] = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button)
            {
                return;
            }

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
