using System.IO;
using System.Text;
using FatimaTTS.Models;

namespace FatimaTTS.Services;

/// <summary>
/// Generates SRT subtitle files from word-level timestamp data
/// returned by Inworld TTS API (timestampType=WORD).
/// 
/// Groups words into subtitle lines of ~8 words or ~4 seconds max,
/// breaking at sentence boundaries when possible.
/// </summary>
public class SrtExportService
{
    private const int MaxWordsPerLine  = 8;
    private const double MaxLineDuration = 4.0; // seconds

    public string GenerateSrt(TtsJob job)
    {
        // Flatten all words from all chunks (already offset-adjusted in processor)
        var words      = new List<string>();
        var startTimes = new List<double>();
        var endTimes   = new List<double>();

        foreach (var chunk in job.Chunks.OrderBy(c => c.ChunkIndex))
        {
            if (chunk.Words.Count == 0) continue;
            words.AddRange(chunk.Words);
            startTimes.AddRange(chunk.WordStartTimes);
            endTimes.AddRange(chunk.WordEndTimes);
        }

        if (words.Count == 0)
            return "; No timestamp data available.\n; Re-generate with a TTS 1.5 model to get word timestamps.";

        // Group into subtitle lines
        var lines  = new List<(double Start, double End, string Text)>();
        int i      = 0;

        while (i < words.Count)
        {
            int    lineStart = i;
            var    sb        = new StringBuilder();
            double start     = startTimes[i];
            double end       = endTimes[i];

            while (i < words.Count)
            {
                var word     = words[i];
                double wEnd  = endTimes[i];
                int    count = i - lineStart + 1;

                // Break conditions
                bool tooLong    = wEnd - start > MaxLineDuration && count > 1;
                bool tooManyWords = count > MaxWordsPerLine;
                bool sentenceEnd = count > 1 && (words[i - 1].EndsWith('.') ||
                                                  words[i - 1].EndsWith('!') ||
                                                  words[i - 1].EndsWith('?'));
                if ((tooLong || tooManyWords) && count > 1) break;
                if (sentenceEnd && count >= 4) break;

                if (sb.Length > 0) sb.Append(' ');
                sb.Append(word);
                end = wEnd;
                i++;
            }

            lines.Add((start, end, sb.ToString().Trim()));
        }

        // Write SRT format
        var srt = new StringBuilder();
        for (int n = 0; n < lines.Count; n++)
        {
            var (start, end, text) = lines[n];
            srt.AppendLine((n + 1).ToString());
            srt.AppendLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");
            srt.AppendLine(text);
            srt.AppendLine();
        }

        return srt.ToString();
    }

    public void ExportSrt(TtsJob job, string outputPath)
    {
        var content = GenerateSrt(job);
        File.WriteAllText(outputPath, content, Encoding.UTF8);
    }

    /// <summary>Returns the SRT path next to the audio file.</summary>
    public static string GetSrtPath(string audioFilePath)
    {
        var dir  = System.IO.Path.GetDirectoryName(audioFilePath) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(audioFilePath);
        return System.IO.Path.Combine(dir, name + ".srt");
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }
}
