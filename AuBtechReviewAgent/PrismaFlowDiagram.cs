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
    public int DuplicatesRemoved { get; set; }
    public int OutsideDateRange { get; set; }
    public int CappedBeyondMax { get; set; }
    public int Screened { get; set; }
    public int ScreeningErrors { get; set; }
    public int ExcludedPeerReview { get; set; }
    public int ExcludedAtScreening { get; set; }
    public int HumanOverrides { get; set; }
    public bool HumanReviewed { get; set; }
    public int Included { get; set; }
    public int FullTextRetrieved { get; set; }
    public int AbstractOnly => Math.Max(0, Included - FullTextRetrieved);

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
                .GroupBy(l => l.SourceName)
                .Select(g => (g.Key, g.Sum(l => l.PapersFound)))
                .ToList(),
            Identified = s.TotalIdentified,
            DuplicatesRemoved = s.DuplicatesRemoved,
            OutsideDateRange = s.OutsideDateRange,
            CappedBeyondMax = s.CappedBeyondMaxResults,
            Screened = s.Screened,
            ScreeningErrors = s.ScreeningErrors,
            ExcludedPeerReview = s.FailedPeerReviewCheck,
            ExcludedAtScreening = s.Excluded,
            HumanOverrides = s.HumanOverrides,
            HumanReviewed = s.HumanReviewed > 0,
            Included = s.Included,
            FullTextRetrieved = s.FullTextRetrieved,
        };
    }
}

/// <summary>Renders the PRISMA 2020 flow diagram (identification, screening, included) as TikZ for main.tex.</summary>
public static class PrismaFlowDiagram
{
    public static string ToTikz(PrismaFlowCounts c)
    {
        string Box(string name, string position, string text) =>
            $"\\node[flowbox{position}] ({name}) {{{text}}};";

        string sources = c.IdentifiedBySource.Count == 0
            ? ""
            : "\\\\ " + string.Join("\\\\ ", c.IdentifiedBySource.Select(x => $"{Escape(x.Source)} (n = {x.Count})"));

        var removed = new List<string> { $"Duplicates (n = {c.DuplicatesRemoved})" };
        if (c.OutsideDateRange > 0) removed.Add($"Outside year range (n = {c.OutsideDateRange})");
        if (c.CappedBeyondMax > 0) removed.Add($"Over per-source cap (n = {c.CappedBeyondMax})");
        if (c.ScreeningErrors > 0) removed.Add($"Screening failed (n = {c.ScreeningErrors})");

        var excluded = new List<string>();
        if (c.ExcludedPeerReview > 0) excluded.Add($"Not peer reviewed (n = {c.ExcludedPeerReview})");
        excluded.Add($"Did not meet criteria (n = {c.ExcludedAtScreening})");
        if (c.HumanReviewed) excluded.Add($"Decisions changed by reviewer: {c.HumanOverrides}");

        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[node distance=0.55cm and 0.9cm, font=\scriptsize,");
        sb.AppendLine(@"  flowbox/.style={draw, rounded corners=2pt, align=center, text width=5.2cm, inner sep=4pt, fill=white},");
        sb.AppendLine(@"  sidebox/.style={draw, rounded corners=2pt, align=left, text width=4.6cm, inner sep=4pt, fill=black!3},");
        sb.AppendLine(@"  arrow/.style={-{Stealth[length=2mm]}}]");
        sb.AppendLine(Box("id", "", $"Records identified from databases (n = {c.Identified}){sources}"));
        sb.AppendLine($"\\node[sidebox, right=of id] (removed) {{Records removed before screening:\\\\ {string.Join("\\\\ ", removed)}}};");
        sb.AppendLine(Box("screened", ", below=of id", $"Records screened (n = {c.Screened})"));
        sb.AppendLine($"\\node[sidebox, right=of screened] (excluded) {{Records excluded:\\\\ {string.Join("\\\\ ", excluded)}}};");
        sb.AppendLine(Box("included", ", below=of screened", $"Studies included in review (n = {c.Included})\\\\ Full text used (n = {c.FullTextRetrieved}); abstract only (n = {c.AbstractOnly})"));
        sb.AppendLine(@"\draw[arrow] (id) -- (screened);");
        sb.AppendLine(@"\draw[arrow] (screened) -- (included);");
        sb.AppendLine(@"\draw[arrow] (id) -- (removed);");
        sb.AppendLine(@"\draw[arrow] (screened) -- (excluded);");
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("&", @"\&").Replace("_", @"\_").Replace("%", @"\%").Replace("#", @"\#");
}
