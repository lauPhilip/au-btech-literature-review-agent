using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The guard on result figures (issue #452). The audit of run cce1c6e4 found one figure from a theme ("34 %", "40-90 %")
/// repeated in the discussion, the answers, the implications and the conclusion, so the review read as the same result
/// over and over. A result figure may appear in its theme, in the abstract and in one place after the results (one
/// answer, or the discussion, or one closing part); the directions for future research and the conclusion use none,
/// as the rationale already does (#441). The guard is soft: it sends an answer back at most <see cref="FigureSendBacks"/>
/// times, then keeps it, and every send-back and every figure kept is written to the run's notes.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>How often a figure problem alone sends an answer back before the answer is kept anyway.</summary>
    public const int FigureSendBacks = 2;

    // A number glued to a word ("GPT-4", "v2", "Llama3") is part of a name, not a figure.
    private static readonly Regex Figure = new(@"(?<![\p{L}\d.,])(?<!\p{L}-)\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// The result figures of a text: its numbers in digits, citation markers left out, without years (1900 to 2099), the
    /// numbers in <paramref name="facts"/> (the review's own counts) and numbers that are part of a name. Public for unit tests.
    /// </summary>
    public static HashSet<string> ResultFigures(string text, string facts = "")
    {
        var own = NumbersIn(facts ?? "");
        return Figure.Matches(Regex.Replace(text ?? "", @"\[[^\]]*\]", ""))
            .Select(m => m.Value.TrimEnd('.', ','))
            .Where(n => !own.Contains(n) && !(n.Length == 4 && int.TryParse(n, out int y) && y >= 1900 && y <= 2099))
            .ToHashSet();
    }

    /// <summary>
    /// The first result figure that is used where it may not be, or null. <paramref name="results"/> are the theme
    /// subsections, whose figures are the result figures; each of <paramref name="parts"/> may use a figure no earlier part
    /// and none of <paramref name="usedBefore"/> uses; a part named in <paramref name="noFigures"/> may use none.
    /// Public for unit tests.
    /// </summary>
    public static string? FigurePlacementProblem(string results, string facts, IReadOnlyList<(string Name, string Text)> parts,
        IEnumerable<string>? usedBefore = null, IReadOnlyCollection<string>? noFigures = null)
    {
        var figures = ResultFigures(results, facts);
        if (figures.Count == 0) return null;
        var placed = new Dictionary<string, string>();
        foreach (var (where, text) in (usedBefore ?? Array.Empty<string>()).Select(t => ("an earlier section", t)).Concat(parts.Select(p => (p.Name, p.Text))))
            foreach (var n in ResultFigures(text, facts).Where(figures.Contains).OrderBy(n => n, StringComparer.Ordinal))
            {
                if (noFigures?.Contains(where) == true)
                    return $"{where} uses {n}, a result figure; it says what the results mean, not the figures again, so leave {n} out.";
                if (placed.TryGetValue(n, out var first) && first != where)
                    return $"{where} uses {n}, a result figure {first} already gives; a figure appears in its theme and in one place after it, so leave it out here and say what it means instead.";
                placed.TryAdd(n, where);
            }
        return null;
    }

    /// <summary>
    /// A validator that adds the figure guard to <paramref name="hard"/>: a hard problem always sends the answer back; a
    /// figure problem does so at most <see cref="FigureSendBacks"/> times, each written to <paramref name="note"/>, and an
    /// answer whose figure problem is left after that is kept, with a note that says so.
    /// </summary>
    public static Func<T, string?> WithFigureGuard<T>(Func<T, string?> hard, Func<T, string?> figures, string step, Action<string>? note)
    {
        int sentBack = 0;
        return answer =>
        {
            if (hard(answer) is { } problem) return problem;
            if (figures(answer) is not { } figure) return null;
            if (sentBack < FigureSendBacks)
            {
                sentBack++;
                note?.Invoke($"The {step} was sent back for a repeated result figure ({sentBack} of {FigureSendBacks}): {figure}");
                return figure;
            }
            note?.Invoke($"The {step} was kept after {FigureSendBacks} send-backs with a repeated result figure: {figure}");
            return null;
        };
    }
}
