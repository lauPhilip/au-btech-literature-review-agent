using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// How the in-text citations of main.tex are set: the numbers of one marker in ascending order, each number once, and a
/// run of three or more consecutive numbers as a range ("[3, 5, 4, 4]" -> "[3–5]"), as numbered styles such as
/// Vancouver do. Only the typeset paper changes: the run's files, the page's citation chips and the citation check keep
/// one number per reference, so every number can still be traced to its verdict. It also marks, in code, each sentence
/// whose citation the check could not confirm (<see cref="MarkUnconfirmed(PrismaReport, IEnumerable{CitationSupportResult})"/>).
/// </summary>
public static class CitationMarkers
{
    /// <summary>The footnote of a sentence whose citation the check could not confirm, in main.tex and on the page.</summary>
    public const string UnconfirmedNote = "The automated check could not confirm this citation.";

    // Placed after a marked sentence before the text is escaped, and set as LaTeX by PaperLatex.Escape (Unicode private use).
    internal const char Dagger = '\uE000', DaggerWithNote = '\uE001';

    /// <summary>
    /// The LaTeX of the two marks: a dagger, and the first in a section a dagger with its footnote. Each starts with a
    /// brace, so LatexBreaks never puts a line break between a sentence's full stop and its dagger.
    /// </summary>
    public const string DaggerLatex = @"{\textsuperscript{\dag}}";
    public const string DaggerWithNoteLatex = @"{\renewcommand{\thefootnote}{\dag}\footnote{" + UnconfirmedNote + "}}";

    /// <summary>True for a final verdict that leaves a citation unconfirmed: not supported, or could not be judged.</summary>
    public static bool IsUnconfirmed(string? verdict) =>
        verdict is CitationSupportChecker.NotSupported or CitationSupportChecker.Unverifiable;

    /// <summary>
    /// The sentences to mark, by field: each whose final verdict on at least one of its citations is unconfirmed, with
    /// the sentence as the check read it, so a sentence that has changed since is not marked by mistake.
    /// </summary>
    public static Dictionary<string, Dictionary<int, string>> Unconfirmed(IEnumerable<CitationSupportResult> checks) => checks
        .Where(c => IsUnconfirmed(c.Verdict))
        .GroupBy(c => c.Field, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.GroupBy(c => c.SentenceIndex).ToDictionary(s => s.Key, s => s.First().Sentence), StringComparer.Ordinal);

    /// <summary>
    /// The text with a mark after each listed sentence that still reads as the check read it; the first mark of the text
    /// carries the footnote. Sentences are split as the check split them (<see cref="ReportEdits.SentenceSpans"/>).
    /// </summary>
    public static string MarkSentences(string text, IReadOnlyDictionary<int, string> sentences)
    {
        if (string.IsNullOrEmpty(text) || sentences.Count == 0) return text;
        var spans = ReportEdits.SentenceSpans(text);
        var result = new System.Text.StringBuilder(text.Length + sentences.Count);
        int from = 0;
        bool first = true;
        for (int i = 0; i < spans.Count; i++)
        {
            var (start, length) = spans[i];
            if (!sentences.TryGetValue(i, out string? read) || text.Substring(start, length).Trim() != read.Trim()) continue;
            result.Append(text, from, start + length - from).Append(first ? DaggerWithNote : Dagger);
            from = start + length;
            first = false;
        }
        return result.Append(text, from, text.Length - from).ToString();
    }

    /// <summary>
    /// A copy of the report for main.tex with every unconfirmed sentence marked, and a sentence on the marks after the
    /// citation-check paragraph; the same report when nothing is marked, so such a run is written exactly as before.
    /// </summary>
    public static PrismaReport MarkUnconfirmed(PrismaReport report, IEnumerable<CitationSupportResult> checks)
    {
        var byField = Unconfirmed(checks);
        if (byField.Count == 0) return report;
        var copy = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(System.Text.Json.JsonSerializer.Serialize(report))!;
        int marked = 0;
        foreach (var (field, sentences) in byField)
        {
            if (ReportEdits.FieldText(copy, field) is not { } text) continue;
            string after = MarkSentences(text, sentences);
            marked += after.Count(c => c is Dagger or DaggerWithNote);
            ReportEdits.SetField(copy, field, after);
        }
        if (marked == 0) return report;
        copy.CitationCheckSummary = (copy.CitationCheckSummary + " " + MarkedSentence(marked)).Trim();
        return copy;
    }

    /// <summary>"3 sentences whose citation the check could not confirm are marked with a dagger (†) and a footnote."</summary>
    public static string MarkedSentence(int count) => count == 1
        ? "One sentence whose citation the check did not confirm (judged not supported, or not judged) is marked with a dagger (†) and a footnote."
        : $"{count} sentences whose citation the check did not confirm (judged not supported, or not judged) are marked with a dagger (†) and a footnote.";

    /// <summary>The escaped text with its marks set as LaTeX.</summary>
    internal static string MarksToLatex(string escaped) =>
        escaped.IndexOfAny(new[] { Dagger, DaggerWithNote }) < 0 ? escaped
            : escaped.Replace(Dagger.ToString(), DaggerLatex).Replace(DaggerWithNote.ToString(), DaggerWithNoteLatex);

    // "[1]", "[1, 2]", "[2-4]", "[2–4, 7]": a marker of reference numbers only.
    private static readonly Regex Marker = new(@"\[(\d{1,4}(?:\s*(?:,|[-–])\s*\d{1,4})*)\]", RegexOptions.Compiled);

    public static string Tidy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Marker.Replace(text, m => Numbers(m.Groups[1].Value) is { } numbers ? $"[{Compress(numbers)}]" : m.Value);
    }

    /// <summary>The numbers of a marker with its ranges expanded, or null when it is not a list of reference numbers.</summary>
    private static List<int>? Numbers(string inside)
    {
        var numbers = new List<int>();
        foreach (string part in inside.Split(','))
        {
            var ends = part.Split(new[] { '-', '–' }).Select(p => int.Parse(p.Trim())).ToArray();
            if (ends.Any(n => n < 1)) return null;
            if (ends.Length == 1) numbers.Add(ends[0]);
            else if (ends.Length == 2 && ends[0] < ends[1] && ends[1] - ends[0] <= 100) numbers.AddRange(Enumerable.Range(ends[0], ends[1] - ends[0] + 1));
            else return null;
        }
        return numbers.Distinct().OrderBy(n => n).ToList();
    }

    private static string Compress(List<int> numbers)
    {
        var parts = new List<string>();
        for (int i = 0; i < numbers.Count;)
        {
            int j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
            if (j - i >= 2) parts.Add($"{numbers[i]}–{numbers[j]}");
            else for (int k = i; k <= j; k++) parts.Add(numbers[k].ToString());
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
