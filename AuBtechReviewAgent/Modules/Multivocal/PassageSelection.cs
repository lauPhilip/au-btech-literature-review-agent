using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>What the model is given of a source's kept text, and what that was, for the file.</summary>
/// <param name="Text">The text sent: the whole kept text, or selected passages of it joined by <see cref="PassageSelection.Break"/>.</param>
/// <param name="Passages">What was sent, e.g. "14 of 83 passages of the full text (8,104 of 41,230 characters)"; null when the whole kept text was sent.</param>
public sealed record TextForModel(string Text, string? Passages);

/// <summary>One passage of a kept text: its number from 1, and where it starts and how long it is in that text.</summary>
public readonly record struct Passage(int Number, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// The shared mechanics of sending a long kept text as passages (<see cref="FormalExtractionText"/>, <see cref="GreyPageText"/>):
/// cutting it into passages, choosing the most relevant by shared words as the systematic review chooses its excerpts
/// (<see cref="GroundingContextBuilder.SelectChunks"/>), and joining the chosen ones. Every passage is a span of the
/// kept text, and passages next to each other are sent as the one span they make there, so what lies between two
/// breaks is copied from the kept text. <see cref="QuoteFound"/> looks for a quote within one such span, so a quote
/// found in what was sent is also found in the kept text the later checks read.
/// </summary>
public static class PassageSelection
{
    /// <summary>What separates two passages that are not next to each other, so no quote can join them.</summary>
    public const string Break = "\n\n[passage break]\n\n";

    /// <summary>The passages of a text made of paragraphs joined by a blank line, such as a formal source's kept full text.</summary>
    public static List<Passage> Paragraphs(string text)
    {
        var passages = new List<Passage>();
        int at = 0;
        while (at < text.Length)
        {
            int end = text.IndexOf("\n\n", at, StringComparison.Ordinal);
            if (end < 0) end = text.Length;
            Add(passages, text, at, end);
            at = end + 2;
        }
        return passages;
    }

    /// <summary>
    /// The passages of a kept page text, which has one paragraph per line: lines are put together until a passage
    /// has at least <paramref name="target"/> characters but no more than <paramref name="most"/>, and a line longer
    /// than that is cut at sentence ends (or, failing that, at a space) so a passage stays short.
    /// </summary>
    public static List<Passage> Lines(string text, int target = 300, int most = 600)
    {
        var passages = new List<Passage>();
        int start = 0, at = 0;
        while (at < text.Length)
        {
            int lineEnd = text.IndexOf('\n', at);
            if (lineEnd < 0) lineEnd = text.Length;
            if (lineEnd - at > most)
            {
                if (at > start) Add(passages, text, start, at - 1);
                foreach (var (from, to) in Pieces(text, at, lineEnd, most)) Add(passages, text, from, to);
                start = at = lineEnd + 1;
                continue;
            }
            if (at > start && lineEnd - start > most)
            {
                Add(passages, text, start, at - 1);
                start = at;
            }
            if (lineEnd - start >= target)
            {
                Add(passages, text, start, lineEnd);
                start = lineEnd + 1;
            }
            at = lineEnd + 1;
        }
        if (start < text.Length) Add(passages, text, start, text.Length);
        return passages;
    }

    /// <summary>The <paramref name="count"/> passages of <paramref name="candidates"/> that share the most words with <paramref name="terms"/>.</summary>
    public static IEnumerable<Passage> MostRelevant(string text, IReadOnlyList<Passage> candidates, HashSet<string> terms, int count)
    {
        var byNumber = candidates.ToDictionary(p => p.Number);
        return GroundingContextBuilder.SelectChunks(candidates.Select(p => new DocumentChunk("", p.Number, TextOf(text, p))).ToList(), terms, count)
            .Select(c => byNumber[c.PageNumber]);
    }

    /// <summary>The passage's text, as it is in the kept text.</summary>
    public static string TextOf(string text, Passage p) => text.Substring(p.Start, p.Length);

    /// <summary>
    /// The chosen passages, in text order, as one text: passages next to each other as the span they make in the kept
    /// text, others separated by <see cref="Break"/>; with what was sent, in words, for the file.
    /// </summary>
    public static TextForModel Join(string text, IEnumerable<Passage> chosen, int total, string of)
    {
        var ordered = chosen.Distinct().OrderBy(p => p.Number).ToList();
        var spans = new List<(int Start, int End)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && ordered[i].Number == ordered[i - 1].Number + 1) spans[^1] = (spans[^1].Start, ordered[i].End);
            else spans.Add((ordered[i].Start, ordered[i].End));
        }
        string sent = string.Join(Break, spans.Select(s => text[s.Start..s.End]));
        string Num(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        return new TextForModel(sent, $"{ordered.Count} of {total} passages of {of} ({Num(ordered.Sum(p => p.Length))} of {Num(text.Length)} characters)");
    }

    /// <summary>
    /// True when the quote is found (<see cref="CitationSupportChecker.QuoteOccursIn"/>) within one stretch of the
    /// text sent between two breaks, each a span of the kept text; for a text sent whole, anywhere in it.
    /// </summary>
    public static bool QuoteFound(string? quote, string sent) => QuoteFound(quote, Spans(sent));

    /// <summary>The same check against the sent text's stretches made canonical once (<see cref="Spans"/>).</summary>
    public static bool QuoteFound(string? quote, IReadOnlyList<CanonicalText> spans) =>
        spans.Any(span => CitationSupportChecker.QuoteOccursIn(quote, span));

    /// <summary>The stretches of a sent text between two breaks, each made canonical once for every quote checked in it.</summary>
    public static IReadOnlyList<CanonicalText> Spans(string sent) =>
        sent.Contains(Break, StringComparison.Ordinal)
            ? sent.Split(Break).Select(CanonicalText.Of).ToList()
            : new[] { CanonicalText.Of(sent) };

    private static void Add(List<Passage> passages, string text, int from, int to)
    {
        while (from < to && char.IsWhiteSpace(text[from])) from++;
        while (to > from && char.IsWhiteSpace(text[to - 1])) to--;
        if (to > from) passages.Add(new Passage(passages.Count + 1, from, to - from));
    }

    // A long line cut at the last sentence end before the limit, or at a space when there is none in its second half.
    private static IEnumerable<(int From, int To)> Pieces(string text, int from, int to, int most)
    {
        while (to - from > most)
        {
            int cut = text.LastIndexOf(". ", from + most - 1, most, StringComparison.Ordinal);
            if (cut > from + most / 2) cut += 1;
            else
            {
                cut = text.LastIndexOf(' ', from + most - 1, most);
                if (cut <= from + most / 2) cut = from + most;
            }
            yield return (from, cut);
            from = cut;
        }
        yield return (from, to);
    }
}
