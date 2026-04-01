using System.Text.Json.Serialization;

namespace FatimaTTS.Models;

public class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string DefaultVoiceId { get; set; } = "Ashley";
    public string DefaultModelId { get; set; } = "inworld-tts-1.5-max";
    public string DefaultAudioEncoding { get; set; } = "MP3";
    public double DefaultTemperature { get; set; } = 1.1;
    public double DefaultSpeakingRate { get; set; } = 1.0;
    public string OutputFolder { get; set; } = string.Empty;
    public bool AutoPlay { get; set; } = true;
    public bool SaveChunksOnComplete { get; set; } = false;

    [JsonIgnore]
    public static readonly string[] AvailableModels =
    [
        "inworld-tts-1.5-max",
        "inworld-tts-1.5-mini",
        "inworld-tts-1-max",
        "inworld-tts-1"
    ];

    [JsonIgnore]
    public static readonly Dictionary<string, string> ModelDisplayNames = new()
    {
        ["inworld-tts-1.5-max"]  = "Inworld TTS 1.5 Max",
        ["inworld-tts-1.5-mini"] = "Inworld TTS 1.5 Mini",
        ["inworld-tts-1-max"]    = "Inworld TTS 1.0 Max",
        ["inworld-tts-1"]        = "Inworld TTS 1.0",
    };

    [JsonIgnore]
    public static readonly Dictionary<string, string> ModelDescriptions = new()
    {
        ["inworld-tts-1.5-max"]  = "Flagship model — best quality + speed balance",
        ["inworld-tts-1.5-mini"] = "Ultra-fast, most cost-efficient (~120ms latency)",
        ["inworld-tts-1-max"]    = "Previous gen — powerful with basic timestamps",
        ["inworld-tts-1"]        = "Previous gen — fastest with basic timestamps",
    };

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
