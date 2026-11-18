using System.Windows;
using System.Windows.Input;
using UsageTools2026.Models;

namespace UsageTools2026;

public partial class UpdateWindow : Window
{
    private readonly bool _installEnabled;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isDownloading;

    public UpdateWindow(UpdateInfo update, bool installEnabled)
    {
        InitializeComponent();
        _installEnabled = installEnabled;
        VersionText.Text = update.Version.ToString();
        PublishedText.Text = $"{update.PublishedAtUtc.LocalDateTime:yyyy/MM/dd HH:mm}";
        SizeText.Text = FormatSize(update.PackageSize);
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? "這個版本包含穩定性與使用體驗改善。"
            : update.ReleaseNotes;
        InstallButton.IsEnabled = installEnabled;
        if (!installEnabled)
        {
            InstallButton.ToolTip = "等待 OVH 更新伺服器與驗章金鑰完成設定";
            SecurityText.Text = "更新介面預覽：伺服器尚未啟用";
            SecurityText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF0, 0xB3, 0x5D));
        }
    }

    public event EventHandler? InstallRequested;

    public CancellationToken BeginDownload()
    {
        if (_isDownloading)
        {
            return _downloadCancellation?.Token ?? CancellationToken.None;
        }

        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        _isDownloading = true;
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadPercentText.Text = "0%";
        DownloadStatusText.Text = "正在安全下載更新檔...";
        DownloadStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xAE, 0xBC, 0xC7));
        InstallButton.IsEnabled = false;
        LaterButton.Content = "取消";
        return _downloadCancellation.Token;
    }

    public void SetDownloadProgress(UpdateDownloadProgress progress)
    {
        DownloadProgressBar.Value = progress.Percent;
        DownloadPercentText.Text = $"{progress.Percent:0}%";
        DownloadStatusText.Text = $"已下載 {FormatSize(progress.BytesReceived)} / {FormatSize(progress.TotalBytes)}";
    }

    public void SetLaunchingInstaller()
    {
        _isDownloading = false;
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 100;
        DownloadPercentText.Text = "100%";
        DownloadStatusText.Text = "檔案驗證完成，正在安全套用新版...";
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
    }

    public void SetDownloadFailed(string message)
    {
        _isDownloading = false;
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadStatusText.Text = message;
        DownloadStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0x78, 0x78));
        DownloadPercentText.Text = string.Empty;
        InstallButton.IsEnabled = _installEnabled;
        LaterButton.Content = "稍後";
        LaterButton.IsEnabled = true;
    }

    public void SetDownloadCancelled()
    {
        _isDownloading = false;
        DownloadStatusText.Text = "已取消下載，不會變更目前版本。";
        DownloadPercentText.Text = string.Empty;
        InstallButton.IsEnabled = _installEnabled;
        LaterButton.Content = "稍後";
        LaterButton.IsEnabled = true;
    }

    private void Install_Click(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            DownloadStatusText.Text = "正在取消下載...";
            _downloadCancellation?.Cancel();
            LaterButton.IsEnabled = false;
            return;
        }

        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _downloadCancellation?.Cancel();
        _downloadCancellation?.Dispose();
        base.OnClosed(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // The mouse can be released while Windows starts the drag.
        }
    }

    private static string FormatSize(long bytes)
    {
        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024
            ? $"{megabytes / 1024d:0.##} GB"
            : $"{megabytes:0.#} MB";
    }
}
