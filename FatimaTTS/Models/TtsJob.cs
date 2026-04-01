namespace FatimaTTS.Models;

public enum JobStatus
{
    Pending,
    Chunking,
    Fetching,
    Completed,
    Failed,
    Interrupted
}

public enum ChunkStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public class TtsJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? Title { get; set; }
    public string InputText { get; set; } = string.Empty;
    public int CharacterCount { get; set; }
    public int ChunkCount { get; set; }
    public string VoiceId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string AudioEncoding { get; set; } = "MP3";
    public string? BatchName { get; set; }  // set for jobs created by batch generation
    public double Temperature { get; set; } = 1.1;
    public double SpeakingRate { get; set; } = 1.0;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Progress { get; set; } = 0;
    public string? ErrorMessage { get; set; }
    public string? OutputFilePath { get; set; }
    public string? OutputFileName { get; set; }
    public long OutputFileSize { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public double? AudioDurationSeconds { get; set; }
    public int CharactersBilled { get; set; }
    public List<TtsChunk> Chunks { get; set; } = [];

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title)
        ? Title
        : InputText.Length > 60 ? InputText[..60] + "…" : InputText;

    public string StatusLabel => Status switch
    {
        JobStatus.Pending     => "Pending",
        JobStatus.Chunking    => "Chunking…",
        JobStatus.Fetching    => "Fetching…",
        JobStatus.Completed   => "Completed",
        JobStatus.Failed      => "Failed",
        JobStatus.Interrupted => "Interrupted",
        _                     => "Unknown"
    };

    public string FormattedFileSize => OutputFileSize switch
    {
        0               => "—",
        < 1024          => $"{OutputFileSize} B",
        < 1_048_576     => $"{OutputFileSize / 1024.0:F1} KB",
        _               => $"{OutputFileSize / 1_048_576.0:F2} MB"
    };

    public string FormattedDuration => AudioDurationSeconds is null ? "—"
        : AudioDurationSeconds < 60
            ? $"0:{(int)AudioDurationSeconds:D2}"
            : $"{(int)(AudioDurationSeconds / 60)}:{(int)(AudioDurationSeconds % 60):D2}";

    // Returns the first chunk that is not yet completed — used for resume
    public int ResumeFromChunkIndex =>
        Chunks.Where(c => c.Status != ChunkStatus.Completed)
              .OrderBy(c => c.ChunkIndex)
              .FirstOrDefault()?.ChunkIndex ?? 0;

    public bool CanResume => Status is JobStatus.Failed or JobStatus.Interrupted
        && Chunks.Any(c => c.Status == ChunkStatus.Completed);

    public bool CanRetry => Status is JobStatus.Failed or JobStatus.Interrupted;
}

public class TtsChunk
{
    public int ChunkIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public int CharacterCount { get; set; }
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;
    public string? AudioFilePath { get; set; }
    public long AudioFileSize { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; } = 0;
    public int ApiProcessedChars { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double ChunkTimeOffset { get; set; } = 0; // seconds offset from start of full audio
    public List<string> Words { get; set; } = [];
    public List<double> WordStartTimes { get; set; } = [];
    public List<double> WordEndTimes { get; set; } = [];

    public const int MaxRetries = 2;
    public bool CanRetry => RetryCount < MaxRetries;

    // HTTP status codes that are retryable
    public static bool IsRetryable(int httpStatus) =>
        httpStatus is 429 or 500 or 502 or 503 or 504;
}
