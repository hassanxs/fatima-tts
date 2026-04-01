using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FatimaTTS.Views;

public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var persistence = App.Services.GetRequiredService<JobPersistenceService>();
        var jobs        = persistence.LoadAllJobs();

        // ── Stats ────────────────────────────────────────────────────────
        TotalJobsStat.Text   = jobs.Count.ToString();
        CompletedStat.Text   = jobs.Count(j => j.Status == JobStatus.Completed).ToString();
        CharsBilledStat.Text = FormatNumber(jobs.Sum(j => j.CharactersBilled));
        SavedStat.Text       = FormatNumber(
            jobs.SelectMany(j => j.Chunks)
                .Where(c => c.Status == ChunkStatus.Completed && c.RetryCount == 0)
                .Sum(c => c.ApiProcessedChars));

        // ── Recent jobs ──────────────────────────────────────────────────
        var recent = jobs.OrderByDescending(j => j.CreatedAt).Take(5).ToList();
        if (recent.Count > 0)
        {
            RecentJobsEmpty.Visibility = Visibility.Collapsed;
            RecentJobsList.Visibility  = Visibility.Visible;
            RecentJobsList.ItemsSource = recent.Select(j => new RecentJobViewModel(j)).ToList();
        }

        // ── Chart ────────────────────────────────────────────────────────
        DrawUsageChart(jobs);
    }

    // ── Chart drawing ─────────────────────────────────────────────────────

    private void DrawUsageChart(List<TtsJob> jobs)
    {
        // Build daily buckets for the last 14 days
        var today  = DateTime.Today;
        var days   = Enumerable.Range(0, 14)
            .Select(i => today.AddDays(-13 + i))
            .ToList();

        var dailyChars = days.Select(day =>
            jobs.Where(j => j.CompletedAt.HasValue &&
                            j.CompletedAt.Value.Date == day &&
                            j.Status == JobStatus.Completed)
                .Sum(j => (long)j.CharactersBilled))
            .ToList();

        long total = dailyChars.Sum();
        ChartTotalText.Text = $"{FormatNumber(total)} total this period";

        // Only show chart if there's any data
        if (total == 0)
        {
            ChartEmptyState.Visibility = Visibility.Visible;
            ChartArea.Visibility       = Visibility.Collapsed;
            return;
        }

        ChartEmptyState.Visibility = Visibility.Collapsed;
        ChartArea.Visibility       = Visibility.Visible;

        // Store data for rendering
        _chartDays       = days;
        _chartDailyChars = dailyChars;
        _chartMax        = dailyChars.Max();

        // Use SizeChanged to catch first layout, and Loaded as fallback
        // Also schedule via Dispatcher to ensure ChartArea visibility has propagated
        ChartCanvas.SizeChanged -= ChartCanvas_SizeChanged;
        ChartCanvas.SizeChanged += ChartCanvas_SizeChanged;

        Dispatcher.InvokeAsync(RenderChart, System.Windows.Threading.DispatcherPriority.Render);
    }

    private List<DateTime> _chartDays = [];
    private List<long>     _chartDailyChars = [];
    private long           _chartMax = 1;

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            ChartCanvas.SizeChanged -= ChartCanvas_SizeChanged;
            RenderChart();
        }
    }

    private void RenderChart()
    {
        if (_chartDays.Count == 0 || _chartMax == 0) return;

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;

        // If not laid out yet, retry after next render pass
        if (w <= 0 || h <= 0)
        {
            Dispatcher.InvokeAsync(RenderChart, System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        ChartCanvas.Children.Clear();
        XAxisCanvas.Children.Clear();
        YAxisCanvas.Children.Clear();

        int    count     = _chartDays.Count;
        double barWidth  = Math.Max(4, (w / count) - 4);
        double slotWidth = w / count;

        // Resolve theme colors
        var accentBrush  = (SolidColorBrush)FindResource("AccentBrush");
        var mutedBrush   = (SolidColorBrush)FindResource("TextMutedBrush");
        var subtleBrush  = (SolidColorBrush)FindResource("BorderSubtleBrush");
        var accentColor  = accentBrush.Color;
        var barFill      = new SolidColorBrush(Color.FromArgb(200, accentColor.R, accentColor.G, accentColor.B));
        var barHoverFill = accentBrush;

        // Horizontal grid lines (4 lines)
        for (int i = 0; i <= 4; i++)
        {
            double y = h * i / 4;
            var line = new Line
            {
                X1              = 0, X2 = w,
                Y1              = y, Y2 = y,
                Stroke          = subtleBrush,
                StrokeThickness = 0.5,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            ChartCanvas.Children.Add(line);

            // Y-axis label
            long labelVal = _chartMax - (_chartMax * i / 4);
            var label = new System.Windows.Controls.TextBlock
            {
                Text       = FormatNumber(labelVal),
                FontSize   = 9,
                Foreground = mutedBrush
            };
            Canvas.SetRight(label, 4);
            Canvas.SetTop(label, y - 7);
            YAxisCanvas.Children.Add(label);
        }

        // Bars
        for (int i = 0; i < count; i++)
        {
            long  val      = _chartDailyChars[i];
            if (val == 0) continue;

            double barH    = val / (double)_chartMax * h;
            double x       = i * slotWidth + (slotWidth - barWidth) / 2;
            double y       = h - barH;

            var rect = new Rectangle
            {
                Width           = barWidth,
                Height          = Math.Max(2, barH),
                Fill            = barFill,
                RadiusX         = 3,
                RadiusY         = 3,
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            ChartCanvas.Children.Add(rect);

            // Value label on tall bars
            if (barH > 24)
            {
                var valLabel = new System.Windows.Controls.TextBlock
                {
                    Text       = FormatNumber(val),
                    FontSize   = 9,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontWeight = FontWeights.SemiBold
                };
                Canvas.SetLeft(valLabel, x + barWidth / 2 - 12);
                Canvas.SetTop(valLabel, y + 4);
                ChartCanvas.Children.Add(valLabel);
            }
        }

        // X-axis date labels — show every 2nd day to avoid crowding
        for (int i = 0; i < count; i += 2)
        {
            double x = i * slotWidth + slotWidth / 2;
            var label = new System.Windows.Controls.TextBlock
            {
                Text       = _chartDays[i].ToString("M/d"),
                FontSize   = 9,
                Foreground = mutedBrush
            };
            Canvas.SetLeft(label, x - 10);
            Canvas.SetTop(label, 4);
            XAxisCanvas.Children.Add(label);
        }
    }

    // ── Quick actions ─────────────────────────────────────────────────────

    private void QuickGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.NavigateToPage("generate");
    }

    private void QuickVoiceLib_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.NavigateToPage("voicelibrary");
    }

    private void QuickClone_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.NavigateToPage("voiceclone");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string FormatNumber(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000     => $"{n / 1_000.0:F1}k",
        _            => n.ToString()
    };
}

// ── Recent job view model ─────────────────────────────────────────────────

public class RecentJobViewModel
{
    public string DisplayTitle  { get; }
    public string MetaLine      { get; }
    public string CreatedAtShort { get; }
    public Brush  StatusColor   { get; }

    public RecentJobViewModel(TtsJob job)
    {
        DisplayTitle   = job.DisplayTitle;
        CreatedAtShort = job.CreatedAt.ToString("MMM d");

        // Extract friendly voice name from raw ID (e.g. "default-xyz__santiago" → "Santiago")
        var voiceRaw  = job.VoiceId ?? "";
        var voiceName = voiceRaw.Contains("__")
            ? voiceRaw[(voiceRaw.LastIndexOf("__") + 2)..]
            : voiceRaw;
        if (voiceName.Length > 0)
            voiceName = char.ToUpper(voiceName[0]) + voiceName[1..];

        MetaLine = $"{voiceName} · {AppSettings.ModelDisplayNames.GetValueOrDefault(job.ModelId, job.ModelId)} · {job.CharacterCount:N0} chars";
        StatusColor    = job.Status switch
        {
            JobStatus.Completed   => new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75)),
            JobStatus.Failed      => new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)),
            JobStatus.Interrupted => new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27)),
            _                     => new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD))
        };
    }
}
