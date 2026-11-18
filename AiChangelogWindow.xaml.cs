using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UsageTools2026.Models;
using UsageTools2026.Services;
using Panel = System.Windows.Controls.Panel;

namespace UsageTools2026;

public partial class AiChangelogWindow : Window
{
    private AiChangelogSnapshot _snapshot = AiChangelogSnapshot.Empty;
    private readonly PublicReleaseTranslationService _translationService = new();
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly Dictionary<string, PublicReleaseTranslation> _translations = new(StringComparer.Ordinal);
    private bool _isReady;
    private bool _allowClose;
    private bool _showChinese;
    private bool _translationFailed;

    public event Action<AiProvider>? ProviderViewed;

    public event Func<Task>? RefreshRequested;

    public AiChangelogWindow()
    {
        InitializeComponent();
        _isReady = true;
    }

    public void SetSnapshot(AiChangelogSnapshot snapshot)
    {
        _snapshot = snapshot;
        OfflineBadgeText.Text = snapshot.IsOfflineFixture ? "離線資料" : "公開同步";
        UpdateSourceText();
        BuildReleaseLists();
    }

    public void ShowProvider(AiProvider provider)
    {
        ProviderTabs.SelectedItem = provider == AiProvider.Codex ? CodexTab : ClaudeTab;
        ProviderViewed?.Invoke(provider);
    }

    private void BuildReleaseLists()
    {
        BuildReleaseList(CodexReleaseList, _snapshot.ForProvider(AiProvider.Codex), "#4F9DF7");
        BuildReleaseList(ClaudeReleaseList, _snapshot.ForProvider(AiProvider.ClaudeCode), "#E8843D");
    }

    private void BuildReleaseList(
        Panel target,
        IReadOnlyList<AiReleaseInfo> releases,
        string accentColor)
    {
        target.Children.Clear();
        if (releases.Count == 0)
        {
            target.Children.Add(new TextBlock
            {
                Text = "UNKNOWN / 未提供",
                Margin = new Thickness(4, 10, 4, 10),
                Foreground = Brush("#91A3B0"),
                FontSize = 13
            });
            return;
        }

        foreach (var release in releases)
        {
            var translationKey = TranslationKey(release);
            var translated = _showChinese && _translations.TryGetValue(translationKey, out var cached)
                ? cached
                : null;
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(7),
                Background = Brush("#17222C"),
                BorderBrush = Brush("#344551"),
                BorderThickness = new Thickness(1)
            };

            var content = new StackPanel();
            var heading = new DockPanel();
            var dateText = new TextBlock
            {
                Text = release.PublishedAt is null
                    ? "日期未提供"
                    : release.PublishedAt.Value.LocalDateTime.ToString("yyyy/MM/dd"),
                Foreground = Brush("#91A3B0"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(dateText, Dock.Right);
            heading.Children.Add(dateText);
            var title = new TextBlock
            {
                Text = translated?.Title ?? release.Title,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(accentColor),
                TextWrapping = TextWrapping.Wrap
            };
            DockPanel.SetDock(title, Dock.Left);
            heading.Children.Insert(0, title);
            content.Children.Add(heading);
            content.Children.Add(new TextBlock
            {
                Text = _showChinese
                    ? $"版本 {release.Version} · {release.Surface}"
                    : $"Version {release.Version} · {release.Surface}",
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = Brush("#C8D4DC"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = translated?.Summary ?? release.Summary,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brush("#E7EDF1"),
                FontSize = 12,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap
            });

            var sourceText = release.SourceUri is null
                ? (_showChinese ? "官方來源：未提供" : "Official source: unavailable")
                : (_showChinese ? $"官方來源：{release.SourceUri.Host}" : $"Official source: {release.SourceUri.Host}");
            content.Children.Add(new TextBlock
            {
                Text = sourceText,
                Margin = new Thickness(0, 9, 0, 0),
                Foreground = Brush("#7F929F"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            });
            card.Child = content;
            target.Children.Add(card);
        }
    }

    private void ProviderTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || !ReferenceEquals(e.Source, ProviderTabs))
        {
            return;
        }

        ProviderViewed?.Invoke(ReferenceEquals(ProviderTabs.SelectedItem, ClaudeTab)
            ? AiProvider.ClaudeCode
            : AiProvider.Codex);

        if (_showChinese)
        {
            _ = EnsureProviderTranslatedAsync(CurrentProvider());
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (RefreshRequested is null)
        {
            return;
        }

        await RefreshRequested.Invoke();
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_showChinese)
        {
            _showChinese = false;
            _translationFailed = false;
            TranslateButton.Content = "中譯";
            TranslateButton.ToolTip = "將公開更新標題與摘要翻譯成繁體中文";
            UpdateSourceText();
            BuildReleaseLists();
            return;
        }

        _showChinese = true;
        _translationFailed = false;
        TranslateButton.Content = "EN";
        TranslateButton.ToolTip = "顯示官方英文原文";
        BuildReleaseLists();
        await EnsureProviderTranslatedAsync(CurrentProvider());
    }

    private async Task EnsureProviderTranslatedAsync(AiProvider provider)
    {
        if (!await _translationGate.WaitAsync(0))
        {
            return;
        }

        TranslateButton.IsEnabled = false;
        TranslateButton.Content = "...";
        UpdateSourceText("翻譯中");
        try
        {
            foreach (var release in _snapshot.ForProvider(provider))
            {
                var key = TranslationKey(release);
                if (_translations.ContainsKey(key))
                {
                    continue;
                }

                var translated = await _translationService.TranslateReleaseAsync(release);
                if (translated is not null)
                {
                    _translations[key] = translated;
                    BuildReleaseLists();
                }
                else
                {
                    _translationFailed = true;
                }
            }
        }
        finally
        {
            _translationGate.Release();
            TranslateButton.IsEnabled = true;
            TranslateButton.Content = _showChinese ? "EN" : "中譯";
            UpdateSourceText();
        }

        if (_showChinese && CurrentProvider() != provider)
        {
            await EnsureProviderTranslatedAsync(CurrentProvider());
        }
    }

    private AiProvider CurrentProvider() =>
        ReferenceEquals(ProviderTabs.SelectedItem, ClaudeTab)
            ? AiProvider.ClaudeCode
            : AiProvider.Codex;

    private void UpdateSourceText(string? activity = null)
    {
        var suffix = activity is not null
            ? $" · {activity}"
            : _showChinese && _translationFailed
                ? " · 部分翻譯失敗，已保留英文原文"
                : string.Empty;
        SourceText.Text = $"資料來源：{_snapshot.SourceLabel} · 載入 {FormatDate(_snapshot.LoadedAt)}{suffix}";
    }

    private static string TranslationKey(AiReleaseInfo release) =>
        $"{release.Provider}:{release.Id}:{release.Title}:{release.Summary}";

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private static SolidColorBrush Brush(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    private static string FormatDate(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue
            ? "UNKNOWN / 未提供"
            : value.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
}
