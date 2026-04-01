using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FatimaTTS.Models;
using FatimaTTS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FatimaTTS.Views;

public partial class VoiceDesignPage : Page
{
    private readonly InworldTtsService  _tts;
    private readonly CredentialService  _credentials;
    private readonly AudioPlayerService _player;
    private readonly ToastService       _toast;

    private readonly ObservableCollection<PreviewVoiceViewModel> _previews = [];
    private string? _selectedVoiceId;

    public VoiceDesignPage()
    {
        InitializeComponent();
        _tts         = App.Services.GetRequiredService<InworldTtsService>();
        _credentials = App.Services.GetRequiredService<CredentialService>();
        _player      = App.Services.GetRequiredService<AudioPlayerService>();
        _toast       = App.Services.GetRequiredService<ToastService>();

        PreviewsList.ItemsSource = _previews;

        // Pre-fill a sensible preview text
        PreviewTextBox.Text =
            "Hey! I'm here. What can I help you with today? " +
            "I'd be happy to assist with whatever you need.";
    }

    // ── Step 1: Generate previews ─────────────────────────────────────────

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = _credentials.LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Please add your Inworld API key in Settings.",
                "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prompt = DesignPromptBox.Text.Trim();
        if (prompt.Length < 30)
        {
            MessageBox.Show(
                "Please write a more detailed description (at least 30 characters).\n\n" +
                "Include age, gender, accent, pitch, and tone for best results.",
                "Description Too Short", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var previewText = PreviewTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(previewText))
        {
            MessageBox.Show("Please enter some preview text.",
                "No Preview Text", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var langCode = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "EN_US";
        var samplesTag = (SamplesComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "3";
        int.TryParse(samplesTag, out int samples);
        if (samples < 1) samples = 3;

        // ── Show generating state ─────────────────────────────────────────
        GenerateButton.IsEnabled   = false;
        GeneratingPanel.Visibility = Visibility.Visible;
        GeneratingText.Text        = $"Generating {samples} voice preview{(samples > 1 ? "s" : "")}…";
        PreviewsPanel.Visibility   = Visibility.Collapsed;
        PublishPanel.Visibility    = Visibility.Collapsed;
        _previews.Clear();
        _selectedVoiceId = null;

        try
        {
            var response = await _tts.DesignVoiceAsync(
                apiKey, langCode, prompt, previewText, samples);

            if (response.PreviewVoices.Count == 0)
            {
                MessageBox.Show("No preview voices were returned. Try adjusting your description.",
                    "No Previews", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Save preview audio to temp files and populate list
            for (int i = 0; i < response.PreviewVoices.Count; i++)
            {
                var pv        = response.PreviewVoices[i];
                var tempPath  = Path.Combine(Path.GetTempPath(),
                    $"fatima_design_preview_{pv.VoiceId.Replace(":", "_")}.mp3");

                if (!string.IsNullOrEmpty(pv.PreviewAudio))
                    File.WriteAllBytes(tempPath, Convert.FromBase64String(pv.PreviewAudio));

                _previews.Add(new PreviewVoiceViewModel
                {
                    VoiceId      = pv.VoiceId,
                    Label        = $"Preview {i + 1}",
                    TempAudioPath = tempPath,
                    IsSelected   = false
                });
            }

            PreviewsPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Voice design failed:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GenerateButton.IsEnabled   = true;
            GeneratingPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ── Step 2: Preview playback + selection ─────────────────────────────

    private void PlayPreview_Click(object sender, RoutedEventArgs e)
    {
        var voiceId = (sender as Button)?.Tag?.ToString();
        var vm      = _previews.FirstOrDefault(p => p.VoiceId == voiceId);
        if (vm?.TempAudioPath is null || !File.Exists(vm.TempAudioPath)) return;

        try
        {
            _player.Load(vm.TempAudioPath);
            _player.Play();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not play preview:\n{ex.Message}",
                "Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PreviewItem_Select(object sender, MouseButtonEventArgs e)
    {
        var voiceId = (sender as Border)?.Tag?.ToString();
        if (voiceId is null) return;

        _selectedVoiceId = voiceId;

        foreach (var vm in _previews)
            vm.IsSelected = vm.VoiceId == voiceId;

        // Pre-fill voice name from prompt
        if (string.IsNullOrWhiteSpace(VoiceNameBox.Text))
        {
            var prompt = DesignPromptBox.Text.Trim();
            var firstWords = prompt.Split(' ').Take(3);
            VoiceNameBox.Text = string.Join(" ", firstWords);
        }

        PublishPanel.Visibility = Visibility.Visible;
    }

    // ── Step 3: Publish ───────────────────────────────────────────────────

    private async void PublishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedVoiceId is null)
        {
            MessageBox.Show("Please select a preview voice first.",
                "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var displayName = VoiceNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            MessageBox.Show("Please enter a name for your voice.",
                "No Name", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var apiKey = _credentials.LoadApiKey()!;
        PublishButton.IsEnabled = false;
        PublishButton.Content   = new TextBlock
        {
            Text = "Publishing…", FontSize = 14, Foreground = System.Windows.Media.Brushes.White
        };

        try
        {
            var voice = await _tts.PublishVoiceAsync(
                apiKey,
                _selectedVoiceId,
                displayName,
                DescriptionBox.Text.Trim().NullIfEmpty());

            _toast.Show("Voice Published!", $"\"{displayName}\" is now in your Voice Library.",
                ToastService.ToastType.Success);

            MessageBox.Show(
                $"✓  \"{displayName}\" published successfully!\n\n" +
                $"Voice ID: {voice.VoiceId}\n\n" +
                $"You can now use it from the Voice Library or Generate Speech page.",
                "Published!", MessageBoxButton.OK, MessageBoxImage.Information);

            // Reset form for another design
            ResetForm();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Publish failed:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PublishButton.IsEnabled = true;
            PublishButton.Content   = new TextBlock
            {
                Text = "✦  Publish to Voice Library", FontSize = 14,
                Foreground = System.Windows.Media.Brushes.White
            };
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ResetForm()
    {
        _previews.Clear();
        _selectedVoiceId     = null;
        VoiceNameBox.Text    = string.Empty;
        DescriptionBox.Text  = string.Empty;
        PreviewsPanel.Visibility = Visibility.Collapsed;
        PublishPanel.Visibility  = Visibility.Collapsed;
    }
}

// ── Preview voice view model ──────────────────────────────────────────────

public class PreviewVoiceViewModel : INotifyPropertyChanged
{
    public string  VoiceId       { get; set; } = string.Empty;
    public string  Label         { get; set; } = string.Empty;
    public string? TempAudioPath { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedVisibility)));
        }
    }

    public Visibility IsSelectedVisibility =>
        _isSelected ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

// ── String extension ──────────────────────────────────────────────────────

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;
}
