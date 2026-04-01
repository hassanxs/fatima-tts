using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;


namespace FatimaTTS.Views;

public partial class MainWindow : Window
{
    private readonly ThemeService      _theme;
    private readonly CredentialService _credentials;
    private readonly SettingsService   _settingsService;

    // Track which nav accent border is active
    private Border? _activeAccent;
    private Button? _activeNavBtn;

    public MainWindow()
    {
        InitializeComponent();

        _theme           = App.Services.GetRequiredService<ThemeService>();
        _credentials     = App.Services.GetRequiredService<CredentialService>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Apply saved theme to toggle state
        var saved = _settingsService.Load().Theme;
        ThemeToggle.IsChecked = (saved == "Dark");

        // Refresh API key status indicator
        RefreshApiStatus();

        // Default page: Generate Speech
        NavigateTo("generate");
    }

    // Force correct TextBox foreground after each page load — WPF bug workaround
    // TextBoxView ignores template foreground in some Windows versions
    private void FixTextBoxForegrounds()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var brush = (Brush)FindResource("TextPrimaryBrush");
            FixTextBoxesInVisual(ContentFrame, brush);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void FixTextBoxesInVisual(DependencyObject parent, Brush brush)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox tb)
                tb.Foreground = brush;
            else
                FixTextBoxesInVisual(child, brush);
        }
    }

    // ── API key status indicator ──────────────────────────────────────────

    public void RefreshApiStatus()
    {
        bool hasKey = _credentials.HasApiKey();
        ApiStatusDot.Fill  = hasKey
            ? new SolidColorBrush(Color.FromRgb(0x1D, 0x9E, 0x75))   // green
            : new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));  // red
        ApiStatusText.Text = hasKey ? "API key active" : "No API key";
    }

    private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
    {
        _theme.ApplyAndSave("Dark");
        FixTextBoxForegrounds();
    }

    private void ThemeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _theme.ApplyAndSave("Light");
        FixTextBoxForegrounds();
    }

    // ── Navigation ───────────────────────────────────────────────────────

    // Called by pages that need to navigate the shell (e.g. "Go to My Jobs")
    public void NavigateToPage(string page) => NavigateTo(page);
    public void NavigateToPage(string page, string parameter) => NavigateTo(page, parameter);

    private void NavDashboard_Click(object sender, RoutedEventArgs e)
        => NavigateTo("dashboard");

    private void NavSettings_Click(object sender, RoutedEventArgs e)
        => NavigateTo("settings");

    private void NavVoiceLibrary_Click(object sender, RoutedEventArgs e)
        => NavigateTo("voicelibrary");

    private void NavVoiceClone_Click(object sender, RoutedEventArgs e)
        => NavigateTo("voiceclone");

    private void NavVoiceDesign_Click(object sender, RoutedEventArgs e)
        => NavigateTo("voicedesign");

    private void NavGenerate_Click(object sender, RoutedEventArgs e)
        => NavigateTo("generate");

    private void NavBatch_Click(object sender, RoutedEventArgs e)
        => NavigateTo("batch");

    private void NavAbout_Click(object sender, RoutedEventArgs e)
        => NavigateTo("about");

    private void NavMyJobs_Click(object sender, RoutedEventArgs e)
        => NavigateTo("myjobs");

    private void NavigateTo(string page, string? parameter = null)
    {
        // Reset previous active state
        if (_activeNavBtn is not null)
            _activeNavBtn.Style = (Style)FindResource("NavButtonStyle");
        if (_activeAccent is not null)
            _activeAccent.Visibility = Visibility.Collapsed;

        Border accent;
        Button btn;
        Page   view;

        switch (page)
        {
            case "dashboard":
                accent = AccentDashboard;   btn = NavDashboard;    view = CreatePage<DashboardPage>();    break;
            case "settings":
                accent = AccentSettings;    btn = NavSettings;     view = CreatePage<SettingsPage>();     break;
            case "about":
                accent = AccentAbout;       btn = NavAbout;        view = CreatePage<AboutPage>();        break;
            case "voicelibrary":
                accent = AccentVoiceLibrary; btn = NavVoiceLibrary; view = CreatePage<VoiceLibraryPage>(); break;
            case "voiceclone":
                accent = AccentVoiceClone;  btn = NavVoiceClone;   view = CreatePage<VoiceClonePage>();   break;
            case "voicedesign":
                accent = AccentVoiceDesign; btn = NavVoiceDesign;  view = CreatePage<VoiceDesignPage>();  break;
            case "batch":
                accent = AccentBatch;       btn = NavBatch;        view = CreatePage<BatchGeneratePage>(); break;
            case "myjobs":
                accent = AccentMyJobs;      btn = NavMyJobs;       view = CreatePage<MyJobsPage>();       break;
            case "batchdetail":
                accent = AccentBatch;       btn = NavBatch;
                var detailPage = new BatchDetailPage(parameter ?? "");
                view = detailPage;
                break;
            default: // "generate"
                accent = AccentGenerate;    btn = NavGenerate;     view = CreatePage<GenerateSpeechPage>(); break;
        }

        accent.Visibility = Visibility.Visible;
        btn.Style         = (Style)FindResource("NavButtonActiveStyle");
        _activeAccent     = accent;
        _activeNavBtn     = btn;

        ContentFrame.Navigate(view);
        FixTextBoxForegrounds();
    }

    private static T CreatePage<T>() where T : Page
        => App.Services.GetRequiredService<T>();
}
