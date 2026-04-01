using System.IO;

namespace FatimaTTS.Services;

/// <summary>
/// Merges multiple audio chunk files into a single output file.
///
/// WAV (LINEAR16):
///   - Each chunk from the API contains a full 44-byte RIFF/WAV header.
///   - We strip the header from every chunk, concatenate the raw PCM data,
///     then write one new RIFF header covering the full data size.
///   - This matches the Laravel stitchAudioFiles() approach exactly.
///
/// MP3 / OGG / FLAC:
///   - Simple binary concatenation. Players handle the multi-frame stream
///     correctly for all three formats.
/// </summary>
public class AudioMergeService
{
    private const int WavHeaderSize = 44;

    public void MergeChunks(
        IEnumerable<string> chunkFilePaths,
        string outputPath,
        string audioEncoding,
        int sampleRateHertz = 24000)
    {
        var paths = chunkFilePaths.ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (audioEncoding == "LINEAR16")
            MergeWav(paths, outputPath, sampleRateHertz);
        else
            MergeBinary(paths, outputPath);
    }

    // ── WAV ──────────────────────────────────────────────────────────────

    private static void MergeWav(List<string> paths, string outputPath, int sampleRateHertz)
    {
        // Collect raw PCM from every chunk (strip the 44-byte WAV header)
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        // Reserve space for the header — we write it after we know the data size
        output.Seek(WavHeaderSize, SeekOrigin.Begin);

        long totalPcmBytes = 0;

        foreach (var path in paths)
        {
            using var fs = File.OpenRead(path);

            if (fs.Length <= WavHeaderSize)
                continue;

            fs.Seek(WavHeaderSize, SeekOrigin.Begin);
            var pcmLength = fs.Length - WavHeaderSize;
            fs.CopyTo(output);
            totalPcmBytes += pcmLength;
        }

        // Go back and write the correct RIFF header
        output.Seek(0, SeekOrigin.Begin);
        WriteWavHeader(output, totalPcmBytes, sampleRateHertz, channels: 1, bitsPerSample: 16);
    }

    private static void WriteWavHeader(
        Stream stream, long dataSize, int sampleRate, int channels, int bitsPerSample)
    {
        int byteRate   = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));

        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((uint)(36 + dataSize));          // ChunkSize
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);                              // Subchunk1Size (PCM)
        writer.Write((short)1);                        // AudioFormat   (PCM = 1)
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataSize);
    }

    // ── MP3 / OGG / FLAC ─────────────────────────────────────────────────

    private static void MergeBinary(List<string> paths, string outputPath)
    {
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (var path in paths)
        {
            using var fs = File.OpenRead(path);
            fs.CopyTo(output);
        }
    }

    /// <summary>
    /// Saves a single chunk's audio bytes directly to disk.
    /// Used during the pre-fetch phase before merging.
    /// </summary>
    public static void SaveChunk(byte[] audioBytes, string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, audioBytes);
    }

    /// <summary>
    /// Calculates approximate WAV duration from file size.
    /// </summary>
    public static double GetWavDurationSeconds(string filePath, int sampleRate = 24000,
        int channels = 1, int bitsPerSample = 16)
    {
        var fileSize = new FileInfo(filePath).Length;
        var dataSize = fileSize - WavHeaderSize;
        if (dataSize <= 0) return 0;
        return (double)dataSize / (sampleRate * channels * (bitsPerSample / 8));
    }
}
