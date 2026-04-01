using System.IO;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace FatimaTTS.Views;

public partial class VoiceClonePage : Page
{
    private readonly InworldTtsService _tts;
    private readonly CredentialService _credentials;
    private string? _selectedAudioPath;

    private static readonly Dictionary<string, string> Languages = new()
    {
        ["EN_US"] = "English (US)",
        ["EN_GB"] = "English (UK)",
        ["ES_ES"] = "Spanish (Spain)",
        ["ES_MX"] = "Spanish (Mexico)",
        ["FR_FR"] = "French",
        ["DE_DE"] = "German",
        ["IT_IT"] = "Italian",
        ["JA_JP"] = "Japanese",
        ["KO_KR"] = "Korean",
        ["ZH_CN"] = "Chinese (Mandarin)",
        ["PT_BR"] = "Portuguese (Brazil)",
        ["RU_RU"] = "Russian",
        ["PL_PL"] = "Polish",
        ["NL_NL"] = "Dutch",
        ["HI_IN"] = "Hindi",
        ["HE_IL"] = "Hebrew",
        ["AR_SA"] = "Arabic",
        ["AUTO"]  = "Auto-detect",
    };

    public VoiceClonePage()
    {
        InitializeComponent();
        _tts         = App.Services.GetRequiredService<InworldTtsService>();
        _credentials = App.Services.GetRequiredService<CredentialService>();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var kvp in Languages)
        {
            LanguageComboBox.Items.Add(new ComboBoxItem
            {
                Content = kvp.Value,
                Tag     = kvp.Key
            });
        }
        LanguageComboBox.SelectedIndex = 0;
    }

    private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select Audio Sample",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.ogg;*.flac|All files|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            _selectedAudioPath      = dlg.FileName;
            SelectedFileText.Text   = Path.GetFileName(dlg.FileName);
        }
    }

    private async void CloneVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("No API key configured. Please go to Settings.",
                "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = VoiceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Please enter a name for your cloned voice.",
                "Missing Name", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedAudioPath is null || !File.Exists(_selectedAudioPath))
        {
            MessageBox.Show("Please select an audio sample file.",
                "No Audio File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var langCode = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "EN_US";

        CloneVoiceButton.IsEnabled = false;
        CloneStatusText.Text       = "Uploading and processing your voice sample…";
        CloneStatusText.Visibility = Visibility.Visible;

        try
        {
            var audioBytes  = await File.ReadAllBytesAsync(_selectedAudioPath);
            var audioBase64 = Convert.ToBase64String(audioBytes);
            var transcription = TranscriptionBox.Text.Trim();

            var result = await _tts.CloneVoiceAsync(
                apiKey,
                name,
                langCode,
                audioBase64,
                string.IsNullOrWhiteSpace(transcription) ? null : transcription,
                DescriptionBox.Text.Trim(),
                RemoveNoiseCheckBox.IsChecked == true);

            CloneStatusText.Text = $"✓ Voice \"{result.DisplayName}\" created successfully! " +
                                   $"Find it in Voice Library.";

            // Clear form
            VoiceNameBox.Text      = "";
            DescriptionBox.Text    = "";
            TranscriptionBox.Text  = "";
            _selectedAudioPath     = null;
            SelectedFileText.Text  = "No file chosen";
        }
        catch (Exception ex)
        {
            CloneStatusText.Text = $"✕ Error: {ex.Message}";
        }
        finally
        {
            CloneVoiceButton.IsEnabled = true;
        }
    }
}
