using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using FatimaTTS.Services;

namespace FatimaTTS.Views;

public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = version is not null
            ? $"Version {version.Major}.{version.Minor}.{version.Build}"
            : "Version 1.0.0";

        // Runtime
        DotNetVersion.Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        // OS
        OsVersion.Text = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        // Data folder
        var dataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FatimaTTS");
        DataFolder.Text = dataDir;
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(GitHubUpdateService.ReleasesUrl);
    }

    private void OpenInworldDocs_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://docs.inworld.ai/docs/tutorial-integrations/text-to-speech/");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FatimaTTS");

        if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", dataDir) { UseShellExecute = true });
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* fail silently */ }
    }
}
