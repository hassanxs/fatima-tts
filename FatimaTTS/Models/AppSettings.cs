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

    // ── New in v1.2.0 ──────────────────────────────────────────────────────
    public string DefaultLanguage { get; set; } = "";            // "" = voice default
    public string DefaultDeliveryMode { get; set; } = "";        // "" = unspecified (tts-2 steering)
    public string DefaultTimestampType { get; set; } = "WORD";   // WORD | CHARACTER
    public bool DefaultApplyTextNormalization { get; set; } = true;
    public int MaxParallelChunks { get; set; } = 3;              // concurrent chunk syntheses
    // Editable per-model price per 1,000,000 characters (USD). Seeded with On-Demand rates.
    public Dictionary<string, double> PricePerMillionChars { get; set; } = new(DefaultPricePerMillion);

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

    // ── Steering / captions / language tables (v1.2.0) ─────────────────────

    [JsonIgnore]
    public static readonly Dictionary<string, string> DeliveryModes = new()
    {
        [""]         = "Default (unspecified)",
        ["STABLE"]   = "Stable — most consistent",
        ["BALANCED"] = "Balanced",
        ["CREATIVE"] = "Creative — most expressive",
    };

    [JsonIgnore]
    public static readonly Dictionary<string, string> TimestampTypes = new()
    {
        ["WORD"]      = "Word-level",
        ["CHARACTER"] = "Character-level",
    };

    // BCP-47 codes for the languages the 1.5 models cover; "" = use the voice's own language.
    [JsonIgnore]
    public static readonly (string Code, string Name)[] CommonLanguages =
    [
        ("",      "Voice default"),
        ("en-US", "English (US)"),
        ("en-GB", "English (UK)"),
        ("es-ES", "Spanish"),
        ("fr-FR", "French"),
        ("de-DE", "German"),
        ("it-IT", "Italian"),
        ("pt-BR", "Portuguese (Brazil)"),
        ("pl-PL", "Polish"),
        ("nl-NL", "Dutch"),
        ("ru-RU", "Russian"),
        ("zh-CN", "Chinese (Mandarin)"),
        ("ja-JP", "Japanese"),
        ("ko-KR", "Korean"),
        ("hi-IN", "Hindi"),
        ("ar-SA", "Arabic"),
        ("he-IL", "Hebrew"),
    ];

    // ── Pricing (USD per 1,000,000 characters) ─────────────────────────────
    // Default = Inworld On-Demand plan rates (verify at https://inworld.ai/pricing).
    [JsonIgnore]
    public static readonly Dictionary<string, double> DefaultPricePerMillion = new()
    {
        ["inworld-tts-2"]        = 25.0,
        ["inworld-tts-1.5-max"]  = 35.0,
        ["inworld-tts-1.5-mini"] = 15.0,
    };

    /// <summary>Price per 1M chars for a model, falling back to seeded defaults for unknown/absent keys.</summary>
    public double GetPricePerMillion(string modelId)
    {
        var id = NormalizeModelId(modelId);
        if (PricePerMillionChars.TryGetValue(id, out var p)) return p;
        return DefaultPricePerMillion.GetValueOrDefault(id, 0.0);
    }

    /// <summary>Estimated USD cost for synthesizing the given number of characters with a model.</summary>
    public double EstimateCost(long characters, string modelId)
        => characters / 1_000_000.0 * GetPricePerMillion(modelId);
}
