using System.IO;
using System.Text;
using FatimaTTS.Models;

namespace FatimaTTS.Services;

/// <summary>
/// Generates SRT subtitle files from the timestamp data returned by Inworld TTS.
///
/// Two granularities are supported:
///   • Word-level  (timestampType=WORD)      — groups words into ~8-word / ~4s lines.
///   • Character-level (timestampType=CHARACTER) — groups characters into ~42-char / ~4s
///     lines with character-accurate in/out times. A word-level SRT can also be derived
///     from character data, so a CHARACTER job can export either format.
///
/// Timestamps are stored CHUNK-RELATIVE (v1.2.0+); this service applies the cumulative
/// timeline offset as it walks the chunks in order. Pre-1.2.0 jobs stored absolute times
/// (TimestampsAreRelative=false) and are handled transparently.
/// </summary>
public class SrtExportService
{
    private const int    MaxWordsPerLine  = 8;
    private const int    MaxCharsPerLine  = 42;   // standard subtitle line width
    private const double MaxLineDuration  = 4.0;  // seconds

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Word-based SRT (default). Falls back to deriving words from character data.</summary>
    public string GenerateSrt(TtsJob job) => GenerateWordSrt(job);

    public string GenerateWordSrt(TtsJob job)
    {
        var (words, starts, ends) = GatherWords(job);
        if (words.Count == 0)
            return "; No word timestamp data available.\n; Generate with a timestamp-capable model to get subtitles.";
        return BuildSrt(GroupWords(words, starts, ends));
    }

    public string GenerateCharSrt(TtsJob job)
    {
        var (chars, starts, ends) = GatherChars(job);
        if (chars.Count == 0)
            return "; No character timestamp data available.\n; Generate with Character-level timestamps to get this subtitle.";
        return BuildSrt(GroupChars(chars, starts, ends));
    }

    public void ExportSrt(TtsJob job, string outputPath, bool characterLevel = false)
    {
        var content = characterLevel ? GenerateCharSrt(job) : GenerateWordSrt(job);
        File.WriteAllText(outputPath, content, Encoding.UTF8);
    }

    /// <summary>True if a word-based SRT can be produced (word data, or character data to derive from).</summary>
    public static bool HasWordData(TtsJob job) =>
        job.Chunks.Any(c => c.Words.Count > 0 || c.Characters.Count > 0);

    /// <summary>True if a character-based SRT can be produced.</summary>
    public static bool HasCharData(TtsJob job) =>
        job.Chunks.Any(c => c.Characters.Count > 0);

    /// <summary>Returns the SRT path next to the audio file.</summary>
    public static string GetSrtPath(string audioFilePath)
    {
        var dir  = Path.GetDirectoryName(audioFilePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(audioFilePath);
        return Path.Combine(dir, name + ".srt");
    }

    // ── Gather absolute-timed tokens across chunks ───────────────────────────

    private static (List<string> tokens, List<double> starts, List<double> ends) GatherWords(TtsJob job)
    {
        var tokens = new List<string>();
        var starts = new List<double>();
        var ends   = new List<double>();
        double running = 0;

        foreach (var chunk in job.Chunks.OrderBy(c => c.ChunkIndex))
        {
            double off = chunk.TimestampsAreRelative ? running : 0;

            if (chunk.Words.Count > 0)
            {
                for (int i = 0; i < chunk.Words.Count; i++)
                {
                    tokens.Add(chunk.Words[i]);
                    starts.Add(SafeAt(chunk.WordStartTimes, i) + off);
                    ends.Add(SafeAt(chunk.WordEndTimes, i) + off);
                }
            }
            else if (chunk.Characters.Count > 0)
            {
                // Derive words from character alignment.
                var (w, ws, we) = WordsFromChars(chunk.Characters, chunk.CharStartTimes, chunk.CharEndTimes);
                for (int i = 0; i < w.Count; i++)
                {
                    tokens.Add(w[i]);
                    starts.Add(ws[i] + off);
                    ends.Add(we[i] + off);
                }
            }

            running += ChunkSpan(chunk);
        }

        return (tokens, starts, ends);
    }

    private static (List<string> tokens, List<double> starts, List<double> ends) GatherChars(TtsJob job)
    {
        var tokens = new List<string>();
        var starts = new List<double>();
        var ends   = new List<double>();
        double running = 0;

        foreach (var chunk in job.Chunks.OrderBy(c => c.ChunkIndex))
        {
            double off = chunk.TimestampsAreRelative ? running : 0;
            for (int i = 0; i < chunk.Characters.Count; i++)
            {
                tokens.Add(chunk.Characters[i]);
                starts.Add(SafeAt(chunk.CharStartTimes, i) + off);
                ends.Add(SafeAt(chunk.CharEndTimes, i) + off);
            }
            running += ChunkSpan(chunk);
        }

        return (tokens, starts, ends);
    }

    // The chunk's own timeline span, used to offset later chunks.
    private static double ChunkSpan(TtsChunk chunk)
    {
        double span = chunk.DurationSeconds;
        double baseOff = chunk.TimestampsAreRelative ? 0 : chunk.ChunkTimeOffset;
        if (span <= 0)
        {
            if (chunk.WordEndTimes.Count > 0) span = chunk.WordEndTimes.Max() - baseOff;
            else if (chunk.CharEndTimes.Count > 0) span = chunk.CharEndTimes.Max() - baseOff;
        }
        return Math.Max(span, 0);
    }

    private static (List<string>, List<double>, List<double>) WordsFromChars(
        List<string> chars, List<double> starts, List<double> ends)
    {
        var w = new List<string>(); var ws = new List<double>(); var we = new List<double>();
        var sb = new StringBuilder();
        double curStart = 0, lastEnd = 0;
        bool inWord = false;

        for (int i = 0; i < chars.Count; i++)
        {
            var ch = chars[i];
            if (string.IsNullOrWhiteSpace(ch))
            {
                if (inWord) { w.Add(sb.ToString()); ws.Add(curStart); we.Add(lastEnd); inWord = false; }
            }
            else
            {
                if (!inWord) { inWord = true; curStart = SafeAt(starts, i); sb.Clear(); }
                sb.Append(ch);
                lastEnd = SafeAt(ends, i);
            }
        }
        if (inWord) { w.Add(sb.ToString()); ws.Add(curStart); we.Add(lastEnd); }
        return (w, ws, we);
    }

    // ── Grouping into subtitle lines ─────────────────────────────────────────

    private static List<(double Start, double End, string Text)> GroupWords(
        List<string> words, List<double> starts, List<double> ends)
    {
        var lines = new List<(double, double, string)>();
        int i = 0;
        while (i < words.Count)
        {
            int    lineStart = i;
            var    sb        = new StringBuilder();
            double start     = starts[i];
            double end       = ends[i];

            while (i < words.Count)
            {
                double wEnd  = ends[i];
                int    count = i - lineStart + 1;

                bool tooLong      = wEnd - start > MaxLineDuration && count > 1;
                bool tooManyWords = count > MaxWordsPerLine;
                bool sentenceEnd  = count > 1 && (words[i - 1].EndsWith('.') ||
                                                  words[i - 1].EndsWith('!') ||
                                                  words[i - 1].EndsWith('?'));
                if (tooLong || tooManyWords) break;
                if (sentenceEnd && count >= 4) break;

                if (sb.Length > 0) sb.Append(' ');
                sb.Append(words[i]);
                end = wEnd;
                i++;
            }

            lines.Add((start, end, sb.ToString().Trim()));
        }
        return lines;
    }

    private static List<(double Start, double End, string Text)> GroupChars(
        List<string> chars, List<double> starts, List<double> ends)
    {
        var lines = new List<(double, double, string)>();
        int i = 0;
        while (i < chars.Count)
        {
            // Skip leading whitespace between lines.
            while (i < chars.Count && string.IsNullOrWhiteSpace(chars[i])) i++;
            if (i >= chars.Count) break;

            var    sb    = new StringBuilder();
            double start = starts[i];
            double end   = ends[i];
            int    len   = 0;

            while (i < chars.Count)
            {
                double cEnd = ends[i];
                bool tooLong  = cEnd - start > MaxLineDuration && len > 0;
                bool tooWide  = len >= MaxCharsPerLine && string.IsNullOrWhiteSpace(chars[i]);
                bool sentence = len >= 1 && (chars[i - 1] is "." or "!" or "?");

                if ((tooLong || tooWide) && len > 0) break;
                if (sentence && len >= 10) break;

                sb.Append(chars[i]);
                end = cEnd;
                len++;
                i++;
            }

            var text = sb.ToString().Trim();
            if (text.Length > 0) lines.Add((start, end, text));
        }
        return lines;
    }

    private static string BuildSrt(List<(double Start, double End, string Text)> lines)
    {
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

    private static double SafeAt(List<double> list, int i) => i >= 0 && i < list.Count ? list[i] : 0;

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }
}
