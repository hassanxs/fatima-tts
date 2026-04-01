using System.Diagnostics;
using System.IO;

namespace FatimaTTS.Services;

/// <summary>
/// Uses FFmpeg to concatenate multiple audio files into one seamless output.
/// FFmpeg handles proper frame-accurate merging for MP3/AAC/WAV/OGG without
/// the glitches that binary concatenation can produce between files.
///
/// Requires ffmpeg.exe to be:
///   a) In the same folder as the app, OR
///   b) On the system PATH
/// </summary>
public class FfmpegMergeService
{
    // ── FFmpeg detection ──────────────────────────────────────────────────

    public static string? FindFfmpeg() => FfmpegManager.FindFfmpeg();
    public static bool IsAvailable()   => FfmpegManager.IsAvailable();

    // ── Merge ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges the given audio files into outputPath using FFmpeg concat demuxer.
    /// Reports progress via the progress callback (0–100).
    /// Throws if FFmpeg is not found or exits with an error.
    /// </summary>
    public async Task MergeAsync(
        IReadOnlyList<string> inputFiles,
        string outputPath,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg()
            ?? throw new InvalidOperationException(
                "ffmpeg.exe not found. Place ffmpeg.exe in the same folder as FatimaTTS.exe, " +
                "or install FFmpeg and add it to your PATH.");

        if (inputFiles.Count == 0)
            throw new ArgumentException("No input files provided.", nameof(inputFiles));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);

        // Write a temporary concat list file
        var listFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"fatima_concat_{Guid.NewGuid():N}.txt");

        try
        {
            // FFmpeg concat list format: each line is  file 'path/to/file.mp3'
            // Paths must use forward slashes and be absolute
            var lines = inputFiles.Select(f =>
                $"file '{f.Replace("\\", "/").Replace("'", "\\'")}'");
            await File.WriteAllLinesAsync(listFile, lines, ct);

            // Build FFmpeg args:
            //   -f concat        use concat demuxer
            //   -safe 0          allow absolute paths
            //   -i <listFile>    input list
            //   -c copy          stream copy (no re-encode — fast, lossless quality)
            //   -y               overwrite output without asking
            var args = $"-f concat -safe 0 -i \"{listFile}\" -c copy -y \"{outputPath}\"";

            progress?.Report(10);

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = ffmpeg,
                    Arguments              = args,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                }
            };

            var stderr = new System.Text.StringBuilder();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            proc.Start();
            proc.BeginErrorReadLine();

            progress?.Report(30);

            await proc.WaitForExitAsync(ct);

            progress?.Report(95);

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg failed (exit {proc.ExitCode}):\n{stderr}");
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("FFmpeg completed but output file is empty.");

            progress?.Report(100);
        }
        finally
        {
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the FFmpeg version string, or null if FFmpeg is not available.
    /// </summary>
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
            var output = await proc.StandardOutput.ReadLineAsync();
            await proc.WaitForExitAsync();
            return output; // e.g. "ffmpeg version 6.1.1 ..."
        }
        catch
        {
            return null;
        }
    }
}
