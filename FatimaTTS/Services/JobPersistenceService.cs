using System.IO;
using System.Text.Json;
using FatimaTTS.Models;

namespace FatimaTTS.Services;

/// <summary>
/// Persists TtsJob state to disk so that interrupted jobs can be resumed.
/// Each job gets its own directory:
///   %AppData%\FatimaTTS\jobs\{jobId}\
///     job.json          — full job metadata + chunk statuses
///     chunk_0.mp3       — audio for completed chunk 0
///     chunk_1.mp3       — audio for completed chunk 1
///     …
///     output.mp3        — final merged file (when complete)
/// </summary>
public class JobPersistenceService
{
    private static readonly string JobsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FatimaTTS", "jobs");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ── Job directory helpers ─────────────────────────────────────────────

    public string GetJobDir(string jobId) => Path.Combine(JobsRoot, jobId);

    public string GetChunkFilePath(string jobId, int chunkIndex, string encoding)
    {
        var ext = AppSettings.AudioExtensions.GetValueOrDefault(encoding, "mp3");
        return Path.Combine(GetJobDir(jobId), $"chunk_{chunkIndex}.{ext}");
    }

    public string GetOutputFilePath(string jobId, string encoding, string? title = null)
    {
        var ext  = AppSettings.AudioExtensions.GetValueOrDefault(encoding, "mp3");
        var name = string.IsNullOrWhiteSpace(title)
            ? $"fatima_tts_{jobId[..8]}.{ext}"
            : $"{Sanitize(title)}.{ext}";
        return Path.Combine(GetJobDir(jobId), name);
    }

    // ── Persist / Load ────────────────────────────────────────────────────

    public void SaveJob(TtsJob job)
    {
        var dir = GetJobDir(job.Id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(job, JsonOpts);
        File.WriteAllText(Path.Combine(dir, "job.json"), json);
    }

    public TtsJob? LoadJob(string jobId)
    {
        var path = Path.Combine(GetJobDir(jobId), "job.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TtsJob>(json, JsonOpts);
        }
        catch { return null; }
    }

    public List<TtsJob> LoadAllJobs()
    {
        if (!Directory.Exists(JobsRoot)) return [];

        var jobs = new List<TtsJob>();
        foreach (var dir in Directory.GetDirectories(JobsRoot))
        {
            var jobFile = Path.Combine(dir, "job.json");
            if (!File.Exists(jobFile)) continue;
            try
            {
                var json = File.ReadAllText(jobFile);
                var job  = JsonSerializer.Deserialize<TtsJob>(json, JsonOpts);
                if (job is not null) jobs.Add(job);
            }
            catch { /* skip corrupted job */ }
        }

        return [.. jobs.OrderByDescending(j => j.CreatedAt)];
    }

    public void DeleteJob(string jobId)
    {
        var dir = GetJobDir(jobId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Copies the merged output file to the user's chosen output folder.
    /// Returns the destination path.
    /// </summary>
    public string? ExportToOutputFolder(TtsJob job, string outputFolder)
    {
        if (job.OutputFilePath is null || !File.Exists(job.OutputFilePath))
            return null;

        Directory.CreateDirectory(outputFolder);
        var dest = Path.Combine(outputFolder, job.OutputFileName ?? Path.GetFileName(job.OutputFilePath));

        // Avoid overwriting — append index if file exists
        if (File.Exists(dest))
        {
            var name = Path.GetFileNameWithoutExtension(dest);
            var ext  = Path.GetExtension(dest);
            var i    = 1;
            while (File.Exists(dest))
            {
                dest = Path.Combine(outputFolder, $"{name}_{i++}{ext}");
            }
        }

        File.Copy(job.OutputFilePath, dest);
        return dest;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
               .Trim()
               .Replace(' ', '_');
    }
}
