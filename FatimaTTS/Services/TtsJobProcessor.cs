using System.IO;
using FatimaTTS.Models;

namespace FatimaTTS.Services;

/// <summary>
/// Orchestrates the full TTS job lifecycle:
///   1. Chunk text
///   2. Synthesize chunks concurrently (bounded by MaxParallelChunks), with per-chunk retry
///   3. Merge audio
///   4. Persist state after every chunk — enabling resume on failure
///
/// Chunk synthesis is parallel: each chunk stores CHUNK-RELATIVE timestamps plus its own
/// duration, so the cumulative timeline offset is applied later (at SRT export) rather than
/// threaded sequentially through synthesis. This keeps the resume invariant intact —
/// completed chunks are skipped and state is durable after every chunk — while removing the
/// former one-at-a-time bottleneck.
/// </summary>
public class TtsJobProcessor
{
    private readonly InworldTtsService     _tts;
    private readonly ChunkingEngine        _chunker;
    private readonly AudioMergeService     _merger;
    private readonly JobPersistenceService _persistence;
    private readonly SettingsService       _settings;
    private readonly AppLogger             _log;

    // Serializes writes to job.json and to shared job counters across parallel chunk tasks.
    private readonly object _saveLock = new();

    // Progress reporting: (chunksDone, totalChunks, currentChunkIndex, message)
    public event Action<int, int, int, string>? ProgressChanged;

    // Called after every chunk completes — UI can update the chunk list
    public event Action<TtsChunk>? ChunkCompleted;

    // Called when a chunk fails (before retry)
    public event Action<TtsChunk, string>? ChunkFailed;

    public TtsJobProcessor(
        InworldTtsService tts,
        ChunkingEngine chunker,
        AudioMergeService merger,
        JobPersistenceService persistence,
        SettingsService settings,
        AppLogger log)
    {
        _tts         = tts;
        _chunker     = chunker;
        _merger      = merger;
        _persistence = persistence;
        _settings    = settings;
        _log         = log;
    }

    /// <summary>
    /// Runs the complete job. Throws on unrecoverable failure.
    /// Intermediate state is saved to disk after every chunk.
    /// </summary>
    public async Task ProcessJobAsync(
        TtsJob job,
        string apiKey,
        CancellationToken ct = default)
    {
        _log.Info($"Job started: \"{job.DisplayTitle}\" ({job.CharacterCount:N0} chars, {job.ModelId})");
        job.Status = JobStatus.Chunking;
        SaveJob(job);

        // ── Step 1: Chunk text (only if not resuming) ─────────────────────
        if (job.Chunks.Count == 0)
        {
            var chunks = _chunker.ChunkText(job.InputText);
            job.ChunkCount = chunks.Count;

            for (int i = 0; i < chunks.Count; i++)
            {
                job.Chunks.Add(new TtsChunk
                {
                    ChunkIndex     = i,
                    Text           = chunks[i],
                    CharacterCount = chunks[i].Length,
                    Status         = ChunkStatus.Pending
                });
            }
            SaveJob(job);
        }

        // ── Step 2: Synthesize pending chunks concurrently ────────────────
        job.Status = JobStatus.Fetching;
        SaveJob(job);

        int totalChunks = job.Chunks.Count;
        var pending     = job.Chunks
            .Where(c => c.Status != ChunkStatus.Completed)
            .OrderBy(c => c.ChunkIndex)
            .ToList();

        int baseDone     = totalChunks - pending.Count;
        int completedNow = 0;

        // Report already-done chunks (resume) up front.
        foreach (var c in job.Chunks.Where(c => c.Status == ChunkStatus.Completed))
            ProgressChanged?.Invoke(baseDone, totalChunks, c.ChunkIndex,
                $"Chunk {c.ChunkIndex + 1} already done — resuming");

        int maxParallel = Math.Clamp(_settings.Load().MaxParallelChunks, 1, 8);
        using var sem   = new SemaphoreSlim(maxParallel);

        var tasks = pending.Select(async chunk =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                lock (_saveLock)
                {
                    chunk.Status = ChunkStatus.Processing;
                    _persistence.SaveJob(job);
                }

                ProgressChanged?.Invoke(baseDone + completedNow, totalChunks, chunk.ChunkIndex,
                    $"Fetching chunk {chunk.ChunkIndex + 1} of {totalChunks}…");

                await SynthesizeChunkWithRetryAsync(job, chunk, apiKey, ct);

                int done;
                lock (_saveLock)
                {
                    completedNow++;
                    done = baseDone + completedNow;
                    job.Progress = (int)Math.Round((double)done / totalChunks * 90);
                    _persistence.SaveJob(job);
                }

                ChunkCompleted?.Invoke(chunk);
                ProgressChanged?.Invoke(done, totalChunks, chunk.ChunkIndex,
                    $"Chunk {chunk.ChunkIndex + 1} of {totalChunks} complete");
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // A chunk task set job.Status (Failed/Interrupted) already; persist and surface.
            SaveJob(job);
            throw;
        }

        // ── Step 3: Merge audio ───────────────────────────────────────────
        ProgressChanged?.Invoke(totalChunks, totalChunks, -1, "Merging audio…");
        RebuildOutput(job);

        // ── Step 4: Finalise job ──────────────────────────────────────────
        job.Status      = JobStatus.Completed;
        job.Progress    = 100;
        job.CompletedAt = DateTime.Now;
        SaveJob(job);

        _log.Info($"Job completed: \"{job.DisplayTitle}\" — {job.FormattedFileSize}, {job.CharactersBilled:N0} chars billed");
        ProgressChanged?.Invoke(totalChunks, totalChunks, -1, "Done!");
    }

    /// <summary>
    /// Re-synthesizes a single already-existing chunk (e.g. the user didn't like the result)
    /// and rebuilds the merged output. The job must already have this chunk. Throws on failure.
    /// </summary>
    public async Task RegenerateChunkAsync(
        TtsJob job, int chunkIndex, string apiKey, CancellationToken ct = default)
    {
        var chunk = job.Chunks.FirstOrDefault(c => c.ChunkIndex == chunkIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(chunkIndex), "Chunk not found in job.");

        _log.Info($"Regenerating chunk {chunkIndex + 1} of \"{job.DisplayTitle}\"");

        chunk.Status       = ChunkStatus.Processing;
        chunk.ErrorMessage = null;
        SaveJob(job);
        ProgressChanged?.Invoke(0, 1, chunkIndex, $"Regenerating chunk {chunkIndex + 1}…");

        await SynthesizeChunkWithRetryAsync(job, chunk, apiKey, ct);
        SaveJob(job);
        ChunkCompleted?.Invoke(chunk);

        // Re-merge with the new chunk audio.
        ProgressChanged?.Invoke(1, 1, -1, "Merging audio…");
        RebuildOutput(job);
        if (job.Status != JobStatus.Completed && job.Chunks.All(c => c.Status == ChunkStatus.Completed))
            job.Status = JobStatus.Completed;
        SaveJob(job);
        ProgressChanged?.Invoke(1, 1, -1, "Done!");
    }

    // ── Merge + output metadata (shared by full run and regenerate) ───────

    private void RebuildOutput(TtsJob job)
    {
        var chunkPaths = job.Chunks
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.AudioFilePath!)
            .ToList();

        var outputPath = _persistence.GetOutputFilePath(job.Id, job.AudioEncoding, job.Title);

        if (chunkPaths.Count == 1)
            File.Copy(chunkPaths[0], outputPath, overwrite: true);
        else
            _merger.MergeChunks(chunkPaths, outputPath, job.AudioEncoding);

        var fileInfo         = new FileInfo(outputPath);
        job.OutputFilePath   = outputPath;
        job.OutputFileName   = fileInfo.Name;
        job.OutputFileSize   = fileInfo.Length;
        job.CharactersBilled = job.Chunks.Sum(c => c.ApiProcessedChars);

        if (job.AudioEncoding == "LINEAR16")
            job.AudioDurationSeconds = AudioMergeService.GetWavDurationSeconds(outputPath);
    }

    // ── Per-chunk synthesis with retry ────────────────────────────────────

    private async Task SynthesizeChunkWithRetryAsync(
        TtsJob job, TtsChunk chunk, string apiKey, CancellationToken ct)
    {
        int attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                var (audioBytes, processedChars, timestamps) = await _tts.SynthesizeAsync(
                    apiKey,
                    chunk.Text,
                    job.VoiceId,
                    job.ModelId,
                    job.AudioEncoding,
                    job.Temperature,
                    job.SpeakingRate,
                    job.Language,
                    job.DeliveryMode,
                    job.TimestampType,
                    job.ApplyTextNormalization == "ON",
                    ct);

                // Save chunk audio to disk immediately
                var chunkPath = _persistence.GetChunkFilePath(job.Id, chunk.ChunkIndex, job.AudioEncoding);
                AudioMergeService.SaveChunk(audioBytes, chunkPath);

                chunk.AudioFilePath      = chunkPath;
                chunk.AudioFileSize      = audioBytes.Length;
                chunk.ApiProcessedChars  = processedChars;
                chunk.RetryCount         = attempt - 1;
                chunk.ErrorMessage       = null;

                // Store CHUNK-RELATIVE timestamps; offsets are applied at SRT export time.
                chunk.Words = []; chunk.WordStartTimes = []; chunk.WordEndTimes = [];
                chunk.Characters = []; chunk.CharStartTimes = []; chunk.CharEndTimes = [];

                if (timestamps?.WordAlignment is { Words.Count: > 0 } wa)
                {
                    chunk.Words          = wa.Words;
                    chunk.WordStartTimes = wa.WordStartTimeSeconds;
                    chunk.WordEndTimes   = wa.WordEndTimeSeconds;
                }
                if (timestamps?.CharacterAlignment is { Characters.Count: > 0 } ca)
                {
                    chunk.Characters     = ca.Characters;
                    chunk.CharStartTimes = ca.CharacterStartTimeSeconds;
                    chunk.CharEndTimes   = ca.CharacterEndTimeSeconds;
                }

                chunk.TimestampsAreRelative = true;
                chunk.DurationSeconds       = ComputeSpanSeconds(chunk);
                chunk.Status                = ChunkStatus.Completed;
                chunk.CompletedAt           = DateTime.Now;
                return;
            }
            catch (OperationCanceledException)
            {
                lock (_saveLock)
                {
                    chunk.Status       = ChunkStatus.Failed;
                    chunk.ErrorMessage = "Cancelled by user";
                    job.Status         = JobStatus.Interrupted;
                    _persistence.SaveJob(job);
                }
                throw;
            }
            catch (InworldApiException ex) when (ex.IsRetryable && attempt <= TtsChunk.MaxRetries)
            {
                // Retryable server/rate-limit error (incl. 429 from parallel overflow) — back off then retry.
                ChunkFailed?.Invoke(chunk, $"Attempt {attempt} failed ({ex.HttpStatusCode}), retrying…");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); // 2s, 4s
            }
            catch (InworldApiException ex) when (ex.IsAuthError)
            {
                lock (_saveLock)
                {
                    chunk.Status       = ChunkStatus.Failed;
                    chunk.ErrorMessage = ex.Message;
                    job.Status         = JobStatus.Failed;
                    job.ErrorMessage   = ex.Message;
                    _persistence.SaveJob(job);
                }
                throw;
            }
            catch (Exception ex)
            {
                if (attempt > TtsChunk.MaxRetries)
                {
                    lock (_saveLock)
                    {
                        chunk.Status       = ChunkStatus.Failed;
                        chunk.ErrorMessage = ex.Message;
                        job.Status         = JobStatus.Failed;
                        job.ErrorMessage   = $"Chunk {chunk.ChunkIndex + 1} failed: {ex.Message}";
                        _persistence.SaveJob(job);
                    }
                    throw;
                }

                ChunkFailed?.Invoke(chunk, $"Attempt {attempt} failed, retrying…");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    // The chunk's own timeline span, from whichever alignment is present.
    private static double ComputeSpanSeconds(TtsChunk chunk)
    {
        double span = 0;
        if (chunk.WordEndTimes.Count > 0) span = Math.Max(span, chunk.WordEndTimes.Max());
        if (chunk.CharEndTimes.Count > 0) span = Math.Max(span, chunk.CharEndTimes.Max());
        return span;
    }

    // Thread-safe wrapper for persistence.
    private void SaveJob(TtsJob job)
    {
        lock (_saveLock) _persistence.SaveJob(job);
    }
}
