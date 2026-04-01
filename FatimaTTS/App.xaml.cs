using FatimaTTS.Services;
using FatimaTTS.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FatimaTTS;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch ALL unhandled exceptions so nothing dies silently
        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show(
                $"Unhandled UI error:\n\n{ex.Exception.GetType().Name}: {ex.Exception.Message}\n\n{ex.Exception.StackTrace}",
                "Fatima TTS — Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            var msg = ex.ExceptionObject is Exception err
                ? $"{err.GetType().Name}: {err.Message}\n\n{err.StackTrace}"
                : ex.ExceptionObject?.ToString() ?? "Unknown error";
            MessageBox.Show($"Fatal error:\n\n{msg}", "Fatima TTS — Fatal",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"Async error:\n\n{ex.Exception.GetType().Name}: {ex.Exception.Message}\n\n{ex.Exception.InnerException?.Message}",
                    "Fatima TTS — Async Error", MessageBoxButton.OK, MessageBoxImage.Error));
            ex.SetObserved();
        };

        var sc = new ServiceCollection();
        RegisterServices(sc);
        Services = sc.BuildServiceProvider();

        // Apply saved theme before any window opens
        var themeService = Services.GetRequiredService<ThemeService>();
        var savedTheme   = themeService.LoadSavedTheme();
        themeService.Apply(savedTheme);
    }

    private static void RegisterServices(IServiceCollection sc)
    {
        sc.AddSingleton<SettingsService>();
        sc.AddSingleton<CredentialService>();
        sc.AddSingleton<ThemeService>();
        sc.AddSingleton<ChunkingEngine>();
        sc.AddSingleton<InworldTtsService>();
        sc.AddSingleton<AudioMergeService>();
        sc.AddSingleton<JobPersistenceService>();
        sc.AddSingleton<AudioPlayerService>();
        sc.AddSingleton<ToastService>();
        sc.AddSingleton<SrtExportService>();
        sc.AddSingleton<FfmpegMergeService>();
        sc.AddSingleton<AppLogger>();
        sc.AddSingleton<GitHubUpdateService>(sp =>
            new GitHubUpdateService(sp.GetRequiredService<AppLogger>()));
        sc.AddSingleton<FfmpegManager>(sp =>
            new FfmpegManager(sp.GetRequiredService<AppLogger>()));
        sc.AddTransient<TtsJobProcessor>();

        // Views — Singleton pages preserve state across navigation, Transient pages are cheap to recreate
        sc.AddTransient<MainWindow>();
        sc.AddTransient<GenerateSpeechPage>();
        sc.AddSingleton<BatchGeneratePage>();   // Singleton — preserves queue across nav
        sc.AddTransient<VoiceLibraryPage>();
        sc.AddTransient<VoiceClonePage>();
        sc.AddTransient<VoiceDesignPage>();
        sc.AddTransient<MyJobsPage>();
        sc.AddTransient<DashboardPage>();
        sc.AddTransient<SettingsPage>();
        sc.AddTransient<AboutPage>();
    }
}
