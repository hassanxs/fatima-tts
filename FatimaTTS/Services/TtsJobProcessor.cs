using System.IO;
using FatimaTTS.Models;

namespace FatimaTTS.Services;

/// <summary>
/// Orchestrates the full TTS job lifecycle:
///   1. Chunk text
///   2. Pre-fetch all chunks (async, with per-chunk retry)
///   3. Merge audio
///   4. Persist state after every chunk — enabling resume on failure
///
/// This directly replaces PHP's processJob() which failed silently on timeout.
/// C# async/await has no web-server timeout ceiling.
/// </summary>
public class TtsJobProcessor
{
    private readonly InworldTtsService     _tts;
    private readonly ChunkingEngine        _chunker;
    private readonly AudioMergeService     _merger;
    private readonly JobPersistenceService _persistence;
    private readonly AppLogger             _log;

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
        AppLogger log)
    {
        _tts         = tts;
        _chunker     = chunker;
        _merger      = merger;
        _persistence = persistence;
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
        _persistence.SaveJob(job);

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
            _persistence.SaveJob(job);
        }

        // ── Step 2: Synthesize each chunk ─────────────────────────────────
        job.Status = JobStatus.Fetching;
        _persistence.SaveJob(job);

        int totalChunks = job.Chunks.Count;
        int doneCount   = job.Chunks.Count(c => c.Status == ChunkStatus.Completed);
        double timeOffset = job.Chunks
            .Where(c => c.Status == ChunkStatus.Completed)
            .Sum(c => c.WordEndTimes.Count > 0 ? c.WordEndTimes.Max() : 0);

        foreach (var chunk in job.Chunks.OrderBy(c => c.ChunkIndex))
        {
            ct.ThrowIfCancellationRequested();

            // Skip already-completed chunks (resume support)
            if (chunk.Status == ChunkStatus.Completed)
            {
                ProgressChanged?.Invoke(doneCount, totalChunks, chunk.ChunkIndex,
                    $"Chunk {chunk.ChunkIndex + 1} already done — resuming");
                continue;
            }

            chunk.Status = ChunkStatus.Processing;
            _persistence.SaveJob(job);

            ProgressChanged?.Invoke(doneCount, totalChunks, chunk.ChunkIndex,
                $"Fetching chunk {chunk.ChunkIndex + 1} of {totalChunks}…");

            var timestamps = await SynthesizeChunkWithRetryAsync(job, chunk, apiKey, timeOffset, ct);

            // Advance cumulative time offset for next chunk
            if (chunk.WordEndTimes.Count > 0)
                timeOffset = chunk.WordEndTimes.Max();

            doneCount++;
            job.Progress = (int)Math.Round((double)doneCount / totalChunks * 90);
            _persistence.SaveJob(job);

            ChunkCompleted?.Invoke(chunk);
            ProgressChanged?.Invoke(doneCount, totalChunks, chunk.ChunkIndex,
                $"Chunk {chunk.ChunkIndex + 1} of {totalChunks} complete");
        }

        // ── Step 3: Merge audio ───────────────────────────────────────────
        ProgressChanged?.Invoke(totalChunks, totalChunks, -1, "Merging audio…");

        var chunkPaths = job.Chunks
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.AudioFilePath!)
            .ToList();

        var outputPath = _persistence.GetOutputFilePath(job.Id, job.AudioEncoding, job.Title);

        if (chunkPaths.Count == 1)
        {
            // Single chunk — just copy it
            File.Copy(chunkPaths[0], outputPath, overwrite: true);
        }
        else
        {
            _merger.MergeChunks(chunkPaths, outputPath, job.AudioEncoding);
        }

        // ── Step 4: Finalise job ──────────────────────────────────────────
        var fileInfo            = new FileInfo(outputPath);
        job.OutputFilePath      = outputPath;
        job.OutputFileName      = fileInfo.Name;
        job.OutputFileSize      = fileInfo.Length;
        job.Status              = JobStatus.Completed;
        job.Progress            = 100;
        job.CompletedAt         = DateTime.Now;
        job.CharactersBilled    = job.Chunks.Sum(c => c.ApiProcessedChars);

        if (job.AudioEncoding == "LINEAR16")
            job.AudioDurationSeconds = AudioMergeService.GetWavDurationSeconds(outputPath);

        _persistence.SaveJob(job);

        _log.Info($"Job completed: \"{job.DisplayTitle}\" — {job.FormattedFileSize}, {job.CharactersBilled:N0} chars billed");
        ProgressChanged?.Invoke(totalChunks, totalChunks, -1, "Done!");
    }

    // ── Per-chunk synthesis with retry ────────────────────────────────────

    private async Task<WordAlignment?> SynthesizeChunkWithRetryAsync(
        TtsJob job, TtsChunk chunk, string apiKey, double timeOffset, CancellationToken ct)
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
                    ct);

                // Save chunk audio to disk immediately
                var chunkPath = _persistence.GetChunkFilePath(job.Id, chunk.ChunkIndex, job.AudioEncoding);
                AudioMergeService.SaveChunk(audioBytes, chunkPath);

                chunk.AudioFilePath      = chunkPath;
                chunk.AudioFileSize      = audioBytes.Length;
                chunk.ApiProcessedChars  = processedChars;
                chunk.Status             = ChunkStatus.Completed;
                chunk.CompletedAt        = DateTime.Now;
                chunk.RetryCount         = attempt - 1;
                chunk.ErrorMessage       = null;
                chunk.ChunkTimeOffset    = timeOffset;

                // Store word timestamps with absolute offset applied
                if (timestamps is not null)
                {
                    chunk.Words          = timestamps.Words;
                    chunk.WordStartTimes = timestamps.WordStartTimeSeconds.Select(t => t + timeOffset).ToList();
                    chunk.WordEndTimes   = timestamps.WordEndTimeSeconds.Select(t => t + timeOffset).ToList();
                }

                return timestamps;
            }
            catch (OperationCanceledException)
            {
                chunk.Status       = ChunkStatus.Failed;
                chunk.ErrorMessage = "Cancelled by user";
                job.Status         = JobStatus.Interrupted;
                _persistence.SaveJob(job);
                throw;
            }
            catch (InworldApiException ex) when (ex.IsRetryable && attempt <= TtsChunk.MaxRetries)
            {
                // Retryable server/rate-limit error — wait then retry
                ChunkFailed?.Invoke(chunk, $"Attempt {attempt} failed ({ex.HttpStatusCode}), retrying…");
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s
                await Task.Delay(delay, ct);
            }
            catch (InworldApiException ex) when (ex.IsAuthError)
            {
                // Auth errors are never retryable
                chunk.Status       = ChunkStatus.Failed;
                chunk.ErrorMessage = ex.Message;
                job.Status         = JobStatus.Failed;
                job.ErrorMessage   = ex.Message;
                _persistence.SaveJob(job);
                throw;
            }
            catch (Exception ex)
            {
                if (attempt > TtsChunk.MaxRetries)
                {
                    chunk.Status       = ChunkStatus.Failed;
                    chunk.ErrorMessage = ex.Message;
                    job.Status         = JobStatus.Failed;
                    job.ErrorMessage   = $"Chunk {chunk.ChunkIndex + 1} failed: {ex.Message}";
                    _persistence.SaveJob(job);
                    throw;
                }

                ChunkFailed?.Invoke(chunk, $"Attempt {attempt} failed, retrying…");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }
}
