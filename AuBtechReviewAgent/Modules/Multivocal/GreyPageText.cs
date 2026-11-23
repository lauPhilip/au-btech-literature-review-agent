using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// What the model is given of a grey source's kept page text (#391). A long page is no longer sent as its first 40,000
/// characters but as passages of 300 to 600 characters (<see cref="PassageSelection.Lines"/>), each copied from the kept
/// text, so every quote is still checked against what was sent and found in the kept page.
///
/// For the extraction (G12), as for a formal source (<see cref="FormalExtractionText"/>): the page's first passages and
/// the passages most relevant to the map and the questions. For the quality check (Table 7) more is kept, because a
/// point the page earns but the model cannot see is a point lost: the start of the page (title, author, date) and its
/// end (references, links, about the author) always, then for each item the model answers the passage with the most of
/// that item's words, then the passages most relevant to the topic, up to <see cref="QualityBudget"/> characters. A page
/// up to <see cref="QualityWholeUpTo"/> characters is sent whole.
/// </summary>
public static class GreyPageText
{
    /// <summary>Up to this many characters, a page is sent whole to the extraction.</summary>
    public const int ExtractionWholeUpTo = FormalExtractionText.WholeUpTo;

    /// <summary>Up to this many characters, a page is sent whole to the quality check.</summary>
    public const int QualityWholeUpTo = 12_000;

    /// <summary>The quality check's passages chosen by the topic are added until the selection has this many characters.</summary>
    public const int QualityBudget = 10_000;

    /// <summary>At least this much of the start and of the end of a page goes to the quality check.</summary>
    public const int QualityEdge = 1_200;

    /// <summary>What a page is called in what was sent, e.g. "21 of 96 passages of the page (…)".</summary>
    public const string Of = "the page";

    /// <summary>The text the extraction is given of a kept page.</summary>
    public static TextForModel ForExtraction(string text, MultivocalPlan plan, MapVersion map)
    {
        if (text.Length <= ExtractionWholeUpTo) return new TextForModel(text, null);
        var passages = PassageSelection.Lines(text);
        int lead = FormalExtractionText.LeadPassages, relevant = FormalExtractionText.RelevantPassages;
        if (passages.Count <= lead + relevant) return new TextForModel(text, null);
        var chosen = passages.Take(lead).Concat(PassageSelection.MostRelevant(text, passages.Skip(lead).ToList(), FormalExtractionText.Terms(plan, map), relevant));
        return PassageSelection.Join(text, chosen, passages.Count, Of);
    }

    /// <summary>The text the quality check is given of a kept page; <paramref name="topic"/> is the review's topic and questions.</summary>
    public static TextForModel ForQuality(string text, string topic)
    {
        if (text.Length <= QualityWholeUpTo) return new TextForModel(text, null);
        var passages = PassageSelection.Lines(text);
        var chosen = new HashSet<Passage>();
        int chars = 0;
        void Take(Passage p) { if (chosen.Add(p)) chars += p.Length; }

        // The start and the end of the page, where its title, author, date, references and links usually are.
        for (int i = 0; i < passages.Count && (i < 2 || chars < QualityEdge); i++) Take(passages[i]);
        int fromEnd = 0;
        for (int i = passages.Count - 1; i >= 0 && (passages.Count - i <= 2 || fromEnd < QualityEdge); i--)
        {
            fromEnd += passages[i].Length;
            Take(passages[i]);
        }
        if (chosen.Count >= passages.Count) return new TextForModel(text, null);

        // For each item, the passage not yet chosen with the most of its words, so an item whose best passage another
        // item took still gets one of its own; an item none of whose words is on the page adds nothing.
        var middle = passages.Where(p => !chosen.Contains(p)).ToList();
        foreach (var words in ItemWords.Values)
        {
            var cues = Stems(words);
            var best = middle.Where(p => !chosen.Contains(p)).Select(p => (Passage: p, Score: Stems(PassageSelection.TextOf(text, p)).Where(cues.Contains).Sum(w => Common.Contains(w) ? 1 : 2)))
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Passage.Number).Select(x => x.Passage).Take(1);
            foreach (var p in best) Take(p);
        }

        // Then the passages most about the topic, while the selection is under the budget.
        var topicTerms = TextRelevance.Terms(topic);
        foreach (var p in middle.Where(p => !chosen.Contains(p))
                     .Select(p => (Passage: p, Score: TextRelevance.Score(topicTerms, PassageSelection.TextOf(text, p))))
                     .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Passage.Number).Select(x => x.Passage))
        {
            if (chars >= QualityBudget) break;
            if (chars + p.Length <= QualityBudget) Take(p);
        }
        return PassageSelection.Join(text, chosen, passages.Count, Of);
    }

    /// <summary>The method's sentence on the quality check of long pages, written in code; empty when every page was sent whole.</summary>
    public static string QualityMethod(IEnumerable<GreyQuality> grey)
    {
        int n = grey.Count(q => q.Passages != null);
        return n == 0 ? "" : $" For {Pages(n)}, the model was given the start and the end of the page and the passages that share the most words with each checklist item and with the topic, not the whole page; every point and quote rests on those passages.";
    }

    /// <summary>The method's sentence on the extraction of long grey pages, written in code; empty when every page was sent whole.</summary>
    public static string ExtractionMethod(MultivocalExtractionFile extraction)
    {
        int n = extraction.Sources.Count(s => !s.Formal && s.Error == null && s.Passages != null);
        return n == 0 ? "" : $" For {Pages(n)}, the model was given the first passages and the passages that share the most words with the map and the questions, not the whole page; each quote was found in those passages.";
    }

    private static string Pages(int n) => $"{n.ToString(System.Globalization.CultureInfo.InvariantCulture)} long {(n == 1 ? "page" : "pages")}";

    /// <summary>
    /// The words of a text as the item cues match them: lower case, cut to their first six letters so "measured" and
    /// "measurements" match, with "?" and "%" kept as words, since a question and a percentage are cues too.
    /// </summary>
    public static HashSet<string> Stems(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+|[?%]")
            .Select(m => m.Value.Length > 6 ? m.Value[..6] : m.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Cue words that ordinary text also uses often; they count half as much as the others.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "only", "not", "may", "how", "why", "?", "our", "my", "yet", "new", "other", "hand", "given", "hold", "both", "see", "also",
        "show", "cover", "first", "case", "works", "workin", "lead", "head", "years", "post", "group", "team", "data", "number",
    };

    /// <summary>
    /// The cue words of each item the model answers (Garousi et al. 2019, Table 7), and of an opinion piece's claim
    /// (G13); a passage's relevance to an item is how many of them it has (<see cref="Stems"/>), a common one counting half.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ItemWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["1.1"] = "organisation organization company institute institution university foundation publisher official agency government reputable founded group lab laboratory association consortium nonprofit",
        ["1.2"] = "author written works working team member employee joined company university engineer staff lab research",
        ["1.3"] = "previously published book books articles posts wrote written talk talks papers series author speaker conference earlier",
        ["1.4"] = "role title engineer researcher professor director architect founder cto ceo head lead experience years decade expert expertise specialist principal senior phd career",
        ["2.1"] = "aim goal purpose objective post article guide explain show introduce describe tutorial walk learn cover",
        ["2.2"] = "method methodology approach measured benchmark experiment survey interviewed collected sample tested evaluated setup analysis ran runs repeated averaged configuration",
        ["2.3"] = "references sources according cited study studies research report documentation doi http paper papers blog original survey",
        ["2.4"] = "limitation limits limited caveat caveats threats validity constraint drawback downside disclaimer assumption rough only not may hold generalise generalize",
        ["2.5"] = "question questions answer whether problem focus ? why how",
        ["2.6"] = "case company team users participants project customers organisation developers practitioners respondents enterprise clients production deployment logs",
        ["3.1"] = "pros cons advantages disadvantages trade-off tradeoffs both alternative compared comparison weaknesses strengths balanced option options prefer however other hand",
        ["3.2"] = "evidence data measured results objective fact facts percent % rate fell rose increase decrease reduced numbers",
        ["3.3"] = "disclosure sponsored sponsor affiliate vendor pricing trial buy sell partner paid advertisement product our company",
        ["3.4"] = "conclusion conclude results show data evidence findings summary given therefore thus default recommend",
        ["5.1"] = "related see also further reading links linked earlier previous prior discussed described references blog article post builds",
        ["6.1"] = "first novel unique unlike different contribution nobody yet new",
        ["6.2"] = "contrary refute disagree confirm support common belief myth conventional wisdom challenge against recommend advice popular",
        ["claim"] = "believe think argue opinion should view claim convinced personally my",
    };
}
