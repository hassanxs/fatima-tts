using System.Text.Json.Serialization;

namespace FatimaTTS.Models;

public class InworldVoice
{
    [JsonPropertyName("voiceId")]
    public string VoiceId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = [];

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("source")]
    public string Source { get; set; } = "SYSTEM";

    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; set; }

    // Derived helpers
    public bool IsCloned => Source is "IVC" or "PVC";
    public bool IsSystem => Source == "SYSTEM";

    public string SourceLabel => Source switch
    {
        "IVC"    => "Instant Clone",
        "PVC"    => "Professional Clone",
        "SYSTEM" => "System",
        _        => Source
    };

    public string LanguageDisplay => Languages.Count > 0
        ? string.Join(", ", Languages)
        : "—";
}

public class ListVoicesResponse
{
    [JsonPropertyName("voices")]
    public List<InworldVoice> Voices { get; set; } = [];
}

public class SynthesizeSpeechRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("voiceId")]
    public string VoiceId { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("audioConfig")]
    public AudioConfig? AudioConfig { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 1.1;

    [JsonPropertyName("applyTextNormalization")]
    public string ApplyTextNormalization { get; set; } = "ON";

    [JsonPropertyName("timestampType")]
    public string TimestampType { get; set; } = "WORD";
}

public class AudioConfig
{
    [JsonPropertyName("audioEncoding")]
    public string AudioEncoding { get; set; } = "MP3";

    [JsonPropertyName("sampleRateHertz")]
    public int SampleRateHertz { get; set; } = 24000;

    [JsonPropertyName("speakingRate")]
    public double SpeakingRate { get; set; } = 1.0;
}

public class SynthesizeSpeechResponse
{
    [JsonPropertyName("audioContent")]
    public string AudioContent { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }

    [JsonPropertyName("timestampInfo")]
    public TimestampInfo? TimestampInfo { get; set; }
}

public class TimestampInfo
{
    [JsonPropertyName("wordAlignment")]
    public WordAlignment? WordAlignment { get; set; }
}

public class WordAlignment
{
    [JsonPropertyName("words")]
    public List<string> Words { get; set; } = [];

    [JsonPropertyName("wordStartTimeSeconds")]
    public List<double> WordStartTimeSeconds { get; set; } = [];

    [JsonPropertyName("wordEndTimeSeconds")]
    public List<double> WordEndTimeSeconds { get; set; } = [];
}

public class UsageInfo
{
    [JsonPropertyName("processedCharactersCount")]
    public int ProcessedCharactersCount { get; set; }

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;
}

public class InworldApiError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class CloneVoiceRequest
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("langCode")]
    public string LangCode { get; set; } = "EN_US";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("voiceSamples")]
    public List<VoiceSample> VoiceSamples { get; set; } = [];

    [JsonPropertyName("audioProcessingConfig")]
    public AudioProcessingConfig? AudioProcessingConfig { get; set; }
}

public class VoiceSample
{
    [JsonPropertyName("audioData")]
    public string AudioData { get; set; } = string.Empty;

    [JsonPropertyName("transcription")]
    public string? Transcription { get; set; }
}

public class AudioProcessingConfig
{
    [JsonPropertyName("removeBackgroundNoise")]
    public bool RemoveBackgroundNoise { get; set; }
}

// ── Voice Design ──────────────────────────────────────────────────────────

public class DesignVoiceRequest
{
    [JsonPropertyName("langCode")]
    public string LangCode { get; set; } = "EN_US";

    [JsonPropertyName("designPrompt")]
    public string DesignPrompt { get; set; } = string.Empty;

    [JsonPropertyName("previewText")]
    public string PreviewText { get; set; } = string.Empty;

    [JsonPropertyName("voiceDesignConfig")]
    public VoiceDesignConfig? VoiceDesignConfig { get; set; }
}

public class VoiceDesignConfig
{
    [JsonPropertyName("numberOfSamples")]
    public int NumberOfSamples { get; set; } = 3;
}

public class DesignVoiceResponse
{
    [JsonPropertyName("langCode")]
    public string LangCode { get; set; } = string.Empty;

    [JsonPropertyName("previewVoices")]
    public List<PreviewVoice> PreviewVoices { get; set; } = [];
}

public class PreviewVoice
{
    [JsonPropertyName("voiceId")]
    public string VoiceId { get; set; } = string.Empty;

    [JsonPropertyName("previewText")]
    public string PreviewText { get; set; } = string.Empty;

    [JsonPropertyName("previewAudio")]
    public string PreviewAudio { get; set; } = string.Empty; // base64 MP3
}

public class PublishVoiceRequest
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}
