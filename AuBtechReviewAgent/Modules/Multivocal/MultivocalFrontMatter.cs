using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The facts the abstract of a multivocal paper may use, counted in code, and the check in code that the abstract uses
/// each pool's numbers for that pool (#457). A real run's abstract said "433 grey sources" for the grey and academic
/// sources together, and that 205 passed the grey quality threshold, though 122 of them were academic sources that are
/// never held to it: the facts it was given named pooled counts as grey. With a formal pool the facts now give each pool
/// its own numbers, and the check sends back an abstract that ties a number to the wrong pool.
/// </summary>
public static class MultivocalFrontMatter
{
    /// <summary>The facts for the front-matter prompt; <paramref name="sectionsWritten"/> is how many theme sections were written.</summary>
    public static string Facts(MultivocalReportInput input, int? sectionsWritten = null)
    {
        var plan = input.Planned.Plan;
        int themed = MultivocalPaperParts.ShownThemes(input).Count;
        int written = Math.Min(sectionsWritten ?? themed, themed);
        string themes = $"Themes written up as result sections: {written}" +
            (written < themed ? $"; {themed - written} more {(themed - written == 1 ? "theme" : "themes")} could not be written up and {(themed - written == 1 ? "is" : "are")} listed with {(themed - written == 1 ? "its" : "their")} sources only" : "") +
            ". Questions about frequency, existence or a relationship are answered by counting, in tables, not by themes. " + MultivocalRelationship.FactsLine(input) +
            $"Research questions: {plan.Numbered().Count}. ";
        string head = $"Kind of review: {plan.ReviewKind} following the Garousi et al. (2019) guidelines. ";
        if (!input.Ledger.HasFormalPool)
        {
            var f = MultivocalPaperParts.Flow(input);
            return head +
                   $"Grey sources found by the searches: {f.Found}, from {f.Searches} searches. " + MultivocalScreeningText.Facts(input.Screening, f.Screened) +
                   $"Included at screening, before the quality check: {f.Included}; excluded at screening: {f.Excluded}. " +
                   $"Passed the quality check ({input.Quality.Threshold} of {input.Quality.MaxPoints} points or more) and kept for the synthesis: {f.Passed}; below the threshold: {f.BelowThreshold}; not assessed (page not readable): {f.NotAssessed}. " +
                   $"Extracted against the systematic map: {f.Extracted}. Findings: {f.Findings}. " + themes +
                   "Use each number only with the meaning given here: the sources the results rest on are the ones kept for the synthesis, not the ones included at screening.";
        }
        var (g, a) = Pools(input);
        var (greySearches, academicSearches) = Searches(input);
        return head +
               "The review has two pools, counted apart: grey literature and academic literature. Every number below says which pool it counts; say the same in the abstract, and never call an academic source or a total of both pools grey. " +
               $"Grey sources found by {greySearches} grey searches: {g.Identified}. " +
               $"Academic records returned by {academicSearches} searches of academic databases: {a.Identified}; {a.DuplicatesRemoved + a.NoAddressRemoved} of them set aside before screening as duplicates or without a web address, leaving {a.Identified - a.DuplicatesRemoved - a.NoAddressRemoved} academic sources. " +
               $"Grey and academic sources together, after duplicates: {input.Ledger.Sources.Count}. " +
               MultivocalScreeningText.Facts(input.Screening, g.Screened + a.Screened) + $"Of them, grey: {g.Screened}; academic: {a.Screened}. " +
               $"Included at screening: {g.Included} grey and {a.Included} academic ({g.Included + a.Included} together); excluded: {g.Excluded} grey and {a.Excluded} academic. " +
               $"The quality check on the grey literature checklist applies to grey sources only: {g.Kept} grey sources passed it ({input.Quality.Threshold} of {input.Quality.MaxPoints} points or more), {g.BelowThreshold} fell below it and {g.NotAssessed} could not be assessed (page not readable). " +
               $"Academic sources have no threshold: they were appraised with MMAT, the appraisal tool of the systematic review, and kept whatever their appraisal; {a.Kept} were appraised and {a.NotAssessed} could not be appraised. " +
               $"Kept for the synthesis, both pools together: {g.Kept + a.Kept} ({g.Kept} grey and {a.Kept} academic). " +
               $"Extracted against the systematic map: {g.Extracted} grey and {a.Extracted} academic sources ({g.Extracted + a.Extracted} together), {a.AbstractOnly} of the academic ones on the abstract alone. " +
               $"Findings: {input.Synthesis.Findings.Count}. " + themes +
               "Use each number only with the meaning and the pool given here: the sources the results rest on are the ones kept for the synthesis, not the ones included at screening.";
    }

    private static readonly Regex PoolPhrase = new(
        @"(?<![\d.,])(?<n>\d+)(?<mid>(?:\s+[A-Za-z-]+){0,2}?)\s+(?<pool>grey|gray|academic|formal|scholarly|peer-reviewed)\b(?:\s+(?:and|or)\s+(?<other>grey|gray|academic|formal|scholarly|peer-reviewed)\b)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private const string Grey = "grey", Academic = "academic", Both = "both";

    private static readonly HashSet<string> LinkWords = new(StringComparer.OrdinalIgnoreCase) { "are", "were", "is", "was", "be", "been", "being", "of", "from", "than", "and", "or", "to", "in" };

    /// <summary>
    /// Why the abstract of a review with both pools misuses a pool's number, or null. A number followed within two words
    /// by "grey" or "academic" must count that pool, as the facts give it ("433 grey sources" is refused when 433 counts
    /// both pools); and a sentence on the quality threshold may not give the total kept by both pools without the grey count.
    /// A number the check cannot tie to a pool is left to the shared check that every number is in the facts.
    /// </summary>
    public static string? AbstractProblem(string abstractText, MultivocalReportInput input)
    {
        if (!input.Ledger.HasFormalPool || string.IsNullOrWhiteSpace(abstractText)) return null;
        var counts = Counts(input);
        foreach (Match m in PoolPhrase.Matches(abstractText))
        {
            string n = m.Groups["n"].Value;
            if (!counts.TryGetValue(n, out var meanings)) continue;
            // "122 of the 205 are academic" says what part of 205 is academic; only a number that names its pool is checked.
            if (m.Groups["mid"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => LinkWords.Contains(w))) continue;
            string said = m.Groups["other"].Success && PoolOf(m.Groups["other"].Value) != PoolOf(m.Groups["pool"].Value) ? Both : PoolOf(m.Groups["pool"].Value);
            if (meanings.Any(x => x.Pool == said)) continue;
            return $"the abstract says \"{m.Value.Trim()}\", but in the facts {n} counts {string.Join(" or ", meanings.Select(x => x.Meaning).Distinct())}; give each pool its own number, as the facts do.";
        }
        var (g, a) = Pools(input);
        int keptBoth = g.Kept + a.Kept;
        if (a.Kept > 0)
        {
            string threshold = $"{input.Quality.Threshold.ToString(CultureInfo.InvariantCulture)} of {input.Quality.MaxPoints.ToString(CultureInfo.InvariantCulture)}";
            foreach (string sentence in CitationSupportChecker.SplitSentences(abstractText))
            {
                var numbers = ReviewWriter.NumbersIn(sentence);
                bool onThreshold = sentence.Contains(threshold, StringComparison.Ordinal) || Regex.IsMatch(sentence, @"\bthreshold\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                if (onThreshold && numbers.Contains(Num(keptBoth)) && !numbers.Contains(Num(g.Kept)))
                    return $"the abstract ties the {keptBoth} sources kept by both pools to the quality threshold, but only the {g.Kept} grey sources were held to it; the {a.Kept} academic sources were appraised with MMAT and kept whatever their appraisal.";
            }
        }
        return null;
    }

    /// <summary>Each pool-specific number of the facts with the pool it counts and what it counts, for <see cref="AbstractProblem"/>.</summary>
    private static Dictionary<string, List<(string Pool, string Meaning)>> Counts(MultivocalReportInput input)
    {
        var (g, a) = Pools(input);
        var (greySearches, academicSearches) = Searches(input);
        var map = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        void Add(int n, string pool, string meaning)
        {
            if (n <= 0) return;
            if (!map.TryGetValue(Num(n), out var list)) map[Num(n)] = list = new();
            list.Add((pool, meaning));
        }
        Add(g.Identified, Grey, "the grey sources found");
        Add(greySearches, Grey, "the grey searches");
        Add(g.Screened, Grey, "the grey sources screened");
        Add(g.Included, Grey, "the grey sources included");
        Add(g.Kept, Grey, "the grey sources that passed the quality threshold");
        Add(g.Extracted, Grey, "the grey sources extracted");
        Add(a.Identified, Academic, "the academic records found");
        Add(academicSearches, Academic, "the academic searches");
        Add(a.Identified - a.DuplicatesRemoved - a.NoAddressRemoved, Academic, "the academic sources after duplicates");
        Add(a.Screened, Academic, "the academic sources screened");
        Add(a.Included, Academic, "the academic sources included");
        Add(a.Kept, Academic, "the academic sources appraised with MMAT");
        Add(a.Extracted, Academic, "the academic sources extracted");
        Add(input.Ledger.Sources.Count, Both, "the grey and academic sources together");
        Add(greySearches + academicSearches, Both, "the grey and academic searches together");
        Add(g.Screened + a.Screened, Both, "the grey and academic sources screened together");
        Add(g.Included + a.Included, Both, "the grey and academic sources included together");
        Add(g.Kept + a.Kept, Both, "the grey and academic sources kept together");
        Add(g.Extracted + a.Extracted, Both, "the grey and academic sources extracted together");
        return map;
    }

    private static string PoolOf(string word) => word.ToLowerInvariant() is "grey" or "gray" ? Grey : Academic;

    private static (PoolCounts Grey, PoolCounts Academic) Pools(MultivocalReportInput input) => (
        MultivocalPools.For(false, input.Ledger, input.Screening, input.Quality, input.Extraction),
        MultivocalPools.For(true, input.Ledger, input.Screening, input.Quality, input.Extraction));

    private static (int Grey, int Academic) Searches(MultivocalReportInput input)
    {
        var ran = input.Ledger.Searches.Where(s => s.Error == null).ToList();
        return (ran.Count(s => !s.Formal), ran.Count(s => s.Formal));
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
