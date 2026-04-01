using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FatimaTTS.Views;

public partial class SettingsPage : Page
{
    private readonly SettingsService   _settingsService;
    private readonly CredentialService _credentials;
    private readonly ThemeService      _theme;
    private readonly InworldTtsService _tts;

    public SettingsPage()
    {
        InitializeComponent();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _credentials     = App.Services.GetRequiredService<CredentialService>();
        _theme           = App.Services.GetRequiredService<ThemeService>();
        _tts             = App.Services.GetRequiredService<InworldTtsService>();
        Loaded += OnLoaded;
    }

    private bool _loaded = false;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();

        // FFmpeg status
        RefreshFfmpegStatus();

        // Log path
        var logger = App.Services.GetRequiredService<AppLogger>();
        LogPathText.Text = logger.LogDirectory;

        // API key status
        RefreshApiKeyStatus();

        // Theme — set flag after assignment to prevent Checked handler
        // from firing and calling ApplyAndSave during init
        ThemeDark.IsChecked  = settings.Theme == "Dark";
        ThemeLight.IsChecked = settings.Theme == "Light";

        // Output folder
        OutputFolderBox.Text = settings.OutputFolder;
        FolderPlaceholder.Visibility = string.IsNullOrEmpty(settings.OutputFolder)
            ? Visibility.Visible : Visibility.Collapsed;

        // Format combo
        DefaultFormatCombo.Items.Clear();
        foreach (var kvp in AppSettings.AudioEncodings)
        {
            DefaultFormatCombo.Items.Add(new ComboBoxItem
            {
                Content = kvp.Value, Tag = kvp.Key
            });
        }
        SelectComboByTag(DefaultFormatCombo, settings.DefaultAudioEncoding);

        // Model combo
        DefaultModelCombo.Items.Clear();
        foreach (var kvp in AppSettings.ModelDisplayNames)
        {
            DefaultModelCombo.Items.Add(new ComboBoxItem
            {
                Content = kvp.Value, Tag = kvp.Key
            });
        }
        SelectComboByTag(DefaultModelCombo, settings.DefaultModelId);

        // Sliders
        DefaultTempSlider.Value = settings.DefaultTemperature;
        DefaultRateSlider.Value = settings.DefaultSpeakingRate;
        DefaultTempValue.Text   = settings.DefaultTemperature.ToString("F1");
        DefaultRateValue.Text   = settings.DefaultSpeakingRate.ToString("F1") + "×";

        _loaded = true;
    }

    private void RefreshApiKeyStatus()
    {
        bool hasKey     = _credentials.HasApiKey();
        ApiDot.Fill     = hasKey
            ? new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75))
            : new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));
        ApiKeyStatus.Text = hasKey ? "Configured" : "Not configured";
    }

    // ── API Key ───────────────────────────────────────────────────────────

    private void SaveApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ValidationResultText.Text       = "Please enter an API key.";
            ValidationResultText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        _credentials.SaveApiKey(key);
        ApiKeyBox.Clear();
        RefreshApiKeyStatus();
        ValidationResultText.Text       = "Key saved to Credential Manager.";
        ValidationResultText.Foreground = (Brush)FindResource("SuccessBrush");

        // Update sidebar indicator
        if (Window.GetWindow(this) is MainWindow mw)
            mw.RefreshApiStatus();
    }

    private async void ValidateKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var key = _credentials.LoadApiKey();
        if (key is null)
        {
            ValidationResultText.Text       = "No key stored — save one first.";
            ValidationResultText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        ValidateKeyButton.IsEnabled     = false;
        ValidationResultText.Text       = "Validating…";
        ValidationResultText.Foreground = (Brush)FindResource("TextMutedBrush");

        bool valid = await _tts.ValidateApiKeyAsync(key);

        ValidateKeyButton.IsEnabled     = true;
        ValidationResultText.Text       = valid ? "✓ API key is valid" : "✕ Invalid key";
        ValidationResultText.Foreground = valid
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("DangerBrush");
    }

    private void DeleteKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Remove the stored API key from Windows Credential Manager?",
            "Remove API Key", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _credentials.DeleteApiKey();
        RefreshApiKeyStatus();
        ValidationResultText.Text       = "Key removed.";
        ValidationResultText.Foreground = (Brush)FindResource("TextMutedBrush");

        if (Window.GetWindow(this) is MainWindow mw)
            mw.RefreshApiStatus();
    }

    // ── Theme ─────────────────────────────────────────────────────────────

    private void ThemeDark_Checked(object sender, RoutedEventArgs e)
    { if (_loaded) _theme.ApplyAndSave("Dark"); }

    private void ThemeLight_Checked(object sender, RoutedEventArgs e)
    { if (_loaded) _theme.ApplyAndSave("Light"); }

    // ── Sliders ───────────────────────────────────────────────────────────

    private void DefaultTempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DefaultTempValue is not null)
            DefaultTempValue.Text = e.NewValue.ToString("F1");
    }

    private void DefaultRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DefaultRateValue is not null)
            DefaultRateValue.Text = e.NewValue.ToString("F1") + "×";
    }

    // ── Output folder ─────────────────────────────────────────────────────

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        // Microsoft.Win32.OpenFolderDialog — WPF native, no WinForms needed
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select output folder for audio files",
        };

        if (!string.IsNullOrWhiteSpace(OutputFolderBox.Text))
            dlg.InitialDirectory = OutputFolderBox.Text;

        if (dlg.ShowDialog() == true)
        {
            OutputFolderBox.Text = dlg.FolderName;
            FolderPlaceholder.Visibility = Visibility.Collapsed;

            // Auto-save immediately so navigating away doesn't lose the pick
            var settings = _settingsService.Load();
            settings.OutputFolder = dlg.FolderName;
            _settingsService.Save(settings);
        }
    }

    // ── Save all settings ─────────────────────────────────────────────────

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();

        settings.Theme                = ThemeDark.IsChecked == true ? "Dark" : "Light";
        settings.DefaultAudioEncoding = GetComboTag(DefaultFormatCombo) ?? "MP3";
        settings.DefaultModelId       = GetComboTag(DefaultModelCombo)  ?? "inworld-tts-1.5-max";
        settings.DefaultTemperature   = DefaultTempSlider.Value;
        settings.DefaultSpeakingRate  = DefaultRateSlider.Value;
        settings.OutputFolder         = OutputFolderBox.Text.Trim();

        _settingsService.Save(settings);

        SavedConfirmText.Visibility = Visibility.Visible;

        // Hide after 3 seconds
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        timer.Tick += (_, _) =>
        {
            SavedConfirmText.Visibility = Visibility.Collapsed;
            timer.Stop();
        };
        timer.Start();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: string t } && t == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? GetComboTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    // ── FFmpeg ────────────────────────────────────────────────────────────

    private readonly FfmpegManager _ffmpegManager =
        App.Services.GetRequiredService<FfmpegManager>();

    private string? _installedFfmpegVersion;

    private async void RefreshFfmpegStatus()
    {
        FfmpegStatusText.Text           = "Checking…";
        FfmpegDownloadButton.Visibility = Visibility.Collapsed;
        FfmpegUpdateButton.Visibility   = Visibility.Collapsed;

        _installedFfmpegVersion = await FfmpegManager.GetVersionAsync();

        if (_installedFfmpegVersion is null)
        {
            FfmpegStatusText.Text           = "Not installed — click Install to download automatically";
            FfmpegDownloadButton.Visibility = Visibility.Visible;
        }
        else
        {
            FfmpegStatusText.Text           = $"Installed — {_installedFfmpegVersion}";
            FfmpegUpdateButton.Content      = new TextBlock { Text = "↑  Check for Update", FontSize = 11 };
            FfmpegUpdateButton.Visibility   = Visibility.Visible;
        }
    }

    private async void FfmpegDownload_Click(object sender, RoutedEventArgs e)
        => await RunFfmpegDownload();

    private async void FfmpegUpdate_Click(object sender, RoutedEventArgs e)
    {
        // First check if an update is actually available
        FfmpegUpdateButton.IsEnabled = false;
        FfmpegStatusText.Text        = "Checking for update…";

        var updateAvailable = await _ffmpegManager.IsUpdateAvailableAsync();

        if (!updateAvailable)
        {
            FfmpegStatusText.Text        = $"Already up to date — {_installedFfmpegVersion}";
            FfmpegUpdateButton.IsEnabled = true;
            FfmpegUpdateButton.Content   = new TextBlock { Text = "↑  Check for Update", FontSize = 11 };
            return;
        }

        // Update is available — show confirmation
        var result = MessageBox.Show(
            "A newer version of FFmpeg is available. Download and install it now?\n\n" +
            "This will replace the current installation (~80 MB download).",
            "FFmpeg Update Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        FfmpegUpdateButton.IsEnabled = true;

        if (result == MessageBoxResult.Yes)
            await RunFfmpegDownload();
        else
            FfmpegStatusText.Text = $"Installed — {_installedFfmpegVersion}";
    }

    private async Task RunFfmpegDownload()
    {
        FfmpegDownloadButton.IsEnabled  = false;
        FfmpegUpdateButton.IsEnabled    = false;
        FfmpegProgressPanel.Visibility  = Visibility.Visible;

        var progress = new Progress<(int Percent, string Status)>(p =>
        {
            FfmpegProgressBar.Value = p.Percent;
            FfmpegProgressText.Text = p.Status;
        });

        var ok = await _ffmpegManager.DownloadLatestAsync(progress);

        FfmpegProgressPanel.Visibility  = Visibility.Collapsed;
        FfmpegDownloadButton.IsEnabled  = true;
        FfmpegUpdateButton.IsEnabled    = true;

        if (ok)
            RefreshFfmpegStatus();
        else
            FfmpegStatusText.Text = "Download failed — check your internet connection";
    }

    // ── Logs ──────────────────────────────────────────────────────────────

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var logger = App.Services.GetRequiredService<AppLogger>();
        if (System.IO.Directory.Exists(logger.LogDirectory))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{logger.LogDirectory}\"")
                { UseShellExecute = true });
    }
}
