using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Shapes;
using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace FatimaTTS.Views;

public partial class GenerateSpeechPage : Page
{
    private readonly InworldTtsService      _tts;
    private readonly ChunkingEngine         _chunker;
    private readonly CredentialService      _credentials;
    private readonly SettingsService        _settingsService;
    private readonly TtsJobProcessor        _processor;
    private readonly JobPersistenceService  _persistence;
    private readonly AudioPlayerService     _player;
    private readonly ToastService           _toast;
    private readonly SrtExportService       _srtExport;

    private List<InworldVoice> _voices = [];
    private string   _selectedModelId  = "inworld-tts-1.5-max";
    private TtsJob?  _currentJob;
    private CancellationTokenSource? _cts;
    private float[]  _waveformData     = [];

    // Chunk view models for the ItemsControl
    private readonly ObservableCollection<ChunkViewModel> _chunkViewModels = [];

    public GenerateSpeechPage()
    {
        InitializeComponent();

        _tts             = App.Services.GetRequiredService<InworldTtsService>();
        _chunker         = App.Services.GetRequiredService<ChunkingEngine>();
        _credentials     = App.Services.GetRequiredService<CredentialService>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _persistence     = App.Services.GetRequiredService<JobPersistenceService>();
        _processor       = App.Services.GetRequiredService<TtsJobProcessor>();
        _player          = App.Services.GetRequiredService<AudioPlayerService>();
        _toast           = App.Services.GetRequiredService<ToastService>();
        _srtExport       = App.Services.GetRequiredService<SrtExportService>();

        ChunkList.ItemsSource = _chunkViewModels;

        _processor.ProgressChanged += OnProgressChanged;
        _processor.ChunkCompleted  += OnChunkCompleted;
        _processor.ChunkFailed     += OnChunkFailed;

        // Wire player events (cross-thread → Dispatcher)
        _player.PositionChanged += (cur, tot) => Dispatcher.Invoke(() => UpdateProgress(cur, tot));
        _player.PlaybackStopped += ()          => Dispatcher.Invoke(() => OnPlaybackStopped());
        _player.PlaybackStarted += ()          => Dispatcher.Invoke(() => PlayPauseIcon.Text = "⏸");

        Loaded     += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    // ── Page load ─────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        PopulateFormats();
        await LoadVoicesAsync();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();

        // Pre-select model radio
        SetModelRadio(settings.DefaultModelId);
        _selectedModelId = settings.DefaultModelId;

        // Pre-select format
        TemperatureSlider.Value  = settings.DefaultTemperature;
        SpeakingRateSlider.Value = settings.DefaultSpeakingRate;
    }

    private void PopulateFormats()
    {
        foreach (var kvp in AppSettings.AudioEncodings)
        {
            FormatComboBox.Items.Add(new ComboBoxItem
            {
                Content = kvp.Value,
                Tag     = kvp.Key
            });
        }

        var settings = _settingsService.Load();
        var idx = AppSettings.AudioEncodings.Keys.ToList().IndexOf(settings.DefaultAudioEncoding);
        FormatComboBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private async Task LoadVoicesAsync()
    {
        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        try
        {
            RefreshVoicesButton.IsEnabled = false;
            _voices = await _tts.ListWorkspaceVoicesAsync(apiKey);
            PopulateVoiceCombo();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load voices: {ex.Message}", isError: true);
        }
        finally
        {
            RefreshVoicesButton.IsEnabled = true;
        }
    }

    private void PopulateVoiceCombo()
    {
        _populatingVoices = true;
        try
        {
            VoiceComboBox.Items.Clear();
            var settings = _settingsService.Load();

            // System voices first, then cloned
            foreach (var v in _voices.Where(v => v.IsSystem))
                VoiceComboBox.Items.Add(new ComboBoxItem { Content = v.DisplayName, Tag = v });

            var cloned = _voices.Where(v => v.IsCloned).ToList();
            if (cloned.Count > 0)
            {
                VoiceComboBox.Items.Add(new ComboBoxItem
                {
                    Content    = "── Cloned Voices ──",
                    IsEnabled  = false,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x78))
                });
                foreach (var v in cloned)
                    VoiceComboBox.Items.Add(new ComboBoxItem { Content = v.DisplayName, Tag = v });

                ClonedBadge.Visibility   = Visibility.Visible;
                ClonedBadgeText.Text     = cloned.Count == 1 ? "1 clone" : $"{cloned.Count} clones";
            }
            else
            {
                ClonedBadge.Visibility = Visibility.Collapsed;
            }

            // Select saved default voice
            var defaultVoice = settings.DefaultVoiceId;
            for (int i = 0; i < VoiceComboBox.Items.Count; i++)
            {
                if (VoiceComboBox.Items[i] is ComboBoxItem { Tag: InworldVoice v }
                    && v.VoiceId == defaultVoice)
                {
                    VoiceComboBox.SelectedIndex = i;
                    break;
                }
            }

            if (VoiceComboBox.SelectedIndex < 0 && VoiceComboBox.Items.Count > 0)
                VoiceComboBox.SelectedIndex = 0;
        }
        finally
        {
            _populatingVoices = false;
        }
    }

    // ── Input handling ────────────────────────────────────────────────────

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text  = InputTextBox.Text;
        var count = text.Length;

        CharCountText.Text = count.ToString("N0");
        CharCountText.Foreground = count > 90_000
            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27))
            : (Brush)FindResource("TextSecondaryBrush");

        // Update chunk estimate
        if (count > 0)
        {
            var chunks = _chunker.ChunkText(text);
            ChunkCountText.Text = chunks.Count == 1
                ? ""
                : $"Will be split into {chunks.Count} chunks";
        }
        else
        {
            ChunkCountText.Text = "";
        }

        // Auto-suggest job title from first sentence if title is still empty
        if (string.IsNullOrWhiteSpace(JobTitleBox.Text) && count > 0)
        {
            var suggested = SuggestTitle(text);
            if (!string.IsNullOrEmpty(suggested))
            {
                _suppressTitleAutoSuggest = true;
                JobTitleBox.Text          = suggested;
                JobTitleBox.Foreground    = (Brush)FindResource("TextMutedBrush");
                _suppressTitleAutoSuggest = false;
            }
        }
        else if (count == 0)
        {
            // Clear suggestion when text is erased
            if (_titleWasAutoSuggested)
            {
                JobTitleBox.Text       = "";
                JobTitleBox.Foreground = (Brush)FindResource("TextPrimaryBrush");
                _titleWasAutoSuggested = false;
            }
        }
        _titleWasAutoSuggested = !string.IsNullOrWhiteSpace(JobTitleBox.Text)
                                  && !_userEditedTitle;
    }

    private bool _suppressTitleAutoSuggest = false;
    private bool _titleWasAutoSuggested    = false;
    private bool _userEditedTitle          = false;
    private bool _populatingVoices         = false;  // suppresses DefaultVoiceId save during PopulateVoiceCombo

    private static string SuggestTitle(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0) return "";

        // Take up to first sentence end or 60 chars
        var endIdx = -1;
        for (int i = 0; i < trimmed.Length && i < 120; i++)
        {
            if (trimmed[i] is '.' or '!' or '?' or '\n')
            { endIdx = i; break; }
        }

        var title = endIdx > 0
            ? trimmed[..endIdx].Trim()
            : trimmed[..Math.Min(60, trimmed.Length)].Trim();

        return title.Length > 60 ? title[..60] + "…" : title;
    }

    private void JobTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTitleAutoSuggest) return;
        // If user typed manually, stop auto-suggesting
        _userEditedTitle = true;
        JobTitleBox.Foreground = (Brush)FindResource("TextPrimaryBrush");
    }

    private void TemperatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TempValueText is not null)
            TempValueText.Text = e.NewValue.ToString("F1");
    }

    private void SpeakingRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RateValueText is not null)
            RateValueText.Text = e.NewValue.ToString("F1") + "×";
    }

    private void ModelRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string modelId)
            _selectedModelId = modelId;
    }

    private void VoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceComboBox.SelectedItem is ComboBoxItem { Tag: InworldVoice voice })
        {
            VoiceDetailPanel.Visibility  = Visibility.Visible;
            VoiceTypeBadgeText.Text      = voice.SourceLabel;
            VoiceDescText.Text           = voice.Description ?? string.Empty;
            VoiceLangText.Text           = $"Language: {voice.LanguageDisplay}";

            // Only persist when user explicitly changes voice, not during initial population
            if (!_populatingVoices)
            {
                var settings = _settingsService.Load();
                if (settings.DefaultVoiceId != voice.VoiceId)
                {
                    settings.DefaultVoiceId = voice.VoiceId;
                    _settingsService.Save(settings);
                }
            }
        }
        else
        {
            VoiceDetailPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SetModelRadio(string modelId)
    {
        foreach (var rb in new[] { ModelRadio1, ModelRadio2, ModelRadio3, ModelRadio4 })
        {
            if (rb.Tag?.ToString() == modelId)
            {
                rb.IsChecked = true;
                return;
            }
        }
        ModelRadio1.IsChecked = true;
    }

    private string GetSelectedFormatKey()
    {
        if (FormatComboBox.SelectedItem is ComboBoxItem { Tag: string key })
            return key;
        return "MP3";
    }

    private InworldVoice? GetSelectedVoice()
    {
        if (VoiceComboBox.SelectedItem is ComboBoxItem { Tag: InworldVoice v }) return v;
        return null;
    }

    // ── Generate ──────────────────────────────────────────────────────────

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(
                "Please add your Inworld API key in Settings before generating speech.",
                "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var text = InputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Please enter some text to synthesize.",
                "No Text", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var voice = GetSelectedVoice();
        if (voice is null)
        {
            MessageBox.Show("Please select a voice.",
                "No Voice Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Build job
        _currentJob = new TtsJob
        {
            Title          = string.IsNullOrWhiteSpace(JobTitleBox.Text) || _titleWasAutoSuggested
                                ? SuggestTitle(text)
                                : JobTitleBox.Text.Trim(),
            InputText      = text,
            CharacterCount = text.Length,
            VoiceId        = voice.VoiceId,
            ModelId        = _selectedModelId,
            AudioEncoding  = GetSelectedFormatKey(),
            Temperature    = TemperatureSlider.Value,
            SpeakingRate   = SpeakingRateSlider.Value,
        };

        // Reset title state for next job
        _userEditedTitle = false;
        _titleWasAutoSuggested = false;

        // Prepare UI
        _chunkViewModels.Clear();
        ProgressPanel.Visibility  = Visibility.Visible;
        PlaybackPanel.Visibility  = Visibility.Collapsed;
        GenerateButton.IsEnabled  = false;
        JobProgressBar.Value      = 0;
        JobStatusText.Text        = "Preparing…";
        JobStatusIcon.Text        = "↻";

        _cts = new CancellationTokenSource();

        try
        {
            await _processor.ProcessJobAsync(_currentJob, apiKey, _cts.Token);

            // Success
            JobStatusIcon.Text = "✓";
            JobStatusText.Text = "Done! Audio ready.";
            JobStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75));
            PlaybackPanel.Visibility = Visibility.Visible;

            // Show SRT button only if job has word timestamps (TTS 1.5 models)
            var hasTimestamps = _currentJob.Chunks.Any(c => c.Words.Count > 0);
            ExportSrtButton.Visibility = hasTimestamps ? Visibility.Visible : Visibility.Collapsed;

            // Toast notification
            _toast.ShowJobCompleted(
                _currentJob.DisplayTitle,
                $"{_currentJob.CharactersBilled:N0} chars · {_currentJob.FormattedFileSize}");

            // Load into in-app player + extract waveform
            if (_currentJob.OutputFilePath is not null)
                LoadAudioForPlayback(_currentJob.OutputFilePath);

            // Auto-save to output folder if configured
            var settings = _settingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.OutputFolder))
            {
                var persistence = App.Services.GetRequiredService<JobPersistenceService>();
                var destPath    = persistence.ExportToOutputFolder(_currentJob, settings.OutputFolder);

                // Auto-save SRT alongside audio if timestamps available
                if (destPath is not null && hasTimestamps)
                {
                    try
                    {
                        var srtPath = System.IO.Path.ChangeExtension(destPath, ".srt");
                        _srtExport.ExportSrt(_currentJob, srtPath);
                    }
                    catch { /* SRT save failure is non-fatal */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            JobStatusIcon.Text       = "⏹";
            JobStatusText.Text       = "Cancelled.";
            JobStatusIcon.Foreground = (Brush)FindResource("WarningBrush");
        }
        catch (Exception ex)
        {
            JobStatusIcon.Text       = "✕";
            JobStatusText.Text       = $"Failed: {ex.Message}";
            JobStatusIcon.Foreground = (Brush)FindResource("DangerBrush");
            _toast.ShowJobFailed(_currentJob?.DisplayTitle ?? "Job", ex.Message);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => _cts?.Cancel();

    // ── Progress callbacks (cross-thread safe) ────────────────────────────

    private void OnProgressChanged(int done, int total, int currentIdx, string message)
    {
        Dispatcher.Invoke(() =>
        {
            JobStatusText.Text   = message;
            JobProgressBar.Value = total > 0 ? (double)done / total * 100 : 0;
        });
    }

    private void OnChunkCompleted(TtsChunk chunk)
    {
        Dispatcher.Invoke(() =>
        {
            var vm = _chunkViewModels.FirstOrDefault(c => c.ChunkIndex == chunk.ChunkIndex);
            if (vm is not null)
            {
                vm.StatusLabel = "✓ Done";
            }
            else
            {
                _chunkViewModels.Add(new ChunkViewModel(chunk));
            }
        });
    }

    private void OnChunkFailed(TtsChunk chunk, string message)
    {
        Dispatcher.Invoke(() =>
        {
            var vm = _chunkViewModels.FirstOrDefault(c => c.ChunkIndex == chunk.ChunkIndex);
            if (vm is not null) vm.StatusLabel = "↻ Retry";
        });
    }

    // ── Playback ──────────────────────────────────────────────────────────

    private void LoadAudioForPlayback(string filePath)
    {
        try
        {
            _player.Load(filePath);

            // Extract waveform on background thread
            Task.Run(() =>
            {
                var data = AudioPlayerService.ExtractWaveform(filePath, 180);
                Dispatcher.Invoke(() =>
                {
                    _waveformData = data;
                    DrawWaveform(0);
                });
            });

            DurationText.Text  = FormatTime(_player.Duration);
            PositionText.Text  = "0:00";
            PlayPauseIcon.Text = "▶";
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load audio: {ex.Message}", isError: true);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_player.HasFile) return;

        if (_player.IsPlaying)
        {
            _player.Pause();
            PlayPauseIcon.Text = "▶";
        }
        else
        {
            _player.Play();
            PlayPauseIcon.Text = "⏸";
        }
    }

    private void RewindButton_Click(object sender, RoutedEventArgs e)
        => _player.SeekTo(_player.Position - TimeSpan.FromSeconds(10));

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
        => _player.SeekTo(_player.Position + TimeSpan.FromSeconds(10));

    private void OnPlaybackStopped()
    {
        PlayPauseIcon.Text = "▶";
        DrawWaveform(0);
        PositionText.Text = "0:00";
        if (ProgressFill is not null) ProgressFill.Width = 0;
    }

    private void ProgressTrack_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement track) return;
        double fraction = e.GetPosition(track).X / track.ActualWidth;
        _player.Seek(Math.Clamp(fraction, 0, 1));
    }

    private void WaveformCanvas_Click(object sender, MouseButtonEventArgs e)
    {
        if (WaveformCanvas.ActualWidth <= 0) return;
        double fraction = e.GetPosition(WaveformCanvas).X / WaveformCanvas.ActualWidth;
        _player.Seek(Math.Clamp(fraction, 0, 1));
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_waveformData.Length > 0)
            DrawWaveform(_player.HasFile ? _player.Position.TotalSeconds / _player.Duration.TotalSeconds : 0);
    }

    private void UpdateProgress(TimeSpan current, TimeSpan total)
    {
        if (total.TotalSeconds <= 0) return;

        double fraction = current.TotalSeconds / total.TotalSeconds;
        PositionText.Text = FormatTime(current);
        DurationText.Text = FormatTime(total);

        // Update progress bar fill
        var track = ProgressFill?.Parent as FrameworkElement;
        if (track is not null && ProgressFill is not null)
            ProgressFill.Width = Math.Max(0, fraction * track.ActualWidth);

        // Redraw waveform playhead
        DrawWaveform(fraction);
    }

    // ── Waveform drawing ──────────────────────────────────────────────────

    private void DrawWaveform(double playedFraction)
    {
        if (WaveformCanvas is null || _waveformData.Length == 0) return;

        WaveformCanvas.Children.Clear();

        double cw = WaveformCanvas.ActualWidth;
        double ch = WaveformCanvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return;

        int    n        = _waveformData.Length;
        double barW     = Math.Max(1, cw / n);
        double midY     = ch / 2;
        double maxH     = midY * 0.92;

        var playedBrush = (Brush)FindResource("AccentBrush");
        var mutedColor  = System.Windows.Media.Color.FromArgb(80, 150, 150, 180);
        var mutedBrush  = new SolidColorBrush(mutedColor);

        for (int i = 0; i < n; i++)
        {
            double amp  = _waveformData[i];
            double barH = Math.Max(2, amp * maxH);
            double x    = i * barW;
            bool   done = (double)i / n < playedFraction;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width        = Math.Max(1, barW - 1),
                Height       = barH * 2,
                Fill         = done ? playedBrush : mutedBrush,
                RadiusX      = 1,
                RadiusY      = 1,
            };
            System.Windows.Controls.Canvas.SetLeft(rect, x);
            System.Windows.Controls.Canvas.SetTop(rect, midY - barH);
            WaveformCanvas.Children.Add(rect);
        }

        // Playhead line
        if (playedFraction > 0 && playedFraction < 1)
        {
            double px = playedFraction * cw;
            var line = new System.Windows.Shapes.Line
            {
                X1              = px, X2 = px,
                Y1              = 0,  Y2 = ch,
                Stroke          = playedBrush,
                StrokeThickness = 1.5,
                Opacity         = 0.8
            };
            WaveformCanvas.Children.Add(line);
        }
    }

    // ── Export ────────────────────────────────────────────────────────────

    private void ExportSrtButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentJob is null) return;

        var hasTimestamps = _currentJob.Chunks.Any(c => c.Words.Count > 0);
        if (!hasTimestamps)
        {
            MessageBox.Show(
                "No word timestamp data available for this job.\n\n" +
                "Timestamps are returned by TTS 1.5 models (Max and Mini). " +
                "Re-generate with Inworld TTS 1.5 Max or Mini to enable SRT export.",
                "No Timestamps", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = System.IO.Path.GetFileNameWithoutExtension(
            _currentJob.OutputFileName ?? "fatima_tts") + ".srt";

        var dlg = new SaveFileDialog
        {
            Title      = "Export SRT Subtitle File",
            FileName   = defaultName,
            Filter     = "SRT subtitle files|*.srt|All files|*.*",
            DefaultExt = "srt"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                _srtExport.ExportSrt(_currentJob, dlg.FileName);
                MessageBox.Show($"SRT file exported successfully.\n{dlg.FileName}",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export SRT: {ex.Message}",
                    "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentJob?.OutputFilePath is null || !File.Exists(_currentJob.OutputFilePath)) return;

        var ext = AppSettings.AudioExtensions.GetValueOrDefault(_currentJob.AudioEncoding, "mp3");
        var dlg = new SaveFileDialog
        {
            Title      = "Save Audio File",
            FileName   = _currentJob.OutputFileName ?? $"fatima_tts.{ext}",
            Filter     = $"{_currentJob.AudioEncoding} files|*.{ext}|All files|*.*",
            DefaultExt = ext
        };
        if (dlg.ShowDialog() == true)
            File.Copy(_currentJob.OutputFilePath, dlg.FileName, overwrite: true);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentJob?.OutputFilePath is null) return;
        var dir = System.IO.Path.GetDirectoryName(_currentJob.OutputFilePath);
        if (dir is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    private async void RefreshVoicesButton_Click(object sender, RoutedEventArgs e)
        => await LoadVoicesAsync();

    private async void PreviewVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        var voice = GetSelectedVoice();
        if (voice is null) return;

        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        var btn = PreviewVoiceButton;
        btn.IsEnabled = false;

        // Update button text
        if (btn.Content is TextBlock tb) tb.Text = "⏳  Previewing…";

        try
        {
            const string PreviewText = "Hello! This is a preview of how this voice sounds.";

            var (audioBytes, _, _) = await _tts.SynthesizeAsync(
                apiKey, PreviewText,
                voice.VoiceId,
                "inworld-tts-1.5-max",
                "MP3",
                temperature: 1.1,
                speakingRate: 1.0);

            // Write to temp file and play
            var tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"fatima_preview_{voice.VoiceId}.mp3");

            await System.IO.File.WriteAllBytesAsync(tempPath, audioBytes);

            _player.Load(tempPath);
            _player.Play();

            if (btn.Content is TextBlock tb2) tb2.Text = "⏹  Stop";
            btn.IsEnabled = true;
            btn.Tag = "playing";

            // Auto-reset button when playback ends
            _player.PlaybackStopped += ResetPreviewButton;
        }
        catch (Exception ex)
        {
            if (btn.Content is TextBlock tb3) tb3.Text = "▶  Preview Voice";
            btn.IsEnabled = true;
            SetStatus($"Preview failed: {ex.Message}", isError: true);
        }
    }

    private void ResetPreviewButton()
    {
        _player.PlaybackStopped -= ResetPreviewButton;
        Dispatcher.Invoke(() =>
        {
            PreviewVoiceButton.IsEnabled = true;
            if (PreviewVoiceButton.Content is TextBlock tb)
                tb.Text = "▶  Preview Voice";
            PreviewVoiceButton.Tag = null;
        });
    }

    private void MyJobsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToPage("myjobs");
    }

    private void SetStatus(string message, bool isError = false)
    {
        JobStatusText.Text       = message;
        JobStatusIcon.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextSecondaryBrush");
    }
}

// ── Chunk view model ──────────────────────────────────────────────────────

public class ChunkViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _statusLabel = "Pending";

    public int    ChunkIndex      { get; }
    public string TextPreview     { get; }
    public int    CharacterCount  { get; }

    public string StatusLabel
    {
        get => _statusLabel;
        set { _statusLabel = value; PropertyChanged?.Invoke(this, new(nameof(StatusLabel))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ChunkViewModel(TtsChunk chunk)
    {
        ChunkIndex     = chunk.ChunkIndex + 1;
        CharacterCount = chunk.CharacterCount;
        TextPreview    = chunk.Text.Length > 60 ? chunk.Text[..60] + "…" : chunk.Text;
        StatusLabel    = chunk.Status switch
        {
            ChunkStatus.Completed  => "✓ Done",
            ChunkStatus.Processing => "↻ Fetching",
            ChunkStatus.Failed     => "✕ Failed",
            _                      => "– Pending"
        };
    }
}
