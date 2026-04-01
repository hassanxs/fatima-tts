using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FatimaTTS.Models;

namespace FatimaTTS.Services;

public class InworldTtsService
{
    private const string BaseUrl = "https://api.inworld.ai";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public InworldTtsService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Voice listing ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns ALL voices (system + cloned).
    /// Uses /voices/v1/voices — NOT /tts/v1/voices which returns system only.
    /// </summary>
    public async Task<List<InworldVoice>> ListWorkspaceVoicesAsync(
        string apiKey, CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Get, "/voices/v1/voices", apiKey);
        var response = await SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<ListVoicesResponse>(body, JsonOpts);
        return result?.Voices ?? [];
    }

    /// <summary>
    /// Returns system voices only via /tts/v1/voices (used for API key validation).
    /// </summary>
    public async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        try
        {
            var request = BuildRequest(HttpMethod.Get, "/tts/v1/voices?filter=language=en", apiKey);
            var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Speech synthesis ─────────────────────────────────────────────────

    /// <summary>
    /// Synthesizes a single text chunk (≤ 2000 chars).
    /// Returns the raw audio bytes decoded from base64.
    /// Throws InworldApiException on API errors with retry guidance.
    /// </summary>
    public async Task<(byte[] AudioBytes, int ProcessedChars, WordAlignment? Timestamps)> SynthesizeAsync(
        string apiKey,
        string text,
        string voiceId,
        string modelId,
        string audioEncoding,
        double temperature,
        double speakingRate,
        CancellationToken ct = default)
    {
        var payload = new SynthesizeSpeechRequest
        {
            Text    = text,
            VoiceId = voiceId,
            ModelId = modelId,
            Temperature = temperature,
            AudioConfig = new AudioConfig
            {
                AudioEncoding  = audioEncoding,
                SampleRateHertz = 24000,
                SpeakingRate   = speakingRate
            }
        };

        var json    = JsonSerializer.Serialize(payload, JsonOpts);
        var request = BuildRequest(HttpMethod.Post, "/tts/v1/voice", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await SendAsync(request, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);
        var result   = JsonSerializer.Deserialize<SynthesizeSpeechResponse>(body, JsonOpts)
            ?? throw new InworldApiException("Empty response from Inworld API", 500);

        var audioBytes     = Convert.FromBase64String(result.AudioContent);
        var processedChars = result.Usage?.ProcessedCharactersCount ?? text.Length;
        var timestamps     = result.TimestampInfo?.WordAlignment;
        return (audioBytes, processedChars, timestamps);
    }

    // ── Voice cloning ─────────────────────────────────────────────────────

    public async Task<InworldVoice> CloneVoiceAsync(
        string apiKey,
        string displayName,
        string langCode,
        string audioBase64,
        string? transcription,
        string? description,
        bool removeBackgroundNoise,
        CancellationToken ct = default)
    {
        var payload = new CloneVoiceRequest
        {
            DisplayName  = displayName,
            LangCode     = langCode,
            Description  = description,
            VoiceSamples = [new VoiceSample { AudioData = audioBase64, Transcription = transcription }],
            AudioProcessingConfig = removeBackgroundNoise
                ? new AudioProcessingConfig { RemoveBackgroundNoise = true }
                : null
        };

        var json    = JsonSerializer.Serialize(payload, JsonOpts);
        var request = BuildRequest(HttpMethod.Post, "/voices/v1/voices:clone", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await SendAsync(request, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        // API returns { "voice": {...}, "audioSamplesValidated": [...] }
        using var doc  = System.Text.Json.JsonDocument.Parse(body);
        var voiceElem  = doc.RootElement.TryGetProperty("voice", out var v) ? v : doc.RootElement;
        return JsonSerializer.Deserialize<InworldVoice>(voiceElem.GetRawText(), JsonOpts)
            ?? throw new InworldApiException("Invalid response from voice clone API", 500);
    }

    public async Task DeleteVoiceAsync(string apiKey, string voiceId, CancellationToken ct = default)
    {
        var request = BuildRequest(HttpMethod.Delete,
            $"/voices/v1/voices/{Uri.EscapeDataString(voiceId)}", apiKey);
        await SendAsync(request, ct);
    }

    // ── Voice Design ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates up to 3 preview voices from a text description.
    /// POST /voices/v1/voices:design
    /// </summary>
    public async Task<DesignVoiceResponse> DesignVoiceAsync(
        string apiKey,
        string langCode,
        string designPrompt,
        string previewText,
        int numberOfSamples = 3,
        CancellationToken ct = default)
    {
        var payload = new DesignVoiceRequest
        {
            LangCode      = langCode,
            DesignPrompt  = designPrompt,
            PreviewText   = previewText,
            VoiceDesignConfig = new VoiceDesignConfig { NumberOfSamples = numberOfSamples }
        };

        var json    = JsonSerializer.Serialize(payload, JsonOpts);
        var request = BuildRequest(HttpMethod.Post, "/voices/v1/voices:design", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await SendAsync(request, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DesignVoiceResponse>(body, JsonOpts)
            ?? throw new InworldApiException("Invalid response from voice design API", 500);
    }

    /// <summary>
    /// Publishes a draft preview voice to the voice library.
    /// POST /voices/v1/voices/{voiceId}:publish
    /// </summary>
    public async Task<InworldVoice> PublishVoiceAsync(
        string apiKey,
        string voiceId,
        string displayName,
        string? description = null,
        CancellationToken ct = default)
    {
        var payload = new PublishVoiceRequest
        {
            DisplayName = displayName,
            Description = description
        };

        var json    = JsonSerializer.Serialize(payload, JsonOpts);
        var request = BuildRequest(HttpMethod.Post,
            $"/voices/v1/voices/{Uri.EscapeDataString(voiceId)}:publish", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await SendAsync(request, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<InworldVoice>(body, JsonOpts)
            ?? throw new InworldApiException("Invalid response from voice publish API", 500);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        // Inworld uses Basic auth where the apiKey is the full base64 credential
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", apiKey);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
            return response;

        var body    = await response.Content.ReadAsStringAsync(ct);
        var status  = (int)response.StatusCode;
        string msg;

        try
        {
            var err = JsonSerializer.Deserialize<InworldApiError>(body, JsonOpts);
            msg = InworldApiException.FriendlyMessage(status, err?.Message ?? body);
        }
        catch
        {
            msg = InworldApiException.FriendlyMessage(status, body);
        }

        throw new InworldApiException(msg, status);
    }
}
