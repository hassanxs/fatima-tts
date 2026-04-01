using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FatimaTTS.Services;

/// <summary>
/// Checks GitHub Releases API for newer versions of Fatima TTS.
/// Repository: https://github.com/YOUR_USERNAME/fatima-tts
/// </summary>
public class GitHubUpdateService
{
    // ── Config — update these before publishing ───────────────────────────
    public const string GitHubOwner    = "YOUR_GITHUB_USERNAME";
    public const string GitHubRepo     = "fatima-tts";
    public const string CurrentVersion = "1.0.0";
    public static readonly string ReleasesUrl =
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";
    public static readonly string LatestApiUrl =
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    // ─────────────────────────────────────────────────────────────────────

    private readonly HttpClient  _http;
    private readonly AppLogger   _log;

    public GitHubUpdateService(AppLogger log)
    {
        _log  = log;
        _http = new HttpClient();
        // GitHub API requires a User-Agent header
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"FatimaTTS/{CurrentVersion}");
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Checks GitHub for a newer release.
    /// Returns null if already up to date or check fails.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            _log.Info($"Checking for updates (current: v{CurrentVersion})");

            var json     = await _http.GetStringAsync(LatestApiUrl, ct);
            var release  = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                _log.Warn("No release found on GitHub");
                return null;
            }

            var latestVersion = release.TagName.TrimStart('v');
            _log.Info($"Latest GitHub release: v{latestVersion}");

            if (!IsNewer(latestVersion, CurrentVersion))
                return null;

            // Find the .exe asset
            var exeAsset = release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            return new UpdateInfo
            {
                Version      = latestVersion,
                ReleaseNotes = release.Body ?? "",
                PublishedAt  = release.PublishedAt,
                DownloadUrl  = exeAsset?.BrowserDownloadUrl ?? ReleasesUrl,
                ReleasesUrl  = ReleasesUrl,
                IsExeAvailable = exeAsset is not null
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _log.Error("Update check failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="latest"/> is a higher version than <paramref name="current"/>.
    /// Compares as semantic version (major.minor.patch).
    /// </summary>
    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest,  out var lv) &&
            Version.TryParse(current, out var cv))
            return lv > cv;

        // Fallback: string comparison
        return string.Compare(latest, current, StringComparison.Ordinal) > 0;
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────

public record UpdateInfo
{
    public string   Version       { get; init; } = "";
    public string   ReleaseNotes  { get; init; } = "";
    public DateTime PublishedAt   { get; init; }
    public string   DownloadUrl   { get; init; } = "";
    public string   ReleasesUrl   { get; init; } = "";
    public bool     IsExeAvailable { get; init; }
}

file class GitHubRelease
{
    [JsonPropertyName("tag_name")]    public string?  TagName     { get; set; }
    [JsonPropertyName("body")]        public string?  Body        { get; set; }
    [JsonPropertyName("published_at")] public DateTime PublishedAt { get; set; }
    [JsonPropertyName("assets")]      public List<GitHubAsset>? Assets { get; set; }
}

file class GitHubAsset
{
    [JsonPropertyName("name")]                 public string Name                { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
}
