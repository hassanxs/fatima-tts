using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace FatimaTTS.Views;

public partial class BatchDetailPage : Page
{
    private readonly string                _batchName;
    private readonly JobPersistenceService _persistence;
    private readonly AudioPlayerService    _player;
    private readonly SrtExportService      _srtExport;

    private List<TtsJob>  _jobs      = [];
    private TtsJob?       _playingJob;

    public BatchDetailPage(string batchName)
    {
        InitializeComponent();
        _batchName   = batchName;
        _persistence = App.Services.GetRequiredService<JobPersistenceService>();
        _player      = App.Services.GetRequiredService<AudioPlayerService>();
        _srtExport   = App.Services.GetRequiredService<SrtExportService>();

        _player.PlaybackStopped += () => Dispatcher.Invoke(() =>
        {
            SetPlayerIcon(false);
            if (_player.Position == TimeSpan.Zero)
            {
                PlayerBar.Visibility = Visibility.Collapsed;
                _playingJob = null;
            }
        });

        _player.PositionChanged += (pos, dur) => Dispatcher.Invoke(() =>
        {
            PlayerPos.Text = FormatTime(pos);
            PlayerDur.Text = FormatTime(dur);
            if (dur.TotalSeconds > 0)
                PlayerFill.Width = PlayerFill.ActualWidth > 0
                    ? 0
                    : ((FrameworkElement)PlayerFill.Parent).ActualWidth
                      * (pos.TotalSeconds / dur.TotalSeconds);
            // Recalculate properly using the parent track width
            if (PlayerFill.Parent is Grid trackGrid)
                PlayerFill.Width = trackGrid.ActualWidth * (pos.TotalSeconds / Math.Max(1, dur.TotalSeconds));
        });

        Loaded += (_, _) => LoadBatch();
    }

    // ── Data ─────────────────────────────────────────────────────────────

    private void LoadBatch()
    {
        BatchTitleText.Text = _batchName;

        var allJobs = _persistence.LoadAllJobs();

        // Match jobs to this batch using same ResolveBatchName logic
        _jobs = allJobs
            .Where(j => ResolveBatchName(j) == _batchName)
            .OrderBy(j => j.CreatedAt)
            .ToList();

        // Stats
        StatTotal.Text     = _jobs.Count.ToString();
        StatCompleted.Text = _jobs.Count(j => j.Status == JobStatus.Completed).ToString();
        StatFailed.Text    = _jobs.Count(j => j.Status == JobStatus.Failed).ToString();
        StatChars.Text     = FormatChars(_jobs.Sum(j => (long)j.CharactersBilled));

        var date    = _jobs.FirstOrDefault()?.CreatedAt;
        var voice   = FriendlyVoice(_jobs.FirstOrDefault()?.VoiceId ?? "");
        BatchSubtitleText.Text = date.HasValue
            ? $"{_jobs.Count} jobs · {voice} · {date.Value:MMM d, yyyy}"
            : $"{_jobs.Count} jobs";

        // Show FFmpeg merge button if 2+ completed files exist
        var completedFiles = _jobs
            .Where(j => j.Status == JobStatus.Completed && j.OutputFilePath is not null
                        && File.Exists(j.OutputFilePath))
            .Select(j => j.OutputFilePath!)
            .ToList();
        MergeButton.Visibility = completedFiles.Count >= 2 && FfmpegMergeService.IsAvailable()
            ? Visibility.Visible : Visibility.Collapsed;

        // Bind job list
        JobList.ItemsSource = _jobs
            .Select((j, i) => new BatchJobViewModel(j, i + 1))
            .ToList();
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToPage("batch");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _jobs.FirstOrDefault(j => j.OutputFilePath is not null)?.OutputFilePath;
        if (path is null) return;

        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        var files = _jobs
            .Where(j => j.Status == JobStatus.Completed && j.OutputFilePath is not null
                        && File.Exists(j.OutputFilePath))
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.OutputFilePath!)
            .ToList();

        if (files.Count < 2) return;

        var ext = AppSettings.AudioExtensions.GetValueOrDefault(
            _jobs.FirstOrDefault()?.AudioEncoding ?? "MP3", "mp3");

        var dlg = new SaveFileDialog
        {
            Title      = "Save Merged Audio",
            FileName   = $"{_batchName}_merged.{ext}",
            Filter     = $"Audio files|*.{ext}|All files|*.*",
            DefaultExt = ext
        };
        if (dlg.ShowDialog() != true) return;

        MergeButton.IsEnabled = false;
        try
        {
            var merger = new FfmpegMergeService();
            await merger.MergeAsync(files, dlg.FileName);

            MessageBox.Show($"Merged file saved:\n{dlg.FileName}",
                "Merge Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"FFmpeg merge failed:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { MergeButton.IsEnabled = true; }
    }

    private void DeleteBatch_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            $"Delete all {_jobs.Count} jobs in \"{_batchName}\"?\nThis removes all audio files for this batch.",
            "Delete Batch", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        foreach (var job in _jobs)
            _persistence.DeleteJob(job.Id);

        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToPage("batch");
    }

    // ── Job row actions ───────────────────────────────────────────────────

    private void PlayJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetJobFromButton(sender);
        if (job?.OutputFilePath is null || !File.Exists(job.OutputFilePath)) return;

        try
        {
            _playingJob = job;
            _player.Load(job.OutputFilePath);
            _player.Play();

            PlayerTitle.Text     = job.DisplayTitle;
            PlayerBar.Visibility = Visibility.Visible;
            SetPlayerIcon(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not play audio: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveJob_Click(object sender, RoutedEventArgs e)
    {
        var job = GetJobFromButton(sender);
        if (job?.OutputFilePath is null || !File.Exists(job.OutputFilePath)) return;

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
        finally { LoadBatch(); }
    }

    // ── Player controls ───────────────────────────────────────────────────

    private void PlayerPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying) { _player.Pause(); SetPlayerIcon(false); }
        else if (_player.IsPaused) { _player.Play(); SetPlayerIcon(true); }
    }

    private void PlayerStop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _playingJob = null;
        PlayerBar.Visibility = Visibility.Collapsed;
    }

    private void PlayerBack_Click(object sender, RoutedEventArgs e)
        => _player.SeekTo(_player.Position - TimeSpan.FromSeconds(10));

    private void PlayerFwd_Click(object sender, RoutedEventArgs e)
        => _player.SeekTo(_player.Position + TimeSpan.FromSeconds(10));

    private void PlayerTrack_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b)
            _player.Seek(Math.Clamp(e.GetPosition(b).X / b.ActualWidth, 0, 1));
    }

    private void PlayerSave_Click(object sender, RoutedEventArgs e)
    {
        if (_playingJob is not null) SaveJob_Click(
            new Button { Tag = _playingJob.Id }, new RoutedEventArgs());
    }

    private void SetPlayerIcon(bool playing)
    {
        PauseIcon.Visibility = playing ? Visibility.Visible  : Visibility.Collapsed;
        PlayIcon.Visibility  = playing ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private TtsJob? GetJobFromButton(object sender)
    {
        var id = (sender as Button)?.Tag?.ToString();
        return _jobs.FirstOrDefault(j => j.Id == id);
    }

    private static string? ResolveBatchName(TtsJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.BatchName)) return job.BatchName;
        var title = job.Title ?? job.DisplayTitle;
        if (title.StartsWith("[Batch:") && title.Contains("]"))
        {
            var end  = title.IndexOf(']');
            var name = title[7..end].Trim();
            return string.IsNullOrWhiteSpace(name) ? "Unnamed Batch" : name;
        }
        if (title.StartsWith("[Batch]"))
            return $"Batch {job.CreatedAt:yyyy-MM-dd}";
        return null;
    }

    private static string FriendlyVoice(string raw)
    {
        if (raw.Contains("__"))
        {
            var name = raw[(raw.LastIndexOf("__") + 2)..];
            return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : raw;
        }
        return raw;
    }

    private static string FormatChars(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000     => $"{n / 1_000.0:F1}k",
        _            => n.ToString()
    };

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
}

// ── Batch job view model ──────────────────────────────────────────────────

public class BatchJobViewModel
{
    private readonly TtsJob _job;
    public int    Index      { get; }
    public string JobId      => _job.Id;
    public string Title      => _job.DisplayTitle;
    public string VoiceName  { get; }
    public string CharCount  => $"{_job.CharacterCount:N0} chars";
    public string FileSize   => _job.FormattedFileSize;
    public string CreatedAt  => _job.CreatedAt.ToString("MMM d h:mm tt");
    public string StatusLabel => _job.StatusLabel;
    public string? ErrorMessage => _job.ErrorMessage;

    public Visibility CanPlay   => _job.Status == JobStatus.Completed && _job.OutputFilePath is not null
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CanResume => _job.CanResume ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasError  => string.IsNullOrWhiteSpace(_job.ErrorMessage)
        ? Visibility.Collapsed : Visibility.Visible;

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

    public BatchJobViewModel(TtsJob job, int index)
    {
        _job      = job;
        Index     = index;
        var raw   = job.VoiceId ?? "";
        VoiceName = raw.Contains("__")
            ? raw[(raw.LastIndexOf("__") + 2)..] is { Length: > 0 } n
                ? char.ToUpper(n[0]) + n[1..] : raw
            : raw;
    }
}
