using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// How many references one sentence may cite (Lau, 25 November 2026). The multivocal run 50e9a8e3 had 19 markers with ten
/// or more sources and one with 36: a reader cannot tell which source says what, and the citation check cannot judge
/// such a claim. A sentence cites at most <see cref="MaxReferencesPerSentence"/> references across all its markers,
/// ideally one or two. The rule is in every writing prompt and checked in code on every written section; a sentence
/// that breaks it sends the answer back on the usual retry path, and the citation repair never adds a reference.
/// Text written in code that lists references by design (the methods' notes on studies not shown, the coverage line on
/// the studies not cited, the count tables) is not a claim and is not under the rule.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>The most references one written sentence may cite, counting every marker in it.</summary>
    public const int MaxReferencesPerSentence = 3;

    /// <summary>The words every send-back for the rule carries, so a step can write it to the notes.</summary>
    private const string LoadMark = "a sentence cites at most";

    /// <summary>The prompt rule on how many references a sentence cites. It names the number code checks.</summary>
    public static string CitationLoadRule(WritingProfile profile) =>
        $"A sentence cites at most {MaxReferencesPerSentence} {profile.Sources} in all its markers together, and ideally one or two. " +
        $"Where more {profile.Sources} report the same thing, cite the one to {MaxReferencesPerSentence} whose evidence is strongest or most direct; " +
        $"say how many others agree only when the facts you are given count them, and otherwise cite only those. Code sends back a sentence that cites more than {MaxReferencesPerSentence}.";

    /// <summary>The sentences of a text that cite more than <see cref="MaxReferencesPerSentence"/> distinct references, with those references.</summary>
    public static IReadOnlyList<(string Sentence, IReadOnlyList<int> References)> OverCitedSentences(string? text) =>
        CitationSupportChecker.SplitSentences(text ?? "")
            .Select(s => (Sentence: s, References: CitationSupportChecker.ReferencesIn(s)))
            .Where(s => s.References.Count > MaxReferencesPerSentence)
            .ToList();

    /// <summary>
    /// Why a written text breaks the rule on references per sentence, or null. A sentence that is already, word for word,
    /// in <paramref name="before"/> (the text a revision starts from) is not held against the revision. <paramref name="part"/>
    /// names the part in the message ("the rationale"). Public for unit tests.
    /// </summary>
    public static string? CitationLoadProblem(string? text, string? before = null, string? part = null)
    {
        var existing = before == null ? new HashSet<string>() : OverCitedSentences(before).Select(s => s.Sentence).ToHashSet(StringComparer.Ordinal);
        var over = OverCitedSentences(text).Where(s => !existing.Contains(s.Sentence)).ToList();
        if (over.Count == 0) return null;
        var (sentence, references) = over[0];
        string shown = sentence.Length <= 160 ? sentence : sentence[..157] + "...";
        return $"{(part == null ? "" : part + ": ")}the sentence \"{shown}\" cites {references.Count} references ({string.Join(", ", references.Select(r => $"[{r}]"))}); " +
               $"{LoadMark} {MaxReferencesPerSentence}, ideally one or two. Keep the most direct one to {MaxReferencesPerSentence}, or split the claim into sentences that each cite one or two" +
               (over.Count > 1 ? $" ({over.Count} sentences break this rule)." : ".");
    }

    /// <summary>
    /// <see cref="CitationLoadProblem"/>, with every send-back written to <paramref name="note"/> (the run's writing notes),
    /// so how often the rule sent an answer back is on file.
    /// </summary>
    public static string? CitationLoadSendBack(string? text, string step, Action<string>? note, string? before = null, string? part = null)
    {
        var problem = CitationLoadProblem(text, before, part);
        if (problem != null) note?.Invoke($"The {step} was sent back for citing too many references in one sentence: {problem}");
        return problem;
    }

    /// <summary>Writes a send-back for the rule on references per sentence to <paramref name="note"/>; other problems pass through unwritten.</summary>
    private static string? Noted(string? problem, string step, Action<string>? note)
    {
        if (problem != null && problem.Contains(LoadMark, StringComparison.Ordinal))
            note?.Invoke($"The {step} was sent back for citing too many references in one sentence: {problem}");
        return problem;
    }

    /// <summary>
    /// A validator that adds a soft check to <paramref name="hard"/>: a hard problem always sends the answer back; a soft
    /// problem does so at most <paramref name="sendBacks"/> times, each written to <paramref name="note"/> as being for
    /// <paramref name="what"/>, and an answer whose soft problem is left after that is kept, with a note that says so.
    /// </summary>
    public static Func<T, string?> WithSoftGuard<T>(Func<T, string?> hard, Func<T, string?> soft, string step, string what, Action<string>? note, int sendBacks = 2)
    {
        int sentBack = 0;
        return answer =>
        {
            if (hard(answer) is { } problem) return problem;
            if (soft(answer) is not { } softProblem) return null;
            if (sentBack < sendBacks)
            {
                sentBack++;
                note?.Invoke($"The {step} was sent back for {what} ({sentBack} of {sendBacks}): {softProblem}");
                return softProblem;
            }
            note?.Invoke($"The {step} was kept after {sendBacks} send-backs with {what}: {softProblem}");
            return null;
        };
    }
}
