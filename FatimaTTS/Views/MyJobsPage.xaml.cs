using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace FatimaTTS.Views;

public partial class MyJobsPage : Page
{
    private readonly JobPersistenceService _persistence;
    private readonly AudioPlayerService   _player;
    private readonly SrtExportService     _srtExport;

    private List<TtsJob> _allJobs   = [];
    private string       _search    = "";
    private string       _filter    = "all";
    private TtsJob?      _playingJob   = null;

    public MyJobsPage()
    {
        InitializeComponent();
        _persistence = App.Services.GetRequiredService<JobPersistenceService>();
        _player      = App.Services.GetRequiredService<AudioPlayerService>();
        _srtExport   = App.Services.GetRequiredService<SrtExportService>();

        _player.PlaybackStopped += () => Dispatcher.Invoke(() =>
        {
            SetPlayerIcon(isPlaying: false);
            if (_player.Position == TimeSpan.Zero)
            {
                PlayerBar.Visibility = Visibility.Collapsed;
                _playingJob = null;
            }
        });

        Loaded += (_, _) => LoadJobs();
    }

    // ── Data ──────────────────────────────────────────────────────────────

    private void LoadJobs()
    {
        _allJobs = _persistence.LoadAllJobs();
        UpdateStats();
        ApplyFilter();
    }

    private void UpdateStats()
    {
        StatTotal.Text       = _allJobs.Count.ToString();
        StatCompleted.Text   = _allJobs.Count(j => j.Status == JobStatus.Completed).ToString();
        StatFailed.Text      = _allJobs.Count(j => j.Status == JobStatus.Failed).ToString();
        StatInterrupted.Text = _allJobs.Count(j => j.Status == JobStatus.Interrupted).ToString();
        JobCountText.Text    = $"{_allJobs.Count} job{(_allJobs.Count == 1 ? "" : "s")}";
    }

    private void ApplyFilter()
    {
        var query = _search.Trim().ToLowerInvariant();

        var filtered = _allJobs
            .Where(j => _filter switch
            {
                "completed"   => j.Status == JobStatus.Completed,
                "failed"      => j.Status == JobStatus.Failed,
                "interrupted" => j.Status == JobStatus.Interrupted,
                _             => true
            })
            .Where(j => string.IsNullOrEmpty(query)
                || j.DisplayTitle.ToLowerInvariant().Contains(query)
                || j.VoiceId.ToLowerInvariant().Contains(query)
                || j.ModelId.ToLowerInvariant().Contains(query))
            .OrderByDescending(j => j.CreatedAt)
            .ToList();

        JobList.ItemsSource = filtered.Select(j => new JobViewModel(j)).ToList();

        bool empty = filtered.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (empty)
        {
            EmptyText.Text = _allJobs.Count == 0
                ? "No jobs yet"
                : "No jobs match your search";
            EmptySubText.Text = _allJobs.Count == 0
                ? "Generate your first speech to see jobs here."
                : "Try adjusting your search or filter.";
        }
    }

    private void RefreshJobList() => ApplyFilter();

    // ── Search & filter events ────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(_search)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        _filter = sender switch
        {
            RadioButton rb when rb == FilterCompleted   => "completed",
            RadioButton rb when rb == FilterFailed      => "failed",
            RadioButton rb when rb == FilterInterrupted => "interrupted",
            _                                           => "all"
        };
        if (_allJobs.Count > 0) ApplyFilter();
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void NewJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToPage("generate");
    }

    private void PlayJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetJobFromButton(sender);
        if (job?.OutputFilePath is null || !File.Exists(job.OutputFilePath)) return;

        try
        {
            _playingJob = job;
            _player.Load(job.OutputFilePath);
            _player.Play();
            ShowPlayerBar(job);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not play audio: {ex.Message}",
                "Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowPlayerBar(TtsJob job)
    {
        PlayerBar.Visibility = Visibility.Visible;
        PlayerTitle.Text     = job.DisplayTitle;
        SetPlayerIcon(isPlaying: true);

        PlayerSrtButton.Tag        = job.Id;
        PlayerSaveButton.Tag       = job.Id;
        PlayerSrtButton.Visibility = job.Chunks.Any(c => c.Words.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;

        // Draw waveform asynchronously so UI doesn't block
        if (job.OutputFilePath is not null)
        {
            var path = job.OutputFilePath;
            Task.Run(() =>
            {
                var peaks = AudioPlayerService.ExtractWaveform(path, 200);
                Dispatcher.InvokeAsync(() => DrawWaveform(peaks),
                    System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        _player.PositionChanged -= OnPlayerPositionChanged;
        _player.PositionChanged += OnPlayerPositionChanged;
    }

    private void DrawWaveform(float[] peaks)
    {
        PlayerWaveformCanvas.Children.Clear();
        var w = PlayerWaveformCanvas.ActualWidth;
        var h = PlayerWaveformCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || peaks.Length == 0) return;

        var accentBrush = (Brush)FindResource("AccentBrush");
        double barW = w / peaks.Length;

        for (int i = 0; i < peaks.Length; i++)
        {
            double barH = Math.Max(2, peaks[i] * h * 0.85);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width   = Math.Max(1, barW - 0.5),
                Height  = barH,
                Fill    = accentBrush,
                Opacity = 0.55,
                RadiusX = 1,
                RadiusY = 1
            };
            System.Windows.Controls.Canvas.SetLeft(rect, i * barW);
            System.Windows.Controls.Canvas.SetTop(rect, (h - barH) / 2);
            PlayerWaveformCanvas.Children.Add(rect);
        }
    }

    private void SetPlayerIcon(bool isPlaying)
    {
        PauseIcon.Visibility = isPlaying ? Visibility.Visible  : Visibility.Collapsed;
        PlayIcon.Visibility  = isPlaying ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPlayerPositionChanged(TimeSpan position, TimeSpan duration)
    {
        Dispatcher.Invoke(() =>
        {
            PlayerPosition.Text = FormatTime(position);
            PlayerDuration.Text = FormatTime(duration);

            // Update waveform playhead overlay width
            if (duration.TotalSeconds > 0)
            {
                var fraction = position.TotalSeconds / duration.TotalSeconds;
                var trackW   = PlayerWaveformCanvas.ActualWidth;
                PlayerProgressFill.Width = trackW * fraction;
            }
        });
    }

    private void PlayerPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying)
        {
            _player.Pause();
            SetPlayerIcon(isPlaying: false);
        }
        else if (_player.IsPaused)
        {
            _player.Play();
            SetPlayerIcon(isPlaying: true);
        }
    }

    private void StopPlayerBar_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _playingJob = null;
        PlayerBar.Visibility = Visibility.Collapsed;
        _player.PositionChanged -= OnPlayerPositionChanged;
    }

    private void PlayerProgressTrack_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border track)
        {
            var x        = e.GetPosition(track).X;
            var fraction = Math.Clamp(x / track.ActualWidth, 0, 1);
            _player.Seek(fraction);
        }
    }

    private void PlayerSave_Click(object sender, RoutedEventArgs e)
    {
        if (_playingJob is null) return;
        SaveJobFile(_playingJob);
    }

    private void StopJob_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _playingJob = null;
        PlayerBar.Visibility = Visibility.Collapsed;
        _player.PositionChanged -= OnPlayerPositionChanged;
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    private void SeekBack_Click(object sender, RoutedEventArgs e)
    {
        var newPos = _player.Position - TimeSpan.FromSeconds(10);
        _player.SeekTo(newPos < TimeSpan.Zero ? TimeSpan.Zero : newPos);
    }

    private void SeekForward_Click(object sender, RoutedEventArgs e)
    {
        var newPos = _player.Position + TimeSpan.FromSeconds(10);
        _player.SeekTo(newPos);
    }

    private void ExportSrtJob_Click(object sender, RoutedEventArgs e)
    {
        var jobId = (sender as Button)?.Tag?.ToString();
        var job   = _allJobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null) return;

        var hasTimestamps = job.Chunks.Any(c => c.Words.Count > 0);
        if (!hasTimestamps)
        {
            MessageBox.Show("No word timestamp data available for this job.\n\nSRT export requires generation with Inworld TTS 1.5 Max or Mini.",
                "No Timestamps", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = System.IO.Path.GetFileNameWithoutExtension(job.OutputFileName ?? "audio") + ".srt";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export SRT Subtitle File",
            FileName = defaultName,
            Filter = "SRT subtitle files|*.srt|All files|*.*",
            DefaultExt = "srt"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                _srtExport.ExportSrt(job, dlg.FileName);
                MessageBox.Show($"SRT exported successfully.", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SaveJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetJobFromButton(sender);
        if (job is not null) SaveJobFile(job);
    }

    private void SaveJobFile(TtsJob job)
    {
        if (job.OutputFilePath is null || !File.Exists(job.OutputFilePath)) return;
        var ext = AppSettings.AudioExtensions.GetValueOrDefault(job.AudioEncoding, "mp3");
        var dlg = new SaveFileDialog
        {
            Title      = "Save Audio",
            FileName   = job.OutputFileName ?? $"fatima_tts.{ext}",
            Filter     = $"{job.AudioEncoding} files|*.{ext}|All files|*.*",
            DefaultExt = ext
        };
        if (dlg.ShowDialog() == true)
            File.Copy(job.OutputFilePath, dlg.FileName, overwrite: true);
    }

    private async void ResumeJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetJobFromButton(sender);
        if (job is null) return;

        var credentials = App.Services.GetRequiredService<CredentialService>();
        var apiKey      = credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Please add your API key in Settings first.",
                "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        job.Status = JobStatus.Pending;
        var processor = App.Services.GetRequiredService<TtsJobProcessor>();

        try
        {
            await processor.ProcessJobAsync(job, apiKey);
            MessageBox.Show("Job completed successfully.",
                "Done", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Resume failed: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoadJobs();
        }
    }

    private void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetJobFromButton(sender);
        if (job is null) return;

        var result = MessageBox.Show(
            $"Delete \"{job.DisplayTitle}\"?\nThis will remove all audio files for this job.",
            "Delete Job", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;
        _persistence.DeleteJob(job.Id);
        LoadJobs();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private TtsJob? GetJobFromButton(object sender)
    {
        if (sender is not Button { Tag: string jobId }) return null;
        return _allJobs.FirstOrDefault(j => j.Id == jobId);
    }
}

// ── Job view model ────────────────────────────────────────────────────────

public class JobViewModel
{
    private readonly TtsJob _job;
    private readonly bool   _isPlaying;

    public string JobId          => _job.Id;
    public string DisplayTitle   => _job.DisplayTitle;
    public string StatusLabel    => _job.StatusLabel;

    // Extract friendly voice name from raw ID like "default-xyz__santiago" → "Santiago"
    public string VoiceId
    {
        get
        {
            var id = _job.VoiceId ?? "";
            var lastPart = id.Contains("__") ? id[(id.LastIndexOf("__") + 2)..] : id;
            return lastPart.Length > 0
                ? char.ToUpper(lastPart[0]) + lastPart[1..]
                : id;
        }
    }

    public string ModelId        => AppSettings.ModelDisplayNames.GetValueOrDefault(_job.ModelId, _job.ModelId);
    public int    CharacterCount => _job.CharacterCount;
    public string FormattedFileSize => _job.FormattedFileSize;
    public string CreatedAtLabel => _job.CreatedAt.ToString("MMM d, yyyy h:mm tt");
    public string? ErrorMessage  => _job.ErrorMessage;
    public int    ResumeFrom     => _job.ResumeFromChunkIndex + 1;
    public bool   HasSrt         => _job.Chunks.Any(c => c.Words.Count > 0);

    public Visibility HasError    => string.IsNullOrWhiteSpace(_job.ErrorMessage)
        ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CanPlay     => _job.Status == JobStatus.Completed && _job.OutputFilePath is not null
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CanResume   => _job.CanResume
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsPlaying   => _isPlaying ? Visibility.Visible  : Visibility.Collapsed;
    public Visibility IsNotPlaying => _isPlaying ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowSrt     => _isPlaying && HasSrt ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowSeek    => _isPlaying ? Visibility.Visible : Visibility.Collapsed;

    public Brush StatusBg => _job.Status switch
    {
        JobStatus.Completed   => new SolidColorBrush(Color.FromArgb(30, 0x1D, 0x9E, 0x75)),
        JobStatus.Failed      => new SolidColorBrush(Color.FromArgb(30, 0xE2, 0x4B, 0x4A)),
        JobStatus.Interrupted => new SolidColorBrush(Color.FromArgb(30, 0xEF, 0x9F, 0x27)),
        _                     => new SolidColorBrush(Color.FromArgb(30, 0x37, 0x8A, 0xDD))
    };

    public Brush StatusFg => _job.Status switch
    {
        JobStatus.Completed   => new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)),
        JobStatus.Failed      => new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)),
        JobStatus.Interrupted => new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27)),
        _                     => new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD))
    };

    public JobViewModel(TtsJob job, bool isPlaying = false)
    {
        _job       = job;
        _isPlaying = isPlaying;
    }
}
