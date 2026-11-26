using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>The numbers behind the PRISMA 2020 flow diagram, taken from the run ledger.</summary>
public class PrismaFlowCounts
{
    public List<(string Source, int Count)> IdentifiedBySource { get; set; } = new();
    public int Identified { get; set; }
    public int IdentifiedViaCitations { get; set; }  // PRISMA "other methods": citation chaining
    public int IdentifiedViaSeedChaining { get; set; }  // ...of which by building out from the most-cited included studies
    public int DuplicatesRemoved { get; set; }
    public int OutsideDateRange { get; set; }
    public int CappedBeyondMax { get; set; }
    public int Screened { get; set; }
    public int ScreeningErrors { get; set; }
    public int ExcludedPeerReview { get; set; }
    public int ExcludedAtScreening { get; set; }
    /// <summary>Of <see cref="ExcludedAtScreening"/>: studies included at screening, then excluded because they were retracted.</summary>
    public int ExcludedRetracted { get; set; }
    /// <summary>The screening exclusions (not the peer-review filter) by reason, when the run recorded reasons.</summary>
    public List<(string Reason, int Count)> ExcludedByReason { get; set; } = new();
    public int HumanOverrides { get; set; }
    public bool HumanReviewed { get; set; }
    public int Included { get; set; }
    public int FullTextRetrieved { get; set; }
    public int AbstractOnly => Math.Max(0, Included - FullTextRetrieved);

    /// <summary>
    /// Why the full texts of included studies could not be read, from the run's FullTextSources ("none (reason)"), grouped
    /// and counted. Those studies are kept and read from the abstract only.
    /// </summary>
    public List<(string Reason, int Count)> NotRetrievedByReason { get; set; } = new();

    /// <summary>The reasons in FullTextSources values such as "none (download failed: 403)", grouped and counted, the most frequent first.</summary>
    public static List<(string Reason, int Count)> NotRetrievedReasons(IEnumerable<string> sources) =>
        sources.Where(v => v.StartsWith("none", StringComparison.Ordinal))
            .Select(v =>
            {
                int open = v.IndexOf('(');
                string inner = open < 0 ? "" : v[(open + 1)..].TrimEnd(')').Trim();
                int colon = inner.IndexOf(':');
                if (colon >= 0) inner = inner[..colon].Trim();
                return inner.Length == 0 ? "No reason recorded" : char.ToUpperInvariant(inner[0]) + inner[1..];
            })
            .GroupBy(r => r).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count())).ToList();

    /// <summary>The lines under "Reports not retrieved": each reason with its count, only when they add up to the count.</summary>
    public List<string> NotRetrievedLines() =>
        NotRetrievedByReason.Sum(r => r.Count) == AbstractOnly ? NotRetrievedByReason.Select(r => $"{r.Reason} (n = {r.Count})").ToList() : new();

    /// <summary>Every identified record must end up in exactly one box. False means the counters disagree.</summary>
    public bool IsConsistent =>
        Identified == DuplicatesRemoved + OutsideDateRange + CappedBeyondMax + Screened + ScreeningErrors
        && Screened == ExcludedPeerReview + ExcludedAtScreening + Included;

    public static PrismaFlowCounts From(ReviewState state)
    {
        var s = state.Stats;
        return new PrismaFlowCounts
        {
            IdentifiedBySource = state.SearchLogs
                .Where(l => !l.SourceName.EndsWith("(citation chaining)", StringComparison.Ordinal) && !l.SourceName.EndsWith("(seed chaining)", StringComparison.Ordinal))
                .GroupBy(l => l.SourceName)
                .Select(g => (g.Key, g.Sum(l => l.PapersFound)))
                .ToList(),
            Identified = s.TotalIdentified,
            IdentifiedViaCitations = s.IdentifiedViaCitations,
            IdentifiedViaSeedChaining = s.IdentifiedViaSeedChaining,
            DuplicatesRemoved = s.DuplicatesRemoved,
            OutsideDateRange = s.OutsideDateRange,
            CappedBeyondMax = s.CappedBeyondMaxResults,
            Screened = s.Screened,
            ScreeningErrors = s.ScreeningErrors,
            ExcludedPeerReview = s.FailedPeerReviewCheck,
            ExcludedAtScreening = s.Excluded,
            ExcludedByReason = ExclusionReasons.Count(state.Phases.Screening)
                .Where(g => g.Key != ExclusionReasons.NotPeerReviewed && g.Key != ExclusionReasons.Unspecified && g.Key != ExclusionReasons.Retracted)
                .Select(g => (g.Label, g.Count))
                .ToList(),
            ExcludedRetracted = state.Phases.Screening.Count(l => l.Decision == "Excluded" && l.ExclusionReason == ExclusionReasons.Retracted),
            HumanOverrides = s.HumanOverrides,
            HumanReviewed = s.HumanReviewed > 0,
            Included = s.Included,
            FullTextRetrieved = s.FullTextRetrieved,
            NotRetrievedByReason = NotRetrievedReasons(state.FullTextSources.Values),
        };
    }
}

/// <summary>Renders the PRISMA 2020 flow diagram (identification, screening, retrieval, eligibility, included) as TikZ for main.tex.</summary>
public static class PrismaFlowDiagram
{
    public static string ToTikz(PrismaFlowCounts c)
    {
        string Box(string name, string position, string text) =>
            $"\\node[flowbox{position}] ({name}) {{{text}}};";

        string sources = c.IdentifiedBySource.Count == 0
            ? ""
            : "\\\\ " + string.Join("\\\\ ", c.IdentifiedBySource.Select(x => $"{Escape(x.Source)} (n = {x.Count})"));
        sources += OtherMethods(c.IdentifiedViaCitations, c.IdentifiedViaSeedChaining);

        var removed = new List<string> { $"Duplicates (n = {c.DuplicatesRemoved})" };
        if (c.OutsideDateRange > 0) removed.Add($"Outside year range (n = {c.OutsideDateRange})");
        if (c.CappedBeyondMax > 0) removed.Add($"Over per-source cap (n = {c.CappedBeyondMax})");
        if (c.ScreeningErrors > 0) removed.Add($"Screening failed (n = {c.ScreeningErrors})");

        var excluded = new List<string>();
        if (c.ExcludedPeerReview > 0) excluded.Add($"Not peer reviewed (n = {c.ExcludedPeerReview})");
        excluded.Add($"Did not meet criteria (n = {c.ExcludedAtScreening - c.ExcludedRetracted})");
        foreach (var (reason, count) in c.ExcludedByReason)
            excluded.Add($"\\quad {Escape(reason)} (n = {count})");
        if (c.ExcludedRetracted > 0) excluded.Add($"Retracted (n = {c.ExcludedRetracted})");
        if (c.HumanReviewed) excluded.Add($"Decisions changed by reviewer: {c.HumanOverrides}");

        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[node distance=0.55cm and 0.9cm, font=\scriptsize,");
        sb.AppendLine(@"  flowbox/.style={draw, rounded corners=2pt, align=center, text width=5.2cm, inner sep=4pt, fill=white},");
        sb.AppendLine(@"  sidebox/.style={draw, rounded corners=2pt, align=left, text width=4.6cm, inner sep=4pt, fill=black!3},");
        sb.AppendLine(@"  arrow/.style={-{Stealth[length=2mm]}}]");
        sb.AppendLine(Box("id", "", $"Records identified{(c.IdentifiedViaCitations > 0 ? "" : " from databases")} (n = {c.Identified}){sources}"));
        sb.AppendLine($"\\node[sidebox, right=of id] (removed) {{Records removed before screening:\\\\ {string.Join("\\\\ ", removed)}}};");
        sb.AppendLine(Box("screened", ", below=of id", $"Records screened (n = {c.Screened})"));
        sb.AppendLine($"\\node[sidebox, right=of screened] (excluded) {{Records excluded:\\\\ {string.Join("\\\\ ", excluded)}}};");
        // The retrieval and eligibility boxes of PRISMA 2020 (item 16a). The tool judges eligibility once, on title and
        // abstract, so a study whose full text was not found is kept and read from its abstract, and none is excluded on
        // its full text; the boxes say so rather than show an assessment that did not happen.
        sb.AppendLine(Box("sought", ", below=of screened", $"Reports sought for retrieval (n = {c.Included})"));
        var notRetrieved = c.NotRetrievedLines().Select(Escape).Append("Kept and read from the abstract only");
        sb.AppendLine($"\\node[sidebox, right=of sought] (notretrieved) {{Reports not retrieved (n = {c.AbstractOnly}):\\\\ {string.Join("\\\\ ", notRetrieved)}}};");
        sb.AppendLine(Box("assessed", ", below=of sought", $"Reports assessed for eligibility (n = {c.Included})\\\\ on title and abstract at screening; full text used (n = {c.FullTextRetrieved}); abstract only (n = {c.AbstractOnly})"));
        sb.AppendLine($"\\node[sidebox, right=of assessed] (reportsexcluded) {{Reports excluded (n = 0):\\\\ the tool does not exclude a study on its full text}};");
        sb.AppendLine(Box("included", ", below=of assessed", $"Studies included in review (n = {c.Included})\\\\ Reports of included studies (n = {c.Included})"));
        sb.AppendLine(@"\draw[arrow] (id) -- (screened);");
        sb.AppendLine(@"\draw[arrow] (screened) -- (sought);");
        sb.AppendLine(@"\draw[arrow] (sought) -- (assessed);");
        sb.AppendLine(@"\draw[arrow] (assessed) -- (included);");
        sb.AppendLine(@"\draw[arrow] (id) -- (removed);");
        sb.AppendLine(@"\draw[arrow] (screened) -- (excluded);");
        sb.AppendLine(@"\draw[arrow] (sought) -- (notretrieved);");
        sb.AppendLine(@"\draw[arrow] (assessed) -- (reportsexcluded);");
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    /// <summary>
    /// The "other methods" lines of the identification box: citation chaining, split into the most-cited seeds and the
    /// other included studies when the seed step found records. Shared with the multivocal flow diagram.
    /// </summary>
    public static string OtherMethods(int viaCitations, int viaSeeds)
    {
        if (viaCitations <= 0) return "";
        if (viaSeeds <= 0) return $"\\\\ Other methods: citation chaining (n = {viaCitations})";
        string lines = $"\\\\ Other methods: citation chaining from the most-cited included studies (n = {viaSeeds})";
        int other = viaCitations - viaSeeds;
        return other > 0 ? lines + $"\\\\ Other methods: citation chaining from the other included studies (n = {other})" : lines;
    }

    private static string Escape(string s) => s.Replace("&", @"\&").Replace("_", @"\_").Replace("%", @"\%").Replace("#", @"\#");
}
