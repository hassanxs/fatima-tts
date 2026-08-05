using System.Text.Json.Serialization;

namespace FatimaTTS.Models;

public class AppSettings
{
    public const string DefaultModel = "inworld-tts-2";

    public string Theme { get; set; } = "Dark";
    public string DefaultVoiceId { get; set; } = "Ashley";
    public string DefaultModelId { get; set; } = DefaultModel;
    public string DefaultAudioEncoding { get; set; } = "MP3";
    public double DefaultTemperature { get; set; } = 1.1;
    public double DefaultSpeakingRate { get; set; } = 1.0;
    public string OutputFolder { get; set; } = string.Empty;
    public bool AutoPlay { get; set; } = true;
    public bool SaveChunksOnComplete { get; set; } = false;

    [JsonIgnore]
    public static readonly string[] AvailableModels =
    [
        "inworld-tts-2",
        "inworld-tts-1.5-max",
        "inworld-tts-1.5-mini"
    ];

    [JsonIgnore]
    public static readonly Dictionary<string, string> ModelDisplayNames = new()
    {
        ["inworld-tts-2"]        = "Inworld TTS 2",
        ["inworld-tts-1.5-max"]  = "Inworld TTS 1.5 Max",
        ["inworld-tts-1.5-mini"] = "Inworld TTS 1.5 Mini",
    };

    [JsonIgnore]
    public static readonly Dictionary<string, string> ModelDescriptions = new()
    {
        ["inworld-tts-2"]        = "Flagship model — natural-language steering, ~120ms latency, 200+ languages",
        ["inworld-tts-1.5-max"]  = "High quality, optimized for stability with enhanced timestamps",
        ["inworld-tts-1.5-mini"] = "Most cost-efficient, ideal for English workloads",
    };

    // Models discontinued 2026-06-15; Inworld auto-routes these to their 1.5 successors.
    // Normalize legacy IDs from older settings.json so the UI/API stay in sync.
    private static readonly Dictionary<string, string> DiscontinuedModelRoutes = new()
    {
        ["inworld-tts-1-max"] = "inworld-tts-1.5-max",
        ["inworld-tts-1"]     = "inworld-tts-1.5-mini",
    };

    public static string NormalizeModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return DefaultModel;
        if (DiscontinuedModelRoutes.TryGetValue(modelId, out var successor))
            return successor;
        return ModelDisplayNames.ContainsKey(modelId) ? modelId : DefaultModel;
    }

    [JsonIgnore]
    public static readonly Dictionary<string, string> AudioEncodings = new()
    {
        ["MP3"]      = "MP3",
        ["LINEAR16"] = "WAV (PCM 16-bit)",
        ["OGG_OPUS"] = "OGG Opus",
        ["FLAC"]     = "FLAC",
    };

    [JsonIgnore]
    public static readonly Dictionary<string, string> AudioExtensions = new()
    {
        ["MP3"]      = "mp3",
        ["LINEAR16"] = "wav",
        ["OGG_OPUS"] = "ogg",
        ["FLAC"]     = "flac",
    };
}
