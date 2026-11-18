using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using UsageTools2026.Models;
using UsageTools2026.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Pen = System.Windows.Media.Pen;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using PasswordBox = System.Windows.Controls.PasswordBox;
using Slider = System.Windows.Controls.Slider;
using MenuItem = System.Windows.Controls.MenuItem;

namespace UsageTools2026;

public partial class MainWindow : Window
{
    private const string ToolTitle = "UsageTools2026.Public";

    private AppSettings _settings = AppSettingsService.Load();
    private SystemUsage _systemUsage = SystemUsage.Empty;
    private readonly DispatcherTimer _resetCreditFlashTimer;
    private bool _resetCreditFlashActive;
    private int _codexNewsUnread;
    private int _claudeNewsUnread;
    private bool _aiNewsUsesOfflineFixture = true;
    private Rect _layoutWorkArea;
    private bool _adjustingLayout;

    public event EventHandler? RequestRefresh;
    public event EventHandler? RequestReport;
    public event EventHandler? RequestHide;
    public event EventHandler? RequestShowUpdate;
    public event Action<AiProvider>? RequestAiChangelog;
    public event EventHandler? RequestQuit;
    public event EventHandler? RequestRestart;
    public event EventHandler? RequestClaudeLogin;
    public event EventHandler? DataSourceSettingsChanged;
    public event EventHandler? WindowTopmostChanged;
    public event EventHandler? AppearanceChanged;

    public UsageSnapshot CurrentSnapshot { get; private set; } = UsageSnapshot.Empty;
    private readonly Button _claudeLoginButton = new() { Height = 30, Margin = new Thickness(0, 0, 0, 8) };

    public MainWindow()
    {
        InitializeComponent();
        _claudeLoginButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ButtonBrush");
        _claudeLoginButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextBrush");
        _claudeLoginButton.Click += (_, _) => { ToolsPopup.IsOpen = false; RequestClaudeLogin?.Invoke(this, EventArgs.Empty); };
        if (MobileButton.Parent is StackPanel tools) tools.Children.Insert(tools.Children.IndexOf(MobileButton), _claudeLoginButton);
        Title = ToolTitle;
        VersionText.Text = CurrentVersionText();
        _resetCreditFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _resetCreditFlashTimer.Tick += (_, _) =>
        {
            _resetCreditFlashTimer.Stop();
            _resetCreditFlashActive = false;
            ApplyResetCreditButtonStyle(CurrentSnapshot.CodexResetCredits);
        };
        ApplyTheme();
        ApplyLanguage();
        ApplyDataSourceSettingsUi();
        SourceInitialized += (_, _) => RefreshWorkArea();
        Loaded += (_, _) => RefreshWorkArea();
        LocationChanged += (_, _) =>
        {
            if (!_adjustingLayout) RefreshWorkArea();
        };
        SizeChanged += (_, _) => KeepInsideWorkArea();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(RefreshWorkArea, DispatcherPriority.Loaded);
        SystemParameters.StaticPropertyChanged += OnDesktopMetricsChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnDesktopMetricsChanged;
    }

    public void ApplySnapshot(UsageSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;

        LastUpdatedText.ToolTip = !_settings.CodexDataEnabled && !_settings.ClaudeDataEnabled
            ? T("AI 資料讀取已全部關閉", "All AI data reading is off")
            : snapshot.LastUpdatedLocal is null
            ? T("\u5c1a\u672a\u8b80\u5230 Codex token_count", "No Codex token_count yet")
            : $"{T("\u6700\u5f8c\u66f4\u65b0", "Last update")} {snapshot.LastUpdatedLocal:yyyy/MM/dd HH:mm:ss}";
        LastUpdatedText.Text = !_settings.CodexDataEnabled && !_settings.ClaudeDataEnabled
            ? T("AI 資料已關閉", "AI data off")
            : snapshot.LastUpdatedLocal is null
            ? T("尚未取得用量", "No usage yet")
            : $"{T("畫面刷新", "UI refreshed")} {snapshot.LastUpdatedLocal:yyyy/MM/dd HH:mm}";

        ApplyCodex(snapshot);
        ApplyClaude(snapshot);
        UpdateResetCreditUi(_settings.CodexDataEnabled ? snapshot.CodexResetCredits : CodexResetCreditSummary.Empty);

        ProjectsList.ItemsSource = (!_settings.CodexDataEnabled
                ? new[] { new ProjectUsage(T("Codex 資料讀取已關閉", "Codex data reading is off"), 0) }
                : snapshot.TopProjects)
            .DefaultIfEmpty(new ProjectUsage(T("\u5c1a\u7121\u4eca\u65e5\u5c08\u6848\u8cc7\u6599", "No project data today"), 0))
            .Select(p => new ProjectRow(p.Name, p.TotalTokens == 0 ? "-" : p.TotalTokens.ToString("N0")))
            .ToList();

        BottomTodayText.Text = _settings.CodexDataEnabled
            ? $"{T("\u4eca\u65e5", "Today")} : {snapshot.Today.TotalTokens:N0} tokens"
            : $"{T("\u4eca\u65e5", "Today")} : -- tokens";
    }

    public void SetAvailableUpdate(UpdateInfo? update)
    {
        UpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.ToolTip = update is null
            ? null
            : T($"\u6709\u65b0\u7248\u672c {update.Version} \u53ef\u7528", $"Version {update.Version} is available");
    }

    public void ResetRestartSwitch()
    {
        RestartToolsToggle.IsEnabled = true;
        RestartToolsToggle.IsChecked = false;
        ToolsPopup.IsOpen = true;
    }

    public void SetOfflinePreviewLabel()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "UNKNOWN";
        VersionText.Text = $"v{version} · Health A Preview";
        VersionText.ToolTip = T(
            "健康修正離線預覽；數字為測試資料，未部署、未連正式 feed",
            "Health A offline preview; fixture data, not deployed, no production feed");
    }

    public void SetAiChangelogStatus(int codexUnread, int claudeUnread, bool usesOfflineFixture)
    {
        _codexNewsUnread = Math.Max(0, codexUnread);
        _claudeNewsUnread = Math.Max(0, claudeUnread);
        _aiNewsUsesOfflineFixture = usesOfflineFixture;
        var theme = ThemeCatalog.Resolve(CurrentThemeId());
        ApplyAiNewsButton(CodexNewsButton, _codexNewsUnread, theme.CodexAccent, "Codex");
        ApplyAiNewsButton(ClaudeNewsButton, _claudeNewsUnread, theme.ClaudeAccent, "Claude Code");
    }

    private void ApplyAiNewsButton(Button button, int unread, string accent, string providerName)
    {
        button.Content = unread > 0 ? "N" : "i";
        button.Foreground = unread > 0 ? Brushes.White : Brush(accent);
        button.Background = unread > 0 ? Brush(accent) : ResourceBrush("ButtonBrush");
        button.BorderBrush = unread > 0 ? Brush(accent) : ResourceBrush("BorderBrush");
        button.ToolTip = unread > 0
            ? T(
                $"{providerName} 有 {unread} 則未讀更新摘要" + (_aiNewsUsesOfflineFixture ? "（離線驗證資料）" : string.Empty),
                $"{providerName} has {unread} unread update summaries" + (_aiNewsUsesOfflineFixture ? " (offline fixture)" : string.Empty))
            : T(
                $"{providerName} 更新資訊" + (_aiNewsUsesOfflineFixture ? "（離線驗證資料）" : string.Empty),
                $"{providerName} update information" + (_aiNewsUsesOfflineFixture ? " (offline fixture)" : string.Empty));
    }

    public void FlashResetCreditReminder()
    {
        if (CurrentSnapshot.CodexResetCredits.AvailableCount <= 0)
        {
            return;
        }

        _resetCreditFlashActive = true;
        _resetCreditFlashTimer.Stop();
        _resetCreditFlashTimer.Start();
        ApplyResetCreditButtonStyle(CurrentSnapshot.CodexResetCredits);
    }

    private void UpdateResetCreditUi(CodexResetCreditSummary summary)
    {
        var count = summary.AvailableCount;
        CodexResetCreditButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count <= 0)
        {
            CodexResetCreditButton.IsChecked = false;
            CodexResetCreditPopup.IsOpen = false;
            CodexResetCreditList.Children.Clear();
            return;
        }

        CodexResetCreditButton.Content = $"\u21BB {count}";
        var earliest = summary.Earliest;
        CodexResetCreditButton.ToolTip = earliest is null
            ? T($"Codex \u91cd\u7f6e\u984d\u5ea6\uff1a\u53ef\u7528 {count} \u6b21", $"Codex reset credits: {count} available")
            : T(
                $"Codex \u91cd\u7f6e\u984d\u5ea6\uff1a\u53ef\u7528 {count} \u6b21\n\u6700\u65e9 {earliest.ExpiresAt.LocalDateTime:MM/dd HH:mm} \u5230\u671f",
                $"Codex reset credits: {count} available\nEarliest expiry {earliest.ExpiresAt.LocalDateTime:MM/dd HH:mm}");
        CodexResetCreditHeader.Text = T($"Codex \u91cd\u7f6e\u984d\u5ea6 \u00b7 \u53ef\u7528 {count} \u6b21", $"Codex reset credits \u00b7 {count} available");
        CodexResetCreditSyncText.Text = summary.IsStale ? T("\u5feb\u53d6", "Cached") : T("\u5b98\u65b9", "Official");
        RebuildResetCreditList(CodexResetCreditList, summary);
        ApplyResetCreditButtonStyle(summary);
    }

    private void RebuildResetCreditList(StackPanel list, CodexResetCreditSummary summary)
    {
        list.Children.Clear();
        var theme = ThemeCatalog.Resolve(CurrentThemeId());
        var now = DateTimeOffset.Now;
        foreach (var credit in summary.Credits.OrderBy(credit => credit.ExpiresAt))
        {
            var expiring = credit.ExpiresAt - now <= TimeSpan.FromHours(24);
            var row = new Border
            {
                Background = expiring ? Brush("#4A1118") : Brush(theme.Panel),
                BorderBrush = expiring ? Brush("#FF3B45") : Brush(theme.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brush(expiring ? "#FF3B45" : theme.Success),
                VerticalAlignment = VerticalAlignment.Center
            });

            var text = new StackPanel { Margin = new Thickness(7, 0, 0, 0) };
            Grid.SetColumn(text, 1);
            text.Children.Add(new TextBlock
            {
                Text = T("\u5b8c\u6574\u91cd\u7f6e\uff08\u6bcf\u9031 + 5 \u5c0f\u6642\uff09", "Full reset (Weekly + 5 hr)"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = expiring ? Brushes.White : Brush(theme.Text)
            });
            text.Children.Add(new TextBlock
            {
                Text = T(
                    $"\u65bc {credit.ExpiresAt.LocalDateTime:yyyy/MM/dd HH:mm} \u5230\u671f",
                    $"Expires {credit.ExpiresAt.LocalDateTime:yyyy/MM/dd HH:mm}"),
                Margin = new Thickness(0, 2, 0, 0),
                FontSize = 10,
                Foreground = expiring ? Brush("#FFD2D5") : Brush(theme.Muted)
            });
            grid.Children.Add(text);
            row.Child = grid;
            list.Children.Add(row);
        }
    }

    private void ApplyResetCreditButtonStyle(CodexResetCreditSummary summary)
    {
        if (CodexResetCreditButton is null)
        {
            return;
        }

        var theme = ThemeCatalog.Resolve(CurrentThemeId());
        var expiring = summary.HasExpiringCredit(TimeSpan.FromHours(24));
        if (_resetCreditFlashActive)
        {
            CodexResetCreditButton.Background = Brush("#D97706");
            CodexResetCreditButton.BorderBrush = Brush("#FFD166");
            CodexResetCreditButton.Foreground = Brushes.White;
        }
        else if (expiring)
        {
            CodexResetCreditButton.Background = Brush("#5E1117");
            CodexResetCreditButton.BorderBrush = Brush("#FF3B45");
            CodexResetCreditButton.Foreground = Brushes.White;
        }
        else
        {
            CodexResetCreditButton.Background = Brush(theme.Button);
            CodexResetCreditButton.BorderBrush = Brush(theme.Border);
            CodexResetCreditButton.Foreground = Brush(theme.CodexAccent);
        }
    }

    public void ApplySystemMetrics(SystemUsage usage)
    {
        _systemUsage = usage;

        CpuBar.Value = usage.CpuPercent;
        RamBar.Value = usage.RamPercent;
        CpuValueText.Text = $"{usage.CpuPercent:0}%";
        RamValueText.Text = usage.TotalMemoryBytes == 0
            ? $"{usage.RamPercent:0}%"
            : $"{usage.RamPercent:0}% / {DisplayFormatService.FormatCompactBytes(usage.TotalMemoryBytes)}";
        NetworkValueText.Text = $"NET  Down {DisplayFormatService.FormatRate(usage.DownloadBytesPerSecond)}   Up {DisplayFormatService.FormatRate(usage.UploadBytesPerSecond)}";
    }

    public void PositionNearTray()
    {
        _settings = AppSettingsService.Load();
        if (_settings.HasMainWindowPosition)
        {
            Left = _settings.MainWindowLeft;
            Top = _settings.MainWindowTop;
            EnsureVisiblePosition();
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, workArea.Right - Width - 18);
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - ActualHeight - 18);
    }

    private void RefreshWorkArea()
    {
        if (_adjustingLayout) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        ApplyWorkAreaBounds(new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
    }

    private void OnDesktopMetricsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.WorkArea) or nameof(SystemParameters.VirtualScreenHeight)
            or nameof(SystemParameters.VirtualScreenWidth))
            Dispatcher.BeginInvoke(RefreshWorkArea, DispatcherPriority.Loaded);
    }

    internal void ApplyWorkAreaBounds(Rect workArea)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0) return;
        _layoutWorkArea = workArea;
        MaxHeight = Math.Max(1, workArea.Height - 16);
        KeepInsideWorkArea();
    }

    private void KeepInsideWorkArea()
    {
        if (_adjustingLayout || _layoutWorkArea.Height <= 0 || ActualHeight <= 0) return;
        _adjustingLayout = true;
        try
        {
            // Content can grow after a quota arrives or a theme changes.
            Left = Math.Clamp(double.IsFinite(Left) ? Left : _layoutWorkArea.Left + 8,
                _layoutWorkArea.Left + 8, Math.Max(_layoutWorkArea.Left + 8, _layoutWorkArea.Right - ActualWidth - 8));
            Top = Math.Clamp(double.IsFinite(Top) ? Top : _layoutWorkArea.Top + 8,
                _layoutWorkArea.Top + 8, Math.Max(_layoutWorkArea.Top + 8, _layoutWorkArea.Bottom - ActualHeight - 8));
        }
        finally { _adjustingLayout = false; }
    }

    private void ApplyCodex(UsageSnapshot snapshot)
    {
        if (!_settings.CodexDataEnabled)
        {
            ApplyCodexDisabled();
            return;
        }

        CodexPlanButton.IsEnabled = true;

        var theme = ThemeCatalog.Resolve(CurrentThemeId());
        RebuildPlanList(
            SubscriptionProvider.Codex,
            snapshot.Codex.PlanType,
            CodexPlanPopupTitle,
            CodexPlanList,
            theme.CodexAccent);
        var hasActiveWork = snapshot.CodexWorkWindows.Count > 0;
        var windows = new List<(double Used, int? Minutes, DateTimeOffset? ResetsAt)>();
        if (snapshot.Codex.PrimaryUsedPercent is { } primaryUsed)
        {
            windows.Add((primaryUsed, snapshot.Codex.PrimaryWindowMinutes, snapshot.Codex.PrimaryResetsAt));
        }

        if (snapshot.Codex.SecondaryUsedPercent is { } secondaryUsed)
        {
            windows.Add((secondaryUsed, snapshot.Codex.SecondaryWindowMinutes, snapshot.Codex.SecondaryResetsAt));
        }

        windows.Sort((left, right) => (left.Minutes ?? int.MaxValue).CompareTo(right.Minutes ?? int.MaxValue));

        CodexPrimaryRow.Visibility = Visibility.Visible;
        CodexSessionBar.Visibility = Visibility.Visible;
        CodexSessionReset.Visibility = Visibility.Visible;

        SetStatus(CodexStatusLight, CodexStatusText, UsageFreshness.State(snapshot.Codex), snapshot.Codex.OfficialSyncedAt);

        if (!snapshot.Codex.HasData || windows.Count == 0)
        {
            CodexSessionLabel.Text = T("\u7528\u91cf", "Usage");
            CodexSessionValue.Text = "--";
            CodexSessionValue.Foreground = ResourceBrush("MutedBrush");
            CodexSessionBar.Value = 0;
            CodexSessionBar.Foreground = ResourceBrush("MutedBrush");
            CodexSessionReset.Text = T("\u66ab\u6642\u7121\u6cd5\u540c\u6b65\uff0c\u7a0d\u5f8c\u91cd\u8a66", "Usage unavailable; retrying");
            CodexSessionValue.ToolTip = CodexSessionReset.Text;
            SetCodexSecondaryVisibility(false);
            RebuildCodexWorkList(snapshot.CodexWorkWindows);
            return;
        }

        var first = windows[0];
        CodexSessionLabel.Text = FormatCodexWindowTitle(first.Minutes, 0);
        CodexSessionValue.Text = FormatUsed(first.Used);
        ApplyUsageBrushes(CodexSessionValue, CodexSessionBar, first.Used, theme);
        CodexSessionBar.Value = first.Used;
        CodexSessionReset.Text = FormatCodexResetForWindow(first.ResetsAt, first.Used, hasActiveWork, first.Minutes);
        CodexSessionValue.ToolTip = CodexLimitTooltip(first.Used, hasActiveWork);

        SetCodexSecondaryVisibility(windows.Count > 1);
        if (windows.Count > 1)
        {
            var second = windows[1];
            CodexWeekLabel.Text = FormatCodexWindowTitle(second.Minutes, 1);
            CodexWeekValue.Text = FormatUsed(second.Used);
            ApplyUsageBrushes(CodexWeekValue, CodexWeekBar, second.Used, theme);
            CodexWeekBar.Value = second.Used;
            CodexWeekReset.Text = FormatCodexResetForWindow(second.ResetsAt, second.Used, hasActiveWork, second.Minutes);
            CodexWeekValue.ToolTip = CodexLimitTooltip(second.Used, hasActiveWork);
        }

        RebuildCodexWorkList(snapshot.CodexWorkWindows);
    }

    private void ApplyCodexDisabled()
    {
        CodexPlanButton.IsEnabled = false;
        CodexPlanPopup.IsOpen = false;
        CodexWorkPopup.IsOpen = false;
        CodexResetCreditPopup.IsOpen = false;
        CodexWorkButton.Visibility = Visibility.Collapsed;
        CodexResetCreditButton.Visibility = Visibility.Collapsed;
        CodexSessionLabel.Text = T("用量", "Usage");
        CodexSessionValue.Text = T("已關閉", "Off");
        CodexSessionValue.Foreground = ResourceBrush("MutedBrush");
        CodexSessionBar.Value = 0;
        CodexSessionBar.Foreground = ResourceBrush("MutedBrush");
        CodexSessionReset.Text = T("Codex 資料讀取與載入已關閉", "Codex data reading and loading is off");
        CodexStatusLight.Fill = ResourceBrush("MutedBrush");
        CodexStatusLight.ToolTip = CodexSessionReset.Text;
        SetCodexSecondaryVisibility(false);
    }

    private void SetCodexSecondaryVisibility(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CodexSecondaryRow.Visibility = visibility;
        CodexWeekBar.Visibility = visibility;
        CodexWeekReset.Visibility = visibility;
    }

    private string FormatCodexWindowTitle(int? windowMinutes, int slot)
    {
        var label = windowMinutes switch
        {
            > 0 and <= 24 * 60 => "Session",
            > 0 and <= 8 * 24 * 60 => T("\u672c\u9031", "Weekly"),
            _ => T("\u7528\u91cf", "Usage")
        };
        var fallback = slot == 0 ? "5h" : "7d";
        return $"{label} {DisplayFormatService.FormatWindowLabel(windowMinutes, fallback)}";
    }

    private void RebuildCodexWorkList(IReadOnlyList<CodexWorkWindow> windows)
    {
        var count = windows.Count;
        CodexWorkButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CodexWorkButton.Content = $"\U0001FA9F {count} ▾";

        if (count == 0)
        {
            CodexWorkButton.IsChecked = false;
            return;
        }

        CodexWorkTitle.Text = T("正在跑的 Codex 工作視窗", "Active Codex windows");
        CodexWorkList.Children.Clear();
        foreach (var window in windows)
        {
            CodexWorkList.Children.Add(BuildWorkRow(window));
        }
    }

    private void RebuildClaudeWorkList(IReadOnlyList<CodexWorkWindow> windows)
    {
        var count = windows.Count;
        ClaudeWorkButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClaudeWorkButton.Content = $"\U0001FA9F {count} ▾";

        if (count == 0)
        {
            ClaudeWorkButton.IsChecked = false;
            return;
        }

        ClaudeWorkTitle.Text = T("正在跑的 Claude Code 工作視窗", "Active Claude Code windows");
        ClaudeWorkList.Children.Clear();
        foreach (var window in windows)
        {
            ClaudeWorkList.Children.Add(BuildWorkRow(window));
        }
    }

    private FrameworkElement BuildWorkRow(CodexWorkWindow window)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ring = BuildContextRing(window.Percent, 44);
        Grid.SetColumn(ring, 0);
        row.Children.Add(ring);

        var info = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(BuildWorkHeading(window));
        info.Children.Add(new TextBlock
        {
            Text = $"{T("上下文", "Context")} {FormatTokens(window.UsedTokens)} / {FormatTokens(window.ContextWindow)}  ·  {RelativeTime(window.LastActivity)}",
            FontSize = 11,
            Foreground = ResourceBrush("MutedBrush"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{T("模型", "Model")} {FormatWorkModel(window.Model)}  ·  Effort {FormatWorkEffort(window.Effort)}",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("AccentBrush"),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        return row;
    }

    private FrameworkElement BuildWorkHeading(CodexWorkWindow window)
    {
        var workName = string.IsNullOrWhiteSpace(window.WorkName)
            ? T("未命名工作", "Untitled work")
            : window.WorkName.Trim();
        return BuildMarqueeText($"{window.Project}  ·  {workName}");
    }

    private FrameworkElement BuildMarqueeText(string text)
    {
        const double gapWidth = 30;
        var viewport = new Grid
        {
            Height = 18,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = text
        };
        var transform = new TranslateTransform();
        var strip = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = transform,
            IsHitTestVisible = false
        };

        TextBlock CreateLabel() => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = ResourceBrush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var primary = CreateLabel();
        var gap = new Border { Width = gapWidth, Visibility = Visibility.Collapsed };
        var duplicate = CreateLabel();
        duplicate.Visibility = Visibility.Collapsed;
        strip.Children.Add(primary);
        strip.Children.Add(gap);
        strip.Children.Add(duplicate);
        viewport.Children.Add(strip);

        void StopAnimation()
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        }

        void UpdateAnimation()
        {
            if (!viewport.IsLoaded || !viewport.IsVisible ||
                viewport.ActualWidth <= 0 || primary.ActualWidth <= viewport.ActualWidth + 2)
            {
                gap.Visibility = Visibility.Collapsed;
                duplicate.Visibility = Visibility.Collapsed;
                StopAnimation();
                return;
            }

            gap.Visibility = Visibility.Visible;
            duplicate.Visibility = Visibility.Visible;
            var travel = primary.ActualWidth + gapWidth;
            var seconds = Math.Clamp(travel / 26.0, 6.0, 20.0);
            var animation = new DoubleAnimation(0, -travel, new Duration(TimeSpan.FromSeconds(seconds)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }

        viewport.Loaded += (_, _) => UpdateAnimation();
        viewport.SizeChanged += (_, _) => UpdateAnimation();
        viewport.IsVisibleChanged += (_, _) => UpdateAnimation();
        viewport.Unloaded += (_, _) => StopAnimation();
        return viewport;
    }

    private static FrameworkElement BuildContextRing(double percent, double size) =>
        ContextRingFactory.Build(percent, size, true);

    private Brush ResourceBrush(string key) => Resources[key] as Brush ?? Brushes.Gray;

    private static string FormatTokens(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:0}k" : tokens.ToString();

    private string FormatWorkModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? T("未記錄", "N/A") : model.Trim();

    private string FormatWorkEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return T("未記錄", "N/A");
        }

        var value = effort.Trim();
        return value.Length == 1
            ? value.ToUpperInvariant()
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private string RelativeTime(DateTimeOffset? time)
    {
        if (time is null)
        {
            return string.Empty;
        }

        var span = DateTimeOffset.Now - time.Value;
        if (span.TotalMinutes < 1)
        {
            return T("剛剛", "just now");
        }

        if (span.TotalMinutes < 60)
        {
            return T($"{span.TotalMinutes:0} 分前", $"{span.TotalMinutes:0}m ago");
        }

        return T($"{span.TotalHours:0} 小時前", $"{span.TotalHours:0}h ago");
    }

    private void ApplyClaude(UsageSnapshot snapshot)
    {
        if (!_settings.ClaudeDataEnabled)
        {
            ApplyClaudeDisabled();
            return;
        }

        ClaudePlanButton.IsEnabled = true;

        var theme = ThemeCatalog.Resolve(CurrentThemeId());
        var claude = snapshot.Claude;
        RebuildPlanList(
            SubscriptionProvider.Claude,
            claude.PlanType,
            ClaudePlanPopupTitle,
            ClaudePlanList,
            theme.ClaudeAccent);
        UpdateClaudeFable(claude, theme);
        RebuildClaudeDetails(claude, theme);

        // Green light only when we actually have an official percentage (live or last-synced).
        var hasOfficial = claude.HasLimitData;
        var isInstalled = claude.SyncState != ClaudeSyncState.NotInstalled;
        SetStatus(ClaudeStatusLight, ClaudeStatusText, UsageFreshness.State(claude), claude.OfficialSyncedAt);

        ClaudeSessionValue.Text = claude.PrimaryUsedPercent is { } primaryUsed
            ? FormatUsed(primaryUsed)
            : isInstalled
                ? T("未提供", "UNKNOWN")
                : T("\u672a\u555f\u7528", "Not enabled");
        ClaudeWeekValue.Text = claude.SecondaryUsedPercent is { } secondaryUsed
            ? FormatUsed(secondaryUsed)
            : isInstalled
                ? T("未提供", "UNKNOWN")
                : T("\u7121\u8cc7\u6599", "No data");
        ClaudeSessionBar.Value = claude.PrimaryUsedPercent ?? 0;
        ClaudeWeekBar.Value = claude.SecondaryUsedPercent ?? 0;
        ApplyUsageBrushes(ClaudeSessionValue, ClaudeSessionBar, claude.PrimaryUsedPercent ?? 0, theme);
        ApplyUsageBrushes(ClaudeWeekValue, ClaudeWeekBar, claude.SecondaryUsedPercent ?? 0, theme);

        ClaudeSessionReset.Text = hasOfficial
            ? FormatClaudeFiveHourReset(claude)
            : T("\u5b98\u65b9\u8cc7\u6599\u4e0d\u53ef\u7528", "Official data unavailable");
        ClaudeWeekReset.Text = hasOfficial
            ? FormatClaudeWeeklyReset(claude)
            : T("\u5b98\u65b9\u8cc7\u6599\u4e0d\u53ef\u7528", "Official data unavailable");
        if (UsageFreshness.State(claude) != UsageDataState.Fresh)
        {
            var cached = UsageFreshness.ClaudeCacheLabel(claude, _settings.Language == AppLanguage.ZhTw);
            ClaudeSessionReset.Text = cached;
            ClaudeWeekReset.Text = cached;
            ClaudeSessionValue.Text = claude.PrimaryUsedPercent is { } five ? FormatUsed(five) + T(" 快取", " cached") : T("已過期／未同步", "Expired / unsynced");
            ClaudeWeekValue.Text = claude.SecondaryUsedPercent is { } seven ? FormatUsed(seven) + T(" 快取", " cached") : T("已過期／未同步", "Expired / unsynced");
        }
        RebuildClaudeWorkList(snapshot.ClaudeWorkWindows);
    }

    private void ApplyClaudeDisabled()
    {
        ClaudePlanButton.IsEnabled = false;
        ClaudePlanPopup.IsOpen = false;
        ClaudeWorkPopup.IsOpen = false;
        ClaudeWorkButton.Visibility = Visibility.Collapsed;
        ClaudeDetailsButton.IsChecked = false;
        ClaudeDetailsButton.Visibility = Visibility.Collapsed;
        ClaudeDetailsList.Children.Clear();
        ClaudeFableBadge.Visibility = Visibility.Collapsed;
        ClaudeSessionValue.Text = T("已關閉", "Off");
        ClaudeWeekValue.Text = T("已關閉", "Off");
        ClaudeSessionValue.Foreground = ResourceBrush("MutedBrush");
        ClaudeWeekValue.Foreground = ResourceBrush("MutedBrush");
        ClaudeSessionBar.Value = 0;
        ClaudeWeekBar.Value = 0;
        ClaudeSessionBar.Foreground = ResourceBrush("MutedBrush");
        ClaudeWeekBar.Foreground = ResourceBrush("MutedBrush");
        ClaudeSessionReset.Text = T("Claude Code 資料讀取與載入已關閉", "Claude Code data reading and loading is off");
        ClaudeWeekReset.Text = ClaudeSessionReset.Text;
        ClaudeStatusLight.Fill = ResourceBrush("MutedBrush");
        ClaudeStatusLight.ToolTip = ClaudeSessionReset.Text;
    }

    private string FormatClaudeFiveHourReset(ClaudeUsage claude)
    {
        if (claude.PrimaryUsedPercent is null) return T("用量未提供", "Usage unavailable");
        if (claude.PrimaryResetsAt is not null)
        {
            var used = claude.PrimaryUsedPercent ?? 0;
            return used >= 99.95
                ? FormatClaudeLimitReachedReset("5h", claude.PrimaryResetsAt)
                : FormatReset(claude.PrimaryResetsAt, used);
        }

        return claude.PrimaryUsedPercent is 0
            ? T("\u5f9e\u767c\u9001\u8a0a\u606f\u6642\u958b\u59cb\uff085h\uff09", "Starts when a message is sent (5h)")
            : T("\u91cd\u7f6e\u6642\u9593\u672a\u63d0\u4f9b", "Reset time unavailable");
    }

    private string FormatClaudeWeeklyReset(ClaudeUsage claude)
    {
        if (claude.SecondaryUsedPercent is null) return T("用量未提供", "Usage unavailable");
        var used = claude.SecondaryUsedPercent ?? 0;
        if (claude.SecondaryResetsAt is null)
        {
            return used >= 99.95
                ? FormatClaudeLimitReachedReset("7d", null)
                : T("\u91cd\u7f6e\u6642\u9593\u672a\u63d0\u4f9b", "Reset time unavailable");
        }

        return used >= 99.95
            ? FormatClaudeLimitReachedReset("7d", claude.SecondaryResetsAt)
            : FormatWeeklyReset(claude.SecondaryResetsAt, used);
    }

    private string FormatClaudeLimitReachedReset(string label, DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return T($"\u5df2\u9054 {label} \u4e0a\u9650 \u00b7 \u91cd\u7f6e\u6642\u9593\u672a\u63d0\u4f9b",
                $"Limit reached ({label}) \u00b7 reset time unavailable");
        }

        var reset = resetAt.Value.LocalDateTime;
        return T($"\u5df2\u9054 {label} \u4e0a\u9650 \u00b7 \u91cd\u7f6e {reset:yyyy/MM/dd HH:mm}",
            $"Limit reached ({label}) \u00b7 Reset {reset:yyyy/MM/dd HH:mm}");
    }
    private void UpdateClaudeFable(ClaudeUsage claude, ThemeDefinition theme)
    {
        if (claude.FableUsedPercent is not { } usedPercent)
        {
            ClaudeFableBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // The official pool is named "Fable" and is shared by every Fable version (5, 5.1, 5.2).
        var clamped = Math.Clamp(usedPercent, 0, 100);
        ClaudeFableBadge.Visibility = Visibility.Visible;
        ClaudeFableText.Text = "Fable";
        ClaudeFableText.Foreground = Brush(theme.ClaudeAccent);
        ClaudeFableFill.Width = Math.Max(0, ClaudeFableBadge.Width - 2) * clamped / 100.0;
        ClaudeFableFill.Background = Brush(theme.ClaudeAccent);
        ClaudeFableBadge.ToolTip = claude.FableResetsAt is { } reset
            ? T(
                $"Fable 週額度已用 {clamped:0}%\n於 {reset.LocalDateTime:MM/dd HH:mm} 重置",
                $"Fable weekly {clamped:0}% used\nResets {reset.LocalDateTime:MM/dd HH:mm}")
            : T($"Fable 週額度已用 {clamped:0}%", $"Fable weekly {clamped:0}% used");
    }

    private void RebuildClaudeDetails(ClaudeUsage claude, ThemeDefinition theme)
    {
        var details = claude.Details;
        var now = DateTimeOffset.Now;
        var resetsCount = details?.Resets?.AvailableCount(now);
        ClaudeDetailsButton.Visibility = details is null ? Visibility.Collapsed : Visibility.Visible;
        if (details is null)
        {
            ClaudeDetailsButton.IsChecked = false;
            return;
        }

        ClaudeDetailsButton.Foreground = Brush(theme.ClaudeAccent);
        ClaudeDetailsButton.Content = (UsageFreshness.State(claude) == UsageDataState.Fresh ? "" : T("快取 · ", "Cached · ")) +
            (resetsCount is > 0 ? T($"券 {resetsCount} · 詳情", $"{resetsCount} resets · details") : T("詳情", "Details"));
        ClaudeDetailsButton.ToolTip = details.Resets is null
            ? T("Claude Code 額度池與可用模型（重置券官方未提供）", "Claude Code pools and models (resets not provided)")
            : T($"Claude Code 重置券可用 {(resetsCount?.ToString() ?? "數量未提供")}，點開看各張到期時間",
                $"Claude Code resets available: {(resetsCount?.ToString() ?? "unknown")}; open for each expiry");
        // Build the list only while the popup is open, so routine polls do not create controls.
        if (ClaudeDetailsButton.IsChecked != true)
        {
            ClaudeDetailsList.Children.Clear();
            return;
        }

        var list = ClaudeDetailsList;
        list.Children.Clear();
        var fresh = UsageFreshness.State(claude) == UsageDataState.Fresh;
        void Heading(string text) => list.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = ResourceBrush("TextBrush"),
            Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 6)
        });
        void Line(string text, string brush = "MutedBrush", double size = 11) => list.Children.Add(new TextBlock
        {
            Text = text, FontSize = size, Foreground = ResourceBrush(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3)
        });

        Heading(T("我的方案", "My plan"));
        Line(SubscriptionPlanCatalog.CurrentLabel(SubscriptionProvider.Claude, claude.PlanType, _settings.Language == AppLanguage.En) + " · " +
             (details.PlanSource is null ? T("官方未提供", "Not provided") : T("官方偵測", "Officially detected")), "TextBrush");
        if (details.PlanSource is { } source && list.Children[^1] is TextBlock planLine) planLine.ToolTip = T("來源欄位：", "Source field: ") + source;
        Line((fresh ? T("官方資料 ", "Official data ") : T("快取資料 ", "Cached data ")) +
             (claude.OfficialSyncedAt?.LocalDateTime.ToString("MM/dd HH:mm") ?? T("時間未提供", "time unknown")));

        Heading(T("額度池", "Quota pools"));
        if (details.Pools.Count == 0) Line(T("官方未提供額度池", "No pools provided"));
        foreach (var pool in details.Pools)
        {
            var name = (pool.Label == "5H" ? T("5 小時", "5 hours") : T("每週 7D", "Weekly 7D")) + (pool.ModelName is { } model ? " · " + model : "");
            var used = UsageFreshness.Window(pool.Percent, pool.ResetsAt, now);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = ResourceBrush("TextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 0) });
            var value = new TextBlock
            {
                Text = used is { } u ? T($"剩 {100 - u:0}%", $"{100 - u:0}% left") : T("未提供", "Unknown"),
                FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = used is >= 80 ? ResourceBrush("DangerBrush") : Brush(theme.ClaudeAccent)
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            list.Children.Add(row);
            list.Children.Add(new ProgressBar
            {
                Height = 5, Minimum = 0, Maximum = 100, Value = used is { } v ? 100 - v : 0, Margin = new Thickness(0, 3, 0, 2),
                Foreground = used is >= 80 ? ResourceBrush("DangerBrush") : Brush(theme.ClaudeAccent),
                Background = ResourceBrush("ButtonBrush"), BorderThickness = new Thickness(0)
            });
            var meta = pool.ResetsAt is { } at ? T($"重置 {at.LocalDateTime:MM/dd HH:mm}", $"Resets {at.LocalDateTime:MM/dd HH:mm}") : T("重置時間未提供", "Reset time unavailable");
            if (pool.AllowancePercentOfWeekly is { } allowance) meta += T($" · 最多占每週額度 {allowance:0}%", $" · up to {allowance:0}% of weekly");
            Line(meta);
        }

        Heading(T("重置券（僅查看）", "Limit resets (read-only)"));
        if (details.Resets is not { } resets) Line(T("官方未提供重置券資料", "Not provided by the official source"));
        else
        {
            Line(T($"可用 {(resetsCount?.ToString() ?? "數量未提供")} 張 · 請到 Claude 設定頁使用", $"{(resetsCount?.ToString() ?? "Unknown")} available · use them in Claude settings"), "TextBrush");
            var index = 0;
            foreach (var grant in resets.Active(now).Where(g => g.Left is null or > 0).OrderBy(g => g.EndsAt ?? DateTimeOffset.MaxValue))
            {
                var units = grant.Left is > 0 ? Math.Min(grant.Left.Value, 20) : 1;
                for (var i = 0; i < units; i++)
                {
                    index++;
                    var expiry = grant.EndsAt is { } end ? T($"{end.LocalDateTime:yyyy/MM/dd HH:mm} 到期", $"expires {end.LocalDateTime:yyyy/MM/dd HH:mm}") : T("到期時間未提供", "expiry not provided");
                    var note = grant.Left is null ? T(" · 可用次數未提供", " · count unknown") : !grant.UsableNow ? T(" · 目前不可使用", " · not usable now") : "";
                    Line((grant.Left is null ? T("數量未知 · ", "Count unknown · ") : T($"第 {index} 張 ", $"#{index} ")) + ClaudeOfficialDetailsParser.CreditKindName(grant.Category, _settings.Language == AppLanguage.En) + " · " + expiry + note, "TextBrush");
                }
                if (grant.Left is > 20) Line(T($"另有 {grant.Left - 20} 張，皆於上述時間到期", $"{grant.Left - 20} more with the same expiry"));
            }
            if (index == 0) Line(T("目前沒有可用的重置券", "No resets available"));
        }

        Heading(T("額外用量（usage credits）", "Usage credits"));
        Line(details.ExtraUsage is not { } extra ? T("官方未提供", "Not provided")
            : extra.Enabled
                ? T("已開啟 · 本月 ", "On · this month ") +
                  (extra.Used is { } spent ? $"{extra.Currency} {spent:0.##}" : T("未提供", "Not provided")) +
                  (extra.MonthlyLimit is { } limit ? $" / {limit:0.##}" : T(" · 上限未提供", " · cap not provided")) + (extra.SpendLimitReached ? T(" · 已達上限", " · cap reached") : "")
                : T("未開啟 · 與重置券分開計算", "Off · separate from limit resets"));

        Heading(T("此帳號可用模型", "Models on this account"));
        if (details.Models is not { } models) Line(T("模型清單尚未同步", "Model list not synced yet"));
        else
        {
            var visible = models.Where(m => !m.Inactive).ToArray();
            Line(string.Join("、", visible.Where(m => !m.Overflow).Select(m => m.Name)), "TextBrush");
            var more = visible.Where(m => m.Overflow).Select(m => m.Name).ToArray();
            if (more.Length > 0) Line(T("更多模型：", "More models: ") + string.Join("、", more), "TextBrush");
            Line(T($"共 {visible.Length} 個可選（含「更多模型」{visible.Count(m => m.Overflow)} 個）", $"{visible.Length} selectable ({visible.Count(m => m.Overflow)} under More models)"));
            if (details.ModelsSyncedAt is { } modelsAt) Line(T("清單更新：", "List updated: ") + modelsAt.LocalDateTime.ToString("MM/dd HH:mm"));
        }
        var sessionModels = CurrentSnapshot.ClaudeWorkWindows.Select(w => w.Model).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().Take(3).ToArray();
        if (sessionModels.Length > 0)
            Line(T("本機工作階段正在用：", "Local sessions use: ") + string.Join("、", sessionModels!));
    }

    private void RebuildPlanList(
        SubscriptionProvider provider,
        string? rawPlan,
        TextBlock title,
        StackPanel list,
        string accent)
    {
        var activeId = SubscriptionPlanCatalog.Normalize(provider, rawPlan);
        var providerName = provider == SubscriptionProvider.Codex ? "Codex" : "Claude Code";
        title.Text = activeId is null
            ? T($"{providerName} 訂閱方案 · 偵測中", $"{providerName} plans · detecting")
            : providerName + "\n" + SubscriptionPlanCatalog.CurrentLabel(provider, rawPlan, _settings.Language == AppLanguage.En);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxWidth = 240;
        title.Foreground = ResourceBrush("TextBrush");
        list.Children.Clear();

        foreach (var plan in SubscriptionPlanCatalog.GetPlans(provider))
        {
            var isActive = string.Equals(plan.Id, activeId, StringComparison.Ordinal);
            var row = new DockPanel
            {
                Height = 24,
                LastChildFill = true,
                Opacity = isActive ? 1.0 : 0.42
            };
            row.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = isActive ? Brush(accent) : ResourceBrush("MutedBrush")
            });
            row.Children.Add(new TextBlock
            {
                Text = _settings.Language == AppLanguage.ZhTw ? plan.NameZh : plan.NameEn,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isActive ? Brush(accent) : ResourceBrush("MutedBrush")
            });
            list.Children.Add(row);
        }
    }

    private void CodexPlan_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClaudePlanPopup.IsOpen = false;
        CodexPlanPopup.IsOpen = !CodexPlanPopup.IsOpen;
    }

    private void ClaudeDetails_Checked(object sender, RoutedEventArgs e)
    {
        ClaudePlanPopup.IsOpen = false;
        RebuildClaudeDetails(CurrentSnapshot.Claude, ThemeCatalog.Resolve(CurrentThemeId()));
    }

    private void ClaudeDetails_Unchecked(object sender, RoutedEventArgs e) => ClaudeDetailsList.Children.Clear();

    private void ClaudeFableBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ClaudeDetailsButton.Visibility != Visibility.Visible) return;
        e.Handled = true;
        ClaudeDetailsButton.IsChecked = ClaudeDetailsButton.IsChecked != true;
    }

    private void ClaudePlan_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CodexPlanPopup.IsOpen = false;
        ClaudeDetailsButton.IsChecked = false;
        ClaudePlanPopup.IsOpen = !ClaudePlanPopup.IsOpen;
    }

    private string ClaudeSyncLine(ClaudeUsage claude)
    {
        var syncedAt = claude.OfficialSyncedAt?.LocalDateTime;
        return claude.SyncState switch
        {
            ClaudeSyncState.LiveSynced => syncedAt is null
                ? T("\u5b98\u65b9\u5df2\u540c\u6b65", "Official usage synced")
                : T($"\u5b98\u65b9\u5df2\u540c\u6b65 {syncedAt:MM/dd HH:mm}", $"Synced {syncedAt:MM/dd HH:mm}"),
            ClaudeSyncState.CachedLocked => T(
                $"\u4e0a\u6b21\u540c\u6b65 {syncedAt:MM/dd HH:mm}\uff08Claude \u57f7\u884c\u4e2d\uff0c\u7121\u6cd5\u5373\u6642\u5237\u65b0\uff09",
                $"Last synced {syncedAt:MM/dd HH:mm} (Claude running, cannot refresh live)"),
            ClaudeSyncState.CachedSyncFailed => T(
                $"\u4e0a\u6b21\u540c\u6b65 {syncedAt:MM/dd HH:mm}\uff08\u672c\u6b21\u540c\u6b65\u5931\u6557\uff0c\u986f\u793a\u820a\u503c\uff09",
                $"Last synced {syncedAt:MM/dd HH:mm} (sync failed, showing cached value)"),
            ClaudeSyncState.NeverSynced => T(
                "\u5c1a\u672a\u540c\u6b65\u5b98\u65b9 Usage\uff1b\u8acb\u5728 Claude Desktop \u95dc\u9589\u5f8c\u81ea\u52d5\u540c\u6b65",
                "Official usage never synced; it will sync automatically once Claude Desktop is closed"),
            _ => T("\u672a\u5075\u6e2c\u5230 Claude Desktop", "Claude Desktop not detected")
        };
    }

    private void SetStatus(System.Windows.Shapes.Ellipse light, System.Windows.Controls.TextBlock text,
        UsageDataState state, DateTimeOffset? synced)
    {
        light.Fill = Brush(UsageFreshness.Color(state));
        light.ToolTip = UsageFreshness.Describe(state, synced, _settings.Language == AppLanguage.ZhTw);
        text.Text = string.Empty;
    }

    private void ApplyLanguage()
    {
        LanguageButton.Content = _settings.Language == AppLanguage.ZhTw ? "EN" : "\u4e2d";
        var currentTheme = ThemeCatalog.Resolve(CurrentThemeId());
        ThemeButton.Content = ThemeButtonLabel(currentTheme);
        ThemeButton.ToolTip = _settings.Language == AppLanguage.ZhTw ? currentTheme.NameZh : currentTheme.NameEn;
        OpacityButton.ToolTip = T("切換背景透明度", "Switch background opacity");
        UpdateOpacityButton();

        ClaudeWeekLabel.Text = T("\u672c\u9031", "Weekly");
        CodexWeekLabel.Text = T("\u672c\u9031", "Weekly");
        ProjectsTitle.Text = T("\u5c08\u6848\u7528\u91cf", "Project Usage");
        SystemTitle.Text = T("\u7cfb\u7d71\u72c0\u614b", "System Status");
        TodayTabButton.Content = T("\u4eca\u65e5", "Today");
        ReportTabButton.Content = T("\u5831\u544a", "Report");
        ContinueTabButton.Content = T("\u958b\u6a5f", "Startup");
        RateLabelText.Text = string.Empty;
        SyncLabelText.Text = string.Empty;
        RefreshButton.Content = T("\u7acb\u5373\u66f4\u65b0", "Refresh");
        QuitButton.Content = T("\u7d50\u675f", "Quit");
        AboutButton.Content = T("\u95dc\u65bc", "About");
        AboutButton.ToolTip = T("\u95dc\u65bc UsageTools2026", "About UsageTools2026");
        ToolsButton.ToolTip = T("資料讀取與工具重新啟動", "Data reading and tool restart");
        ToolsPopupTitle.Text = T("資料與工具", "Data & tools");
        _claudeLoginButton.Content = T("登入 Claude 並同步", "Sign in to Claude and sync");
        CodexDataLabel.Text = T("Codex 資料讀取", "Codex data reading");
        ClaudeDataLabel.Text = T("Claude Code 資料讀取", "Claude Code data reading");
        RestartToolsLabel.Text = T("Tools Restart", "Tools Restart");
        AlwaysOnTopLabel.Text = T("視窗置頂", "Always on top");
        var mobileConfigured = PersonalMobileService.IsConfigured;
        PersonalMobileLabel.Text = mobileConfigured
            ? T("手機用量與通知", "Mobile usage and alerts")
            : T("手機服務尚未設定", "Mobile service not configured");
        PersonalMobileToggle.IsChecked = mobileConfigured && _settings.PersonalMobileEnabled;
        MobileButton.Content = T("\U0001F4F1 手機連線", "\U0001F4F1 Connect phone");
        if (!mobileConfigured)
        {
            var setupHint = T("需先部署手機服務並設定 HTTPS 網址。", "Deploy the mobile service and configure its HTTPS URL.");
            MobileButton.Visibility = Visibility.Collapsed;
            PersonalMobileLabel.ToolTip = setupHint;
            PersonalMobileToggle.IsEnabled = false;
            PersonalMobileToggle.ToolTip = setupHint;
        }
        VersionText.ToolTip = T($"\u76ee\u524d\u7248\u672c {VersionText.Text}", $"Current version {VersionText.Text}");
        ApplyThemeLabels(currentTheme);
        SetAiChangelogStatus(_codexNewsUnread, _claudeNewsUnread, _aiNewsUsesOfflineFixture);

        ApplySnapshot(CurrentSnapshot);
        ApplySystemMetrics(_systemUsage);
        ApplyDataSourceSettingsUi();
    }

    private void ApplyDataSourceSettingsUi()
    {
        Topmost = _settings.KeepWindowOnTop;
        CodexDataToggle.IsChecked = _settings.CodexDataEnabled;
        ClaudeDataToggle.IsChecked = _settings.ClaudeDataEnabled;
        AlwaysOnTopToggle.IsChecked = _settings.KeepWindowOnTop;
        CodexDataToggle.ToolTip = T("開關 Codex 用量、專案與工作資訊讀取", "Toggle Codex usage, project, and work data");
        ClaudeDataToggle.ToolTip = T("開關 Claude Code 用量與工作資訊讀取", "Toggle Claude Code usage and work data");
        AlwaysOnTopToggle.ToolTip = T("開啟時工具保持最上層；關閉後可被其他程式視窗蓋住", "Keep UsageTools above other windows; turn off to let other apps cover it");
        RestartToolsToggle.ToolTip = T("重新啟動 UsageTools2026", "Restart UsageTools2026");
    }

    private string CurrentThemeId() =>
        !string.IsNullOrEmpty(_settings.ThemeId)
            ? _settings.ThemeId
            : (_settings.Theme == AppTheme.Day ? "day" : "deep-night");

    private static string CurrentVersionText()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "v0.0.0.0" : $"v{version}";
    }

    private void ApplyTheme()
    {
        var theme = ThemeCatalog.Resolve(CurrentThemeId());

        SetBrush("ShellBrush", theme.Shell);
        SetBrush("PanelBrush", theme.Panel);
        SetBrush("CardBrush", theme.Card);
        SetBrush("TrackBrush", theme.Track);
        SetBrush("BorderBrush", theme.Border);
        SetBrush("OuterBorderBrush", theme.Outer);
        SetBrush("TextBrush", theme.Text);
        SetBrush("MutedBrush", theme.Muted);
        SetBrush("ButtonBrush", theme.Button);
        SetBrush("PrimaryButtonBrush", theme.PrimaryButton);
        SetBrush("AccentBrush", theme.Accent);
        SetBrush("SuccessBrush", theme.Success);
        SetBrush("WarningBrush", theme.Warning);
        SetBrush("DangerBrush", theme.Danger);
        ApplyThemeSurfaces(theme);
        ApplyBackgroundOpacity();
        ApplyThemeChrome(theme);
        UpdateOpacityButton();
        UpdateResetCreditUi(CurrentSnapshot.CodexResetCredits);

        CodexPlanPopupBorder.Background = ResourceBrush("CardBrush");
        CodexPlanPopupBorder.BorderBrush = ResourceBrush("OuterBorderBrush");
        ClaudePlanPopupBorder.Background = ResourceBrush("CardBrush");
        ClaudePlanPopupBorder.BorderBrush = ResourceBrush("OuterBorderBrush");
        ClaudeFableBadge.Background = ResourceBrush("ButtonBrush");
        ClaudeFableBadge.BorderBrush = ResourceBrush("BorderBrush");

        try
        {
            FontFamily = new System.Windows.Media.FontFamily(theme.Font);
        }
        catch
        {
            // fall back to the inherited font if the theme font is unavailable
        }
    }

    private void SetBrush(string key, string color)
    {
        Resources[key] = Brush(color);
    }

    private void ApplyBackgroundOpacity()
    {
        var opacity = NormalizeBackgroundOpacity(_settings.MainBackgroundOpacity);
        Resources["ShellBrush"] = WithOpacity(ResourceBrush("ShellBrush"), opacity);
        Resources["PanelBrush"] = WithOpacity(ResourceBrush("PanelBrush"), opacity);
        Resources["CardBrush"] = WithOpacity(ResourceBrush("CardBrush"), opacity);
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        var clone = brush.CloneCurrentValue();
        clone.Opacity = opacity;
        return clone;
    }

    private static double NormalizeBackgroundOpacity(double value)
    {
        if (Math.Abs(value - 0.3) < 0.05)
        {
            return 0.3;
        }

        if (Math.Abs(value - 0.5) < 0.05)
        {
            return 0.5;
        }

        if (Math.Abs(value - 0.7) < 0.05)
        {
            return 0.7;
        }

        return 0.9;
    }

    private void UpdateOpacityButton()
    {
        if (OpacityButton is null)
        {
            return;
        }

        OpacityButton.Content = $"{NormalizeBackgroundOpacity(_settings.MainBackgroundOpacity) * 100:0}%";
    }

    private void ApplyUsageBrushes(TextBlock valueText, ProgressBar bar, double percent, ThemeDefinition theme)
    {
        if (theme.VisualStyle == "win95")
        {
            var brush = Win95UsageBrush(IsClaudeBar(bar));
            valueText.Foreground = brush;
            bar.Foreground = brush;
            bar.Background = Win95TrackBrush();
            bar.BorderBrush = Brush("#7A7A7A");
            bar.BorderThickness = new Thickness(1);
            bar.Height = 9;
            return;
        }

        if (theme.VisualStyle == "cartoon-platformer")
        {
            var brush = HpMpUsageBrush(IsSessionBar(bar));
            valueText.Foreground = brush;
            bar.Foreground = brush;
            return;
        }

        if (theme.VisualStyle == "dragon-lair")
        {
            var brush = HpMpUsageBrush(IsSessionBar(bar));
            valueText.Foreground = brush;
            bar.Foreground = brush;
            bar.Background = DragonLairTrackBrush();
            bar.BorderBrush = BronzeBorderBrush();
            bar.BorderThickness = new Thickness(1);
            bar.Height = 9;
            return;
        }

        if (theme.BarMode == BarColorMode.ByUsage)
        {
            valueText.Foreground = DisplayFormatService.PercentBrush(percent);
            bar.Foreground = DisplayFormatService.UsageBarBrush(percent);
            return;
        }

        valueText.Foreground = ThemeValueBrush(theme);
        bar.Foreground = ThemeBarBrush(theme);
    }

    private static Brush ThemeValueBrush(ThemeDefinition theme)
    {
        if (theme.BarMode == BarColorMode.ThemeGradient && theme.BarGradient.Length > 0)
        {
            return Brush(theme.BarGradient[Math.Min(theme.BarGradient.Length - 1, 1)]);
        }

        return Brush(theme.BarColor);
    }

    private static Brush ThemeBarBrush(ThemeDefinition theme)
    {
        if (theme.BarMode == BarColorMode.ThemeGradient && theme.BarGradient.Length > 1)
        {
            return HorizontalGradient(theme.BarGradient);
        }

        return Brush(theme.BarColor);
    }

    private void ApplyThemeSurfaces(ThemeDefinition theme)
    {
        switch (theme.VisualStyle)
        {
            case "aquarium":
                Resources["ShellBrush"] = VerticalGradient("#010817", "#052B4D", "#075D77");
                Resources["PanelBrush"] = AquariumBrush();
                Resources["CardBrush"] = VerticalGradient("#06243D", "#093653", "#0B4B66");
                break;
            case "celestial":
                Resources["ShellBrush"] = CelestialBrush();
                Resources["PanelBrush"] = VerticalGradient("#080810", "#10101A", "#07070E");
                Resources["CardBrush"] = VerticalGradient("#15111B", "#0D0B13");
                break;
            case "black-hole":
                Resources["ShellBrush"] = BlackHoleBrush();
                Resources["PanelBrush"] = VerticalGradient("#030303", "#090504", "#130807");
                Resources["CardBrush"] = VerticalGradient("#0A0605", "#160B08");
                break;
            case "prism":
                Resources["ShellBrush"] = HorizontalGradient("#12001F", "#27105E", "#001D4C");
                Resources["PanelBrush"] = VerticalGradient("#16002F", "#0B102E");
                Resources["CardBrush"] = HorizontalGradient("#23063E", "#1B0B53", "#0A2D5B");
                break;
            case "dragon-lair":
                Resources["PanelBrush"] = WithOpacity(VerticalGradient("#171920", "#0F1117", "#181A20"), 0.82);
                Resources["CardBrush"] = WithOpacity(VerticalGradient("#24262D", "#15171D", "#202229"), 0.9);
                Resources["TrackBrush"] = DragonLairTrackBrush();
                Resources["ButtonBrush"] = VerticalGradient("#4B453D", "#25252A", "#111217");
                Resources["PrimaryButtonBrush"] = VerticalGradient("#6B4A29", "#2D2118", "#16120F");
                Resources["BorderBrush"] = BronzeBorderBrush();
                Resources["OuterBorderBrush"] = BronzeBorderBrush();
                break;
            case "newspaper":
                Resources["ShellBrush"] = NewspaperBrush();
                Resources["PanelBrush"] = Brush("#F5F0E5");
                Resources["CardBrush"] = Brush("#FFFDF4");
                break;
            case "cyberpunk":
                Resources["ShellBrush"] = CyberpunkBrush();
                Resources["PanelBrush"] = HorizontalGradient("#070016", "#10164A", "#190020");
                Resources["CardBrush"] = HorizontalGradient("#171035", "#0B2148", "#26072E");
                break;
            case "cartoon-platformer":
                Resources["ShellBrush"] = CartoonPosterBrush();
                Resources["PanelBrush"] = VerticalGradient("#FFD43A", "#FFE86E", "#FFD43A");
                Resources["CardBrush"] = VerticalGradient("#FFFDF2", "#FFF3C9");
                Resources["TrackBrush"] = VerticalGradient("#090909", "#2A2A2A", "#090909");
                break;
        }
    }

    private void ApplyThemeChrome(ThemeDefinition theme)
    {
        AppTitleText.Text = ToolTitle;
        HeaderPanel.Background = Brushes.Transparent;
        HeaderPanel.Margin = new Thickness(0, 0, 0, 8);
        HeaderPanel.LastChildFill = true;
        AppTitleText.Foreground = ResourceBrush("TextBrush");
        AppTitleText.FontSize = 16;
        AppTitleText.FontWeight = FontWeights.Bold;
        CodexTitleText.FontWeight = FontWeights.Bold;
        ClaudeTitleText.FontWeight = FontWeights.Bold;
        CodexTitleText.FontSize = 15;
        ClaudeTitleText.FontSize = 15;
        CodexPlanButton.Margin = new Thickness(0, 0, 4, 0);
        ClaudePlanButton.Margin = new Thickness(0, 0, 4, 0);
        CodexWorkButton.Width = 50;
        ClaudeWorkButton.Width = 50;
        CodexWorkButton.Padding = new Thickness(3, 2, 3, 2);
        ClaudeWorkButton.Padding = new Thickness(3, 2, 3, 2);
        ClaudeFableBadge.Width = 50;
        OuterShell.Effect = null;
        PanelFrame.Effect = null;
        ResetUsageBars(theme);

        OuterShell.CornerRadius = new CornerRadius(13);
        OuterShell.Padding = new Thickness(8);
        OuterShell.BorderThickness = new Thickness(1);
        PanelFrame.CornerRadius = new CornerRadius(10);
        PanelFrame.BorderThickness = new Thickness(1);

        var cards = ThemeCards();
        foreach (var card in cards)
        {
            card.CornerRadius = new CornerRadius(8);
            card.Padding = new Thickness(10);
            card.BorderThickness = new Thickness(1);
            card.BorderBrush = ResourceBrush("BorderBrush");
            card.Background = ResourceBrush("CardBrush");
        }

        SetAccentChrome(theme);

        switch (theme.VisualStyle)
        {
            case "celestial":
                AppTitleText.Text = ToolTitle;
                AppTitleText.FontSize = 17;
                AppTitleText.Foreground = Brush("#F4D18B");
                HeaderPanel.Background = VerticalGradient("#17101B", "#0A0910");
                OuterShell.CornerRadius = new CornerRadius(12);
                OuterShell.Padding = new Thickness(7);
                OuterShell.BorderThickness = new Thickness(1.5);
                PanelFrame.CornerRadius = new CornerRadius(10);
                foreach (var card in cards)
                {
                    card.CornerRadius = new CornerRadius(9);
                    card.Padding = new Thickness(11);
                    card.BorderThickness = new Thickness(1.2);
                    card.BorderBrush = Brush("#9C733F");
                }
                break;
            case "dragon-lair":
                AppTitleText.Text = ToolTitle;
                AppTitleText.FontSize = 17;
                AppTitleText.Foreground = Brush("#E7C27A");
                HeaderPanel.Background = VerticalGradient("#2A2B31", "#111318");
                HeaderPanel.Margin = new Thickness(0, 0, 0, 7);
                OuterShell.CornerRadius = new CornerRadius(3);
                OuterShell.Padding = new Thickness(8);
                OuterShell.BorderThickness = new Thickness(2.5);
                OuterShell.BorderBrush = BronzeBorderBrush();
                OuterShell.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 9,
                    ShadowDepth = 2,
                    Opacity = 0.58,
                    Color = Colors.Black
                };
                PanelFrame.CornerRadius = new CornerRadius(2);
                PanelFrame.BorderThickness = new Thickness(1.5);
                PanelFrame.BorderBrush = BronzeBorderBrush();
                foreach (var card in cards)
                {
                    card.CornerRadius = new CornerRadius(2);
                    card.Padding = new Thickness(10);
                    card.BorderThickness = new Thickness(1.5, 1, 1.5, 2);
                    card.BorderBrush = BronzeBorderBrush();
                    card.Background = WithOpacity(VerticalGradient("#282A31", "#14161C", "#202229"), 0.9);
                }
                break;
            case "newspaper":
                AppTitleText.Text = ToolTitle;
                AppTitleText.FontSize = 18;
                foreach (var card in cards)
                {
                    card.CornerRadius = new CornerRadius(0);
                    card.BorderThickness = new Thickness(1, 2, 1, 1);
                }
                break;
            case "win95":
                AppTitleText.Text = ToolTitle;
                AppTitleText.Foreground = Brushes.White;
                HeaderPanel.Background = Brush("#000080");
                HeaderPanel.Margin = new Thickness(0, 0, 0, 6);
                OuterShell.CornerRadius = new CornerRadius(0);
                OuterShell.Padding = new Thickness(3);
                OuterShell.BorderThickness = new Thickness(2);
                PanelFrame.CornerRadius = new CornerRadius(0);
                PanelFrame.BorderThickness = new Thickness(2);
                foreach (var card in cards)
                {
                    card.CornerRadius = new CornerRadius(0);
                    card.Padding = new Thickness(8);
                    card.BorderThickness = new Thickness(2);
                    card.BorderBrush = Brush("#404040");
                }
                break;
            case "cyberpunk":
                AppTitleText.Text = ToolTitle;
                AppTitleText.FontSize = 16;
                AppTitleText.Foreground = Brush("#00E7FF");
                HeaderPanel.Background = HorizontalGradient("#14002A", "#00314C", "#3A0047");
                OuterShell.CornerRadius = new CornerRadius(6);
                OuterShell.Padding = new Thickness(6);
                OuterShell.BorderThickness = new Thickness(2);
                PanelFrame.CornerRadius = new CornerRadius(4);
                PanelFrame.BorderThickness = new Thickness(1.5);
                foreach (var card in cards)
                {
                    card.CornerRadius = new CornerRadius(2);
                    card.Padding = new Thickness(10);
                    card.BorderThickness = new Thickness(1.5);
                    card.BorderBrush = Brush("#FF2BD6");
                }
                break;
            case "cartoon-platformer":
                AppTitleText.Text = ToolTitle;
                AppTitleText.FontSize = 18;
                AppTitleText.Foreground = Brush("#111111");
                HeaderPanel.Background = VerticalGradient("#FFF176", "#FFD43A");
                OuterShell.CornerRadius = new CornerRadius(16);
                OuterShell.Padding = new Thickness(6);
                OuterShell.BorderThickness = new Thickness(3);
                PanelFrame.CornerRadius = new CornerRadius(12);
                PanelFrame.BorderThickness = new Thickness(2);
                foreach (var card in cards)
                {
                    card.CornerRadius = new CornerRadius(10);
                    card.Padding = new Thickness(11);
                    card.BorderThickness = new Thickness(2);
                    card.BorderBrush = Brush("#111111");
                }
                CodexTitleText.FontWeight = FontWeights.ExtraBold;
                ClaudeTitleText.FontWeight = FontWeights.ExtraBold;
                break;
        }
    }

    private void ApplyThemeLabels(ThemeDefinition theme)
    {
        switch (theme.VisualStyle)
        {
            case "celestial":
                ProjectsTitle.Text = _settings.Language == AppLanguage.ZhTw ? "命盤紀錄" : "ASTRAL LEDGER";
                SystemTitle.Text = _settings.Language == AppLanguage.ZhTw ? "星象儀" : "STAR GAUGE";
                break;
            case "cyberpunk":
                ProjectsTitle.Text = _settings.Language == AppLanguage.ZhTw ? "資料金庫" : "DATA VAULT";
                SystemTitle.Text = _settings.Language == AppLanguage.ZhTw ? "霓虹核心" : "NEON CORE";
                break;
            case "newspaper":
                ProjectsTitle.Text = _settings.Language == AppLanguage.ZhTw ? "專案用量快報" : "PROJECT LEDGER";
                SystemTitle.Text = _settings.Language == AppLanguage.ZhTw ? "系統電訊" : "SYSTEM WIRE";
                break;
            case "dragon-lair":
                ProjectsTitle.Text = _settings.Language == AppLanguage.ZhTw ? "戰利品紀錄" : "LOOT LEDGER";
                SystemTitle.Text = _settings.Language == AppLanguage.ZhTw ? "能力狀態" : "STATUS PANEL";
                break;
            case "cartoon-platformer":
                ProjectsTitle.Text = _settings.Language == AppLanguage.ZhTw ? "金幣紀錄" : "Coin Score";
                SystemTitle.Text = _settings.Language == AppLanguage.ZhTw ? "能量狀態" : "Power Meter";
                break;
        }
    }

    private Border[] ThemeCards() => new[] { CodexCard, ClaudeCard, ProjectsCard, SystemCard, FooterCard };

    private void SetAccentChrome(ThemeDefinition theme)
    {
        CodexTitleText.Foreground = Brush(theme.CodexAccent);
        ClaudeTitleText.Foreground = Brush(theme.ClaudeAccent);
        CodexWorkButton.Foreground = Brush(theme.CodexAccent);
        ClaudeWorkButton.Foreground = Brush(theme.ClaudeAccent);
        SetAiChangelogStatus(_codexNewsUnread, _claudeNewsUnread, _aiNewsUsesOfflineFixture);
        MobileButton.Foreground = Brush(theme.CodexAccent);

        CodexBadge.Background = BadgeBrush(theme.CodexAccent);
        CodexBadge.BorderBrush = Brush(theme.CodexAccent);
        ClaudeBadge.Background = BadgeBrush(theme.ClaudeAccent);
        ClaudeBadge.BorderBrush = Brush(theme.ClaudeAccent);
        ProjectBadge.Background = BadgeBrush(theme.Success);
        ProjectBadge.BorderBrush = Brush(theme.Success);
        SystemBadge.Background = BadgeBrush(theme.Accent);
        SystemBadge.BorderBrush = Brush(theme.Accent);
    }

    private static Brush BadgeBrush(string accent)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accent);
        return new SolidColorBrush(System.Windows.Media.Color.FromArgb(48, color.R, color.G, color.B));
    }

    private static Brush HorizontalGradient(params string[] colors) => Gradient(new Point(0, 0.5), new Point(1, 0.5), colors);

    private static Brush VerticalGradient(params string[] colors) => Gradient(new Point(0.5, 0), new Point(0.5, 1), colors);

    private static Brush Gradient(Point start, Point end, params string[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (var i = 0; i < colors.Length; i++)
        {
            var offset = colors.Length == 1 ? 0 : i / (double)(colors.Length - 1);
            brush.GradientStops.Add(new GradientStop(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]),
                offset));
        }

        return brush;
    }

    private static Brush BlackHoleBrush()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.72, 0.18),
            GradientOrigin = new Point(0.63, 0.2),
            RadiusX = 0.95,
            RadiusY = 0.72
        };
        brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
        brush.GradientStops.Add(new GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#120503"), 0.38));
        brush.GradientStops.Add(new GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8A2400"), 0.58));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 0.78));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1));
        return brush;
    }

    private static Brush FamicomBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(VerticalGradient("#B7161C", "#8A1116"), null, new RectangleGeometry(new Rect(0, 0, 120, 120))));
        group.Children.Add(new GeometryDrawing(Brush("#E8E1D2"), null, new RectangleGeometry(new Rect(0, 0, 120, 72))));
        group.Children.Add(new GeometryDrawing(Brush("#C8BFB0"), null, new RectangleGeometry(new Rect(0, 72, 120, 16))));
        group.Children.Add(new GeometryDrawing(Brush("#D32027"), null, new RectangleGeometry(new Rect(0, 88, 120, 32))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#2B2522"), 1.2), new LineGeometry(new Point(0, 72), new Point(120, 72))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#2B2522"), 1), new LineGeometry(new Point(0, 88), new Point(120, 88))));
        group.Children.Add(new GeometryDrawing(Brush("#B7AEA0"), null, new RectangleGeometry(new Rect(18, 18, 34, 20))));
        group.Children.Add(new GeometryDrawing(Brush("#2B2522"), null, new RectangleGeometry(new Rect(22, 26, 26, 4))));
        group.Children.Add(new GeometryDrawing(Brush("#2B2522"), null, new RectangleGeometry(new Rect(33, 15, 4, 26))));
        group.Children.Add(new GeometryDrawing(Brush("#D32027"), null, new EllipseGeometry(new Point(88, 24), 7, 7)));
        group.Children.Add(new GeometryDrawing(Brush("#D32027"), null, new EllipseGeometry(new Point(104, 34), 7, 7)));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 120, 120),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush CyberpunkBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(HorizontalGradient("#05000F", "#10164A", "#260030"), null, new RectangleGeometry(new Rect(0, 0, 132, 132))));
        var cyan = new Pen(Brush("#00E7FF"), 0.9);
        var magenta = new Pen(Brush("#FF2BD6"), 1.05);
        group.Children.Add(new GeometryDrawing(null, cyan, new LineGeometry(new Point(0, 24), new Point(132, 24))));
        group.Children.Add(new GeometryDrawing(null, cyan, new LineGeometry(new Point(0, 72), new Point(132, 72))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#1249FF"), 0.75), new LineGeometry(new Point(22, 0), new Point(22, 132))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#1249FF"), 0.75), new LineGeometry(new Point(88, 0), new Point(88, 132))));
        group.Children.Add(new GeometryDrawing(null, magenta, new LineGeometry(new Point(0, 116), new Point(116, 0))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#FFE646"), 0.9), new LineGeometry(new Point(36, 132), new Point(132, 40))));
        group.Children.Add(new GeometryDrawing(Brush("#00E7FF"), null, new RectangleGeometry(new Rect(98, 18, 14, 3))));
        group.Children.Add(new GeometryDrawing(Brush("#FF2BD6"), null, new RectangleGeometry(new Rect(18, 92, 24, 3))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 132, 132),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush MatrixBrush(string baseColor = "#000000", string lineColor = "#063D0C")
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 48, 8))));
        group.Children.Add(new GeometryDrawing(Brush(lineColor), null, new RectangleGeometry(new Rect(0, 0, 48, 1))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 48, 8),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush AquariumBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(VerticalGradient("#05223C", "#083E5A"), null, new RectangleGeometry(new Rect(0, 0, 96, 96))));
        var bubblePen = new Pen(Brush("#55BDE0"), 1);
        group.Children.Add(new GeometryDrawing(null, bubblePen, new EllipseGeometry(new Point(18, 24), 4, 4)));
        group.Children.Add(new GeometryDrawing(null, bubblePen, new EllipseGeometry(new Point(62, 36), 6, 6)));
        group.Children.Add(new GeometryDrawing(null, bubblePen, new EllipseGeometry(new Point(40, 78), 3, 3)));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 96, 96),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush NewspaperBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brush("#F2EAD9"), null, new RectangleGeometry(new Rect(0, 0, 72, 72))));
        var linePen = new Pen(Brush("#D6CCB9"), 0.8);
        group.Children.Add(new GeometryDrawing(null, linePen, new LineGeometry(new Point(0, 18), new Point(72, 18))));
        group.Children.Add(new GeometryDrawing(null, linePen, new LineGeometry(new Point(0, 42), new Point(72, 42))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#E7DDCA"), 0.6), new LineGeometry(new Point(24, 0), new Point(24, 72))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 72, 72),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush CelestialBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(VerticalGradient("#03040A", "#090912", "#05050C"), null, new RectangleGeometry(new Rect(0, 0, 180, 180))));

        var dimGold = new Pen(Brush("#5F4523"), 0.8);
        var gold = new Pen(Brush("#C99A43"), 1.05);
        var softGold = Brush("#D9AF60");

        group.Children.Add(new GeometryDrawing(null, dimGold, new EllipseGeometry(new Point(92, 48), 44, 44)));
        group.Children.Add(new GeometryDrawing(null, dimGold, new EllipseGeometry(new Point(92, 48), 31, 31)));
        group.Children.Add(new GeometryDrawing(null, gold, new EllipseGeometry(new Point(92, 48), 18, 18)));
        group.Children.Add(new GeometryDrawing(null, dimGold, new LineGeometry(new Point(92, 0), new Point(92, 96))));
        group.Children.Add(new GeometryDrawing(null, dimGold, new LineGeometry(new Point(36, 48), new Point(148, 48))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#7E5B2D"), 0.75), new LineGeometry(new Point(58, 16), new Point(126, 82))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#7E5B2D"), 0.75), new LineGeometry(new Point(126, 16), new Point(58, 82))));

        group.Children.Add(new GeometryDrawing(Brush("#F5D789"), null, new EllipseGeometry(new Point(92, 48), 3.2, 3.2)));
        group.Children.Add(new GeometryDrawing(softGold, null, new EllipseGeometry(new Point(24, 24), 2.2, 2.2)));
        group.Children.Add(new GeometryDrawing(softGold, null, new EllipseGeometry(new Point(152, 30), 1.8, 1.8)));
        group.Children.Add(new GeometryDrawing(softGold, null, new EllipseGeometry(new Point(34, 122), 2.4, 2.4)));
        group.Children.Add(new GeometryDrawing(softGold, null, new EllipseGeometry(new Point(142, 132), 2, 2)));

        var constellation = new Pen(Brush("#8D6A36"), 0.8);
        group.Children.Add(new GeometryDrawing(null, constellation, new LineGeometry(new Point(18, 146), new Point(46, 132))));
        group.Children.Add(new GeometryDrawing(null, constellation, new LineGeometry(new Point(46, 132), new Point(78, 152))));
        group.Children.Add(new GeometryDrawing(null, constellation, new LineGeometry(new Point(126, 118), new Point(154, 104))));
        group.Children.Add(new GeometryDrawing(null, constellation, new LineGeometry(new Point(154, 104), new Point(170, 132))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 180, 180),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush InfernalBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(VerticalGradient("#030000", "#130403", "#050000"), null, new RectangleGeometry(new Rect(0, 0, 150, 150))));
        group.Children.Add(new GeometryDrawing(Brush("#170604"), null, new RectangleGeometry(new Rect(0, 0, 150, 32))));
        group.Children.Add(new GeometryDrawing(Brush("#0B0201"), null, new RectangleGeometry(new Rect(0, 86, 150, 64))));

        var ember = new Pen(Brush("#7D1B10"), 1.2);
        var hot = new Pen(Brush("#D9471D"), 1.05);
        group.Children.Add(new GeometryDrawing(null, ember, new LineGeometry(new Point(12, 16), new Point(64, 72))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#3D0B07"), 2.2), new LineGeometry(new Point(20, 112), new Point(118, 28))));
        group.Children.Add(new GeometryDrawing(null, hot, new LineGeometry(new Point(92, 0), new Point(132, 46))));
        group.Children.Add(new GeometryDrawing(null, ember, new LineGeometry(new Point(0, 136), new Point(42, 92))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#A52A16"), 0.8), new LineGeometry(new Point(88, 118), new Point(150, 84))));

        group.Children.Add(new GeometryDrawing(Brush("#401008"), null, new RectangleGeometry(new Rect(0, 42, 150, 10))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#B13A1C"), 0.8), new LineGeometry(new Point(0, 42), new Point(150, 52))));
        group.Children.Add(new GeometryDrawing(Brush("#F05A24"), null, new EllipseGeometry(new Point(104, 42), 2.4, 2.4)));
        group.Children.Add(new GeometryDrawing(Brush("#D83A18"), null, new EllipseGeometry(new Point(34, 96), 2, 2)));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 150, 150),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush BronzeBorderBrush() =>
        VerticalGradient("#4A3925", "#C5A369", "#6E5432", "#D1B176", "#3B2C1D");

    private static Brush DragonLairTrackBrush() =>
        VerticalGradient("#030407", "#11131A", "#050608");

    private static Brush FieldBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brush("#0B5525"), null, new RectangleGeometry(new Rect(0, 0, 120, 120))));
        group.Children.Add(new GeometryDrawing(Brush("#116B2F"), null, new RectangleGeometry(new Rect(0, 0, 120, 24))));
        group.Children.Add(new GeometryDrawing(Brush("#116B2F"), null, new RectangleGeometry(new Rect(0, 48, 120, 24))));
        group.Children.Add(new GeometryDrawing(Brush("#116B2F"), null, new RectangleGeometry(new Rect(0, 96, 120, 24))));
        var pen = new Pen(Brush("#DFFFE0"), 1.4);
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(60, 0), new Point(60, 120))));
        group.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(60, 60), 18, 18)));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 120, 120),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static bool IsSessionBar(ProgressBar bar) =>
        bar.Name.Contains("Session", StringComparison.OrdinalIgnoreCase) ||
        bar.Name.Contains("FiveHour", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeBar(ProgressBar bar) =>
        bar.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase);

    private ProgressBar[] UsageBars() => new[]
    {
        CodexSessionBar,
        CodexWeekBar,
        ClaudeSessionBar,
        ClaudeWeekBar
    };

    private void ResetUsageBars(ThemeDefinition theme)
    {
        foreach (var bar in UsageBars())
        {
            bar.Height = 6;
            bar.BorderThickness = new Thickness(0);
            bar.BorderBrush = ResourceBrush("BorderBrush");
            bar.Background = ResourceBrush("TrackBrush");
        }
    }

    private static Brush Win95UsageBrush(bool isClaude)
    {
        var color = isClaude ? "#E6A000" : "#2A9AD9";
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brush(color), null, new RectangleGeometry(new Rect(0, 0, 7, 9))));
        group.Children.Add(new GeometryDrawing(Brush("#C0C0C0"), null, new RectangleGeometry(new Rect(7, 0, 1, 9))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 9),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush Win95TrackBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brush("#F2F2F2"), null, new RectangleGeometry(new Rect(0, 0, 8, 9))));
        group.Children.Add(new GeometryDrawing(Brush("#C0C0C0"), null, new RectangleGeometry(new Rect(7, 0, 1, 9))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 9),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush HpMpUsageBrush(bool isSession)
    {
        return isSession
            ? HorizontalGradient("#4C0606", "#B71318", "#F04431", "#FF8A62")
            : HorizontalGradient("#08114F", "#1C35B9", "#4C6BFF", "#A06DFF");
    }

    private static Brush CartoonPosterBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(VerticalGradient("#E91417", "#F03224", "#FF7A2A"), null, new RectangleGeometry(new Rect(0, 0, 120, 120))));
        group.Children.Add(new GeometryDrawing(Brush("#FFD43A"), null, new RectangleGeometry(new Rect(0, 86, 120, 34))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#111111"), 2), new LineGeometry(new Point(0, 86), new Point(120, 86))));
        group.Children.Add(new GeometryDrawing(Brush("#168EF2"), null, new EllipseGeometry(new Point(106, 22), 9, 9)));
        group.Children.Add(new GeometryDrawing(Brush("#22A83A"), null, new RectangleGeometry(new Rect(8, 94, 24, 14), 7, 7)));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 120, 120),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static Brush CartoonCardBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(VerticalGradient("#FFF7B8", "#FFD928"), null, new RectangleGeometry(new Rect(0, 0, 96, 48))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#111111"), 1.25), new LineGeometry(new Point(0, 16), new Point(96, 16))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#111111"), 1.25), new LineGeometry(new Point(0, 32), new Point(96, 32))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#111111"), 1.25), new LineGeometry(new Point(24, 0), new Point(24, 16))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#111111"), 1.25), new LineGeometry(new Point(72, 16), new Point(72, 32))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#111111"), 1.25), new LineGeometry(new Point(48, 32), new Point(48, 48))));
        group.Children.Add(new GeometryDrawing(Brush("#FFFFFF"), null, new EllipseGeometry(new Point(82, 10), 3, 3)));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 96, 48),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    private static SolidColorBrush Brush(string color)
    {
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private string T(string zhTw, string en) => _settings.Language == AppLanguage.ZhTw ? zhTw : en;

    private string ThemeButtonLabel(ThemeDefinition theme) =>
        theme.Id switch
        {
            "deep-night" => _settings.Language == AppLanguage.ZhTw ? "深夜" : "Night",
            "day" => _settings.Language == AppLanguage.ZhTw ? "白天" : "Day",
            "cloud_observation" => _settings.Language == AppLanguage.ZhTw ? "天衡" : "Astral",
            "black_hole" => _settings.Language == AppLanguage.ZhTw ? "黑洞" : "Hole",
            "prism_arcade" => _settings.Language == AppLanguage.ZhTw ? "霓虹" : "Prism",
            "newspaper" => _settings.Language == AppLanguage.ZhTw ? "報紙" : "Paper",
            "world_cup" => _settings.Language == AppLanguage.ZhTw ? "賽博" : "Cyber",
            "cartoon_platformer" => _settings.Language == AppLanguage.ZhTw ? "卡通" : "Cartoon",
            _ => _settings.Language == AppLanguage.ZhTw ? theme.NameZh : theme.NameEn
        };

    private string FormatUsed(double percent)
    {
        return _settings.Language == AppLanguage.ZhTw
            ? $"{percent:0.#}% \u5df2\u7528"
            : $"{percent:0.#}% used";
    }

    private string FormatReset(DateTimeOffset? resetAt, double usedPercent)
    {
        if (resetAt is null)
        {
            return T("\u91cd\u7f6e\u6642\u9593\u672a\u77e5", "Reset unknown");
        }

        var remaining = Math.Max(0, 100 - usedPercent);
        var reset = resetAt.Value.LocalDateTime;
        return _settings.Language == AppLanguage.ZhTw
            ? $"\u91cd\u7f6e {reset:tt h:mm}\uff0c\u5269\u9918 {remaining:0.#}%"
            : $"Reset {reset:h:mm tt}, {remaining:0.#}% left";
    }

    private string FormatCodexResetForWindow(DateTimeOffset? resetAt, double usedPercent, bool hasActiveWork, int? windowMinutes)
    {
        return windowMinutes is > 24 * 60
            ? FormatCodexWeeklyReset(resetAt, usedPercent, hasActiveWork)
            : FormatCodexReset(resetAt, usedPercent, hasActiveWork);
    }

    private string FormatCodexWeeklyReset(DateTimeOffset? resetAt, double usedPercent, bool hasActiveWork)
    {
        if (usedPercent < 99.95)
        {
            return FormatWeeklyReset(resetAt, usedPercent);
        }

        var resetText = resetAt is null
            ? T("重置時間未提供", "Reset time unavailable")
            : T(
                $"重置 {resetAt.Value.LocalDateTime:yyyy/MM/dd HH:mm}",
                $"Reset {resetAt.Value.LocalDateTime:yyyy/MM/dd HH:mm}");

        return hasActiveWork
            ? T($"已達上限 · 當前回合可能繼續 · {resetText}",
                $"Limit reached · active turn may continue · {resetText}")
            : T($"已達上限 · {resetText}",
                $"Limit reached · {resetText}");
    }

    private string FormatWeeklyReset(DateTimeOffset? resetAt, double usedPercent)
    {
        if (resetAt is null)
        {
            return T("重置時間未提供", "Reset time unavailable");
        }

        var remaining = Math.Max(0, 100 - usedPercent);
        var reset = resetAt.Value.LocalDateTime;
        return _settings.Language == AppLanguage.ZhTw
            ? $"重置 {reset:yyyy/MM/dd HH:mm}，剩餘 {remaining:0.#}%"
            : $"Reset {reset:yyyy/MM/dd HH:mm}, {remaining:0.#}% left";
    }

    private string FormatCodexReset(DateTimeOffset? resetAt, double usedPercent, bool hasActiveWork)
    {
        if (usedPercent < 99.95)
        {
            return FormatReset(resetAt, usedPercent);
        }

        var resetText = resetAt is null
            ? T("\u91cd\u7f6e\u6642\u9593\u672a\u77e5", "Reset time unknown")
            : T(
                $"\u91cd\u7f6e {resetAt.Value.LocalDateTime:tt h:mm}",
                $"Reset {resetAt.Value.LocalDateTime:h:mm tt}");

        return hasActiveWork
            ? T($"\u5df2\u9054\u4e0a\u9650 \u00b7 \u7576\u524d\u56de\u5408\u53ef\u80fd\u7e7c\u7e8c \u00b7 {resetText}",
                $"Limit reached \u00b7 active turn may continue \u00b7 {resetText}")
            : T($"\u5df2\u9054\u4e0a\u9650 \u00b7 {resetText}",
                $"Limit reached \u00b7 {resetText}");
    }

    private string? CodexLimitTooltip(double usedPercent, bool hasActiveWork)
    {
        if (usedPercent < 99.95)
        {
            return null;
        }

        return hasActiveWork
            ? T(
                "\u5075\u6e2c\u5230 Codex \u6b63\u5728\u5de5\u4f5c\u3002\u4f9d OpenAI \u898f\u5247\uff0c\u76ee\u524d\u56de\u5408\u53ef\u80fd\u5728 fair-use \u9650\u5236\u5167\u7e7c\u7e8c\uff1b\u9019\u4e0d\u4ee3\u8868\u5f8c\u7e8c\u56de\u5408\u4e5f\u53ef\u7e7c\u7e8c\u3002",
                "Codex is active. Under OpenAI policy, the current turn may continue subject to fair-use limits; later turns can still be blocked.")
            : T(
                "\u7528\u91cf\u5df2\u9054\u4e0a\u9650\uff0c\u672a\u5075\u6e2c\u5230\u57f7\u884c\u4e2d\u7684 Codex \u5de5\u4f5c\u3002",
                "Usage limit reached and no active Codex work was detected.");
    }

    private async void Mobile_Click(object sender, RoutedEventArgs e)
    {
        MobileButton.IsEnabled = false;
        PersonalMobileToggle.IsEnabled = false;
        try
        {
            var path = await PersonalMobileService.PreparePairingAsync();
            _settings.PersonalMobileEnabled = true;
            PersonalMobileToggle.IsChecked = true;
            AppSettingsService.Save(_settings);
            RequestRefresh?.Invoke(this, EventArgs.Empty);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write("Mobile pairing failed: " + ex.GetType().Name);
            System.Windows.MessageBox.Show(T("手機配對尚未完成。請確認網路連線後重試；既有用量不受影響。", "Pairing was not completed. Check your connection and retry; existing usage is unaffected."));
        }
        finally { MobileButton.IsEnabled = true; PersonalMobileToggle.IsEnabled = true; }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RequestRefresh?.Invoke(this, EventArgs.Empty);

    private void PersonalMobileToggle_Click(object sender, RoutedEventArgs e)
    {
        if (PersonalMobileToggle.IsChecked == true)
        {
            PersonalMobileToggle.IsChecked = _settings.PersonalMobileEnabled;
            Mobile_Click(sender, e);
            return;
        }
        _settings.PersonalMobileEnabled = PersonalMobileToggle.IsChecked == true;
        AppSettingsService.Save(_settings);
        if (_settings.PersonalMobileEnabled) RequestRefresh?.Invoke(this, EventArgs.Empty);
    }

    private void Report_Click(object sender, RoutedEventArgs e) => RequestReport?.Invoke(this, EventArgs.Empty);

    private void Update_Click(object sender, RoutedEventArgs e) => RequestShowUpdate?.Invoke(this, EventArgs.Empty);

    private void CodexNews_Click(object sender, RoutedEventArgs e) =>
        RequestAiChangelog?.Invoke(AiProvider.Codex);

    private void ClaudeNews_Click(object sender, RoutedEventArgs e) =>
        RequestAiChangelog?.Invoke(AiProvider.ClaudeCode);

    private void Hide_Click(object sender, RoutedEventArgs e) => RequestHide?.Invoke(this, EventArgs.Empty);

    private void Quit_Click(object sender, RoutedEventArgs e) => RequestQuit?.Invoke(this, EventArgs.Empty);

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow(ThemeCatalog.Resolve(CurrentThemeId()), _settings.Language)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void Tools_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToolsPopup.IsOpen = !ToolsPopup.IsOpen;
    }

    private void CodexDataToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.CodexDataEnabled = CodexDataToggle.IsChecked == true;
        AppSettingsService.Save(_settings);
        ApplySnapshot(CurrentSnapshot);
        DataSourceSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClaudeDataToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClaudeDataEnabled = ClaudeDataToggle.IsChecked == true;
        AppSettingsService.Save(_settings);
        ApplySnapshot(CurrentSnapshot);
        DataSourceSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AlwaysOnTopToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.KeepWindowOnTop = AlwaysOnTopToggle.IsChecked == true;
        Topmost = _settings.KeepWindowOnTop;
        AppSettingsService.Save(_settings);
        WindowTopmostChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestartToolsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (RestartToolsToggle.IsChecked != true)
        {
            return;
        }

        RestartToolsToggle.IsEnabled = false;
        Dispatcher.BeginInvoke(
            () => RequestRestart?.Invoke(this, EventArgs.Empty),
            DispatcherPriority.Background);
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        var enabled = StartupService.ToggleLaunchAtStartup();
        System.Windows.MessageBox.Show(
            enabled ? T("UsageTools 2026 \u5c07\u96a8 Windows \u555f\u52d5\u3002", "UsageTools 2026 will start with Windows.") : T("\u5df2\u79fb\u9664\u958b\u6a5f\u81ea\u555f\u3002", "Startup entry removed."),
            "UsageTools 2026",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = _settings.Language == AppLanguage.ZhTw ? AppLanguage.En : AppLanguage.ZhTw;
        AppSettingsService.Save(_settings);
        ApplyLanguage();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        BuildThemeMenu();
        ThemePopup.IsOpen = true;
    }

    private void BackgroundOpacity_Click(object sender, RoutedEventArgs e)
    {
        var current = NormalizeBackgroundOpacity(_settings.MainBackgroundOpacity);
        _settings.MainBackgroundOpacity = current >= 0.85 ? 0.7 : current >= 0.65 ? 0.5 : current >= 0.45 ? 0.3 : 0.9;
        AppSettingsService.Save(_settings);
        ApplyTheme();
    }

    private void BuildThemeMenu()
    {
        ThemeMenuList.Children.Clear();
        var currentId = CurrentThemeId();

        foreach (var theme in ThemeCatalog.Themes)
        {
            var name = _settings.Language == AppLanguage.ZhTw ? theme.NameZh : theme.NameEn;
            var item = new Button
            {
                Content = (theme.Id == currentId ? "● " : "    ") + name,
                Tag = theme.Id,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 5, 14, 5),
                Margin = new Thickness(0, 1, 0, 1),
                FontSize = 12,
                Foreground = ResourceBrush("TextBrush"),
                Background = ResourceBrush("ButtonBrush"),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            item.Click += ThemeItem_Click;
            ThemeMenuList.Children.Add(item);
        }
    }

    private void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string id)
        {
            _settings.ThemeId = id;
            AppSettingsService.Save(_settings);
            ThemePopup.IsOpen = false;
            ApplyTheme();
            ApplyLanguage();
            AppearanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HeaderPanel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragWindowFromMouseEvent(e);
    }

    private void WindowDrag_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragWindowFromMouseEvent(e);
    }

    private void DragWindowFromMouseEvent(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
        {
            return;
        }

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
            SaveMainWindowPosition();
            e.Handled = true;
        }
        catch
        {
            // DragMove can throw if the mouse button is released while Windows is starting the drag.
        }
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button ||
                source is ToggleButton ||
                source is ProgressBar ||
                source is ListBox ||
                source is ComboBox ||
                source is TextBox ||
                source is PasswordBox ||
                source is Slider ||
                source is System.Windows.Controls.Primitives.ScrollBar ||
                source is System.Windows.Controls.Primitives.Thumb ||
                source is MenuItem)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void SaveMainWindowPosition()
    {
        _settings = AppSettingsService.Load();
        _settings.HasMainWindowPosition = true;
        _settings.MainWindowLeft = Left;
        _settings.MainWindowTop = Top;
        AppSettingsService.Save(_settings);
    }

    private void EnsureVisiblePosition()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        var visible = Left + Width > left + 24 &&
                      Left < right - 24 &&
                      Top + ActualHeight > top + 24 &&
                      Top < bottom - 24;

        if (visible)
        {
            return;
        }

        _settings.HasMainWindowPosition = false;
        AppSettingsService.Save(_settings);
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, workArea.Right - Width - 18);
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - ActualHeight - 18);
    }

    private sealed record ProjectRow(string Name, string TokensText);
}
