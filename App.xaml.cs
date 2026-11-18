using UsageTools2026.Services;
using UsageTools2026.Models;
using System.Windows.Threading;
using System.IO;
using System.Diagnostics;

namespace UsageTools2026;

public partial class App : System.Windows.Application
{
    private static readonly bool MobileServerFeatureEnabled = false;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _aiChangelogRefreshGate = new(1, 1);
    private MainWindow? _window;
    private MiniStatusWindow? _miniWindow;
    private UsageRefreshService? _refreshService;
    private UpdateService? _updateService;
    private SystemMetricsService? _systemMetricsService;
    private TrayIconService? _trayIconService;
    private System.Threading.Timer? _timer;
    private ClaudeOfficialSyncService? _claudeSyncService;
    private DispatcherTimer? _claudeSyncTimer;
    private DispatcherTimer? _claudeActivitySyncTimer;
    private FileSystemWatcher? _claudeWatcher;
    private DateTimeOffset _lastClaudeSyncAttempt = DateTimeOffset.MinValue;
    private MobileServerService? _mobileServer;
    private PersonalMobileService? _personalMobile;
    private MiniUsageAnnouncementService? _desktopAnnouncements;
    private DispatcherTimer? _systemTimer;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _codexRefreshDebounceTimer;
    private FileSystemWatcher? _codexWatcher;
    private UsageSnapshot _currentSnapshot = UsageSnapshot.Empty;
    private SystemUsage _currentSystemUsage = SystemUsage.Empty;
    private bool _systemMetricsBusy;
    private int _codexRefreshScheduled;
    private UpdateInfo? _availableUpdate;
    private AiChangelogService? _aiChangelogService;
    private AiChangelogSnapshot _aiChangelogSnapshot = AiChangelogSnapshot.Empty;
    private AiChangelogWindow? _aiChangelogWindow;
    private DispatcherTimer? _aiChangelogTimer;
    private bool _isShuttingDown;
    private CodexResetReminderState? _codexResetReminderState = CodexResetReminderStateService.Load();

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--personal-mobile-enable"))
        {
            var settings = AppSettingsService.Load();
            settings.PersonalMobileEnabled = true;
            AppSettingsService.Save(settings);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--mobile-selftest"))
        {
            Shutdown(SelfTestRunner.Run("mobile", PersonalMobileService.RunTests, e.Args));
            return;
        }
        if (e.Args.Contains("--personal-mobile-setup"))
        {
            var i = Array.IndexOf(e.Args, "--personal-mobile-setup");
            PersonalMobileService.Setup(e.Args[i + 1]);
            Shutdown();
            return;
        }
        var startInMiniMode = e.Args.Any(a => string.Equals(a, "--mini", StringComparison.OrdinalIgnoreCase));
        var previewUpdate = e.Args.Any(a => string.Equals(a, "--preview-update", StringComparison.OrdinalIgnoreCase));

        if (e.Args.Any(a => string.Equals(a, "--parse-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _isShuttingDown = true;
            Shutdown(SelfTestRunner.Run("parse", ClaudeParseSelfTest.Run, e.Args));
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--stage-b-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _isShuttingDown = true;
            Shutdown(SelfTestRunner.Run("stage-b", StageBSelfTest.Run, e.Args));
            return;
        }

        if (e.Args.Contains("--health-selftest"))
        {
            _isShuttingDown = true;
            Shutdown(SelfTestRunner.Run("health-a", HealthSelfTest.Run, e.Args));
            return;
        }

        var artifactArgumentIndex = Array.FindIndex(
            e.Args,
            argument => string.Equals(argument, "--stage-b-artifacts", StringComparison.OrdinalIgnoreCase));
        if (artifactArgumentIndex >= 0)
        {
            var outputDirectory = artifactArgumentIndex + 1 < e.Args.Length
                ? e.Args[artifactArgumentIndex + 1]
                : Path.Combine(Path.GetTempPath(), "UsageTools2026-stage-b-artifacts");
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            AppSettingsService.UseTestStore(Path.Combine(outputDirectory, "isolated-settings.json"));
            DiagnosticsService.TestLogDirectory = outputDirectory;
            _isShuttingDown = true;
            Shutdown(RunStageBArtifacts(outputDirectory));
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--stage-b-preview", StringComparison.OrdinalIgnoreCase)))
        {
            var isolatedPreview = Path.Combine(Path.GetTempPath(), "UsageTools2026-health-preview", Guid.NewGuid().ToString("N"));
            AppSettingsService.UseTestStore(Path.Combine(isolatedPreview, "settings.json"));
            DiagnosticsService.TestLogDirectory = isolatedPreview;
            RunStageBInteractivePreview();
            return;
        }

        if (e.Args.Contains("--claude-login"))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            var confirmed = await ClaudeWebViewUsageFetcher.ShowLoginAsync();
            if (confirmed)
            {
                var result = await new ClaudeOfficialSyncService().SyncAsync();
                DiagnosticsService.Write("Claude login sync: " + result);
                System.Windows.MessageBox.Show(result.StartsWith("OK", StringComparison.Ordinal)
                    ? "Claude 官方用量已同步。" : "尚未同步成功：" + result, "UsageTools2026.Public");
            }
            Shutdown();
            return;
        }
        if (e.Args.Any(a => string.Equals(a, "--probe-claude", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            await new ClaudeOfficialSyncService().SyncAsync();
            RunClaudeProbe();
            _isShuttingDown = true;
            Shutdown();
            return;
        }

        DiagnosticsService.Install();
        _aiChangelogService = new AiChangelogService();
        _aiChangelogSnapshot = _aiChangelogService.LoadCachedOrOfflineFixture();

        _window = new MainWindow();
        _window.RequestRefresh += async (_, _) => await SyncClaudeAndRefreshAsync();
        _window.RequestReport += (_, _) => OpenLatestReport();
        _window.RequestHide += (_, _) => HideMainToMini();
        _window.RequestShowUpdate += (_, _) => ShowUpdateDialog();
        _window.RequestAiChangelog += ShowAiChangelog;
        _window.DataSourceSettingsChanged += async (_, _) => await ApplyDataSourceSettingsAsync();
        _window.WindowTopmostChanged += (_, _) => ApplyWindowTopmostPreference();
        _window.RequestRestart += (_, _) => RestartApplication();
        _window.RequestClaudeLogin += async (_, _) =>
        {
            if (_claudeSyncService?.IsSyncing == true)
            {
                System.Windows.MessageBox.Show("Claude 同步正在進行，請稍候再登入。", "UsageTools2026.Public");
                return;
            }
            if (await ClaudeWebViewUsageFetcher.ShowLoginAsync())
            {
                _claudeSyncService?.RetryAfterLogin();
                await SyncClaudeAndRefreshAsync();
            }
        };
        _window.RequestQuit += (_, _) =>
        {
            _isShuttingDown = true;
            Shutdown();
        };
        _window.Closing += (_, args) =>
        {
            if (_isShuttingDown)
            {
                return;
            }

            args.Cancel = true;
            HideMainToMini();
        };

        _desktopAnnouncements = new MiniUsageAnnouncementService();
        _miniWindow = new MiniStatusWindow(_desktopAnnouncements);
        ApplyWindowTopmostPreference();
        _window.AppearanceChanged += (_, _) => _miniWindow.ReloadAppearance();
        _miniWindow.RequestRestore += (_, _) => ShowMainWindow();
        _miniWindow.RequestShowUpdate += (_, _) => ShowUpdateDialog();
        _miniWindow.RequestRefresh += async (_, _) => await SyncClaudeAndRefreshAsync();
        _miniWindow.RequestQuit += (_, _) =>
        {
            _isShuttingDown = true;
            Shutdown();
        };

        _refreshService = new UsageRefreshService();
        _updateService = new UpdateService();
        _claudeSyncService = new ClaudeOfficialSyncService();
        _systemMetricsService = new SystemMetricsService();
        StartMobileServer();
        _personalMobile = new PersonalMobileService();
        _trayIconService = new TrayIconService(
            () => ToggleMainWindow(),
            () => ShowMiniWindow(),
            async () => await SyncClaudeAndRefreshAsync(),
            () => OpenLatestReport(),
            () =>
            {
                _isShuttingDown = true;
                Shutdown();
            });

        // The updater health check validates that WPF/BAML, settings, services, and tray startup
        // succeeded. Do not make rollback depend on the first potentially slow usage scan.
        UpdateInstallerService.TryWriteHealthMarker(e.Args);
        RefreshAiChangelogButtons();
        _aiChangelogTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(3) };
        _aiChangelogTimer.Tick += async (_, _) => await RefreshAiChangelogAsync();
        _aiChangelogTimer.Start();

        _ = UpdateSystemMetricsAsync();
        _window.PositionNearTray();
        _window.Show();
        _window.Activate();
        _ = Dispatcher.InvokeAsync(async () => await RefreshAsync(), DispatcherPriority.Background);
        if (previewUpdate)
        {
            ApplyAvailableUpdate(UpdateService.CreatePreview());
        }
        else
        {
            _ = CheckForUpdatesAfterStartupAsync(delayBeforeCheck: true);
            _updateTimer = new DispatcherTimer { Interval = UpdateConfiguration.CheckInterval };
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAfterStartupAsync(delayBeforeCheck: false);
            _updateTimer.Start();
        }

        _timer = new System.Threading.Timer(async _ => await Dispatcher.InvokeAsync(RefreshAsync), null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));

        _systemTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _systemTimer.Tick += async (_, _) => await UpdateSystemMetricsAsync();
        _systemTimer.Start();

        // Keep a low-frequency safety poll. Active Claude sessions also request a debounced sync,
        // capped at one official request per minute, so usage catches up without hammering WebView2.
        _claudeSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _claudeSyncTimer.Tick += async (_, _) => await SyncClaudeAndRefreshAsync();
        if (AppSettingsService.Load().ClaudeDataEnabled)
        {
            _claudeSyncTimer.Start();
            _ = Dispatcher.InvokeAsync(async () => await SyncClaudeAndRefreshAsync());
        }
        _ = Dispatcher.InvokeAsync(async () => await RefreshAiChangelogAsync());
        _ = startInMiniMode
            ? Dispatcher.InvokeAsync(ShowMiniWindow, DispatcherPriority.ApplicationIdle)
            : Dispatcher.InvokeAsync(EnsureStartupMainVisible, DispatcherPriority.ApplicationIdle);

        ConfigureCodexSessionWatcher(AppSettingsService.Load().CodexDataEnabled);
        ConfigureClaudeSessionWatcher(AppSettingsService.Load().ClaudeDataEnabled);
        var updateFailure = UpdateInstallerService.ConsumeFailureMessage();
        if (!string.IsNullOrWhiteSpace(updateFailure))
        {
            _ = Dispatcher.InvokeAsync(
                () => System.Windows.MessageBox.Show(
                    _window,
                    updateFailure,
                    "UsageTools2026 更新回復",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning),
                DispatcherPriority.ApplicationIdle);
        }
    }

    private void EnsureStartupMainVisible()
    {
        if (_window is null)
        {
            return;
        }

        _miniWindow?.Hide();
        _window.PositionNearTray();
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
    }

    private async Task SyncClaudeAndRefreshAsync()
    {
        if (!AppSettingsService.Load().ClaudeDataEnabled)
        {
            await RefreshAsync();
            return;
        }

        if (_claudeSyncService is null || _claudeSyncService.IsSyncing)
        {
            return;
        }

        try
        {
            _lastClaudeSyncAttempt = DateTimeOffset.Now;
            var result = await _claudeSyncService.SyncAsync();
            if (result != "backoff") DiagnosticsService.Write("Claude official sync: " + result);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write("Claude official sync threw: " + ex);
        }

        await RefreshAsync();
    }

    private async Task ApplyDataSourceSettingsAsync()
    {
        var settings = AppSettingsService.Load();
        ConfigureCodexSessionWatcher(settings.CodexDataEnabled);
        ConfigureClaudeSessionWatcher(settings.ClaudeDataEnabled);
        if (settings.ClaudeDataEnabled)
        {
            _claudeSyncTimer?.Start();
        }
        else
        {
            _claudeSyncTimer?.Stop();
        }

        if (settings.ClaudeDataEnabled && !_currentSnapshot.Claude.HasLimitData)
        {
            await SyncClaudeAndRefreshAsync();
        }
        else
        {
            await RefreshAsync();
        }
    }

    private void RestartApplication()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                throw new InvalidOperationException("Current process path is unavailable.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            };
            if (string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? throw new InvalidOperationException("Entry assembly path is unavailable."));
            }

            Process.Start(startInfo);
            DiagnosticsService.Write("UsageTools restart requested from the normal-mode Tools switch.");
            _isShuttingDown = true;
            Shutdown();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write("UsageTools restart failed: " + ex);
            if (_window is not null)
            {
                _window.ResetRestartSwitch();
                System.Windows.MessageBox.Show(
                    _window,
                    "重新啟動失敗，工具會繼續執行。\n" + ex.Message,
                    "UsageTools2026.Public",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _timer?.Dispose();
        _claudeSyncTimer?.Stop();
        _claudeActivitySyncTimer?.Stop();
        _claudeWatcher?.Dispose();
        _mobileServer?.Stop();
        _systemTimer?.Stop();
        _updateTimer?.Stop();
        _aiChangelogTimer?.Stop();
        _codexRefreshDebounceTimer?.Stop();
        _codexWatcher?.Dispose();
        _miniWindow?.Close();
        _aiChangelogWindow?.CloseForShutdown();
        _trayIconService?.Dispose();
        _updateService?.Dispose();
        _refreshGate.Dispose();
        _aiChangelogRefreshGate.Dispose();
        base.OnExit(e);
    }

    private void OpenLatestReport()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                      ?? "UNKNOWN / 未提供";
        var context = new ReportContext(
            _currentSnapshot,
            AppSettingsService.Load(),
            _aiChangelogSnapshot,
            _aiChangelogService?.ReadReleaseIds ?? new HashSet<string>(StringComparer.Ordinal),
            version,
            _availableUpdate,
            DateTimeOffset.Now);
        ReportService.OpenLatestReport(context);
    }

    private void ShowAiChangelog(AiProvider provider)
    {
        if (_window is null || _aiChangelogService is null)
        {
            return;
        }

        _aiChangelogWindow ??= CreateAiChangelogWindow();
        _aiChangelogWindow.SetSnapshot(_aiChangelogSnapshot);
        if (!_aiChangelogWindow.IsVisible)
        {
            _aiChangelogWindow.Show();
        }

        _aiChangelogWindow.ShowProvider(provider);
        _aiChangelogWindow.Activate();
    }

    private AiChangelogWindow CreateAiChangelogWindow()
    {
        var window = new AiChangelogWindow
        {
            Owner = _window
        };
        window.ProviderViewed += provider =>
        {
            _aiChangelogService?.MarkProviderRead(_aiChangelogSnapshot, provider);
            RefreshAiChangelogButtons();
        };
        window.RefreshRequested += async () => await RefreshAiChangelogAsync();
        return window;
    }

    private async Task RefreshAiChangelogAsync()
    {
        if (_aiChangelogService is null || !await _aiChangelogRefreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            _aiChangelogSnapshot = await _aiChangelogService.RefreshOfficialAsync();
            DiagnosticsService.Write(
                $"AI changelog sync: {_aiChangelogSnapshot.SourceLabel}; " +
                $"Codex={_aiChangelogSnapshot.ForProvider(AiProvider.Codex).Count}; " +
                $"Claude={_aiChangelogSnapshot.ForProvider(AiProvider.ClaudeCode).Count}");
            _aiChangelogWindow?.SetSnapshot(_aiChangelogSnapshot);
            RefreshAiChangelogButtons();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write("AI changelog sync failed: " + ex.GetType().Name);
        }
        finally
        {
            _aiChangelogRefreshGate.Release();
        }
    }

    private void RefreshAiChangelogButtons()
    {
        if (_window is null || _aiChangelogService is null)
        {
            return;
        }

        _window.SetAiChangelogStatus(
            _aiChangelogService.GetUnreadCount(_aiChangelogSnapshot, AiProvider.Codex),
            _aiChangelogService.GetUnreadCount(_aiChangelogSnapshot, AiProvider.ClaudeCode),
            _aiChangelogSnapshot.IsOfflineFixture);
    }

    /// <summary>
    /// Safe verification mode (UsageTools2026.exe --probe-claude). Runs the real reader and reports
    /// ONLY: whether the profile/org were found, the sync state, the official percentages, reset and
    /// sync times. It NEVER prints cookies, session tokens, or authorization headers.
    /// Close Claude Desktop first to verify a real live sync.
    /// </summary>
    private static void RunClaudeProbe()
    {
        string text;
        try
        {
            var claude = new UsageRefreshService().LoadSnapshot().Claude;
            var lines = new[]
            {
                $"SyncState         : {claude.SyncState}",
                $"HasLimitData      : {claude.HasLimitData}",
                $"Primary (5h) used : {(claude.PrimaryUsedPercent is { } p ? p.ToString("0.#") + "%" : "<none>")}",
                $"Secondary (7d)used: {(claude.SecondaryUsedPercent is { } s ? s.ToString("0.#") + "%" : "<none>")}",
                $"5h resets at      : {claude.PrimaryResetsAt?.LocalDateTime.ToString("yyyy/MM/dd HH:mm") ?? "<none>"}",
                $"7d resets at      : {claude.SecondaryResetsAt?.LocalDateTime.ToString("yyyy/MM/dd HH:mm") ?? "<none>"}",
                $"Official synced at: {claude.OfficialSyncedAt?.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss") ?? "<none>"}",
                $"Status detail     : {claude.StatusDetail}"
            };
            text = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            text = "Probe failed: " + ex.GetType().Name + " - " + ex.Message;
        }

        try
        {
            var probePath = Path.Combine(Path.GetTempPath(), "UsageTools2026-claude-probe.txt");
            File.WriteAllText(probePath, text);
            text += Environment.NewLine + Environment.NewLine + "(saved to " + probePath + ")";
        }
        catch
        {
            // Writing the probe file is best-effort.
        }
    }

    /// <summary>
    /// Render real WPF controls from isolated offline fixtures; any failure yields a nonzero exit.
    /// </summary>
    private static int RunStageBArtifacts(string outputDirectory)
    {
        string text;
        try
        {
            var paths = StageBArtifactGenerator.Generate(outputDirectory);
            text = "PASS Stage B artifacts" + Environment.NewLine +
                   string.Join(Environment.NewLine, paths);
        }
        catch (Exception ex)
        {
            text = "FAIL Stage B artifacts: " + ex;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "artifact-generation.txt"), text);
        }
        catch
        {
            return 2;
        }
        return text.StartsWith("PASS", StringComparison.Ordinal) ? 0 : 1;
    }

    private void RunStageBInteractivePreview()
    {
        var previewStatePath = Path.Combine(
            Path.GetTempPath(),
            "UsageTools2026-stage-b-preview-" + Guid.NewGuid().ToString("N") + ".json");
        var changelogService = new AiChangelogService(previewStatePath);
        var changelog = changelogService.LoadOfflineFixture();
        var reportContext = StageBArtifactGenerator.CreateReportContext(changelog);
        AiChangelogWindow? changelogWindow = null;
        MiniStatusWindow? previewMiniWindow = null;

        var previewWindow = new MainWindow
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Title = "UsageTools2026 - Health A Offline Preview"
        };
        _window = previewWindow;
        previewWindow.SetOfflinePreviewLabel();
        previewWindow.ApplySnapshot(reportContext.Snapshot);

        void RefreshButtons() => previewWindow.SetAiChangelogStatus(
            changelogService.GetUnreadCount(changelog, AiProvider.Codex),
            changelogService.GetUnreadCount(changelog, AiProvider.ClaudeCode),
            changelog.IsOfflineFixture);

        void OpenChangelog(AiProvider provider)
        {
            if (changelogWindow is null)
            {
                changelogWindow = new AiChangelogWindow { Owner = previewWindow };
                changelogWindow.ProviderViewed += viewedProvider =>
                {
                    changelogService.MarkProviderRead(changelog, viewedProvider);
                    RefreshButtons();
                };
            }

            changelogWindow.SetSnapshot(changelog);
            if (!changelogWindow.IsVisible)
            {
                changelogWindow.Show();
            }

            changelogWindow.ShowProvider(provider);
            changelogWindow.Activate();
        }

        void ExitPreview()
        {
            _isShuttingDown = true;
            changelogWindow?.CloseForShutdown();
            previewMiniWindow?.Close();
            previewWindow.Close();
            Shutdown();
        }

        void ShowPreviewMini()
        {
            previewWindow.Hide();
            if (previewMiniWindow is null)
            {
                previewMiniWindow = new MiniStatusWindow();
                _miniWindow = previewMiniWindow;
                ApplyWindowTopmostPreference();
                previewMiniWindow.RequestRestore += (_, _) =>
                {
                    previewMiniWindow.Hide();
                    previewWindow.Show();
                    previewWindow.Activate();
                };
                previewMiniWindow.RequestRefresh += (_, _) =>
                {
                    previewMiniWindow.ApplySnapshot(reportContext.Snapshot);
                    previewMiniWindow.ApplySystemMetrics(SystemUsage.Empty);
                };
                previewMiniWindow.RequestQuit += (_, _) => ExitPreview();
            }

            previewMiniWindow.ReloadAppearance();
            previewMiniWindow.ApplySnapshot(reportContext.Snapshot);
            previewMiniWindow.ApplySystemMetrics(SystemUsage.Empty);
            previewMiniWindow.SetAvailableUpdate(null);
            previewMiniWindow.ShowAtSavedOrDefault();
        }

        previewWindow.RequestAiChangelog += OpenChangelog;
        previewWindow.RequestReport += (_, _) => ReportService.OpenLatestReport(reportContext);
        previewWindow.RequestRefresh += (_, _) => previewWindow.ApplySnapshot(reportContext.Snapshot);
        previewWindow.RequestHide += (_, _) => ShowPreviewMini();
        previewWindow.WindowTopmostChanged += (_, _) => ApplyWindowTopmostPreference();
        previewWindow.RequestQuit += (_, _) => ExitPreview();
        previewWindow.Closed += (_, _) =>
        {
            if (!_isShuttingDown)
            {
                _isShuttingDown = true;
                changelogWindow?.CloseForShutdown();
                Shutdown();
            }
        };

        RefreshButtons();
        previewWindow.Show();
        previewWindow.Activate();
    }

    private void ToggleMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            HideMainToMini();
            return;
        }

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _miniWindow?.Hide();
        _window.PositionNearTray();
        _window.ApplySnapshot(_currentSnapshot);
        _window.ApplySystemMetrics(_currentSystemUsage);
        _window.SetAvailableUpdate(_availableUpdate);
        _window.Show();
        _window.Activate();
    }

    private void ApplyWindowTopmostPreference()
    {
        var keepOnTop = AppSettingsService.Load().KeepWindowOnTop;
        if (_window is not null) _window.Topmost = keepOnTop;
        if (_miniWindow is not null) _miniWindow.Topmost = keepOnTop;
    }

    private void HideMainToMini()
    {
        ShowMiniWindow();
    }

    private void ShowMiniWindow()
    {
        if (_window is null || _miniWindow is null)
        {
            return;
        }

        _window.Hide();
        _miniWindow.ReloadAppearance();
        _miniWindow.ApplySnapshot(_currentSnapshot);
        _miniWindow.ApplySystemMetrics(_currentSystemUsage);
        _miniWindow.SetAvailableUpdate(_availableUpdate);
        _miniWindow.ShowAtSavedOrDefault();
    }

    private async Task CheckForUpdatesAfterStartupAsync(bool delayBeforeCheck)
    {
        if (_updateService?.IsConfigured != true)
        {
            return;
        }

        if (delayBeforeCheck)
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
        }
        var settings = AppSettingsService.Load();
        var now = DateTimeOffset.UtcNow;
        if (settings.LastUpdateCheckUtc is { } lastCheck &&
            now - lastCheck < UpdateConfiguration.CheckInterval)
        {
            return;
        }

        var update = await _updateService.CheckForUpdateAsync();
        if (!_updateService.LastCheckSucceeded)
        {
            return;
        }

        settings.LastUpdateCheckUtc = now;
        AppSettingsService.Save(settings);
        ApplyAvailableUpdate(update);
    }

    private void ApplyAvailableUpdate(UpdateInfo? update)
    {
        _availableUpdate = update;
        _window?.SetAvailableUpdate(update);
        _miniWindow?.SetAvailableUpdate(update);
    }

    private void ShowUpdateDialog()
    {
        if (_availableUpdate is null || _window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            ShowMainWindow();
        }

        var installEnabled = _updateService?.IsConfigured == true && UpdateInstallerService.IsAvailable;
        var dialog = new UpdateWindow(_availableUpdate, installEnabled)
        {
            Owner = _window
        };
        dialog.InstallRequested += async (_, _) => await DownloadAndInstallUpdateAsync(dialog, _availableUpdate);
        dialog.ShowDialog();
    }

    private async Task DownloadAndInstallUpdateAsync(UpdateWindow dialog, UpdateInfo update)
    {
        if (_updateService is null)
        {
            dialog.SetDownloadFailed("更新服務尚未初始化。請重新啟動工具後再試。");
            return;
        }

        var cancellationToken = dialog.BeginDownload();
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(dialog.SetDownloadProgress);
            var packagePath = await _updateService.DownloadPackageAsync(update, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            dialog.SetLaunchingInstaller();
            UpdateInstallerService.Start(update, packagePath);
            await Task.Delay(300);
            _isShuttingDown = true;
            Shutdown();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            dialog.SetDownloadCancelled();
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write($"Update install start failed: {ex.GetType().Name} - {ex.Message}");
            dialog.SetDownloadFailed("下載或驗證失敗，目前版本完全沒有變更。請稍後再試。");
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshService is null || _trayIconService is null || _window is null)
        {
            return;
        }

        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var snapshot = await Task.Run(_refreshService.LoadSnapshot);
            _currentSnapshot = snapshot;
            _desktopAnnouncements?.Observe(snapshot);
            if (_miniWindow?.IsVisible != true && _desktopAnnouncements is not null)
            {
                foreach (var item in _desktopAnnouncements.Pending().Where(item => item.Kind == MiniUsageAnnouncementKind.ResetCreditsAdded))
                {
                    var isClaude = item.Provider == "Claude Code";
                    if (isClaude ? UsageFreshness.State(snapshot.Claude) != UsageDataState.Fresh
                        : snapshot.CodexResetCredits.IsStale || snapshot.CodexResetCredits.SyncedAt is null) continue;
                    if (!isClaude) _window.FlashResetCreditReminder();
                    if (_trayIconService is not null)
                    {
                        _trayIconService.ShowNewResetCredits(item.AddedCreditCount, item.CreditCount,
                            isClaude ? "Claude Code 新增" + ClaudeOfficialDetailsParser.CreditKindName(item.CreditKind) : "Codex 新增重置券");
                        _desktopAnnouncements.Acknowledge(item);
                        DiagnosticsService.Write("New reset credit desktop reminder dispatched.");
                    }
                }
            }
            if (_window.IsVisible)
            {
                _window.ApplySnapshot(snapshot);
                _window.ApplySystemMetrics(_currentSystemUsage);
            }

            if (_miniWindow?.IsVisible == true)
            {
                _miniWindow.ApplySnapshot(snapshot);
                _miniWindow.ApplySystemMetrics(_currentSystemUsage);
            }

            _trayIconService?.ApplySnapshot(snapshot);
            HandleCodexResetCreditReminder(snapshot.CodexResetCredits);
            _mobileServer?.UpdateSnapshot(_currentSnapshot, _currentSystemUsage);
            if (_personalMobile is { } mobile) _ = Task.Run(() => mobile.PublishAsync(snapshot));
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write("Refresh failed: " + ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void HandleCodexResetCreditReminder(CodexResetCreditSummary summary)
    {
        var earliest = summary.Earliest;
        var now = DateTimeOffset.Now;
        if (earliest is null || earliest.ExpiresAt <= now ||
            earliest.ExpiresAt - now > TimeSpan.FromHours(24))
        {
            return;
        }

        if (_codexResetReminderState is not null &&
            string.Equals(_codexResetReminderState.CreditId, earliest.Id, StringComparison.Ordinal) &&
            now - _codexResetReminderState.RemindedAt < TimeSpan.FromHours(1))
        {
            return;
        }

        _codexResetReminderState = new CodexResetReminderState(earliest.Id, now);
        CodexResetReminderStateService.Save(_codexResetReminderState);
        if (_window?.IsVisible == true)
        {
            _window.FlashResetCreditReminder();
        }

        if (_miniWindow?.IsVisible == true)
        {
            _miniWindow.FlashResetCreditReminder();
        }

        _trayIconService?.ShowResetCreditReminder(summary.AvailableCount, earliest.ExpiresAt);
        DiagnosticsService.Write($"Codex reset credit reminder shown; expires {earliest.ExpiresAt:O}.");
    }

    private async Task UpdateSystemMetricsAsync()
    {
        if (_systemMetricsService is null || _window is null || _systemMetricsBusy || _isShuttingDown)
        {
            return;
        }

        _systemMetricsBusy = true;
        try
        {
            _currentSystemUsage = await Task.Run(_systemMetricsService.Read);
        }
        catch (Exception ex)
        {
            DiagnosticsService.Write("System metrics failed: " + ex.GetType().Name);
            return;
        }
        finally { _systemMetricsBusy = false; }
        if (_isShuttingDown) return;
        if (_window.IsVisible)
        {
            _window.ApplySystemMetrics(_currentSystemUsage);
        }

        if (_miniWindow?.IsVisible == true)
        {
            _miniWindow.ApplySystemMetrics(_currentSystemUsage);
        }

        _mobileServer?.UpdateSnapshot(_currentSnapshot, _currentSystemUsage);
    }

    private void StartMobileServer()
    {
        var settings = AppSettingsService.Load();
        if (!MobileServerFeatureEnabled)
        {
            if (settings.MobileServerEnabled)
            {
                settings.MobileServerEnabled = false;
                AppSettingsService.Save(settings);
            }

            return;
        }

        if (string.IsNullOrEmpty(settings.MobileAccessKey))
        {
            settings.MobileAccessKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            AppSettingsService.Save(settings);
        }

        if (!settings.MobileServerEnabled)
        {
            return;
        }

        _mobileServer = new MobileServerService(settings.MobilePort, settings.MobileAccessKey);
        _mobileServer.Start();
    }

    public static (string Url, string Lan) GetMobileAccessInfo()
    {
        var settings = AppSettingsService.Load();
        var lan = GetLanIPv4() ?? "127.0.0.1";
        var url = $"http://{lan}:{settings.MobilePort}/?key={settings.MobileAccessKey}";
        return (url, lan);
    }

    private static string? GetLanIPv4()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var ip = ua.Address.ToString();
                        if (!ip.StartsWith("169.254", StringComparison.Ordinal))
                        {
                            return ip;
                        }
                    }
                }
            }
        }
        catch
        {
            // fall back to loopback
        }

        return null;
    }

    private void ConfigureCodexSessionWatcher(bool enabled)
    {
        if (!enabled)
        {
            _codexRefreshDebounceTimer?.Stop();
            _codexRefreshDebounceTimer = null;
            _codexWatcher?.Dispose();
            _codexWatcher = null;
            return;
        }

        if (_codexWatcher is not null)
        {
            return;
        }

        StartCodexSessionWatcher();
    }

    private void StartCodexSessionWatcher()
    {
        var sessionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");

        if (!Directory.Exists(sessionsPath))
        {
            DiagnosticsService.Write("Codex sessions folder not found: " + sessionsPath);
            return;
        }

        _codexRefreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _codexRefreshDebounceTimer.Tick += async (_, _) =>
        {
            _codexRefreshDebounceTimer.Stop();
            await RefreshAsync();
        };

        _codexWatcher = new FileSystemWatcher(sessionsPath, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler scheduleRefresh = (_, _) => QueueCodexRefresh();
        RenamedEventHandler scheduleRenamedRefresh = (_, _) => QueueCodexRefresh();

        _codexWatcher.Changed += scheduleRefresh;
        _codexWatcher.Created += scheduleRefresh;
        _codexWatcher.Renamed += scheduleRenamedRefresh;
    }

    private void QueueCodexRefresh()
    {
        if (_isShuttingDown || Interlocked.Exchange(ref _codexRefreshScheduled, 1) != 0) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _codexRefreshScheduled, 0);
            ScheduleCodexRefresh();
        }, DispatcherPriority.Background);
    }

    private void ScheduleCodexRefresh()
    {
        if (_codexRefreshDebounceTimer is null)
        {
            return;
        }

        _codexRefreshDebounceTimer.Stop();
        _codexRefreshDebounceTimer.Start();
    }

    private void ConfigureClaudeSessionWatcher(bool enabled)
    {
        if (!enabled)
        {
            _claudeActivitySyncTimer?.Stop();
            _claudeActivitySyncTimer = null;
            _claudeWatcher?.Dispose();
            _claudeWatcher = null;
            return;
        }

        if (_claudeWatcher is not null)
        {
            return;
        }

        var projectsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");
        if (!Directory.Exists(projectsPath))
        {
            DiagnosticsService.Write("Claude projects folder not found: " + projectsPath);
            return;
        }

        _claudeActivitySyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _claudeActivitySyncTimer.Tick += async (_, _) =>
        {
            _claudeActivitySyncTimer.Stop();
            var remaining = TimeSpan.FromMinutes(1) - (DateTimeOffset.Now - _lastClaudeSyncAttempt);
            if (remaining > TimeSpan.Zero)
            {
                _claudeActivitySyncTimer.Interval = remaining;
                _claudeActivitySyncTimer.Start();
                return;
            }

            await SyncClaudeAndRefreshAsync();
        };

        _claudeWatcher = new FileSystemWatcher(projectsPath, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler scheduleSync = (_, _) => Dispatcher.BeginInvoke(ScheduleClaudeUsageSync);
        RenamedEventHandler scheduleRenamedSync = (_, _) => Dispatcher.BeginInvoke(ScheduleClaudeUsageSync);
        _claudeWatcher.Changed += scheduleSync;
        _claudeWatcher.Created += scheduleSync;
        _claudeWatcher.Renamed += scheduleRenamedSync;
    }

    private void ScheduleClaudeUsageSync()
    {
        if (_claudeActivitySyncTimer is null)
        {
            return;
        }

        _claudeActivitySyncTimer.Stop();
        _claudeActivitySyncTimer.Interval = TimeSpan.FromSeconds(20);
        _claudeActivitySyncTimer.Start();
    }
}
