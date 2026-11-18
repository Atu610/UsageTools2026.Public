using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UsageTools2026.Models;
using UsageTools2026.Services;
using Border = System.Windows.Controls.Border;
using DockPanel = System.Windows.Controls.DockPanel;
using Grid = System.Windows.Controls.Grid;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfRun = System.Windows.Documents.Run;

namespace UsageTools2026;

public partial class MiniStatusWindow : Window
{
    private const double AnnouncementAreaHeight = 72;
    private readonly DispatcherTimer _rotationTimer;
    private readonly DispatcherTimer _resetCreditFlashTimer;
    private readonly DispatcherTimer _announcementTimer;
    private readonly MiniUsageAnnouncementService _announcementService;
    private readonly Queue<MiniUsageAnnouncement> _announcementQueue = new();
    private AppSettings _settings = AppSettingsService.Load();
    private UsageSnapshot _snapshot = UsageSnapshot.Empty;
    private SystemUsage _systemUsage = SystemUsage.Empty;
    private MiniProvider _provider = MiniProvider.Codex;
    private bool _resetCreditFlashActive;
    private MiniUsageAnnouncement? _activeAnnouncement;
    private int _announcementShowsRemaining;
    private bool _announcementInGap;
    private bool _initialUsageShown;
    private string? _appearanceKey;
    private string? _planKey;
    private readonly CreditBalanceAnnouncementTracker _balanceTracker = new();

    public event EventHandler? RequestRestore;
    public event EventHandler? RequestShowUpdate;
    public event EventHandler? RequestRefresh;
    public event EventHandler? RequestQuit;

    public MiniStatusWindow()
        : this(new MiniUsageAnnouncementService())
    {
    }

    internal MiniStatusWindow(MiniUsageAnnouncementService announcementService)
    {
        _announcementService = announcementService;
        InitializeComponent();
        _resetCreditFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _resetCreditFlashTimer.Tick += (_, _) =>
        {
            _resetCreditFlashTimer.Stop();
            _resetCreditFlashActive = false;
            ApplyResetCreditButtonStyle(_snapshot.CodexResetCredits);
        };
        ApplyTheme();
        Loaded += (_, _) => EnsureVisiblePosition();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                StartNextAnnouncement();
            }
            else
            {
                CancelAnnouncements(clearQueue: true);
                MiniWorkPopup.IsOpen = false;
                MiniResetCreditPopup.IsOpen = false;
                MiniPlanPopup.IsOpen = false;
                MiniResetCreditButton.IsChecked = false;
            }
        };

        _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _rotationTimer.Tick += (_, _) => RotateProvider();
        _rotationTimer.Start();

        _announcementTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _announcementTimer.Tick += (_, _) => AdvanceAnnouncement();
    }

    public void SetAvailableUpdate(UpdateInfo? update)
    {
        MiniUpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        MiniUpdateButton.ToolTip = update is null
            ? null
            : (_settings.Language == AppLanguage.ZhTw
                ? $"\u6709\u65b0\u7248\u672c {update.Version} \u53ef\u7528"
                : $"Version {update.Version} is available");
    }

    public void ApplySnapshot(UsageSnapshot snapshot)
    {
        // Keep theme/language in sync with the main window (settings may have changed since last poll).
        _settings = AppSettingsService.Load();
        var appearanceKey = $"{_settings.ThemeId}|{_settings.Theme}|{_settings.Language}|{_settings.MainBackgroundOpacity}|{_settings.KeepWindowOnTop}";
        if (_appearanceKey != appearanceKey)
        {
            ApplyTheme();
            _appearanceKey = appearanceKey;
        }

        if (_snapshot.Codex.AccountKey != snapshot.Codex.AccountKey)
            CancelAnnouncements(clearQueue: true);
        _snapshot = snapshot;
        if (UsageFreshness.State(snapshot.Claude) != UsageDataState.Fresh)
        {
            var retained = _announcementQueue.Where(a => a.Provider != "Claude Code").ToArray();
            _announcementQueue.Clear();
            foreach (var item in retained) _announcementQueue.Enqueue(item);
            if (_activeAnnouncement?.Provider == "Claude Code") CancelAnnouncements(clearQueue: false);
        }
        UpdateResetCreditUi(snapshot.CodexResetCredits);

        if (!CanShowProvider(_provider))
        {
            _provider = CanShowClaude ? MiniProvider.Claude : MiniProvider.Codex;
        }

        RenderProvider();
        if (IsVisible && _settings.CodexDataEnabled && _balanceTracker.Observe(snapshot.Codex, DateTimeOffset.UtcNow) is { } balance)
        {
            var retained = _announcementQueue.Where(a => a.Kind != MiniUsageAnnouncementKind.CreditBalanceChanged).ToArray();
            _announcementQueue.Clear();
            foreach (var item in retained) _announcementQueue.Enqueue(item);
            EnqueueAnnouncements([balance]);
        }
        _announcementService.Observe(snapshot);
        var pending = _announcementService.Pending().Where(item => item.Provider != "Claude Code" ||
            UsageFreshness.State(snapshot.Claude) == UsageDataState.Fresh).ToArray();
        EnqueueAnnouncements(pending);
        if (!_initialUsageShown && IsVisible)
        {
            var current = _announcementService.Current(snapshot);
            if (current.Count > 0)
            {
                _initialUsageShown = true;
                if (pending.Length == 0) EnqueueAnnouncements(current);
            }
        }
    }

    public void ReloadAppearance()
    {
        _appearanceKey = null;
        _planKey = null;
        _settings = AppSettingsService.Load();
        ApplyTheme();
        RenderProvider();
        DiagnosticsService.Write($"Mini appearance applied: {CurrentTheme().Id}.");
    }

    public void FlashResetCreditReminder()
    {
        if (_snapshot.CodexResetCredits.AvailableCount <= 0)
        {
            return;
        }

        _resetCreditFlashActive = true;
        _resetCreditFlashTimer.Stop();
        _resetCreditFlashTimer.Start();
        ApplyResetCreditButtonStyle(_snapshot.CodexResetCredits);
    }

    public void ApplySystemMetrics(SystemUsage usage)
    {
        _systemUsage = usage;
        SystemText.Text = $"CPU {usage.CpuPercent:0}%   RAM {usage.RamPercent:0}%   NET Down {DisplayFormatService.FormatRate(usage.DownloadBytesPerSecond)} Up {DisplayFormatService.FormatRate(usage.UploadBytesPerSecond)}";
    }

    public void ShowAtSavedOrDefault()
    {
        _settings = AppSettingsService.Load();
        if (_settings.HasMiniWindowPosition)
        {
            Left = _settings.MiniWindowLeft;
            // MiniWindowTop stores the dashboard top, below the space reserved for notifications.
            Top = _settings.MiniWindowTop - AnnouncementAreaHeight;
        }
        else
        {
            ResetToDefaultPosition();
        }

        EnsureVisiblePosition();
        RenderProvider();
        MiniWorkPopup.IsOpen = false;
        MiniResetCreditPopup.IsOpen = false;
        MiniPlanPopup.IsOpen = false;
        MiniResetCreditButton.IsChecked = false;
        Show();
        Activate();
    }

    public void ResetToDefaultPosition()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 8, workArea.Right - Width - 12);
        Top = Math.Max(workArea.Top + 8, workArea.Bottom - Height - 12);
        SavePosition();
    }

    private bool CanShowClaude => _snapshot.Claude.HasLimitData;

    private bool CanShowProvider(MiniProvider provider) => provider == MiniProvider.Codex || CanShowClaude;

    private void RotateProvider()
    {
        if (!IsVisible || _activeAnnouncement is not null ||
            MiniWorkPopup.IsOpen || MiniResetCreditPopup.IsOpen || MiniPlanPopup.IsOpen)
        {
            return;
        }

        if (!CanShowClaude)
        {
            if (_provider == MiniProvider.Codex) return;
            _provider = MiniProvider.Codex;
            RenderProvider();
            return;
        }

        _provider = _provider == MiniProvider.Codex ? MiniProvider.Claude : MiniProvider.Codex;
        RenderProvider();
    }

    private void RenderProvider()
    {
        UpdateProviderIdentity();
        UpdateResetCreditVisibility();
        UpdateMiniFable();
        if (_provider == MiniProvider.Claude && CanShowClaude)
        {
            if (!_snapshot.Claude.HasLimitData)
            {
                RenderUnavailableClaude();
                UpdateWorkButton();
                return;
            }

            RenderUsage(
                "Claude Code",
                _snapshot.Claude.PrimaryUsedPercent,
                _snapshot.Claude.SecondaryUsedPercent,
                _snapshot.Claude.HasLimitData,
                !_snapshot.Claude.HasStatusFile);
            UpdateWorkButton();
            return;
        }

        var codexWindows = new List<(double Used, int? Minutes)>();
        if (_snapshot.Codex.PrimaryUsedPercent is { } primaryUsed)
        {
            codexWindows.Add((primaryUsed, _snapshot.Codex.PrimaryWindowMinutes));
        }

        if (_snapshot.Codex.SecondaryUsedPercent is { } secondaryUsed)
        {
            codexWindows.Add((secondaryUsed, _snapshot.Codex.SecondaryWindowMinutes));
        }

        codexWindows.Sort((left, right) => (left.Minutes ?? int.MaxValue).CompareTo(right.Minutes ?? int.MaxValue));
        if (!_snapshot.Codex.HasData || codexWindows.Count == 0)
        {
            RenderUnavailableCodex();
            UpdateWorkButton();
            return;
        }

        RenderUsage(
            "Codex",
            codexWindows[0].Used,
            codexWindows.Count > 1 ? codexWindows[1].Used : null,
            _snapshot.Codex.HasData,
            _snapshot.Codex.IsStale,
            codexWindows[0].Minutes,
            codexWindows.Count > 1 ? codexWindows[1].Minutes : null);
        ApplyCodexLimitTooltip(FiveHourRun, FiveHourBar, codexWindows[0].Used);
        if (codexWindows.Count > 1)
        {
            ApplyCodexLimitTooltip(SevenDayRun, SevenDayBar, codexWindows[1].Used);
        }
        UpdateWorkButton();
    }

    // Same rule as the main window: the badge appears only with a real official Fable value,
    // shows the bar without a percentage text, and reveals the number on mouse-over.
    private void UpdateMiniFable()
    {
        var claude = _snapshot.Claude;
        if (_provider != MiniProvider.Claude || !CanShowClaude || claude.FableUsedPercent is not { } usedPercent)
        {
            MiniFableBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var theme = CurrentTheme();
        var clamped = Math.Clamp(usedPercent, 0, 100);
        MiniFableBadge.Visibility = Visibility.Visible;
        MiniFableText.Foreground = Brush(theme.ClaudeAccent);
        MiniFableFill.Width = 50 * clamped / 100.0;
        MiniFableFill.Background = Brush(theme.ClaudeAccent);
        MiniFableBadge.ToolTip = claude.FableResetsAt is { } reset
            ? Local(
                $"Fable 已用 {clamped:0}%\n於 {reset.LocalDateTime:MM/dd HH:mm} 重置",
                $"Fable {clamped:0}% used\nResets {reset.LocalDateTime:MM/dd HH:mm}")
            : Local($"Fable 已用 {clamped:0}%", $"Fable {clamped:0}% used");
    }

    private void RenderUnavailableCodex()
    {
        var theme = CurrentTheme();
        ProviderTitleText.Text = "Codex";
        ProviderTitleText.Foreground = Brush(theme.CodexAccent);
        FiveHourLabelRun.Text = DisplayFormatService.FormatWindowLabel(_snapshot.Codex.PrimaryWindowMinutes, "5h") + " ";
        SevenDayLabelRun.Text = DisplayFormatService.FormatWindowLabel(_snapshot.Codex.SecondaryWindowMinutes, "7d") + " ";
        FiveHourRun.Text = "--";
        SevenDayRun.Text = "--";
        FiveHourRun.Foreground = Brush(theme.Muted);
        SevenDayRun.Foreground = Brush(theme.Muted);
        FiveHourBar.Value = 0;
        SevenDayBar.Value = 0;
        FiveHourBar.Foreground = Brush(theme.Muted);
        SetWindowPanelCount(false);
        StatusLight.Fill = StatusBrush(false, false);
        var tooltip = _settings.Language == AppLanguage.ZhTw
            ? "Codex \u7528\u91cf\u66ab\u6642\u7121\u6cd5\u540c\u6b65\uff0c\u7a0d\u5f8c\u91cd\u8a66"
            : "Codex usage is temporarily unavailable; retrying";
        StatusLight.ToolTip = tooltip;
        FiveHourRun.ToolTip = tooltip;
        SevenDayRun.ToolTip = tooltip;
    }

    private void RenderUnavailableClaude()
    {
        var theme = CurrentTheme();
        SetWindowPanelCount(true);
        ProviderTitleText.Text = "Claude Code";
        ProviderTitleText.Foreground = Brush(theme.ClaudeAccent);
        FiveHourLabelRun.Text = "5h ";
        SevenDayLabelRun.Text = "7d ";
        FiveHourRun.Text = "--";
        SevenDayRun.Text = "--";
        FiveHourRun.Foreground = Brush(theme.Muted);
        SevenDayRun.Foreground = Brush(theme.Muted);
        FiveHourBar.Value = 0;
        SevenDayBar.Value = 0;
        FiveHourBar.Foreground = Brush(theme.Muted);
        SevenDayBar.Foreground = Brush(theme.Muted);
        StatusLight.Fill = Brush(UsageFreshness.Color(UsageDataState.Unavailable));
        StatusLight.ToolTip = _settings.Language == AppLanguage.ZhTw
            ? "\u5df2\u5075\u6e2c Claude Code\uff1b\u5b98\u65b9 Usage \u672a\u540c\u6b65"
            : "Claude Code detected; official Usage is not synced";
    }

    private void RenderUsage(
        string providerName,
        double? fiveHour,
        double? sevenDay,
        bool isRunning,
        bool isMissingOrDisabled,
        int? primaryWindowMinutes = null,
        int? secondaryWindowMinutes = null)
    {
        var theme = CurrentTheme();
        ProviderTitleText.Text = providerName;
        ProviderTitleText.Foreground = Brush(providerName == "Codex" ? theme.CodexAccent : theme.ClaudeAccent);
        SetWindowPanelCount(providerName == "Claude Code" || sevenDay is not null);
        FiveHourLabelRun.Text = DisplayFormatService.FormatWindowLabel(primaryWindowMinutes, "5h") + " ";
        FiveHourRun.Text = fiveHour is { } firstUsed ? FormatUsed(firstUsed) : "UNKNOWN";
        var isClaude = providerName == "Claude Code";
        ApplyUsageBrushes(FiveHourRun, FiveHourBar, fiveHour ?? 0, theme, isClaude);
        FiveHourBar.Value = fiveHour ?? 0;
        SevenDayLabelRun.Text = DisplayFormatService.FormatWindowLabel(secondaryWindowMinutes, "7d") + " ";
        SevenDayRun.Text = "UNKNOWN";
        SevenDayBar.Value = 0;
        if (sevenDay is { } secondUsed)
        {
            SevenDayLabelRun.Text = DisplayFormatService.FormatWindowLabel(secondaryWindowMinutes, "7d") + " ";
            SevenDayRun.Text = FormatUsed(secondUsed);
            ApplyUsageBrushes(SevenDayRun, SevenDayBar, secondUsed, theme, isClaude);
            SevenDayBar.Value = secondUsed;
        }
        var state = isClaude ? UsageFreshness.State(_snapshot.Claude) : UsageFreshness.State(_snapshot.Codex);
        var synced = isClaude ? _snapshot.Claude.OfficialSyncedAt : _snapshot.Codex.OfficialSyncedAt;
        StatusLight.Fill = Brush(UsageFreshness.Color(state));
        StatusLight.ToolTip = UsageFreshness.Describe(state, synced, _settings.Language == AppLanguage.ZhTw);
        var cachedClaude = isClaude && state != UsageDataState.Fresh;
        CacheStatusText.Visibility = cachedClaude ? Visibility.Visible : Visibility.Collapsed;
        CacheStatusText.Text = cachedClaude ? UsageFreshness.ClaudeCacheLabel(_snapshot.Claude, _settings.Language == AppLanguage.ZhTw) : "";
        RootBorder.Height = cachedClaude ? 114 : 96;
        MinHeight = 168;
        MaxHeight = 186;
        Height = cachedClaude ? 186 : 168;
        if (cachedClaude)
        {
            FiveHourRun.Text = fiveHour is { } five ? FormatUsed(five) + Local(" 快取", " cached") : Local("已過期／未同步", "Expired / unsynced");
            SevenDayRun.Text = sevenDay is { } seven ? FormatUsed(seven) + Local(" 快取", " cached") : Local("已過期／未同步", "Expired / unsynced");
        }
    }

    private void SetWindowPanelCount(bool hasSecondary)
    {
        PrimaryWindowPanel.Visibility = Visibility.Visible;
        SecondaryWindowPanel.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(PrimaryWindowPanel, hasSecondary ? 1 : 3);
    }

    private void ApplyCodexLimitTooltip(WpfRun valueText, WpfProgressBar bar, double usedPercent)
    {
        if (usedPercent < 99.95)
        {
            valueText.ToolTip = null;
            bar.ToolTip = null;
            return;
        }

        var hasActiveWork = _snapshot.CodexWorkWindows.Count > 0;
        var tooltip = hasActiveWork
            ? (_settings.Language == AppLanguage.ZhTw
                ? "\u5df2\u9054\u4e0a\u9650\uff1b\u5075\u6e2c\u5230\u57f7\u884c\u4e2d\u5de5\u4f5c\uff0c\u76ee\u524d\u56de\u5408\u53ef\u80fd\u5728 fair-use \u9650\u5236\u5167\u7e7c\u7e8c\u3002"
                : "Limit reached; an active turn was detected and may continue subject to fair-use limits.")
            : (_settings.Language == AppLanguage.ZhTw
                ? "\u5df2\u9054\u4e0a\u9650\uff0c\u7b49\u5f85\u7528\u91cf\u91cd\u7f6e\u3002"
                : "Limit reached; waiting for usage reset.");
        valueText.ToolTip = tooltip;
        bar.ToolTip = tooltip;
    }

    private static System.Windows.Media.Brush StatusBrush(bool isRunning, bool isMissingOrDisabled)
    {
        if (isRunning)
        {
            return Brush("#35E66B");
        }

        if (isMissingOrDisabled)
        {
            return Brush("#F2F5F4");
        }

        return Brush("#FF4A4A");
    }

    private string StatusTooltip(bool isRunning, bool isMissingOrDisabled)
    {
        if (isRunning)
        {
            return _settings.Language == AppLanguage.ZhTw ? "\u904b\u4f5c\u4e2d" : "Running";
        }

        if (isMissingOrDisabled)
        {
            return _settings.Language == AppLanguage.ZhTw ? "\u672a\u5b89\u88dd/\u672a\u555f\u7528" : "Not installed/enabled";
        }

        return _settings.Language == AppLanguage.ZhTw ? "\u672a\u904b\u4f5c" : "Not running";
    }

    private void ApplyTheme()
    {
        var theme = CurrentTheme();

        RootBorder.Background = MiniSurfaceBrush(theme);
        RootBorder.BorderBrush = Brush(theme.Outer);
        RootBorder.CornerRadius = theme.VisualStyle switch
        {
            "celestial" => new CornerRadius(10),
            "cyberpunk" => new CornerRadius(4),
            "cartoon-platformer" => new CornerRadius(14),
            "win95" or "newspaper" => new CornerRadius(0),
            "dragon-lair" => new CornerRadius(7),
            _ => new CornerRadius(8)
        };
        RootBorder.BorderThickness = theme.VisualStyle is "win95" or "dragon-lair"
            ? new Thickness(2)
            : new Thickness(1);
        if (theme.VisualStyle == "dragon-lair")
        {
            RootBorder.BorderBrush = BronzeBorderBrush();
        }

        var label = Brush(theme.Muted);
        FiveHourLabelRun.Foreground = label;
        SevenDayLabelRun.Foreground = label;

        var track = Brush(theme.Track);
        FiveHourBar.Background = track;
        SevenDayBar.Background = track;
        FiveHourBar.Height = 5;
        SevenDayBar.Height = 5;
        FiveHourBar.BorderThickness = new Thickness(0);
        SevenDayBar.BorderThickness = new Thickness(0);
        FiveHourBar.BorderBrush = null;
        SevenDayBar.BorderBrush = null;

        SystemText.Foreground = Brush(theme.Muted);

        MiniWorkButton.Foreground = Brush(theme.Text);
        MiniWorkButton.Background = Brush(theme.Button);
        MiniWorkButton.BorderBrush = Brush(theme.Border);
        MiniWorkPopupBorder.Background = Brush(theme.Card);
        MiniWorkPopupBorder.BorderBrush = Brush(theme.Outer);
        MiniWorkPopupBorder.CornerRadius = theme.VisualStyle is "win95" or "newspaper"
            ? new CornerRadius(0)
            : new CornerRadius(8);
        MiniWorkPopupTitle.Foreground = Brush(theme.Muted);
        MiniWorkNameText.Foreground = Brush(theme.Text);
        MiniWorkContextText.Foreground = Brush(theme.Muted);
        MiniWorkModelText.Foreground = Brush(theme.Accent);
        MiniResetCreditPopupBorder.Background = Brush(theme.Card);
        MiniResetCreditPopupBorder.BorderBrush = Brush(theme.Outer);
        MiniResetCreditPopupBorder.CornerRadius = theme.VisualStyle is "win95" or "newspaper"
            ? new CornerRadius(0)
            : new CornerRadius(8);
        MiniResetCreditHeader.Foreground = Brush(theme.Text);
        MiniResetCreditSyncText.Foreground = Brush(theme.Muted);
        MiniProviderIconBadge.Background = BadgeBrush(
            _provider == MiniProvider.Claude ? theme.ClaudeAccent : theme.CodexAccent);
        MiniProviderIconBadge.BorderBrush = Brush(
            _provider == MiniProvider.Claude ? theme.ClaudeAccent : theme.CodexAccent);
        MiniPlanPopupBorder.Background = Brush(theme.Card);
        MiniPlanPopupBorder.BorderBrush = Brush(theme.Outer);
        MiniPlanPopupTitle.Foreground = Brush(theme.Text);

        ShowUsageMenuItem.Header = Local("查看目前額度", "Show current usage");
        DismissAnnouncementButton.ToolTip = Local("關閉額度提醒", "Dismiss usage notification");
        System.Windows.Automation.AutomationProperties.SetName(DismissAnnouncementButton,
            (string)DismissAnnouncementButton.ToolTip);
        UpdateResetCreditUi(_snapshot.CodexResetCredits);
        if (_activeAnnouncement is not null)
        {
            ApplyAnnouncementColors();
        }

        try { FontFamily = new System.Windows.Media.FontFamily(theme.Font); } catch { }
    }

    private void UpdateProviderIdentity()
    {
        var isClaude = _provider == MiniProvider.Claude && CanShowClaude;
        var theme = CurrentTheme();
        var accent = isClaude ? theme.ClaudeAccent : theme.CodexAccent;
        MiniProviderBadgeText.Text = isClaude ? "CC" : "CX";
        MiniProviderIconBadge.Background = BadgeBrush(accent);
        MiniProviderIconBadge.BorderBrush = Brush(accent);
        MiniProviderIconButton.ToolTip = Local(
            $"{(isClaude ? "Claude Code" : "Codex")} 訂閱方案",
            $"{(isClaude ? "Claude Code" : "Codex")} plans");
        RebuildMiniPlanList(isClaude ? SubscriptionProvider.Claude : SubscriptionProvider.Codex);
    }

    private void RebuildMiniPlanList(SubscriptionProvider provider)
    {
        var rawPlan = provider == SubscriptionProvider.Claude
            ? _snapshot.Claude.PlanType
            : _snapshot.Codex.PlanType;
        var activeId = SubscriptionPlanCatalog.Normalize(provider, rawPlan);
        var key = $"{provider}|{rawPlan}|{CurrentTheme().Id}|{_settings.Language}";
        if (_planKey == key) return;
        _planKey = key;
        var providerName = provider == SubscriptionProvider.Claude ? "Claude Code" : "Codex";
        var theme = CurrentTheme();
        var accent = provider == SubscriptionProvider.Claude ? theme.ClaudeAccent : theme.CodexAccent;

        MiniPlanPopupTitle.Text = activeId is null
            ? Local($"{providerName} 訂閱方案 · 偵測中", $"{providerName} plans · detecting")
            : providerName + "\n" + SubscriptionPlanCatalog.CurrentLabel(provider, rawPlan, _settings.Language == AppLanguage.En);
        MiniPlanPopupTitle.TextWrapping = TextWrapping.Wrap;
        MiniPlanPopupTitle.MaxWidth = 240;
        MiniPlanList.Children.Clear();
        foreach (var plan in SubscriptionPlanCatalog.GetPlans(provider))
        {
            var isActive = string.Equals(plan.Id, activeId, StringComparison.Ordinal);
            var row = new DockPanel
            {
                Height = 23,
                LastChildFill = true,
                Opacity = isActive ? 1.0 : 0.42
            };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = isActive ? Brush(accent) : Brush(theme.Muted)
            });
            row.Children.Add(new TextBlock
            {
                Text = _settings.Language == AppLanguage.ZhTw ? plan.NameZh : plan.NameEn,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10.5,
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isActive ? Brush(accent) : Brush(theme.Muted)
            });
            MiniPlanList.Children.Add(row);
        }
    }

    private void MiniProviderPlan_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UpdateProviderIdentity();
        if (MiniPlanPopup.IsOpen)
        {
            MiniPlanPopup.IsOpen = false;
            return;
        }

        var topLeft = MiniProviderIconButton.PointToScreen(new System.Windows.Point(0, 0));
        var bottomLeft = MiniProviderIconButton.PointToScreen(new System.Windows.Point(0, MiniProviderIconButton.ActualHeight));
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y)));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var expectedPopupHeight = 205 * dpi.DpiScaleY;
        var spaceAbove = topLeft.Y - screen.WorkingArea.Top;
        var spaceBelow = screen.WorkingArea.Bottom - bottomLeft.Y;
        MiniPlanPopup.Placement = spaceBelow < expectedPopupHeight && spaceAbove > spaceBelow
            ? System.Windows.Controls.Primitives.PlacementMode.Top
            : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MiniPlanPopup.IsOpen = true;
    }

    private ThemeDefinition CurrentTheme()
    {
        var themeId = !string.IsNullOrEmpty(_settings.ThemeId)
            ? _settings.ThemeId
            : (_settings.Theme == AppTheme.Day ? "day" : "deep-night");
        return ThemeCatalog.Resolve(themeId);
    }

    private CodexWorkWindow? LatestWork()
    {
        var windows = _provider == MiniProvider.Claude
            ? _snapshot.ClaudeWorkWindows
            : _snapshot.CodexWorkWindows;
        return windows.OrderByDescending(window => window.LastActivity).FirstOrDefault();
    }

    private void UpdateResetCreditUi(CodexResetCreditSummary summary)
    {
        var count = summary.AvailableCount;
        UpdateResetCreditVisibility();
        if (count <= 0)
        {
            MiniResetCreditButton.IsChecked = false;
            MiniResetCreditPopup.IsOpen = false;
            MiniResetCreditList.Children.Clear();
            return;
        }

        MiniResetCreditButton.Content = $"\u21BB {count}";
        var earliest = summary.Earliest;
        MiniResetCreditButton.ToolTip = earliest is null
            ? Local($"Codex \u91cd\u7f6e\u984d\u5ea6\uff1a\u53ef\u7528 {count} \u6b21", $"Codex reset credits: {count} available")
            : Local(
                $"Codex \u91cd\u7f6e\u984d\u5ea6\uff1a\u53ef\u7528 {count} \u6b21\n\u6700\u65e9 {earliest.ExpiresAt.LocalDateTime:MM/dd HH:mm} \u5230\u671f",
                $"Codex reset credits: {count} available\nEarliest expiry {earliest.ExpiresAt.LocalDateTime:MM/dd HH:mm}");
        MiniResetCreditHeader.Text = Local($"Codex \u91cd\u7f6e\u984d\u5ea6 \u00b7 {count} \u6b21", $"Codex resets \u00b7 {count}");
        MiniResetCreditSyncText.Text = summary.IsStale ? Local("\u5feb\u53d6", "Cached") : Local("\u5b98\u65b9", "Official");
        RebuildResetCreditList(summary);
        ApplyResetCreditButtonStyle(summary);
    }

    private void UpdateResetCreditVisibility()
    {
        var visible = _provider == MiniProvider.Codex &&
                      _snapshot.CodexResetCredits.AvailableCount > 0;
        MiniResetCreditButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            MiniResetCreditButton.IsChecked = false;
            MiniResetCreditPopup.IsOpen = false;
        }
    }

    private void RebuildResetCreditList(CodexResetCreditSummary summary)
    {
        MiniResetCreditList.Children.Clear();
        var theme = CurrentTheme();
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
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new System.Windows.Shapes.Ellipse
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
                Text = Local("\u5b8c\u6574\u91cd\u7f6e\uff08\u6bcf\u9031 + 5 \u5c0f\u6642\uff09", "Full reset (Weekly + 5 hr)"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = expiring ? Brush("#FFFFFF") : Brush(theme.Text)
            });
            text.Children.Add(new TextBlock
            {
                Text = Local(
                    $"\u65bc {credit.ExpiresAt.LocalDateTime:yyyy/MM/dd HH:mm} \u5230\u671f",
                    $"Expires {credit.ExpiresAt.LocalDateTime:yyyy/MM/dd HH:mm}"),
                Margin = new Thickness(0, 2, 0, 0),
                FontSize = 9.5,
                Foreground = expiring ? Brush("#FFD2D5") : Brush(theme.Muted)
            });
            grid.Children.Add(text);
            row.Child = grid;
            MiniResetCreditList.Children.Add(row);
        }
    }

    private void ApplyResetCreditButtonStyle(CodexResetCreditSummary summary)
    {
        if (MiniResetCreditButton is null)
        {
            return;
        }

        var theme = CurrentTheme();
        var expiring = summary.HasExpiringCredit(TimeSpan.FromHours(24));
        if (_resetCreditFlashActive)
        {
            MiniResetCreditButton.Background = Brush("#D97706");
            MiniResetCreditButton.BorderBrush = Brush("#FFD166");
            MiniResetCreditButton.Foreground = Brush("#FFFFFF");
        }
        else if (expiring)
        {
            MiniResetCreditButton.Background = Brush("#5E1117");
            MiniResetCreditButton.BorderBrush = Brush("#FF3B45");
            MiniResetCreditButton.Foreground = Brush("#FFFFFF");
        }
        else
        {
            MiniResetCreditButton.Background = Brush(theme.Button);
            MiniResetCreditButton.BorderBrush = Brush(theme.Border);
            MiniResetCreditButton.Foreground = Brush(theme.CodexAccent);
        }
    }

    private void MiniResetCredit_Click(object sender, RoutedEventArgs e)
    {
        if (MiniResetCreditButton.IsChecked != true)
        {
            MiniResetCreditPopup.IsOpen = false;
            return;
        }

        UpdateResetCreditUi(_snapshot.CodexResetCredits);
        var topLeft = MiniResetCreditButton.PointToScreen(new System.Windows.Point(0, 0));
        var bottomLeft = MiniResetCreditButton.PointToScreen(new System.Windows.Point(0, MiniResetCreditButton.ActualHeight));
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y)));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var expectedPopupHeight = 250 * dpi.DpiScaleY;
        var spaceAbove = topLeft.Y - screen.WorkingArea.Top;
        var spaceBelow = screen.WorkingArea.Bottom - bottomLeft.Y;
        MiniResetCreditPopup.Placement = spaceBelow < expectedPopupHeight && spaceAbove > spaceBelow
            ? System.Windows.Controls.Primitives.PlacementMode.Top
            : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MiniResetCreditPopup.IsOpen = true;
    }

    private void MiniResetCreditPopup_Closed(object? sender, EventArgs e) =>
        MiniResetCreditButton.IsChecked = false;

    private void UpdateWorkButton()
    {
        var work = LatestWork();
        if (work is null)
        {
            MiniWorkPopup.IsOpen = false;
            MiniWorkButton.Visibility = Visibility.Collapsed;
            return;
        }

        MiniWorkButton.Visibility = Visibility.Visible;
        MiniWorkButton.ToolTip = _settings.Language == AppLanguage.ZhTw
            ? "顯示最新工作"
            : "Show latest work";

        if (MiniWorkPopup.IsOpen)
        {
            RenderLatestWork();
        }
        else
        {
            SetWorkButtonContent(false);
        }
    }

    private bool RenderLatestWork()
    {
        var work = LatestWork();
        if (work is null)
        {
            return false;
        }

        var theme = CurrentTheme();
        var isClaude = _provider == MiniProvider.Claude;
        var workName = string.IsNullOrWhiteSpace(work.WorkName)
            ? (_settings.Language == AppLanguage.ZhTw ? "未命名工作" : "Untitled work")
            : work.WorkName.Trim();
        var model = string.IsNullOrWhiteSpace(work.Model) ? "N/A" : work.Model.Trim();
        var effort = string.IsNullOrWhiteSpace(work.Effort) ? "N/A" : FormatEffort(work.Effort);

        MiniWorkPopupTitle.Text = _settings.Language == AppLanguage.ZhTw
            ? $"最新{(isClaude ? " Claude Code" : " Codex")}工作"
            : $"Latest {(isClaude ? "Claude Code" : "Codex")} work";
        MiniWorkPopupTitle.Foreground = Brush(isClaude ? theme.ClaudeAccent : theme.CodexAccent);
        MiniWorkNameText.Text = $"{work.Project} · {workName}";
        MiniWorkContextText.Text = _settings.Language == AppLanguage.ZhTw
            ? $"上下文 {FormatTokens(work.UsedTokens)} / {FormatTokens(work.ContextWindow)} · {RelativeTime(work.LastActivity)}"
            : $"Context {FormatTokens(work.UsedTokens)} / {FormatTokens(work.ContextWindow)} · {RelativeTime(work.LastActivity)}";
        MiniWorkModelText.Text = $"{(_settings.Language == AppLanguage.ZhTw ? "模型" : "Model")} {model} · Effort {effort}";

        MiniWorkRingHost.Children.Clear();
        MiniWorkRingHost.Children.Add(ContextRingFactory.Build(work.Percent, 38, true));
        return true;
    }

    private void MiniWork_Click(object sender, RoutedEventArgs e)
    {
        if (MiniWorkPopup.IsOpen)
        {
            MiniWorkPopup.IsOpen = false;
            return;
        }

        if (!RenderLatestWork())
        {
            return;
        }

        var topLeft = MiniWorkButton.PointToScreen(new System.Windows.Point(0, 0));
        var bottomLeft = MiniWorkButton.PointToScreen(new System.Windows.Point(0, MiniWorkButton.ActualHeight));
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y)));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var expectedPopupHeight = 118 * dpi.DpiScaleY;
        var spaceAbove = topLeft.Y - screen.WorkingArea.Top;
        var spaceBelow = screen.WorkingArea.Bottom - bottomLeft.Y;
        var openUpward = spaceBelow < expectedPopupHeight && spaceAbove > spaceBelow;

        MiniWorkPopup.Placement = openUpward
            ? System.Windows.Controls.Primitives.PlacementMode.Top
            : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        SetWorkButtonContent(openUpward);
        MiniWorkPopup.IsOpen = true;
    }

    private void MiniWorkPopup_Closed(object? sender, EventArgs e) => SetWorkButtonContent(false);

    private void SetWorkButtonContent(bool upward)
    {
        var label = _settings.Language == AppLanguage.ZhTw ? "工作" : "Work";
        MiniWorkButton.Content = $"{label} {(upward ? "▴" : "▾")}";
    }

    private string RelativeTime(DateTimeOffset? time)
    {
        if (time is null)
        {
            return _settings.Language == AppLanguage.ZhTw ? "時間未知" : "unknown";
        }

        var span = DateTimeOffset.Now - time.Value;
        if (span.TotalMinutes < 1)
        {
            return _settings.Language == AppLanguage.ZhTw ? "剛剛" : "just now";
        }

        if (span.TotalMinutes < 60)
        {
            return _settings.Language == AppLanguage.ZhTw
                ? $"{span.TotalMinutes:0} 分鐘前"
                : $"{span.TotalMinutes:0}m ago";
        }

        return _settings.Language == AppLanguage.ZhTw
            ? $"{span.TotalHours:0} 小時前"
            : $"{span.TotalHours:0}h ago";
    }

    private static string FormatTokens(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:0}k" : tokens.ToString();

    private static string FormatEffort(string effort)
    {
        var value = effort.Trim();
        return value.Length <= 1
            ? value.ToUpperInvariant()
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private void EnqueueAnnouncements(IEnumerable<MiniUsageAnnouncement> announcements)
    {
        foreach (var announcement in announcements)
        {
            if (_activeAnnouncement == announcement || _announcementQueue.Contains(announcement)) continue;
            if (_activeAnnouncement?.IsAlarm == true && _announcementShowsRemaining <= 0)
            {
                _activeAnnouncement = null;
            }
            _announcementQueue.Enqueue(announcement);
        }

        StartNextAnnouncement();
    }

    private void StartNextAnnouncement()
    {
        if (!IsVisible || _activeAnnouncement is not null || _announcementQueue.Count == 0)
        {
            return;
        }

        _activeAnnouncement = _announcementQueue.Dequeue();
        _announcementShowsRemaining = Math.Max(1, _activeAnnouncement.RepeatCount);
        _announcementInGap = false;
        _provider = string.Equals(_activeAnnouncement.Provider, "Claude Code", StringComparison.Ordinal)
            ? MiniProvider.Claude
            : MiniProvider.Codex;
        if (!CanShowProvider(_provider))
        {
            _provider = MiniProvider.Codex;
        }

        RenderProvider();
        ShowAnnouncement();
        _announcementTimer.Stop();
        _announcementTimer.Interval = TimeSpan.FromSeconds(8);
        _announcementTimer.Start();
    }

    private void AdvanceAnnouncement()
    {
        if (_activeAnnouncement is null)
        {
            _announcementTimer.Stop();
            StartNextAnnouncement();
            return;
        }

        if (!_announcementInGap)
        {
            if (_activeAnnouncement.IsAlarm && _announcementShowsRemaining == 1)
            {
                _announcementShowsRemaining = 0;
                _announcementTimer.Stop();
                DiagnosticsService.Write("Mini alert awaiting dismissal: " + _activeAnnouncement.Provider + " " + _activeAnnouncement.WindowLabel);
                if (_announcementQueue.Count > 0)
                {
                    _announcementService.Acknowledge(_activeAnnouncement);
                    _activeAnnouncement = null;
                    StartNextAnnouncement();
                }
                return;
            }
            AnnouncementBanner.Visibility = Visibility.Collapsed;
            _announcementShowsRemaining--;
            _announcementInGap = true;
            _announcementTimer.Interval = TimeSpan.FromMilliseconds(850);
            if (_announcementShowsRemaining <= 0)
            {
                _announcementService.Acknowledge(_activeAnnouncement);
                DiagnosticsService.Write("Mini announcement completed: " + _activeAnnouncement.Provider + " " + _activeAnnouncement.WindowLabel);
                _activeAnnouncement = null;
            }
            return;
        }

        _announcementInGap = false;
        if (_activeAnnouncement is null)
        {
            _announcementTimer.Stop();
            StartNextAnnouncement();
            return;
        }

        ShowAnnouncement();
        _announcementTimer.Interval = TimeSpan.FromSeconds(8);
    }

    private void ShowAnnouncement()
    {
        if (_activeAnnouncement is null)
        {
            return;
        }

        AnnouncementText.Text = FormatAnnouncement(_activeAnnouncement);
        EnsureVisiblePosition();
        DiagnosticsService.Write("Mini announcement shown: " + _activeAnnouncement.Provider + " " + _activeAnnouncement.WindowLabel + " remaining=" + _activeAnnouncement.RemainingPercent);
        AnnouncementBanner.Visibility = Visibility.Visible;
        ApplyAnnouncementColors();
    }

    private string FormatAnnouncement(MiniUsageAnnouncement announcement)
    {
        if (_settings.Language == AppLanguage.En)
        {
            return announcement.Kind switch
            {
                MiniUsageAnnouncementKind.CreditBalanceChanged =>
                    $"Codex credits: {(announcement.Balance?.Unlimited == true ? "Unlimited" : announcement.Balance?.Display)} remaining",
                MiniUsageAnnouncementKind.UsageMilestone =>
                    $"{announcement.Provider} {announcement.QuotaLabel}: {announcement.RemainingPercent}% remaining",
                MiniUsageAnnouncementKind.LowRemaining =>
                    $"Warning: {announcement.Provider} {announcement.QuotaLabel} has only {announcement.RemainingPercent}% left",
                MiniUsageAnnouncementKind.UsageReset =>
                    $"{announcement.Provider} {announcement.QuotaLabel} reset\n{announcement.RemainingPercent}% remaining",
                MiniUsageAnnouncementKind.ResetCreditsAdded when announcement.Provider == "Claude Code" =>
                    $"Claude Code: new {ClaudeOfficialDetailsParser.CreditKindName(announcement.CreditKind, true)} ×{announcement.AddedCreditCount}\n{announcement.CreditCount} available",
                MiniUsageAnnouncementKind.ResetCreditsAdded =>
                    $"Codex reset credits updated\n{announcement.CreditCount} available",
                _ => string.Empty
            };
        }

        return announcement.Kind switch
        {
            MiniUsageAnnouncementKind.CreditBalanceChanged =>
                $"Codex 可用點數 {announcement.Balance?.Display}",
            MiniUsageAnnouncementKind.UsageMilestone =>
                $"{announcement.Provider} {announcement.QuotaLabel} 目前剩餘 {announcement.RemainingPercent}%",
            MiniUsageAnnouncementKind.LowRemaining =>
                $"警告：{announcement.Provider} {announcement.QuotaLabel} 僅剩 {announcement.RemainingPercent}%",
            MiniUsageAnnouncementKind.UsageReset =>
                $"{announcement.Provider} {announcement.QuotaLabel} 額度已重置\n目前剩餘 {announcement.RemainingPercent}%",
            MiniUsageAnnouncementKind.ResetCreditsAdded when announcement.Provider == "Claude Code" =>
                $"Claude Code 新增 {announcement.AddedCreditCount} 張{ClaudeOfficialDetailsParser.CreditKindName(announcement.CreditKind)}\n目前可用 {announcement.CreditCount} 張",
            MiniUsageAnnouncementKind.ResetCreditsAdded =>
                $"Codex 收到新的 reset 卷\n目前可用 {announcement.CreditCount} 張",
            _ => string.Empty
        };
    }

    private void ApplyAnnouncementColors()
    {
        var theme = CurrentTheme();
        var alarm = _activeAnnouncement?.IsAlarm == true;
        var border = alarm ? theme.Danger : theme.Outer;
        AnnouncementBanner.Background = Brush(theme.Card);
        AnnouncementBanner.BorderBrush = Brush(border);
        AnnouncementText.Foreground = Brush(alarm ? theme.Danger : theme.Text);
        DismissAnnouncementButton.Foreground = AnnouncementText.Foreground;
        AnnouncementText.FontSize = alarm ? 12 : 11;
    }

    private void ShowCurrentUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.CodexDataEnabled && _balanceTracker.Observe(_snapshot.Codex, DateTimeOffset.UtcNow, true) is { } balance)
            EnqueueAnnouncements([balance]);
        EnqueueAnnouncements(_announcementService.Current(_snapshot));
    }

    private void DismissAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAnnouncement is not { } announcement)
        {
            return;
        }

        _announcementService.Acknowledge(announcement);
        DiagnosticsService.Write("Mini announcement dismissed: " + announcement.Provider + " " + announcement.WindowLabel);
        CancelAnnouncements(clearQueue: false);
        StartNextAnnouncement();
    }

    private void CancelAnnouncements(bool clearQueue)
    {
        _announcementTimer.Stop();
        _activeAnnouncement = null;
        _announcementShowsRemaining = 0;
        _announcementInGap = false;
        AnnouncementBanner.Visibility = Visibility.Collapsed;
        if (clearQueue)
        {
            _announcementQueue.Clear();
        }
    }

    private void ApplyUsageBrushes(System.Windows.Documents.Run valueRun, System.Windows.Controls.ProgressBar bar, double percent, ThemeDefinition theme, bool isClaude)
    {
        if (theme.VisualStyle == "win95")
        {
            var brush = Win95UsageBrush(isClaude);
            valueRun.Foreground = brush;
            bar.Foreground = brush;
            bar.Background = Win95TrackBrush();
            bar.BorderBrush = Brush("#7A7A7A");
            bar.BorderThickness = new Thickness(1);
            bar.Height = 9;
            return;
        }

        if (theme.VisualStyle == "cartoon-platformer")
        {
            var brush = HpMpUsageBrush(bar.Name.Contains("FiveHour", StringComparison.OrdinalIgnoreCase));
            valueRun.Foreground = brush;
            bar.Foreground = brush;
            return;
        }

        if (theme.VisualStyle == "dragon-lair")
        {
            var brush = HpMpUsageBrush(bar.Name.Contains("FiveHour", StringComparison.OrdinalIgnoreCase));
            valueRun.Foreground = brush;
            bar.Foreground = brush;
            bar.Background = DragonLairTrackBrush();
            bar.BorderBrush = BronzeBorderBrush();
            bar.BorderThickness = new Thickness(1);
            bar.Height = 7;
            return;
        }

        if (theme.BarMode == BarColorMode.ByUsage)
        {
            valueRun.Foreground = DisplayFormatService.PercentBrush(percent);
            bar.Foreground = DisplayFormatService.UsageBarBrush(percent);
            return;
        }

        valueRun.Foreground = ThemeValueBrush(theme);
        bar.Foreground = ThemeBarBrush(theme);
    }

    private static System.Windows.Media.Brush ThemeValueBrush(ThemeDefinition theme)
    {
        if (theme.BarMode == BarColorMode.ThemeGradient && theme.BarGradient.Length > 0)
        {
            return Brush(theme.BarGradient[Math.Min(theme.BarGradient.Length - 1, 1)]);
        }

        return Brush(theme.BarColor);
    }

    private static System.Windows.Media.Brush ThemeBarBrush(ThemeDefinition theme)
    {
        if (theme.BarMode == BarColorMode.ThemeGradient && theme.BarGradient.Length > 1)
        {
            return HorizontalGradient(theme.BarGradient);
        }

        return Brush(theme.BarColor);
    }

    private static System.Windows.Media.Brush MiniSurfaceBrush(ThemeDefinition theme) =>
        theme.VisualStyle switch
        {
            "deep-night" => VerticalGradient("#10151B", "#151B22"),
            "day" => VerticalGradient("#FFFFFF", "#E7EFF0"),
            "classic" => VerticalGradient("#08201F", "#0D3433"),
            "aquarium" => VerticalGradient("#05223C", "#083E5A"),
            "celestial" => CelestialMiniBrush(),
            "black-hole" => VerticalGradient("#030303", "#190905"),
            "prism" => HorizontalGradient("#23063E", "#1B0B53", "#0A2D5B"),
            "dragon-lair" => DragonLairMiniBrush(),
            "newspaper" => NewspaperBrush(),
            "cyberpunk" => CyberpunkMiniBrush(),
            "cartoon-platformer" => CartoonMiniBrush(),
            _ => Brush(theme.Panel)
        };

    private static System.Windows.Media.Brush HorizontalGradient(params string[] colors) =>
        Gradient(new System.Windows.Point(0, 0.5), new System.Windows.Point(1, 0.5), colors);

    private static System.Windows.Media.Brush VerticalGradient(params string[] colors) =>
        Gradient(new System.Windows.Point(0.5, 0), new System.Windows.Point(0.5, 1), colors);

    private static System.Windows.Media.Brush Gradient(System.Windows.Point start, System.Windows.Point end, params string[] colors)
    {
        var brush = new System.Windows.Media.LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (var i = 0; i < colors.Length; i++)
        {
            var offset = colors.Length == 1 ? 0 : i / (double)(colors.Length - 1);
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]),
                offset));
        }

        return brush;
    }

    private static System.Windows.Media.Brush MatrixBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#000000"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 48, 8))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#063D0C"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 48, 1))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 48, 8),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush FamicomMiniBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(VerticalGradient("#F7F1E5", "#E5DCCC"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 160, 96))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#D32027"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 160, 5))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#D32027"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 91, 160, 5))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#B7AEA0"), 0.8), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 54), new System.Windows.Point(160, 54))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 160, 96),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush CyberpunkMiniBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(HorizontalGradient("#070016", "#0B1435", "#15001D"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 180, 96))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#0D1E44"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 50, 180, 22))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#00E7FF"), 0.45), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 22), new System.Windows.Point(180, 22))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#FF2BD6"), 0.45), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 78), new System.Windows.Point(180, 78))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#00E7FF"), null, new System.Windows.Media.RectangleGeometry(new Rect(148, 14, 16, 2))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 180, 96),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush NewspaperBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#F2EAD9"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 72, 72))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#D6CCB9"), 0.8), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 18), new System.Windows.Point(72, 18))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#E7DDCA"), 0.6), new System.Windows.Media.LineGeometry(new System.Windows.Point(24, 0), new System.Windows.Point(24, 72))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 72, 72),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush CelestialMiniBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(VerticalGradient("#04050B", "#10101A"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 120, 80))));
        var dimGold = new System.Windows.Media.Pen(Brush("#5E4423"), 0.8);
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, dimGold, new System.Windows.Media.EllipseGeometry(new System.Windows.Point(72, 28), 24, 24)));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, dimGold, new System.Windows.Media.EllipseGeometry(new System.Windows.Point(72, 28), 14, 14)));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, dimGold, new System.Windows.Media.LineGeometry(new System.Windows.Point(72, 0), new System.Windows.Point(72, 58))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, dimGold, new System.Windows.Media.LineGeometry(new System.Windows.Point(38, 28), new System.Windows.Point(106, 28))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#F1CF82"), null, new System.Windows.Media.EllipseGeometry(new System.Windows.Point(72, 28), 2.4, 2.4)));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#C99A43"), null, new System.Windows.Media.EllipseGeometry(new System.Windows.Point(18, 18), 1.8, 1.8)));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#C99A43"), null, new System.Windows.Media.EllipseGeometry(new System.Windows.Point(108, 62), 1.8, 1.8)));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 120, 80),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush DragonLairMiniBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(VerticalGradient("#24262D", "#101218", "#1A1C22"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 120, 80))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#3C404A"), 0.7), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 24), new System.Windows.Point(120, 18))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#565A64"), 0.55), new System.Windows.Media.LineGeometry(new System.Windows.Point(18, 0), new System.Windows.Point(42, 80))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#B18A4D"), 1), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 2), new System.Windows.Point(120, 2))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#6A5232"), 1), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 77), new System.Windows.Point(120, 77))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 120, 80),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush BronzeBorderBrush() =>
        VerticalGradient("#493824", "#C3A167", "#6B5131", "#D0AF73", "#3B2C1D");

    private static System.Windows.Media.Brush DragonLairTrackBrush() =>
        VerticalGradient("#020306", "#11131A", "#050608");

    private static System.Windows.Media.Brush FieldBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#0B5525"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 120, 80))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#116B2F"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 120, 20))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#116B2F"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 40, 120, 20))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#DFFFE0"), 1.2), new System.Windows.Media.LineGeometry(new System.Windows.Point(60, 0), new System.Windows.Point(60, 80))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 120, 80),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush HpMpUsageBrush(bool isSession)
    {
        return isSession
            ? HorizontalGradient("#4C0606", "#B71318", "#F04431", "#FF8A62")
            : HorizontalGradient("#08114F", "#1C35B9", "#4C6BFF", "#A06DFF");
    }

    private static System.Windows.Media.Brush Win95UsageBrush(bool isClaude)
    {
        var color = isClaude ? "#E6A000" : "#2A9AD9";
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush(color), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 7, 9))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#C0C0C0"), null, new System.Windows.Media.RectangleGeometry(new Rect(7, 0, 1, 9))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 9),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush Win95TrackBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#F2F2F2"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 8, 9))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#C0C0C0"), null, new System.Windows.Media.RectangleGeometry(new Rect(7, 0, 1, 9))));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 9),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush CartoonMiniBrush()
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(VerticalGradient("#FFFDF2", "#FFE66D"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 112, 72))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#FFD43A"), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 52, 112, 20))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(null, new System.Windows.Media.Pen(Brush("#111111"), 1.2), new System.Windows.Media.LineGeometry(new System.Windows.Point(0, 52), new System.Windows.Point(112, 52))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(Brush("#168EF2"), null, new System.Windows.Media.EllipseGeometry(new System.Windows.Point(98, 60), 4, 4)));
        return new System.Windows.Media.DrawingBrush(group)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, 112, 72),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
    }

    private static System.Windows.Media.Brush Brush(string color)
    {
        return new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private static System.Windows.Media.Brush BadgeBrush(string accent)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(accent);
        return new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(48, color.R, color.G, color.B));
    }

    private void EnsureVisiblePosition()
    {
        // Keep the notification banner on the same monitor as the dashboard.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)((Left + Width / 2) * dpi.DpiScaleX), (int)((Top + Height / 2) * dpi.DpiScaleY)));
        var area = screen.WorkingArea;
        Left = Math.Clamp(Left, area.Left / dpi.DpiScaleX, Math.Max(area.Left / dpi.DpiScaleX, area.Right / dpi.DpiScaleX - Width));
        Top = Math.Clamp(Top, area.Top / dpi.DpiScaleY, Math.Max(area.Top / dpi.DpiScaleY, area.Bottom / dpi.DpiScaleY - Height));
    }

    private void SavePosition()
    {
        _settings.HasMiniWindowPosition = true;
        _settings.MiniWindowLeft = Left;
        _settings.MiniWindowTop = Top + AnnouncementAreaHeight;
        AppSettingsService.Save(_settings);
    }

    private string ClaudeSyncTooltip(ClaudeUsage claude)
    {
        var syncedAt = claude.OfficialSyncedAt?.LocalDateTime;
        var zh = _settings.Language == AppLanguage.ZhTw;
        return claude.SyncState switch
        {
            ClaudeSyncState.LiveSynced => zh
                ? (syncedAt is null ? "官方已同步" : $"官方已同步 {syncedAt:MM/dd HH:mm}")
                : (syncedAt is null ? "Official usage synced" : $"Synced {syncedAt:MM/dd HH:mm}"),
            ClaudeSyncState.CachedLocked => zh
                ? $"上次同步 {syncedAt:MM/dd HH:mm}（Claude 執行中無法即時刷新）"
                : $"Last synced {syncedAt:MM/dd HH:mm} (Claude running, cannot refresh live)",
            ClaudeSyncState.CachedSyncFailed => zh
                ? $"上次同步 {syncedAt:MM/dd HH:mm}（本次同步失敗，顯示舊值）"
                : $"Last synced {syncedAt:MM/dd HH:mm} (sync failed, showing cached value)",
            _ => zh ? "官方 Usage 未同步" : "Official usage not synced"
        };
    }

    private string FormatUsed(double percent)
    {
        return _settings.Language == AppLanguage.ZhTw
            ? $"{percent:0.#}% \u5df2\u7528"
            : $"{percent:0.#}% used";
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            RequestRestore?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            DragMove();
            SavePosition();
        }
        catch
        {
            // DragMove can throw if the mouse button state changes mid-drag.
        }
    }

    private void OpenMenu_Click(object sender, RoutedEventArgs e) => RequestRestore?.Invoke(this, EventArgs.Empty);

    private void MiniUpdate_Click(object sender, RoutedEventArgs e) => RequestShowUpdate?.Invoke(this, EventArgs.Empty);

    private void RefreshMenu_Click(object sender, RoutedEventArgs e) => RequestRefresh?.Invoke(this, EventArgs.Empty);

    private void ResetPositionMenu_Click(object sender, RoutedEventArgs e) => ResetToDefaultPosition();

    private void QuitMenu_Click(object sender, RoutedEventArgs e) => RequestQuit?.Invoke(this, EventArgs.Empty);

    private string Local(string zhTw, string en) =>
        _settings.Language == AppLanguage.ZhTw ? zhTw : en;

    private enum MiniProvider
    {
        Codex,
        Claude
    }
}
