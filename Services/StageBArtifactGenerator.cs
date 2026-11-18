using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageTools2026.Models;

namespace UsageTools2026.Services;

public static class StageBArtifactGenerator
{
    public static IReadOnlyList<string> Generate(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var statePath = Path.Combine(outputDirectory, "isolated-changelog-state.json");
        var service = new AiChangelogService(statePath);
        var changelog = service.LoadOfflineFixture();
        var reportContext = CreateReportContext(changelog);
        var output = new List<string>();

        var window = new AiChangelogWindow();
        try
        {
            window.SetSnapshot(changelog);
            window.Show();
            window.ShowProvider(AiProvider.Codex);
            FlushRender(window);
            output.Add(RenderWindow(window, Path.Combine(outputDirectory, "ai-changelog-codex.png")));

            window.ShowProvider(AiProvider.ClaudeCode);
            FlushRender(window);
            output.Add(RenderWindow(window, Path.Combine(outputDirectory, "ai-changelog-claude.png")));
        }
        finally
        {
            window.CloseForShutdown();
        }

        var uiSettings = AppSettingsService.Load();
        uiSettings.KeepWindowOnTop = false;
        AppSettingsService.Save(uiSettings);
        var mainWindow = new MainWindow();
        try
        {
            mainWindow.ApplySnapshot(reportContext.Snapshot);
            mainWindow.SetAiChangelogStatus(
                service.GetUnreadCount(changelog, AiProvider.Codex),
                service.GetUnreadCount(changelog, AiProvider.ClaudeCode),
                changelog.IsOfflineFixture);
            mainWindow.Show();
            FlushRender(mainWindow);
            if (mainWindow.Topmost)
                throw new InvalidOperationException("Window topmost setting was not applied.");
            var topmostToggle = (System.Windows.Controls.Primitives.ToggleButton)mainWindow.FindName("AlwaysOnTopToggle");
            if (topmostToggle.IsChecked != false)
                throw new InvalidOperationException("Window topmost toggle does not reflect the saved setting.");
            var toolsPopup = (System.Windows.Controls.Primitives.Popup)mainWindow.FindName("ToolsPopup");
            toolsPopup.IsOpen = true;
            FlushRender(mainWindow);
            if (toolsPopup.Child is not System.Windows.FrameworkElement popupContent)
                throw new InvalidOperationException("Tools popup content is unavailable.");
            popupContent.UpdateLayout();
            if (!PersonalMobileService.IsConfigured)
            {
                var connect = (System.Windows.Controls.Button)mainWindow.FindName("MobileButton");
                var sync = (System.Windows.Controls.Primitives.ToggleButton)mainWindow.FindName("PersonalMobileToggle");
                var status = (System.Windows.Controls.TextBlock)mainWindow.FindName("PersonalMobileLabel");
                var message = uiSettings.Language == AppLanguage.ZhTw ? "手機服務尚未設定" : "Mobile service not configured";
                if (connect.Visibility != System.Windows.Visibility.Collapsed || sync.IsEnabled ||
                    sync.IsChecked != false || status.Text != message)
                    throw new InvalidOperationException("Unconfigured mobile pairing was presented as available.");
            }
            output.Add(RenderElement(popupContent, Path.Combine(outputDirectory, "tools-popup-window-topmost-off.png")));
            toolsPopup.IsOpen = false;
            output.Add(RenderWindow(mainWindow, Path.Combine(outputDirectory, "main-window-ai-news.png")));

            var now = DateTimeOffset.Now;
            var partial = reportContext.Snapshot with
            {
                Codex = reportContext.Snapshot.Codex with { IsStale = true, OfficialSyncedAt = now.AddMinutes(-8) },
                Claude = reportContext.Snapshot.Claude with
                {
                    PrimaryUsedPercent = null, PrimaryResetsAt = null,
                    SecondaryUsedPercent = 60, SyncState = ClaudeSyncState.CachedSyncFailed,
                    OfficialSyncedAt = now.AddMinutes(-8)
                }
            };
            mainWindow.ApplySnapshot(partial);
            FlushRender(mainWindow);
            var missingText = (System.Windows.Controls.TextBlock)mainWindow.FindName("ClaudeSessionValue");
            var claudeLight = (System.Windows.Shapes.Ellipse)mainWindow.FindName("ClaudeStatusLight");
            if (missingText.Text.Contains("0%") || !claudeLight.Fill.ToString().Equals("#FFFFBD59", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Partial or stale UI regression.");
            output.Add(RenderWindow(mainWindow, Path.Combine(outputDirectory, "health-stale-partial.png")));

            mainWindow.ApplySnapshot(partial with
            {
                Claude = partial.Claude with
                {
                    PrimaryUsedPercent = 0, OfficialSyncedAt = now, SyncState = ClaudeSyncState.LiveSynced
                }
            });
            FlushRender(mainWindow);
            if (!missingText.Text.Contains("0%")) throw new InvalidOperationException("Real zero was lost.");
            output.Add(RenderWindow(mainWindow, Path.Combine(outputDirectory, "health-real-zero.png")));

            // FIXTURE: 2026-11-11 response shape (see ClaudeParseSelfTest.RealShapeUsage); not live data.
            var claudeParsed = ClaudeOfficialSyncService.ParseUsage(ClaudeParseSelfTest.RealShapeUsage, null, now);
            var claudeDetails = claudeParsed.Details with
            {
                Pools = claudeParsed.Details.Pools.Select(p => p with { ResetsAt = now.AddHours(p.Label == "5H" ? 4 : 110), Percent = p.IsModelScoped ? 12 : p.Percent }).ToArray(),
                Resets = claudeParsed.Details.Resets! with
                {
                    Grants = claudeParsed.Details.Resets.Grants.Select((g, i) => g with { EndsAt = now.AddDays(i == 0 ? 5 : 29) }).ToArray()
                },
                Models = ClaudeOfficialDetailsParser.ParseModels("""[{"model":"claude-fable-5-2","name":"Claude Fable 5.2"},{"model":"claude-opus-5-5","name":"Claude Opus 5.5"},{"model":"claude-sonnet-5-5","name":"Claude Sonnet 5.5"},{"model":"claude-haiku-5-5","name":"Claude Haiku 5.5"},{"model":"claude-fable-5-1","name":"Claude Fable 5.1","overflow":true},{"model":"claude-opus-4-5","name":"Claude Opus 4.5","inactive":true}]"""),
                ModelsSyncedAt = now
            };
            mainWindow.ApplySnapshot(reportContext.Snapshot with
            {
                Claude = reportContext.Snapshot.Claude with
                {
                    HasLimitData = true, SyncState = ClaudeSyncState.LiveSynced, OfficialSyncedAt = now,
                    PrimaryUsedPercent = 16, PrimaryResetsAt = now.AddHours(4), SecondaryUsedPercent = 3, SecondaryResetsAt = now.AddHours(110),
                    FableUsedPercent = 12, FableResetsAt = now.AddHours(110), PlanType = claudeParsed.PlanType, Details = claudeDetails
                }
            });
            var detailsToggle = (System.Windows.Controls.Primitives.ToggleButton)mainWindow.FindName("ClaudeDetailsButton");
            detailsToggle.IsChecked = true;
            FlushRender(mainWindow);
            var detailsPopup = (System.Windows.Controls.Primitives.Popup)mainWindow.FindName("ClaudeDetailsPopup");
            var detailsList = (System.Windows.Controls.StackPanel)mainWindow.FindName("ClaudeDetailsList");
            if (detailsToggle.Visibility != System.Windows.Visibility.Visible || detailsList.Children.Count < 10 ||
                detailsPopup.Child is not System.Windows.FrameworkElement detailsContent)
                throw new InvalidOperationException("Claude details popup did not render.");
            detailsContent.UpdateLayout();
            output.Add(RenderElement(detailsContent, Path.Combine(outputDirectory, "claude-details-popup.png")));
            var edgeDetails = claudeDetails with
            {
                Resets = claudeDetails.Resets! with { Grants = claudeDetails.Resets.Grants.Select(g => g with { Left = 0 }).ToArray() },
                ExtraUsage = new ClaudeExtraUsage(true, null, null, null, false)
            };
            mainWindow.ApplySnapshot(mainWindow.CurrentSnapshot with { Claude = mainWindow.CurrentSnapshot.Claude with { Details = edgeDetails } });
            FlushRender(mainWindow);
            var detailText = string.Join("\n", detailsList.Children.OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text));
            if (detailText.Contains("第 1 張") || detailText.Contains("未設上限") ||
                !detailText.Contains("本月 未提供") || !detailText.Contains("上限未提供"))
                throw new InvalidOperationException("Exhausted grants or unknown money displayed as available/known.");
            mainWindow.ApplySnapshot(mainWindow.CurrentSnapshot with { Claude = mainWindow.CurrentSnapshot.Claude with { Details = claudeDetails } });
            detailsToggle.IsChecked = false;
            FlushRender(mainWindow);
            if (detailsList.Children.Count != 0) throw new InvalidOperationException("Closed Claude details kept controls alive.");
            output.Add(RenderWindow(mainWindow, Path.Combine(outputDirectory, "main-window-claude-details.png")));

            File.WriteAllText(Path.Combine(outputDirectory, "health-stale-report.html"),
                ReportService.BuildHtml(reportContext with { Snapshot = partial }), new UTF8Encoding(false));
        }
        finally
        {
            mainWindow.Close();
        }

        var about = new AboutWindow(ThemeCatalog.Resolve(uiSettings.ThemeId), uiSettings.Language);
        try
        {
            about.Show();
            FlushRender(about);
            var participants = (System.Windows.Controls.TextBlock)about.FindName("AuthorValueText");
            if (participants.Text != "EricsWei、Atu")
                throw new InvalidOperationException("About window project team does not match the draft.");
            output.Add(RenderWindow(about, Path.Combine(outputDirectory, "about-window.png")));
        }
        finally { about.Close(); }

        output.AddRange(GenerateHeaderArtifacts(outputDirectory, reportContext.Snapshot));

        var miniStatePath = Path.Combine(outputDirectory, "mini-announcements-state.json");
        if (File.Exists(miniStatePath))
        {
            File.Delete(miniStatePath);
        }
        var miniWindow = new MiniStatusWindow(new MiniUsageAnnouncementService(miniStatePath));
        try
        {
            miniWindow.ApplySnapshot(reportContext.Snapshot);
            miniWindow.Show();
            miniWindow.ApplySnapshot(reportContext.Snapshot with
            {
                Codex = reportContext.Snapshot.Codex with
                {
                    SecondaryUsedPercent = 81,
                    SecondaryWindowMinutes = 10080,
                    SecondaryResetsAt = DateTimeOffset.Now.AddDays(4)
                }
            });
            FlushRender(miniWindow);
            var banner = (System.Windows.Controls.Border)miniWindow.FindName("AnnouncementBanner");
            var message = (System.Windows.Controls.TextBlock)miniWindow.FindName("AnnouncementText");
            if (banner.Visibility != System.Windows.Visibility.Visible ||
                !message.Text.Contains("19%", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Mini usage alarm preview was not rendered.");
            }
            output.Add(RenderWindow(miniWindow, Path.Combine(outputDirectory, "mini-usage-alarm.png")));
            miniWindow.Top = -40;
            miniWindow.Hide();
            miniWindow.Show();
            miniWindow.ApplySnapshot(reportContext.Snapshot with
            {
                Codex = reportContext.Snapshot.Codex with
                {
                    SecondaryUsedPercent = 81,
                    SecondaryWindowMinutes = 10080,
                    SecondaryResetsAt = DateTimeOffset.Now.AddDays(4)
                }
            });
            FlushRender(miniWindow);
            var dashboard = (System.Windows.FrameworkElement)miniWindow.FindName("RootBorder");
            var dashboardCenter = dashboard.PointToScreen(new System.Windows.Point(
                dashboard.ActualWidth / 2, dashboard.ActualHeight / 2));
            var workArea = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)dashboardCenter.X, (int)dashboardCenter.Y)).WorkingArea;
            var bannerTop = banner.PointToScreen(new System.Windows.Point(0, 0)).Y;
            var bannerBottom = banner.PointToScreen(new System.Windows.Point(0, banner.ActualHeight)).Y;
            if (banner.Visibility != System.Windows.Visibility.Visible ||
                bannerTop < workArea.Top - 1 || bannerBottom > workArea.Bottom + 1)
                throw new InvalidOperationException("Pending alarm was lost on hide/show or the notification was outside the work area.");
            output.Add(RenderWindow(miniWindow, Path.Combine(outputDirectory, "mini-recovered-alarm.png")));
            var advance = typeof(MiniStatusWindow).GetMethod("AdvanceAnnouncement",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            for (var repeat = 0; repeat < 5; repeat++) advance.Invoke(miniWindow, null);
            FlushRender(miniWindow);
            if (banner.Visibility != System.Windows.Visibility.Visible)
                throw new InvalidOperationException("Alarm disappeared before dismissal.");
            var dismiss = (System.Windows.Controls.Button)miniWindow.FindName("DismissAnnouncementButton");
            dismiss.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            FlushRender(miniWindow);
            if (banner.Visibility != System.Windows.Visibility.Collapsed)
                throw new InvalidOperationException("Closing the notification did not dismiss the alarm.");
            output.Add(RenderWindow(miniWindow, Path.Combine(outputDirectory, "mini-dismissed-alarm.png")));
            var currentUsage = (System.Windows.Controls.MenuItem)miniWindow.FindName("ShowUsageMenuItem");
            currentUsage.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            FlushRender(miniWindow);
            if (banner.Visibility != System.Windows.Visibility.Visible || string.IsNullOrWhiteSpace(message.Text))
                throw new InvalidOperationException("The current usage menu did not show a notification.");
        }
        finally
        {
            miniWindow.Close();
        }

        var creditState = Path.Combine(outputDirectory, "credit-preview-state.json");
        if (File.Exists(creditState)) File.Delete(creditState);
        uiSettings.ThemeId = "cloud_observation";
        AppSettingsService.Save(uiSettings);
        var creditWindow = new MiniStatusWindow(new MiniUsageAnnouncementService(creditState));
        try
        {
            var full = reportContext.Snapshot with { Codex = reportContext.Snapshot.Codex with {
                PrimaryUsedPercent = null, SecondaryUsedPercent = 100, IsStale = false,
                OfficialSyncedAt = DateTimeOffset.Now, PlanType = "pro-usd200-confirmed", AccountKey = "fixture-credit",
                Credits = new(61740.53m) }, Claude = ClaudeUsage.Missing };
            creditWindow.Show();
            creditWindow.ApplySnapshot(full);
            FlushRender(creditWindow);
            var text = (System.Windows.Controls.TextBlock)creditWindow.FindName("AnnouncementText");
            if (!text.Text.Contains("61,740")) throw new InvalidOperationException("Initial balance notification missing at full quota.");
            output.Add(RenderWindow(creditWindow, Path.Combine(outputDirectory, "mini-credit-balance.png")));
            var popup = (System.Windows.Controls.Primitives.Popup)creditWindow.FindName("MiniPlanPopup");
            popup.IsOpen = true;
            FlushRender(creditWindow);
            output.Add(RenderElement((System.Windows.FrameworkElement)popup.Child, Path.Combine(outputDirectory, "codex-current-plan.png")));
            popup.IsOpen = false;
            var samples = new List<double>();
            for (var i = 0; i < 100; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                creditWindow.ApplySnapshot(full);
                FlushRender(creditWindow);
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            File.WriteAllText(Path.Combine(outputDirectory, "full-quota-render.json"),
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
                    ["Fixture"] = true, ["Iterations"] = samples.Count, ["AverageMs"] = samples.Average(),
                    ["MaxMs"] = samples.Max(), ["SortedMs"] = samples.Order().ToArray() }));
        }
        finally { creditWindow.Close(); }

        var reportPath = Path.Combine(outputDirectory, "enhanced-report-offline-smoke.html");
        File.WriteAllText(
            reportPath,
            ReportService.BuildHtml(reportContext),
            new UTF8Encoding(false));
        output.Add(reportPath);
        output.AddRange(GenerateAdaptiveHeightArtifacts(outputDirectory, reportContext.Snapshot));
        return output;
    }

    internal static ReportContext CreateReportContext(AiChangelogSnapshot changelog)
    {
        var now = DateTimeOffset.Now;
        var snapshot = new UsageSnapshot(
            now,
            new CodexUsage(
                null,
                null,
                null,
                27,
                10080,
                now.AddDays(4),
                false,
                true,
                "prolite",
                now),
            new ClaudeUsage(
                true,
                now,
                "官方同步測試資料",
                11,
                14,
                true,
                ClaudeSyncState.LiveSynced,
                now,
                now.AddHours(2),
                now.AddDays(3),
                "default_claude_max_5x",
                13,
                now.AddDays(3)),
            new TokenTotals(120000, 34000, 9000, 4500, 167500, 3),
            [
                new ProjectUsage("UsageTools2026.Public", 108500),
                new ProjectUsage("SampleProject", 59000)
            ],
            [
                new CodexWorkWindow(
                    "UsageTools2026.Public",
                    "offline-codex",
                    185000,
                    353000,
                    now.AddMinutes(-2),
                    "gpt-5.6-sol",
                    "High",
                    "AI Changelog 與 Report 強化")
            ],
            [
                new CodexWorkWindow(
                    "SampleProject",
                    "offline-claude",
                    92000,
                    200000,
                    now.AddMinutes(-5),
                    "claude-offline-fixture",
                    "High",
                    "離線驗證")
            ],
            new CodexResetCreditSummary(
                [
                    new CodexResetCredit("offline-reset-1", "Full reset (Weekly + 5 hr)", now.AddDays(2)),
                    new CodexResetCredit("offline-reset-2", "Full reset (Weekly + 5 hr)", now.AddDays(9))
                ],
                now,
                false,
                2));
        var settings = new AppSettings();
        return new ReportContext(
            snapshot,
            settings,
            changelog,
            new HashSet<string>(StringComparer.Ordinal),
            (typeof(StageBArtifactGenerator).Assembly.GetName().Version?.ToString() ?? "UNKNOWN") + "-stage-b",
            null,
            now);
    }

    private static IReadOnlyList<string> GenerateHeaderArtifacts(string outputDirectory, UsageSnapshot snapshot)
    {
        var settings = AppSettingsService.Load();
        var originalTheme = settings.ThemeId;
        var originalLanguage = settings.Language;
        var output = new List<string>();
        var results = new List<Dictionary<string, object>>();
        try
        {
            foreach (var theme in ThemeCatalog.Themes)
            foreach (var language in new[] { AppLanguage.ZhTw, AppLanguage.En })
            {
                settings.ThemeId = theme.Id;
                settings.Language = language;
                AppSettingsService.Save(settings);
                var window = new MainWindow();
                try
                {
                    window.ApplySnapshot(snapshot);
                    window.Show();
                    FlushRender(window);
                    System.Windows.Rect Bounds(string name)
                    {
                        var element = (System.Windows.FrameworkElement)window.FindName(name);
                        return element.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(element.RenderSize));
                    }
                    var title = Bounds("AppTitleText");
                    var hide = Bounds("HeaderHideButton");
                    var controls = new[] { "LanguageButton", "ThemeButton", "OpacityButton" }.Select(Bounds).ToArray();
                    if (title.Right > hide.Left || controls.Any(b => b.Top < Math.Max(title.Bottom, hide.Bottom)))
                        throw new InvalidOperationException($"Header overlap: {theme.Id}/{language}");
                    for (var i = 0; i < controls.Length; i++)
                        if (controls[i].Left < 0 || controls[i].Right > window.ActualWidth ||
                            (i > 0 && controls[i - 1].Right > controls[i].Left))
                            throw new InvalidOperationException($"Header controls overflow: {theme.Id}/{language}");
                    var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("MainScrollViewer");
                    scroll.ScrollToBottom();
                    FlushRender(window);
                    var text = (System.Windows.Controls.TextBlock)window.FindName("LastUpdatedText");
                    var updated = Bounds("LastUpdatedText");
                    var version = Bounds("VersionText");
                    var updatedCenter = updated.Top + updated.Height / 2;
                    var versionCenter = version.Top + version.Height / 2;
                    var untrimmed = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentUICulture,
                        text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                        text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(window).PixelsPerDip);
                    if (updated.Right > version.Left || Math.Abs(updatedCenter - versionCenter) > 1 ||
                        untrimmed.WidthIncludingTrailingWhitespace > updated.Width + 0.5)
                        throw new InvalidOperationException($"Footer timestamp and version do not fit on the same row: {theme.Id}/{language}");
                    results.Add(new Dictionary<string, object>
                    {
                        ["theme"] = theme.Id, ["language"] = language.ToString(), ["timestamp"] = text.Text,
                        ["title"] = title.ToString(), ["updated"] = updated.ToString(), ["hide"] = hide.ToString(),
                        ["version"] = version.ToString(), ["passed"] = true
                    });
                    if (theme.Id is "deep-night" or "cloud_observation")
                        output.Add(RenderWindow(window, Path.Combine(outputDirectory, $"header-{theme.Id}-{language}.png")));
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            settings.ThemeId = originalTheme;
            settings.Language = originalLanguage;
            AppSettingsService.Save(settings);
        }
        var report = Path.Combine(outputDirectory, "header-layout-tests.json");
        File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(results,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        output.Add(report);
        return output;
    }

    private static IReadOnlyList<string> GenerateAdaptiveHeightArtifacts(string outputDirectory, UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        var parsed = ClaudeOfficialSyncService.ParseUsage(ClaudeParseSelfTest.RealShapeUsage, null, now);
        snapshot = snapshot with { ClaudeWorkWindows = Enumerable.Range(1, 20)
            .Select(i => new CodexWorkWindow("Fixture", "fixture-" + i, 100, 200000, now, "claude-test")).ToArray(), Claude = snapshot.Claude with
        {
            HasLimitData = true, OfficialSyncedAt = now, SyncState = ClaudeSyncState.LiveSynced,
            Details = parsed.Details with
            {
                Pools = parsed.Details.Pools.Select(p => p with { ResetsAt = now.AddDays(2) }).ToArray(),
                Resets = parsed.Details.Resets! with { Grants = parsed.Details.Resets.Grants.Select(g => g with { EndsAt = now.AddDays(5) }).ToArray() }
            }
        } };
        var settings = AppSettingsService.Load();
        var originalTheme = settings.ThemeId;
        var originalLanguage = settings.Language;
        var output = new List<string>();
        var results = new List<Dictionary<string, object>>();
        var plus = snapshot with
        {
            Codex = snapshot.Codex with
            {
                PlanType = "plus", PrimaryUsedPercent = 36, PrimaryWindowMinutes = 300,
                PrimaryResetsAt = DateTimeOffset.Now.AddHours(2)
            }
        };
        try
        {
            foreach (var theme in ThemeCatalog.Themes)
            foreach (var language in new[] { AppLanguage.ZhTw, AppLanguage.En })
            {
                settings.ThemeId = theme.Id;
                settings.Language = language;
                AppSettingsService.Save(settings);
                var window = new MainWindow();
                try
                {
                    window.ApplySnapshot(plus);
                    window.Show();
                    FlushRender(window);
                    var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("MainScrollViewer");
                    System.Windows.Rect Bounds(string name)
                    {
                        var element = (System.Windows.FrameworkElement)window.FindName(name);
                        return element.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(element.RenderSize));
                    }
                    foreach (var workHeight in new[] { 1100, 600 })
                    {
                        foreach (var dual in new[] { true, false })
                        {
                            window.ApplyWorkAreaBounds(new System.Windows.Rect(0, 0, 1920, workHeight));
                            window.ApplySnapshot(dual ? plus : snapshot);
                            scroll.ScrollToTop();
                            FlushRender(window);
                            var secondary = (System.Windows.FrameworkElement)window.FindName("CodexSecondaryRow");
                            if ((secondary.Visibility == System.Windows.Visibility.Visible) != dual)
                                throw new InvalidOperationException("Quota layout fixture did not select the requested window count.");
                            if (window.ActualHeight > workHeight - 15 || window.Top < 0 ||
                                window.Top + window.ActualHeight > workHeight + 0.5)
                                throw new InvalidOperationException($"Normal window exceeds work area: {theme.Id}/{language}/{workHeight}");
                            if (workHeight == 1100 && scroll.ScrollableHeight > 0.5)
                                throw new InvalidOperationException($"Unnecessary normal-window scroll: {theme.Id}/{language}");
                            if (workHeight == 600 && scroll.ScrollableHeight <= 0)
                                throw new InvalidOperationException("Small-screen overflow has no scroll fallback.");
                            var topViewport = Bounds("MainScrollViewer");
                            var title = Bounds("AppTitleText");
                            var hide = Bounds("HeaderHideButton");
                            if (title.Right > hide.Left || hide.Right > topViewport.Right)
                                throw new InvalidOperationException($"Scrollable header overlap: {theme.Id}/{language}/{workHeight}");
                            foreach (var name in new[] { "LanguageButton", "ThemeButton", "OpacityButton" })
                            {
                                var bounds = Bounds(name);
                                if (bounds.Top < Math.Max(title.Bottom, hide.Bottom) || bounds.Right > topViewport.Right)
                                    throw new InvalidOperationException($"Scrollable header control overflow: {name}/{theme.Id}/{language}");
                            }
                            foreach (var provider in new[] { "Codex", "Claude" })
                            {
                                var label = (System.Windows.Controls.TextBlock)window.FindName(provider + "TitleText");
                                var header = (System.Windows.FrameworkElement)label.Parent;
                                var headerBounds = header.TransformToAncestor(window).TransformBounds(new System.Windows.Rect(header.RenderSize));
                                var labelBounds = Bounds(provider + "TitleText");
                                var newsBounds = Bounds(provider + "NewsButton");
                                var lightBounds = Bounds(provider + "StatusLight");
                                if (label.FontSize != 15 || labelBounds.Right > newsBounds.Left + 0.5 || lightBounds.Right > headerBounds.Right + 0.5)
                                {
                                    RenderWindow(window, Path.Combine(outputDirectory, "provider-header-failure.png"));
                                    throw new InvalidOperationException($"Provider header overflow: {provider}/{theme.Id}/{language}/{workHeight}; title={labelBounds}; news={newsBounds}; light={lightBounds}; header={headerBounds}");
                                }
                                if (provider == "Claude" && Bounds("ClaudeFableBadge").Right > lightBounds.Left + 0.5)
                                    throw new InvalidOperationException("Fable badge overlaps the connection light.");
                                var work = (System.Windows.Controls.Primitives.ToggleButton)window.FindName(provider + "WorkButton");
                                var workText = new FormattedText(work.Content?.ToString() ?? "", System.Globalization.CultureInfo.CurrentCulture,
                                    System.Windows.FlowDirection.LeftToRight, new Typeface(work.FontFamily, work.FontStyle, work.FontWeight, work.FontStretch),
                                    work.FontSize, System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip);
                                if (workText.WidthIncludingTrailingWhitespace > work.ActualWidth - work.Padding.Left - work.Padding.Right - 2 + 0.5)
                                    throw new InvalidOperationException($"Work count clipped: {provider}/{theme.Id}/{language}");
                            }
                            scroll.ScrollToBottom();
                            FlushRender(window);
                            var viewport = Bounds("MainScrollViewer");
                            foreach (var name in new[] { "ToolsButton", "AboutButton", "BottomTodayText", "LastUpdatedText", "VersionText" })
                            {
                                var bounds = Bounds(name);
                                if (bounds.Bottom > viewport.Bottom + 0.5 || bounds.Top < viewport.Top - 0.5 ||
                                    bounds.Left < viewport.Left - 0.5 || bounds.Right > viewport.Right + 0.5)
                                    throw new InvalidOperationException($"Unreachable footer {name}: {theme.Id}/{language}/{workHeight}");
                            }
                            results.Add(new Dictionary<string, object>
                            {
                                ["theme"] = theme.Id, ["language"] = language.ToString(), ["codexDualWindow"] = dual,
                                ["workAreaHeight"] = workHeight, ["windowHeight"] = window.ActualHeight,
                                ["scrollableHeight"] = scroll.ScrollableHeight, ["footerReachable"] = true
                            });
                            if (language == AppLanguage.ZhTw && dual && (workHeight == 1100 || theme.Id == "matrix"))
                                output.Add(RenderWindow(window, Path.Combine(outputDirectory, theme.Id == "matrix"
                                    ? $"adaptive-classic-plus-{workHeight}.png" : $"adaptive-{(theme.Id == "classic" ? "classic-theme" : theme.Id)}-plus-{workHeight}.png")));
                        }
                    }
                    window.ApplyWorkAreaBounds(new System.Windows.Rect(0, 0, 1920, 1100));
                    window.ApplySnapshot(plus);
                    FlushRender(window);
                    var expandedHeight = window.ActualHeight;
                    window.ApplySnapshot(snapshot);
                    FlushRender(window);
                    if (window.ActualHeight >= expandedHeight - 20)
                        throw new InvalidOperationException($"Window did not shrink after hiding 5H: {theme.Id}/{language}");
                    var negativeWorkArea = new System.Windows.Rect(-1920, -200, 1920, 900);
                    window.ApplyWorkAreaBounds(negativeWorkArea);
                    FlushRender(window);
                    var dpi = VisualTreeHelper.GetDpi(window);
                    // Windows rounds actual window positions to physical pixels.
                    var pixelX = 1 / dpi.DpiScaleX;
                    var pixelY = 1 / dpi.DpiScaleY;
                    if (window.Left < negativeWorkArea.Left + 8 - pixelX ||
                        window.Left + window.ActualWidth > negativeWorkArea.Right - 8 + pixelX ||
                        window.Top < negativeWorkArea.Top + 8 - pixelY ||
                        window.Top + window.ActualHeight > negativeWorkArea.Bottom - 8 + pixelY)
                        throw new InvalidOperationException($"Negative-coordinate monitor positioning: {theme.Id}/{language}");
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            settings.ThemeId = originalTheme;
            settings.Language = originalLanguage;
            AppSettingsService.Save(settings);
        }
        var path = Path.Combine(outputDirectory, "adaptive-height-tests.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(results,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        output.Add(path);
        return output;
    }

    private static void FlushRender(System.Windows.Window window)
    {
        window.Dispatcher.Invoke(
            () => window.UpdateLayout(),
            DispatcherPriority.Render);
    }

    private static string RenderWindow(System.Windows.Window window, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        return outputPath;
    }

    private static string RenderElement(System.Windows.FrameworkElement element, string outputPath)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            element.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new System.Windows.Rect(element.DesiredSize));
            element.UpdateLayout();
        }

        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        return outputPath;
    }
}
