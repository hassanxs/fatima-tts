using System.IO;
using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FatimaTTS.Views;

public partial class VoiceLibraryPage : Page
{
    private readonly InworldTtsService  _tts;
    private readonly CredentialService  _credentials;
    private readonly SettingsService    _settingsService;
    private readonly AudioPlayerService _player;

    private List<InworldVoice> _allVoices    = [];
    private string _filterMode = "all";
    private string _searchQuery = "";
    private Button? _activePreviewBtn = null;

    public VoiceLibraryPage()
    {
        InitializeComponent();
        _tts             = App.Services.GetRequiredService<InworldTtsService>();
        _credentials     = App.Services.GetRequiredService<CredentialService>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();
        _player          = App.Services.GetRequiredService<AudioPlayerService>();

        _player.PlaybackStopped += () => Dispatcher.Invoke(ResetPreviewButtons);

        Loaded += async (_, _) => await LoadVoicesAsync();
    }

    private async Task LoadVoicesAsync()
    {
        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            LoadingText.Text = "No API key configured. Please add your key in Settings.";
            return;
        }

        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text        = "Loading voices from Inworld…";
        SystemVoicesList.Visibility = Visibility.Collapsed;
        ClonedVoicesList.Visibility = Visibility.Collapsed;

        try
        {
            _allVoices = await _tts.ListWorkspaceVoicesAsync(apiKey);
            LoadingPanel.Visibility = Visibility.Collapsed;
            _voicesLoaded = true;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"Error loading voices: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query   = _searchQuery.ToLowerInvariant();
        var filtered = _allVoices
            .Where(v => string.IsNullOrEmpty(query)
                || v.DisplayName.ToLowerInvariant().Contains(query)
                || (v.Description?.ToLowerInvariant().Contains(query) ?? false))
            .ToList();

        var system = filtered
            .Where(v => v.IsSystem && (_filterMode == "all" || _filterMode == "system"))
            .ToList();

        var cloned = filtered
            .Where(v => v.IsCloned && (_filterMode == "all" || _filterMode == "cloned"))
            .ToList();

        // Bind voice view models
        SystemVoicesList.ItemsSource = system.Select(v => new VoiceCardViewModel(v)).ToList();
        ClonedVoicesList.ItemsSource = cloned.Select(v => new VoiceCardViewModel(v)).ToList();

        SystemSectionHeader.Visibility = system.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SystemVoicesList.Visibility    = system.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClonedSectionHeader.Visibility = cloned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClonedVoicesList.Visibility    = cloned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        int total = system.Count + cloned.Count;
        VoiceCountText.Text = $"{total} voice{(total == 1 ? "" : "s")} " +
                              $"({system.Count} system, {cloned.Count} cloned)";
    }

    private bool _voicesLoaded = false;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text;
        if (_voicesLoaded) ApplyFilter();
    }

    private void FilterAll_Checked(object sender, RoutedEventArgs e)
    { _filterMode = "all";    if (_voicesLoaded) ApplyFilter(); }

    private void FilterSystem_Checked(object sender, RoutedEventArgs e)
    { _filterMode = "system"; if (_voicesLoaded) ApplyFilter(); }

    private void FilterCloned_Checked(object sender, RoutedEventArgs e)
    { _filterMode = "cloned"; if (_voicesLoaded) ApplyFilter(); }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await LoadVoicesAsync();

    private async void PreviewVoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string voiceId } btn) return;

        // If already playing this voice — stop
        if (_activePreviewBtn == btn)
        {
            _player.Stop();
            ResetPreviewButtons();
            return;
        }

        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        ResetPreviewButtons();
        _activePreviewBtn = btn;
        btn.IsEnabled = false;

        if (btn.Content is TextBlock tb) tb.Text = "⏳";

        try
        {
            const string PreviewText = "Hello! This is a preview of how this voice sounds.";

            var (audioBytes, _, _) = await _tts.SynthesizeAsync(
                apiKey, PreviewText, voiceId,
                "inworld-tts-1.5-max", "MP3",
                temperature: 1.1, speakingRate: 1.0);

            var tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"fatima_preview_{voiceId}.mp3");

            await System.IO.File.WriteAllBytesAsync(tempPath, audioBytes);
            _player.Load(tempPath);
            _player.Play();

            btn.IsEnabled = true;
            if (btn.Content is TextBlock tb2) tb2.Text = "⏹";
        }
        catch
        {
            btn.IsEnabled = true;
            if (btn.Content is TextBlock tb3) tb3.Text = "▶";
            _activePreviewBtn = null;
        }
    }

    private void ResetPreviewButtons()
    {
        if (_activePreviewBtn is not null)
        {
            _activePreviewBtn.IsEnabled = true;
            if (_activePreviewBtn.Content is TextBlock tb)
                tb.Text = "▶  Preview";
            _activePreviewBtn = null;
        }
    }

    private void UseVoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string voiceId })
        {
            var settings = _settingsService.Load();
            settings.DefaultVoiceId = voiceId;
            _settingsService.Save(settings);
            MessageBox.Show($"Voice set as default. It will be pre-selected on Generate Speech.",
                "Voice Updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void DeleteVoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string voiceId }) return;

        var voice = _allVoices.FirstOrDefault(v => v.VoiceId == voiceId);
        if (voice is null) return;

        var result = MessageBox.Show(
            $"Permanently delete cloned voice \"{voice.DisplayName}\"?\n\nThis cannot be undone.",
            "Delete Voice", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        try
        {
            var tts = App.Services.GetRequiredService<InworldTtsService>();
            await tts.DeleteVoiceAsync(apiKey, voiceId);
            await LoadVoicesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete voice: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public class VoiceCardViewModel
{
    public string VoiceId        { get; }
    public string DisplayName    { get; }
    public string? Description   { get; }
    public string LanguageDisplay { get; }
    public string SourceLabel    { get; }
    public Visibility HasDescription =>
        string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;

    public VoiceCardViewModel(InworldVoice v)
    {
        VoiceId         = v.VoiceId;
        DisplayName     = v.DisplayName;
        Description     = v.Description;
        LanguageDisplay = v.LanguageDisplay;
        SourceLabel     = v.SourceLabel;
    }
}
