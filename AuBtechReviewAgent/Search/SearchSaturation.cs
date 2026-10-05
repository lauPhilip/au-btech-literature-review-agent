using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// What one search string brought in: against how many sources it was run (a source stops once its cap is
/// filled), the records retrieved over those sources, and how many of them no earlier string had found.
/// </summary>
public sealed record SearchStringYield(string SearchString, int Searches, int Retrieved, int New);

/// <summary>
/// A simple saturation check for the search: the search strings are taken in the order they were run, and for each
/// one the records that none of the earlier strings had found are counted (by identifier, DOI or normalised title,
/// as in duplicate removal). When the last strings add little that is new, more phrasings of the same question are
/// unlikely to find much more; when the last one still adds a lot, the search has not settled and a further string
/// or citation chaining may be worth it. It says nothing about databases that were not searched.
/// </summary>
public static class SearchSaturation
{
    /// <summary>Below this share of new records, the last string counts as adding little.</summary>
    public const double LowShare = 0.10;

    /// <param name="passes">One entry per search actually run: which string, and the records it returned.</param>
    public static List<SearchStringYield> Compute(IReadOnlyList<string> searchStrings, IEnumerable<(int StringIndex, IReadOnlyList<AcademicPaper> Papers)> passes)
    {
        var bySearchString = passes
            .Where(r => r.StringIndex >= 0 && r.StringIndex < searchStrings.Count)
            .GroupBy(r => r.StringIndex)
            .ToDictionary(g => g.Key, g => (Searches: g.Count(), Papers: g.SelectMany(r => r.Papers).ToList()));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yields = new List<SearchStringYield>();
        for (int i = 0; i < searchStrings.Count; i++)
        {
            var (searches, papers) = bySearchString.TryGetValue(i, out var run) ? run : (0, new List<AcademicPaper>());
            int fresh = 0;
            var thisString = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var paper in papers)
            {
                var keys = Keys(paper).ToList();
                if (keys.Count == 0) continue;
                bool knownBefore = keys.Any(seen.Contains);
                bool countedHere = keys.Any(thisString.Contains); // the same record from a second source
                if (!knownBefore && !countedHere) fresh++;
                foreach (var key in keys) thisString.Add(key);
            }
            foreach (var key in thisString) seen.Add(key);
            yields.Add(new SearchStringYield(searchStrings[i], searches, papers.Count, fresh));
        }
        return yields;
    }

    private static IEnumerable<string> Keys(AcademicPaper paper)
    {
        if (!string.IsNullOrWhiteSpace(paper.Id)) yield return "id:" + paper.Id;
        if (PrismaReviewEngine.NormalizeDoi(paper.Doi) is { } doi) yield return "doi:" + doi;
        string title = PrismaReviewEngine.NormalizeTitle(paper.Title);
        if (title.Length > 0) yield return "title:" + title;
    }

    /// <summary>One plain sentence about the last search string, or null when there was only one string or nothing was found.</summary>
    public static string? Hint(IReadOnlyList<SearchStringYield> yields)
    {
        var ran = yields.Where(y => y.Searches > 0).ToList();
        if (ran.Count < 2) return null;
        var last = ran[^1];
        int total = yields.Sum(y => y.New);
        if (total == 0) return null;
        string records(int n) => n == 1 ? "1 new record" : $"{n} new records";
        string earlier = string.Join(", ", ran.Take(ran.Count - 1).Select(y => y.New));
        string skipped = ran.Count < yields.Count ? $" {yields.Count - ran.Count} further string(s) were not needed because every source had already filled its cap." : "";
        string head = $"The last search string added {records(last.New)} (of {last.Retrieved} retrieved); the earlier ones added {earlier}.";
        if (last.Retrieved == 0) return $"The last search string retrieved nothing; the earlier ones added {earlier} new records.{skipped}";
        return last.New <= Math.Max(1, (int)Math.Floor(last.Retrieved * LowShare))
            ? head + " The search is close to saturation: more phrasings of the same question are unlikely to find much more." + skipped
            : head + " The search has not settled yet: another phrasing or citation chaining may find further studies." + skipped;
    }
}
