using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace FatimaTTS.Services;

/// <summary>
/// Manages a bundled FFmpeg binary stored in %AppData%\FatimaTTS\ffmpeg\ffmpeg.exe
/// On first run (or when missing) downloads the latest FFmpeg Windows build from
/// https://github.com/BtbN/FFmpeg-Builds/releases
///
/// Call FindFfmpeg() to get the resolved path — checks the managed location first,
/// then falls back to PATH.
/// </summary>
public class FfmpegManager
{
    private readonly AppLogger  _log;
    private readonly HttpClient _http;

    private static readonly string ManagedDir  =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FatimaTTS", "ffmpeg");

    public static string ManagedExe => Path.Combine(ManagedDir, "ffmpeg.exe");

    // BtbN pre-built FFmpeg releases (GPL, static build, Windows x64)
    // These are stable "latest" URLs that always point to the most recent build
    private const string LatestApiUrl  =
        "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";
    private const string DirectZipUrl  =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public FfmpegManager(AppLogger log)
    {
        _log  = log;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FatimaTTS/1.0");
        _http.Timeout = TimeSpan.FromMinutes(5); // download can take a while
        Directory.CreateDirectory(ManagedDir);
    }

    // ── Find ffmpeg ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns path to ffmpeg.exe, checking managed location first, then PATH.
    /// </summary>
    public static string? FindFfmpeg()
    {
        // 1. Managed location (AppData)
        if (File.Exists(ManagedExe)) return ManagedExe;

        // 2. Same directory as the exe (manual placement)
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory);
        if (exeDir is not null)
        {
            var local = Path.Combine(exeDir, "ffmpeg.exe");
            if (File.Exists(local)) return local;
        }

        // 3. System PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static bool IsAvailable() => FindFfmpeg() is not null;

    // ── Version info ──────────────────────────────────────────────────────

    public static async Task<string?> GetVersionAsync()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return null;

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = ffmpeg,
                    Arguments              = "-version",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                }
            };
            proc.Start();
            var line = await proc.StandardOutput.ReadLineAsync();
            await proc.WaitForExitAsync();
            // "ffmpeg version 7.1 Copyright ..." → "7.1"
            if (line is not null && line.StartsWith("ffmpeg version "))
            {
                var parts = line.Split(' ');
                return parts.Length > 2 ? parts[2] : line;
            }
            return line;
        }
        catch { return null; }
    }

    // ── Download / update ─────────────────────────────────────────────────

    /// <summary>
    /// Downloads the latest FFmpeg Windows x64 static build from BtbN/FFmpeg-Builds.
    /// Reports progress 0–100 via <paramref name="progress"/>.
    /// </summary>
    public async Task<bool> DownloadLatestAsync(
        IProgress<(int Percent, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // Use the stable direct URL — always points to latest win64-gpl build
            // ffmpeg-master-latest-win64-gpl.zip contains ffmpeg.exe, ffprobe.exe, ffplay.exe
            var downloadUrl = DirectZipUrl;

            _log.Info($"Downloading FFmpeg from: {downloadUrl}");
            progress?.Report((5, "Starting FFmpeg download…"));

            var zipPath = Path.Combine(ManagedDir, "ffmpeg_download.zip");

            // Stream download with progress
            using (var response = await _http.GetAsync(downloadUrl,
                HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total  = response.Content.Headers.ContentLength ?? 80_000_000L;
                var buffer = new byte[81920];
                long downloaded = 0;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var file   = File.Create(zipPath);

                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    var pct = (int)(5 + downloaded * 80 / total);
                    progress?.Report((Math.Min(85, pct),
                        $"Downloading… {downloaded / 1_048_576}MB / {total / 1_048_576}MB"));
                }
            }

            progress?.Report((87, "Extracting ffmpeg.exe…"));
            _log.Info("Extracting ffmpeg.exe from zip");

            // Extract ffmpeg.exe — it lives in a bin/ subfolder inside the zip
            string tmpExe = ManagedExe + ".tmp";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.Contains("bin", StringComparison.OrdinalIgnoreCase));

                // Fallback: any ffmpeg.exe in the zip
                entry ??= zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    _log.Warn($"ffmpeg.exe not found in zip. Entries: " +
                              string.Join(", ", zip.Entries.Take(10).Select(e => e.FullName)));
                    progress?.Report((0, "ffmpeg.exe not found in downloaded zip"));
                    return false;
                }

                entry.ExtractToFile(tmpExe, overwrite: true);
            } // zip is fully closed here before we delete the file

            // Atomic replace
            if (File.Exists(ManagedExe)) File.Delete(ManagedExe);
            File.Move(tmpExe, ManagedExe);

            // Now safe to delete the zip
            File.Delete(zipPath);

            var version = await GetVersionAsync();
            _log.Info($"FFmpeg installed successfully: {version}");
            progress?.Report((100, $"FFmpeg ready — {version}"));
            return true;
        }
        catch (OperationCanceledException)
        {
            progress?.Report((0, "Download cancelled"));
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("FFmpeg download failed", ex);
            progress?.Report((0, $"Download failed: {ex.Message}"));
            return false;
        }
    }

    // ── Check if update needed ────────────────────────────────────────────

    public async Task<bool> IsUpdateAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            if (!IsAvailable()) return true; // not installed = needs download

            // Check if a newer build exists by comparing the zip's last-modified header
            // vs the installed exe's write time — simpler than parsing release JSON
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Head, DirectZipUrl);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;

            var remoteDate  = resp.Content.Headers.LastModified?.UtcDateTime ?? DateTime.MinValue;
            var installedDate = File.GetLastWriteTimeUtc(ManagedExe);

            _log.Info($"FFmpeg: installed={installedDate:u} remote={remoteDate:u}");
            return remoteDate > installedDate.AddDays(1); // 1-day buffer to avoid noise
        }
        catch { return false; }
    }
}
