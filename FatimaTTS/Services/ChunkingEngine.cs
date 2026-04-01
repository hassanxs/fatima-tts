using System.Text.RegularExpressions;

namespace FatimaTTS.Services;

/// <summary>
/// Ports the Laravel InworldTtsService chunking algorithm to C# with one improvement:
/// Priority 4b — a second space search with no floor threshold, preventing hard mid-word
/// cuts when a valid word boundary exists below the 50% threshold.
///
/// Priority ladder (findSplitPoint):
///   1. Last paragraph break (\n\n)  — only if position > 30% of maxLen
///   2. Last sentence end   ([.!?]+\s) — only if position > 30% of maxLen  (last valid match)
///   3. Last clause boundary ([,;:—–-]\s) — only if position > 50% of maxLen (last valid match)
///   4a. Last space          — only if position > 50% of maxLen
///   4b. Last space          — any position > 0  (NEW: prevents hard mid-word cut)
///   Fallback: hard cut at maxLen (only if truly no spaces exist, e.g. URLs/code)
/// </summary>
public partial class ChunkingEngine
{
    private const int MaxChunkSize = 2000;

    // Regex: sentence-ending punctuation followed by whitespace/newline
    [GeneratedRegex(@"[.!?]+[\s\n]", RegexOptions.Compiled)]
    private static partial Regex SentenceEndRegex();

    // Regex: clause boundary punctuation followed by a space
    [GeneratedRegex(@"[,;:\-\u2013\u2014]\s", RegexOptions.Compiled)]
    private static partial Regex ClauseBoundaryRegex();

    /// <summary>
    /// Splits text into chunks of at most 2000 characters, choosing split points
    /// that preserve word and sentence boundaries wherever possible.
    /// </summary>
    public IReadOnlyList<string> ChunkText(string text)
    {
        text = text.Trim();

        if (text.Length <= MaxChunkSize)
            return [text];

        var chunks    = new List<string>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            if (remaining.Length <= MaxChunkSize)
            {
                var final = remaining.Trim();
                if (final.Length > 0) chunks.Add(final);
                break;
            }

            int splitAt  = FindSplitPoint(remaining, MaxChunkSize);
            var chunk    = remaining[..splitAt].Trim();
            remaining    = remaining[splitAt..].Trim();

            if (chunk.Length > 0)
                chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>
    /// Finds the optimal split position within [0, maxLen] of the given text.
    /// </summary>
    private static int FindSplitPoint(string text, int maxLen)
    {
        // Search area is the first maxLen characters
        var searchArea = text.Length <= maxLen ? text : text[..maxLen];

        // ── Priority 1: Paragraph break (\n\n) ───────────────────────────
        // Must be past 30% of maxLen to avoid tiny leading chunks
        int lastParagraph = searchArea.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (lastParagraph != -1 && lastParagraph > maxLen * 0.3)
            return lastParagraph + 2;

        // ── Priority 2: Sentence end ([.!?]+[\s\n]) ──────────────────────
        // Take the LAST match that is ≤ maxLen and > 30% of maxLen
        // (mirrors PHP: iterates all matches, keeps overwriting bestSentence)
        int bestSentence = 0;
        foreach (Match m in SentenceEndRegex().Matches(searchArea))
        {
            int pos = m.Index + m.Length;
            if (pos <= maxLen && pos > maxLen * 0.3)
                bestSentence = pos;
        }
        if (bestSentence > 0)
            return bestSentence;

        // ── Priority 3: Clause boundary ([,;:—–-]\s) ─────────────────────
        // Must be past 50% of maxLen (stricter — weak boundary)
        int lastClause = 0;
        foreach (Match m in ClauseBoundaryRegex().Matches(searchArea))
        {
            int pos = m.Index + m.Length;
            if (pos <= maxLen && pos > maxLen * 0.5)
                lastClause = pos;
        }
        if (lastClause > 0)
            return lastClause;

        // ── Priority 4a: Last space (> 50% floor) ────────────────────────
        // Same 50% floor as Laravel Priority 4
        int lastSpaceHigh = searchArea.LastIndexOf(' ');
        if (lastSpaceHigh != -1 && lastSpaceHigh > maxLen * 0.5)
            return lastSpaceHigh + 1;

        // ── Priority 4b: Last space (any position > 0) ───────────────────
        // IMPROVEMENT over Laravel: prevents mid-word hard cut when a valid
        // word boundary exists below the 50% threshold.
        int lastSpaceAny = searchArea.LastIndexOf(' ');
        if (lastSpaceAny > 0)
            return lastSpaceAny + 1;

        // ── Fallback: hard cut at maxLen ─────────────────────────────────
        // Only reached for pathological input: URLs, code, or scripts with
        // no spaces in the first 2000 characters.
        return maxLen;
    }

    /// <summary>
    /// Returns a preview of how many chunks a given text would produce.
    /// Cheap — does not allocate chunk strings, only counts splits.
    /// </summary>
    public int EstimateChunkCount(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;
        if (text.Length <= MaxChunkSize) return 1;

        int count     = 0;
        int remaining = text.Length;
        int offset    = 0;

        while (remaining > 0)
        {
            if (remaining <= MaxChunkSize) { count++; break; }

            int splitAt  = FindSplitPoint(text[offset..], MaxChunkSize);
            // Account for trim — we approximate; trim rarely changes count
            offset    += splitAt;
            remaining -= splitAt;
            // skip leading whitespace
            while (remaining > 0 && offset < text.Length && text[offset] == ' ' || (offset < text.Length && text[offset] == '\n'))
            {
                offset++;
                remaining--;
            }
            count++;
        }

        return count;
    }
}
