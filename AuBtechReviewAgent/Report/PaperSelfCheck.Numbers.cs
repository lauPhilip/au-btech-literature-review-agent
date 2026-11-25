using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Checks 1 and 5: the numbers of the abstract and the conclusion, and where the result figures appear. The editor's
/// review of run 50e9a8e3 found an abstract that called 433 sources "grey" when 433 counted both pools, and a figure from
/// one theme repeated in the answers, the discussion and the conclusion. Both are found here from the text alone.
/// </summary>
public static partial class PaperSelfCheck
{
    private static readonly Regex Number = new(@"(?<![\p{L}\d.,])(?<!\p{L}-)\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    // A standard or a model named by letters and a number ("ISO 26262", "GPT 4") is a name, not a figure.
    private static readonly Regex AcronymNumber = new(@"\b[A-Z][A-Z0-9]+[- ]\d+(?:[.,]\d+)?\b", RegexOptions.Compiled);

    // "One study found" is ordinary prose, not a count, so counts in words start at two.
    private const string WordNumbers = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty";
    private static readonly Regex WordCount = new(@"\b(" + string.Join("|", WordNumbers.Split('|').Skip(1)) + @")\b(?=\s+(?:\p{L}+\s+)?(?:stud|record|paper|source|theme|code|duplicate|journal|conference|preprint|question|citation))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentAfter = new(@"^\s*(?:%|percent|per\s*cent|percentage|[.,]\d|x\b|×|-fold|fold\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClauseStop = new(@"[,;:—–()]|\b(?:of|and|while|but|after|from|whereas|than|with)\b|\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Partitive = new(@"\d\s+(?:out\s+)?of\s+(?:the\s+|all\s+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A text without citation markers and without names that carry a number, so only figures and counts are left.</summary>
    internal static string Plain(string text) => AcronymNumber.Replace(Regex.Replace(text ?? "", @"\[[^\]]*\]", ""), "");

    /// <summary>The numbers of a sentence, with where each is and whether it is a percentage or a fold: digits, and counts written as words.</summary>
    internal static List<(string Value, int Index, int Length, bool Figure)> NumbersOf(string sentence)
    {
        var real = ReviewWriter.ResultFigures(sentence);
        var found = Number.Matches(sentence).Cast<Match>()
            .Select(m => (Value: m.Value.TrimEnd('.', ','), m.Index, m.Length))
            .Where(m => real.Contains(m.Value))
            .Select(m => (m.Value, m.Index, m.Length, Figure: m.Value.Contains('.') || m.Value.Contains(',') || PercentAfter.IsMatch(sentence[(m.Index + m.Length)..])))
            .ToList();
        var words = WordNumbers.Split('|');
        found.AddRange(WordCount.Matches(sentence).Cast<Match>()
            .Select(m => ((Array.FindIndex(words, w => w.Equals(m.Value, StringComparison.OrdinalIgnoreCase)) + 1).ToString(), m.Index, m.Length, false)));
        return found.OrderBy(f => f.Index).ToList();
    }

    /// <summary>The words that name what a number counts: those after it in its clause, or else those before it.</summary>
    private static List<PaperFact> NamedFacts(string sentence, int index, int length, IReadOnlyList<PaperFact> facts)
    {
        string after = sentence[(index + length)..];
        var stop = ClauseStop.Match(after);
        after = stop.Success ? after[..stop.Index] : after;
        var named = Named(string.Join(' ', after.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5)), facts);
        if (named.Count > 0) return named;
        string before = sentence[..index];
        int cut = Math.Max(before.LastIndexOfAny(",;:—–()".ToCharArray()), Regex.Match(before, @"\d(?=\D*$)").Index);
        before = cut > 0 ? before[(cut + 1)..] : before;
        return Named(string.Join(' ', before.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(5)), facts);
    }

    private static List<PaperFact> Named(string window, IReadOnlyList<PaperFact> facts) =>
        facts.Where(f => f.Words.Any(w => Regex.IsMatch(window, @"\b(?:" + w + ")", RegexOptions.IgnoreCase))).ToList();

    /// <summary>Check 1: every number of the abstract and the conclusion is one of the run's counts or in the results, and is attached to the right count.</summary>
    internal static PaperCheck NumbersCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "numbers",
            Title = "Numbers in the abstract and conclusion",
            Explanation = "Every number in the abstract and the conclusion should be one of the run's own counts or a figure from the results, " +
                "and a count should name the right thing: the number of studies included is not the number screened. Names with a number in them, years and citation markers are left out.",
        };
        var known = new HashSet<string>(input.Facts.Select(f => f.Value.ToString()));
        known.UnionWith(ReviewWriter.NumbersIn(input.FactsText));
        known.UnionWith(ReviewWriter.NumbersIn(Plain(string.Join("\n", input.Of(PaperPartKind.Theme, PaperPartKind.Overview).Select(p => p.Text)))));
        known.Add(input.Questions.Count.ToString());
        int numbers = 0;
        foreach (var part in input.Of(PaperPartKind.Abstract, PaperPartKind.Conclusion))
            foreach (var sentence in CitationSupportChecker.SplitSentences(Plain(part.Text)))
                foreach (var (value, index, length, figure) in NumbersOf(sentence))
                {
                    numbers++;
                    if (!known.Contains(value))
                    {
                        check.Evidence.Add(new(part.Name, part.Field, Short(sentence), $"{value} is neither one of the run's counts nor a figure in the results."));
                        continue;
                    }
                    // "3 of the 4 included studies": the second number is the whole the first is part of, not what the noun counts.
                    if (figure || Partitive.IsMatch(sentence[..index])) continue;
                    var named = NamedFacts(sentence, index, length, input.Facts);
                    if (named.Count == 0 || named.Any(f => f.Value.ToString() == value)) continue;
                    var actual = input.Facts.Where(f => f.Value.ToString() == value).Select(f => f.Label).ToList();
                    check.Evidence.Add(new(part.Name, part.Field, Short(sentence),
                        $"{value} is given as {named[0].Label}, but the run counts {named[0].Value} {named[0].Label}" +
                        (actual.Count > 0 ? $"; {value} is the number of {string.Join(" or ", actual)}." : ".")));
                }
        check.Outcome = check.Evidence.Count == 0 ? CheckOutcome.Pass : CheckOutcome.Fault;
        check.Summary = check.Evidence.Count == 0
            ? $"All {Plural(numbers, "number", "numbers")} match the run's counts or the results."
            : $"{Plural(check.Evidence.Count, "number does", "numbers do")} not match the run's counts or the results.";
        return check;
    }

    /// <summary>The first sentence of a text that uses <paramref name="figure"/> as a result figure.</summary>
    private static string SentenceWith(string text, string figure, string facts) =>
        CitationSupportChecker.SplitSentences(text).FirstOrDefault(s => ReviewWriter.ResultFigures(Plain(s), facts).Contains(figure)) ?? "";

    /// <summary>
    /// Check 5: a result figure appears in its theme, in the abstract and in one place after the results (one answer, the
    /// discussion or one closing part), and never in the directions for future research or the conclusion (the figure
    /// guard's rule, issue #452). The guard sends an answer back twice and then keeps it; this says what was kept.
    /// </summary>
    internal static PaperCheck FiguresCheck(PaperCheckInput input)
    {
        var check = new PaperCheck
        {
            Id = "figures",
            Title = "Result figures not repeated",
            Explanation = "A figure from the results (a percentage, an effect size, a count a study reports) belongs in its theme, the abstract and at most one place after the results. " +
                "The directions for future research and the conclusion say what the results mean, so they repeat no figure.",
        };
        var results = input.Of(PaperPartKind.Theme).ToList();
        if (results.Count == 0) results = input.Of(PaperPartKind.Overview).ToList();
        string facts = string.Join(" ", input.Facts.Select(f => f.Value)) + "\n" + input.FactsText + $"\n{input.Questions.Count}";
        var figures = ReviewWriter.ResultFigures(Plain(string.Join("\n", results.Select(p => p.Text))), facts);
        var after = input.Of(PaperPartKind.Discussion, PaperPartKind.Answer, PaperPartKind.Limitations, PaperPartKind.Implications).ToList();
        var none = input.Of(PaperPartKind.FutureResearch, PaperPartKind.Conclusion).ToList();
        int repeated = 0;
        foreach (var figure in figures.OrderBy(f => f, StringComparer.Ordinal))
        {
            var used = after.Where(p => ReviewWriter.ResultFigures(Plain(p.Text), facts).Contains(figure)).ToList();
            var barred = none.Where(p => ReviewWriter.ResultFigures(Plain(p.Text), facts).Contains(figure)).ToList();
            var extra = used.Skip(1).Concat(barred).ToList();
            if (extra.Count == 0) continue;
            repeated++;
            var places = used.Concat(barred).Select(p => p.Name).ToList();
            string where = places.Count == 1 ? places[0] : string.Join(", ", places.Take(places.Count - 1)) + " and " + places[^1];
            check.Evidence.Add(new(string.Join(", ", extra.Select(p => p.Name)), extra[0].Field, Short(SentenceWith(extra[0].Text, figure, facts)),
                $"{figure} from the results appears again in: {where}." + (barred.Count > 0 ? $" {string.Join(" and ", barred.Select(p => p.Name))} should use none." : "")));
        }
        check.Outcome = check.Evidence.Count == 0 ? CheckOutcome.Pass : CheckOutcome.Fault;
        check.Summary = figures.Count == 0 ? "The results give no figures to repeat."
            : check.Evidence.Count == 0 ? $"None of the {Plural(figures.Count, "result figure", "result figures")} is repeated beyond its place."
            : $"{Plural(repeated, "result figure is", "result figures are")} repeated where the rule does not allow it.";
        return check;
    }
}
