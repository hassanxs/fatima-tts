using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace FatimaTTS.Views;

public partial class BatchGeneratePage : Page
{
    private readonly InworldTtsService     _tts;
    private readonly CredentialService     _credentials;
    private readonly SettingsService       _settingsService;
    private readonly TtsJobProcessor       _processor;
    private readonly JobPersistenceService _persistence;
    private readonly ToastService          _toast;
    private readonly SrtExportService      _srtExport;

    private List<InworldVoice> _voices = [];
    private string? _selectedFilePath;
    private CancellationTokenSource? _cts;

    private readonly ObservableCollection<QueueItemViewModel> _queue = [];

    // Track current tab

    public BatchGeneratePage()
    {
        InitializeComponent();
        _tts             = App.Services.GetRequiredService<InworldTtsService>();
        _credentials     = App.Services.GetRequiredService<CredentialService>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _processor       = App.Services.GetRequiredService<TtsJobProcessor>();
        _persistence     = App.Services.GetRequiredService<JobPersistenceService>();
        _toast           = App.Services.GetRequiredService<ToastService>();
        _srtExport       = App.Services.GetRequiredService<SrtExportService>();

        QueueList.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => { UpdateQueueUI(); UpdateUsageStats(); };

        Loaded += OnLoaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    // ── Init ─────────────────────────────────────────────────────────────

    private bool _initialized = false;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        PopulateSettings();
        await LoadVoicesAsync();
        LoadRecentBatches();
    }

    // Re-check voices and recent batches each time page becomes visible
    private async void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _initialized)
        {
            await LoadVoicesAsync();
            LoadRecentBatches();
        }
    }

    private void PopulateSettings()
    {
        var settings = _settingsService.Load();

        // Model
        ModelComboBox.Items.Clear();
        foreach (var kvp in AppSettings.ModelDisplayNames)
            ModelComboBox.Items.Add(new ComboBoxItem { Content = kvp.Value, Tag = kvp.Key });
        SelectComboByTag(ModelComboBox, settings.DefaultModelId);

        // Format
        FormatComboBox.Items.Clear();
        foreach (var kvp in AppSettings.AudioEncodings)
            FormatComboBox.Items.Add(new ComboBoxItem { Content = kvp.Value, Tag = kvp.Key });
        SelectComboByTag(FormatComboBox, settings.DefaultAudioEncoding);

        // Speaking rate
        SpeakingRateComboBox.Items.Clear();
        foreach (var r in new[] { "0.8", "0.9", "1.0", "1.1", "1.2", "1.3", "1.5" })
            SpeakingRateComboBox.Items.Add(new ComboBoxItem { Content = r + "×", Tag = r });
        SelectComboByTag(SpeakingRateComboBox, settings.DefaultSpeakingRate.ToString("F1"));
        if (SpeakingRateComboBox.SelectedIndex < 0) SpeakingRateComboBox.SelectedIndex = 2; // 1.0
    }

    private async Task LoadVoicesAsync()
    {
        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        try
        {
            _voices = await _tts.ListWorkspaceVoicesAsync(apiKey);
            VoiceComboBox.Items.Clear();
            foreach (var v in _voices.Where(v => v.IsSystem))
                VoiceComboBox.Items.Add(new ComboBoxItem { Content = v.DisplayName, Tag = v.VoiceId });

            var cloned = _voices.Where(v => v.IsCloned).ToList();
            if (cloned.Count > 0)
            {
                VoiceComboBox.Items.Add(new ComboBoxItem
                {
                    Content = "── Cloned Voices ──", IsEnabled = false,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x78))
                });
                foreach (var v in cloned)
                    VoiceComboBox.Items.Add(new ComboBoxItem { Content = v.DisplayName, Tag = v.VoiceId });
            }

            var settings = _settingsService.Load();
            SelectComboByTag(VoiceComboBox, settings.DefaultVoiceId);
            if (VoiceComboBox.SelectedIndex < 0 && VoiceComboBox.Items.Count > 0)
                VoiceComboBox.SelectedIndex = 0;
        }
        catch { /* fail silently — voice load is non-critical */ }
    }

    private void LoadRecentBatches()
    {
        var allJobs = _persistence.LoadAllJobs();

        // Resolve batch name for every job:
        // New jobs  → BatchName field is set directly
        // Old jobs  → title starts with "[Batch] " or "[Batch: X]" — extract name from there
        var batchJobs = allJobs
            .Select(j => (job: j, batch: ResolveBatchName(j)))
            .Where(x => x.batch is not null)
            .ToList();

        if (batchJobs.Count == 0)
        {
            NoBatchesYet.Visibility    = Visibility.Visible;
            RecentBatchList.Visibility = Visibility.Collapsed;
            return;
        }

        var batches = batchJobs
            .GroupBy(x => x.batch!)
            .Select(g =>
            {
                var jobs     = g.Select(x => x.job).ToList();
                var latest   = jobs.Max(j => j.CreatedAt);
                var completed = jobs.Count(j => j.Status == JobStatus.Completed);
                var total    = jobs.Count;
                var chars    = jobs.Sum(j => (long)j.CharacterCount);
                return new RecentBatchViewModel
                {
                    Name      = g.Key,
                    JobCount  = total,
                    Summary   = $"{completed}/{total} completed · {FormatChars(chars)} · {latest:MMM d, yyyy}",
                    CreatedAt = latest
                };
            })
            .OrderByDescending(b => b.CreatedAt)
            .Take(8)
            .ToList();

        NoBatchesYet.Visibility    = Visibility.Collapsed;
        RecentBatchList.Visibility = Visibility.Visible;
        RecentBatchList.ItemsSource = batches;
    }

    /// <summary>
    /// Returns the batch name for a job, or null if it's not a batch job.
    /// Handles both the new BatchName field and old "[Batch] Title" / "[Batch: Name]" title formats.
    /// </summary>
    private static string? ResolveBatchName(TtsJob job)
    {
        // New format: BatchName field set directly
        if (!string.IsNullOrWhiteSpace(job.BatchName))
            return job.BatchName;

        var title = job.Title ?? job.DisplayTitle;

        // Old format 1: "[Batch: EpisodeName] JobTitle" or "[Batch:EpisodeName]..."
        if (title.StartsWith("[Batch:") && title.Contains("]"))
        {
            var end  = title.IndexOf(']');
            var name = title[7..end].Trim();
            return string.IsNullOrWhiteSpace(name) ? "Unnamed Batch" : name;
        }

        // Old format 2: "[Batch] JobTitle" — group by creation date (same day = same batch)
        if (title.StartsWith("[Batch]"))
            return $"Batch {job.CreatedAt:yyyy-MM-dd}";

        return null;
    }

    // ── Tabs ─────────────────────────────────────────────────────────────

    private void TabFile_Click(object sender, RoutedEventArgs e)
    {
        ManualPanel.Visibility = Visibility.Collapsed;
        FilePanel.Visibility   = Visibility.Visible;
        TabFile.Style          = (Style)FindResource("NavButtonActiveStyle");
        TabManual.Style        = (Style)FindResource("NavButtonStyle");
    }

    private void TabManual_Click(object sender, RoutedEventArgs e)
    {
        ManualPanel.Visibility = Visibility.Visible;
        FilePanel.Visibility   = Visibility.Collapsed;
        TabFile.Style          = (Style)FindResource("NavButtonStyle");
        TabManual.Style        = (Style)FindResource("NavButtonActiveStyle");
    }

    // ── Manual entry ─────────────────────────────────────────────────────

    private void ManualTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ManualCharCount.Text = $"{ManualTextBox.Text.Length:N0} characters";
    }

    private void AddToQueueButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ManualTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show("Please enter some text.", "No Text",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var title = ManualTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = text.Length > 50 ? text[..50] + "…" : text;

        _queue.Add(new QueueItemViewModel(_queue.Count + 1, title, text));
        ManualTitleBox.Text = "";
        ManualTextBox.Text  = "";
        JobEntryNumber.Text = (_queue.Count + 1).ToString();
    }

    // ── File loading ─────────────────────────────────────────────────────

    private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select TXT or CSV file",
            Filter = "Supported files|*.txt;*.csv|Text files|*.txt|CSV files|*.csv|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        _selectedFilePath       = dlg.FileName;
        SelectedFileLabel.Text  = System.IO.Path.GetFileName(dlg.FileName);
        LoadFileButton.IsEnabled = true;
    }

    private void LoadFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFilePath is null || !File.Exists(_selectedFilePath)) return;

        try
        {
            var ext   = System.IO.Path.GetExtension(_selectedFilePath).ToLowerInvariant();
            var items = ext == ".csv"
                ? ParseCsv(_selectedFilePath)
                : ParseTxt(_selectedFilePath);

            _queue.Clear();
            foreach (var (title, text) in items)
                _queue.Add(new QueueItemViewModel(_queue.Count + 1, title, text));

            MessageBox.Show($"Loaded {_queue.Count} job{(_queue.Count == 1 ? "" : "s")} into queue.",
                "File Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to parse file:\n{ex.Message}",
                "Parse Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Parsers ───────────────────────────────────────────────────────────

    private static List<(string Title, string Text)> ParseTxt(string path)
    {
        var content = File.ReadAllText(path);
        var sections = content.Split(["\n---\n", "\r\n---\r\n"], StringSplitOptions.RemoveEmptyEntries);
        var result  = new List<(string, string)>();

        foreach (var section in sections)
        {
            var lines = section.Trim().Split('\n');
            string title = "";
            int    start = 0;

            if (lines.Length > 0 && lines[0].TrimStart().StartsWith('#'))
            {
                title = lines[0].TrimStart('#', ' ').Trim();
                start = 1;
            }

            var text = string.Join("\n", lines[start..]).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (string.IsNullOrWhiteSpace(title))
                title = text.Length > 50 ? text[..50] + "…" : text;

            result.Add((title, text));
        }
        return result;
    }

    private static List<(string Title, string Text)> ParseCsv(string path)
    {
        var result = new List<(string, string)>();
        var lines  = File.ReadAllLines(path);

        // Skip header row if it matches Title,Text
        int startLine = 0;
        if (lines.Length > 0 &&
            lines[0].Trim().Equals("Title,Text", StringComparison.OrdinalIgnoreCase))
            startLine = 1;

        for (int i = startLine; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var (title, text) = SplitCsvRow(line);
            if (!string.IsNullOrWhiteSpace(text))
                result.Add((title, text));
        }
        return result;
    }

    /// <summary>Handles quoted CSV fields with embedded commas.</summary>
    private static (string Title, string Text) SplitCsvRow(string line)
    {
        if (line.StartsWith('"'))
        {
            // Quoted first field
            int close = line.IndexOf('"', 1);
            while (close > 0 && close + 1 < line.Length && line[close + 1] == '"')
                close = line.IndexOf('"', close + 2);

            if (close < 0) return (line, "");
            var title = line[1..close].Replace("\"\"", "\"");
            var rest  = line[(close + 1)..].TrimStart(',');
            var text  = rest.StartsWith('"') ? rest[1..^1].Replace("\"\"", "\"") : rest;
            return (title, text);
        }
        else
        {
            var commaIdx = line.IndexOf(',');
            if (commaIdx < 0) return (line, "");
            var title = line[..commaIdx].Trim();
            var rest  = line[(commaIdx + 1)..].Trim();
            var text  = rest.StartsWith('"') ? rest[1..^1].Replace("\"\"", "\"") : rest;
            return (title, text);
        }
    }

    // ── Queue management ─────────────────────────────────────────────────

    private void UpdateQueueUI()
    {
        bool hasItems = _queue.Count > 0;
        QueueCard.Visibility        = hasItems ? Visibility.Visible : Visibility.Collapsed;
        StartBatchButton.IsEnabled  = hasItems;

        // Re-number items
        for (int i = 0; i < _queue.Count; i++)
            _queue[i].Index = i + 1;

        JobEntryNumber.Text = (_queue.Count + 1).ToString();
    }

    private void EditQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int idx }) return;
        var item = _queue.FirstOrDefault(q => q.Index == idx);
        if (item is null) return;

        // Close any other open editors first
        foreach (var other in _queue.Where(q => q.IsEditing))
            other.IsEditing = false;

        item.EditTitle = item.Title;
        item.EditText  = item.Text;
        item.IsEditing = true;
    }

    private void CancelEditQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int idx }) return;
        var item = _queue.FirstOrDefault(q => q.Index == idx);
        if (item is not null) item.IsEditing = false;
    }

    private void SaveEditQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int idx }) return;
        var item = _queue.FirstOrDefault(q => q.Index == idx);
        if (item is null) return;

        item.ApplyEdit();
        item.IsEditing = false;
    }

    private void RemoveQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int idx })
        {
            var item = _queue.FirstOrDefault(q => q.Index == idx);
            if (item is not null) _queue.Remove(item);
        }
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        _queue.Clear();
    }

    // ── Batch processing ─────────────────────────────────────────────────

    private void CancelBatchButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelBatchButton.IsEnabled = false;
        BatchProgressText.Text = "Cancelling…";
    }

    private async void StartBatchButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Please add your Inworld API key in Settings.",
                "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var voiceId  = GetComboTag(VoiceComboBox);
        var modelId  = GetComboTag(ModelComboBox) ?? "inworld-tts-1.5-max";
        var encoding = GetComboTag(FormatComboBox) ?? "MP3";
        var rateStr  = GetComboTag(SpeakingRateComboBox) ?? "1.0";
        double.TryParse(rateStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double speakingRate);
        if (speakingRate <= 0) speakingRate = 1.0;

        if (string.IsNullOrWhiteSpace(voiceId))
        {
            MessageBox.Show("Please select a voice.", "No Voice",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // ── Read batch name ───────────────────────────────────────────────
        var batchName = BatchNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(batchName))
            batchName = $"Batch_{DateTime.Now:yyyyMMdd_HHmm}";

        // ── Output folder: OutputFolder\BatchName\ ────────────────────────
        var settings      = _settingsService.Load();
        var baseFolder    = string.IsNullOrWhiteSpace(settings.OutputFolder)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FatimaTTS")
            : settings.OutputFolder;

        // Create a named subfolder for this batch (sanitized for filesystem)
        var safeBatchName  = SanitizeFileName(batchName);
        if (string.IsNullOrWhiteSpace(safeBatchName))
            safeBatchName  = $"Batch_{DateTime.Now:yyyyMMdd_HHmm}";

        var batchFolder = System.IO.Path.Combine(baseFolder, safeBatchName);

        // Handle duplicate batch folder names by appending _2, _3 etc.
        if (Directory.Exists(batchFolder))
        {
            int n = 2;
            while (Directory.Exists($"{batchFolder}_{n}")) n++;
            batchFolder = $"{batchFolder}_{n}";
        }

        try { Directory.CreateDirectory(batchFolder); }
        catch (Exception dirEx)
        {
            MessageBox.Show($"Could not create batch folder:\n{batchFolder}\n\n{dirEx.Message}",
                "Folder Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            batchFolder = System.IO.Path.Combine(baseFolder, $"Batch_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(batchFolder);
        }

        // ── UI: switch to running state ───────────────────────────────────
        StartBatchButton.Visibility     = Visibility.Collapsed;
        BatchProgressPanel.Visibility   = Visibility.Visible;
        CancelBatchButton.IsEnabled     = true;
        _cts = new CancellationTokenSource();

        int succeeded = 0, failed = 0, cancelled = 0;
        int total     = _queue.Count;
        long totalCharsBilled = 0;
        var items = _queue.ToList();
        var completedFilePaths = new List<string>(); // for FFmpeg merge

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];

            // Update progress UI
            BatchProgressText.Text    = $"Processing: {item.Title}";
            BatchProgressCounter.Text = $"{i + 1} / {total}";
            BatchProgressBar.Value    = (double)i / total * 100;
            item.StatusIcon  = "↻";
            item.StatusColor = (Brush)FindResource("AccentBrush");

            var job = new TtsJob
            {
                Title          = item.Title,
                BatchName      = batchName,   // stored for recent batches grouping
                InputText      = item.Text,
                CharacterCount = item.Text.Length,
                VoiceId        = voiceId,
                ModelId        = modelId,
                AudioEncoding  = encoding,
                Temperature    = 1.1,
                SpeakingRate   = speakingRate,
            };

            try
            {
                await _processor.ProcessJobAsync(job, apiKey, _cts.Token);

                // Export into batch subfolder — 01-Title.mp3, 02-Title.mp3, etc.
                if (job.OutputFilePath is not null && File.Exists(job.OutputFilePath))
                {
                    var ext       = System.IO.Path.GetExtension(job.OutputFilePath);
                    var safeTitle = SanitizeFileName(item.Title);
                    if (string.IsNullOrWhiteSpace(safeTitle))
                        safeTitle = $"Job_{i + 1}";
                    // Zero-padded sequential prefix: 01-, 02-, … 10-, 11-, …
                    var prefix   = (i + 1).ToString().PadLeft(total > 9 ? 2 : 1, '0');
                    var destPath = System.IO.Path.Combine(batchFolder, $"{prefix}-{safeTitle}{ext}");

                    int collision = 1;
                    while (File.Exists(destPath))
                        destPath = System.IO.Path.Combine(batchFolder, $"{safeTitle}_{collision++}{ext}");

                    try
                    {
                        File.Copy(job.OutputFilePath, destPath, overwrite: false);
                        completedFilePaths.Add(destPath); // track for merge

                        // Auto-save SRT alongside audio if timestamps available
                        var hasTimestamps = job.Chunks.Any(c => c.Words.Count > 0);
                        if (hasTimestamps)
                        {
                            try
                            {
                                var srtPath = System.IO.Path.ChangeExtension(destPath, ".srt");
                                _srtExport.ExportSrt(job, srtPath);
                            }
                            catch { /* SRT save failure is non-fatal */ }
                        }
                    }
                    catch (Exception copyEx)
                    {
                        MessageBox.Show(
                            $"Could not copy '{item.Title}':\n{copyEx.Message}\n\nDest: {destPath}",
                            "Copy Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    MessageBox.Show(
                        $"Job '{item.Title}' completed but output file was not found.\n" +
                        $"Path: {job.OutputFilePath ?? "(null)"}",
                        "Missing Output", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                item.StatusIcon  = "✓";
                item.StatusColor = (Brush)FindResource("SuccessBrush");
                succeeded++;
                totalCharsBilled += job.CharactersBilled;
            }
            catch (OperationCanceledException)
            {
                item.StatusIcon  = "⏹";
                item.StatusColor = (Brush)FindResource("WarningBrush");
                cancelled++;
                // Mark remaining as skipped
                for (int j = i + 1; j < items.Count; j++)
                {
                    items[j].StatusIcon  = "–";
                    items[j].StatusColor = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x80));
                }
                break;
            }
            catch
            {
                item.StatusIcon  = "✕";
                item.StatusColor = (Brush)FindResource("DangerBrush");
                failed++;
            }
        }

        // ── Final progress bar state ──────────────────────────────────────
        BatchProgressBar.Value    = 100;
        BatchProgressText.Text    = cancelled > 0
            ? $"Cancelled — {succeeded} completed"
            : $"Done — {succeeded} succeeded, {failed} failed";
        BatchProgressCounter.Text = $"{succeeded} / {total}";

        // ── UI: restore start state after short delay ─────────────────────
        await Task.Delay(2000);
        BatchProgressPanel.Visibility = Visibility.Collapsed;
        StartBatchButton.Visibility   = Visibility.Visible;
        StartBatchButton.IsEnabled    = _queue.Count > 0;

        // Update usage stats
        UpdateUsageStats();

        // Toast
        _toast.Show(
            cancelled > 0 ? "Batch Cancelled" : "Batch Complete",
            $"{succeeded} succeeded · {failed} failed · {FormatChars(totalCharsBilled)} chars",
            cancelled > 0 ? ToastService.ToastType.Info : ToastService.ToastType.Success);

        // ── Auto-open output folder ───────────────────────────────────────
        if (succeeded > 0)
        {
            // Offer FFmpeg merge if multiple files succeeded
            if (succeeded > 1 && FfmpegMergeService.IsAvailable())
            {
                var mergeResult = MessageBox.Show(
                    $"Batch complete! {succeeded} files saved to:\n{batchFolder}\n\n" +
                    $"Merge all {succeeded} files into one audio file using FFmpeg?",
                    "Batch Complete", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (mergeResult == MessageBoxResult.Yes)
                    await MergeBatchWithFfmpegAsync(completedFilePaths, batchFolder, safeBatchName, encoding);
                else
                {
                    var open = MessageBox.Show($"Open output folder?", "Open Folder",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (open == MessageBoxResult.Yes)
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{batchFolder}\"")
                            { UseShellExecute = true });
                }
            }
            else
            {
                var open = MessageBox.Show(
                    $"Batch complete! {succeeded} file{(succeeded == 1 ? "" : "s")} saved to:\n\n{batchFolder}\n\nOpen folder?",
                    "Batch Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (open == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{batchFolder}\"")
                        { UseShellExecute = true });
            }
        }

        LoadRecentBatches();

        // Show "New Batch" action panel after completion
        if (succeeded > 0)
            ShowNewBatchPanel(batchName, succeeded);
    }

    private void ShowNewBatchPanel(string completedBatchName, int succeededCount)
    {
        NewBatchPanel.Visibility  = Visibility.Visible;
        NewBatchSummaryText.Text  =
            $"✓  \"{completedBatchName}\" — {succeededCount} file{(succeededCount == 1 ? "" : "s")} generated";
    }

    private void NewBatchButton_Click(object sender, RoutedEventArgs e)
    {
        _queue.Clear();
        BatchNameBox.Text          = string.Empty;
        ManualTitleBox.Text        = string.Empty;
        ManualTextBox.Text         = string.Empty;
        NewBatchPanel.Visibility   = Visibility.Collapsed;
        StartBatchButton.IsEnabled = false;
        TabManual_Click(this, new RoutedEventArgs());
    }

    private async Task MergeBatchWithFfmpegAsync(
        List<string> filePaths, string batchFolder, string batchName, string encoding)
    {
        var ext = Models.AppSettings.AudioExtensions.GetValueOrDefault(encoding, "mp3");
        var outputPath = System.IO.Path.Combine(batchFolder, $"{batchName}_merged.{ext}");

        // Ask where to save the merged file
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Save Merged Audio",
            FileName   = $"{batchName}_merged.{ext}",
            Filter     = $"Audio files|*.{ext}|All files|*.*",
            DefaultExt = ext,
            InitialDirectory = batchFolder
        };

        if (dlg.ShowDialog() != true) return;
        outputPath = dlg.FileName;

        // Show progress
        BatchProgressPanel.Visibility = Visibility.Visible;
        StartBatchButton.Visibility   = Visibility.Collapsed;
        BatchProgressText.Text        = "Merging with FFmpeg…";
        BatchProgressBar.Value        = 0;

        try
        {
            var merger   = new FfmpegMergeService();
            var progress = new Progress<int>(p =>
            {
                Dispatcher.Invoke(() => BatchProgressBar.Value = p);
            });

            await merger.MergeAsync(filePaths, outputPath, progress);

            BatchProgressBar.Value = 100;
            BatchProgressText.Text = "Merge complete!";
            await Task.Delay(1000);

            var open = MessageBox.Show(
                $"Merged file saved:\n{outputPath}\n\nOpen containing folder?",
                "Merge Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (open == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe",
                        $"/select,\"{outputPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"FFmpeg merge failed:\n{ex.Message}",
                "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BatchProgressPanel.Visibility = Visibility.Collapsed;
            StartBatchButton.Visibility   = Visibility.Visible;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    // ── Usage stats ───────────────────────────────────────────────────────

    private void UpdateUsageStats()
    {
        int   jobCount   = _queue.Count;
        long  totalChars = _queue.Sum(q => (long)q.Text.Length);
        StatJobCount.Text   = jobCount.ToString();
        StatTotalChars.Text = FormatChars(totalChars);
    }

    private static string FormatChars(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000     => $"{n / 1_000.0:F1}k",
        _            => n.ToString()
    };

    // ── Navigation ────────────────────────────────────────────────────────

    private void SingleJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToPage("generate");
    }

    private void RecentBatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var batchName = (sender as Border)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(batchName)) return;

        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateToPage("batchdetail", batchName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: string t } && t == tag)
            { combo.SelectedIndex = i; return; }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? GetComboTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
}

// ── Batch view model ──────────────────────────────────────────────────────

public class RecentBatchViewModel
{
    public string   Name      { get; set; } = string.Empty;
    public string   Summary   { get; set; } = string.Empty;
    public int      JobCount  { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ── Queue item view model ─────────────────────────────────────────────────

public class QueueItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private int    _index;
    private string _title;
    private string _text;
    private string _statusIcon  = "–";
    private Brush  _statusColor = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x80));
    private bool   _isEditing   = false;
    private string _editTitle   = "";
    private string _editText    = "";

    public string Title
    {
        get => _title;
        private set { _title = value; OnChanged(nameof(Title)); OnChanged(nameof(CharCount)); }
    }
    public string Text
    {
        get => _text;
        private set { _text = value; OnChanged(nameof(Text)); OnChanged(nameof(CharCount)); }
    }
    public string CharCount => $"{Text.Length:N0}";

    public int Index
    {
        get => _index;
        set { _index = value; OnChanged(nameof(Index)); }
    }
    public string StatusIcon
    {
        get => _statusIcon;
        set { _statusIcon = value; OnChanged(nameof(StatusIcon)); OnChanged(nameof(StatusVisibility)); }
    }
    public Brush StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnChanged(nameof(StatusColor)); }
    }
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            _isEditing = value;
            OnChanged(nameof(IsEditing));
            OnChanged(nameof(EditPanelVisibility));
        }
    }
    public string EditTitle
    {
        get => _editTitle;
        set { _editTitle = value; OnChanged(nameof(EditTitle)); }
    }
    public string EditText
    {
        get => _editText;
        set { _editText = value; OnChanged(nameof(EditText)); }
    }

    public Visibility StatusVisibility
        => _statusIcon == "–" ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EditPanelVisibility
        => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Commits the edited values back to Title/Text.</summary>
    public void ApplyEdit()
    {
        if (!string.IsNullOrWhiteSpace(_editTitle)) Title = _editTitle.Trim();
        if (!string.IsNullOrWhiteSpace(_editText))  Text  = _editText.Trim();
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new(name));

    public QueueItemViewModel(int index, string title, string text)
    {
        _index = index;
        _title = title;
        _text  = text;
    }
}
