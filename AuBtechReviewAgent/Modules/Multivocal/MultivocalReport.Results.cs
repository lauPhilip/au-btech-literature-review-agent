using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

// The results per research question, how each guideline was followed, the limits and the source repository.
public sealed partial class MultivocalReporter
{
    private static IEnumerable<ReportSection> Results(MultivocalReportInput input, IReadOnlyDictionary<string, int> numbers)
    {
        var s = input.Synthesis;
        var byId = s.Findings.ToDictionary(f => f.Id);
        yield return new ReportSection { Heading = "7. Results" }.With(ReportBlock.Paragraph(
            "The themes' names and descriptions were written by the language model; everything else below is counted or quoted in code. Each quotation is a finding whose quote was found in the kept text of its source's page, and [S3] is source 3 of the source repository."));
        foreach (var (number, question) in input.Planned.Plan.Numbered())
        {
            var section = new ReportSection { Heading = $"{number}: {question.Text}", Level = 3 };
            var themes = s.Themes.Where(t => t.Question == number).ToList();
            if (themes.Count == 0 && !s.NotCoded.Any(n => n.Question == number))
                section.Blocks.Add(ReportBlock.Paragraph(s.Unanswered.Contains(number)
                    ? "No extracted source answers this question, so the review cannot answer it from these sources."
                    : "No theme was found for this question."));
            foreach (var theme in themes)
            {
                var findings = theme.Findings.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                string cited = Cite(findings.Select(f => f.Address), numbers);
                section.Blocks.Add(ReportBlock.Paragraph(
                    $"**{theme.Name}.** {Sentence(theme.Description)} This theme rests on {Count(theme.Sources, "source")} {cited}: " +
                    $"{Num(theme.FirstTier)} from 1st-tier, {Num(theme.SecondTier)} from 2nd-tier and {Num(theme.ThirdTier)} from 3rd-tier outlets, with a mean quality of {theme.MeanQuality.ToString("0.#", CultureInfo.InvariantCulture)} of {input.Quality.MaxPoints} points ({theme.EvidenceLabel})."));
                foreach (var f in Representative(findings))
                    section.Blocks.Add(ReportBlock.Quote($"\"{f.Quote}\" ({f.Attribute}: {f.Value}) {Cite(new[] { f.Address }, numbers)}"));
                if (theme.Tensions.Count > 0)
                {
                    section.Blocks.Add(ReportBlock.Paragraph("Tensions between sources:"));
                    section.Blocks.Add(ReportBlock.List(theme.Tensions.Select(t =>
                        $"{t.Description} {Cite(new[] { byId.GetValueOrDefault(t.FindingA)?.Address, byId.GetValueOrDefault(t.FindingB)?.Address }.OfType<string>(), numbers)}")));
                }
            }
            foreach (var other in s.NotCoded.Where(n => n.Question == number))
                section.Blocks.Add(ReportBlock.Paragraph(
                    $"Not coded by the map: {Count(other.Findings.Count, "finding")} on {other.Attribute} from {Count(other.Sources, "source")} {Cite(other.Findings.Where(byId.ContainsKey).Select(id => byId[id].Address), numbers)} had the value \"Other\"; the map may need a new value for them."));
            yield return section;
        }
        if (s.Unassigned.Count > 0)
            yield return new ReportSection { Heading = "Findings in no theme", Level = 3 }
                .With(ReportBlock.List(s.Unassigned.Select(u => $"{u.Finding}: {Sentence(u.Reason)}")));
    }

    /// <summary>
    /// The findings a theme shows: one per source, from the highest outlet tier and quality first, each value of the theme
    /// shown once before a value is shown again. A value can sit in two themes; without this, a theme that also holds
    /// another theme's value showed that theme's quotes and none of its own (seen in a live run).
    /// </summary>
    public static IReadOnlyList<SynthesisFinding> Representative(IEnumerable<SynthesisFinding> findings)
    {
        var ordered = findings.OrderBy(f => f.Tier).ThenByDescending(f => f.QualityPoints).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var chosen = new List<SynthesisFinding>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ordered)
        {
            if (chosen.Count == QuotesPerTheme) break;
            if (sources.Contains(f.Address) || !values.Add(f.Value.Trim())) continue;
            chosen.Add(f);
            sources.Add(f.Address);
        }
        foreach (var f in ordered)
        {
            if (chosen.Count == QuotesPerTheme) break;
            if (sources.Add(f.Address)) chosen.Add(f);
        }
        return chosen.OrderBy(f => f.Tier).ThenByDescending(f => f.QualityPoints).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
    }

    private static ReportSection Guidelines(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        var section = new ReportSection { Heading = "8. How the guidelines were followed (G1–G14)" };
        var fixedMap = input.Map.Versions.FirstOrDefault(v => v.Number == input.Map.FixedVersion) ?? input.Map.Latest;
        var rows = new List<string[]>
        {
            new[] { "G1", "Protocol", $"Written before any search on {input.Planned.CreatedUtc:yyyy-MM-dd}, with its SHA-256 fingerprint recorded." },
            new[] { "G2", "Need, goal and audience", $"Goal stated; written for {Audience(plan.Audience)}." },
            new[] { "G3", "Whether to include grey literature", $"Decided with Table 4: {plan.IncludeGrey.Count(a => a == true)} of {MultivocalGuidelines.IncludeGreyQuestions.Count} answers \"yes\"." },
            new[] { "G4", "Research questions", $"{Count(plan.Numbered().Count, "question")}, tied to the goal." },
            new[] { "G5", "Types of research question", $"A type set for {plan.Numbered().Count(x => MultivocalGuidelines.FindType(x.Question.Type) != null)} of {plan.Numbered().Count}." },
            new[] { "G6", "Kinds of grey literature", $"{Count(plan.GreyTypes.Count, "kind")} named in the protocol." },
            new[] { "G7", "Where to search", $"{Count(input.Ledger.Searches.Select(x => x.Source).Distinct().Count(), "free search source")}; no general web search engine; no one contacted." },
            new[] { "G8", "When to stop", Upper(Sentence(input.Ledger.StoppingApplied)) },
            new[] { "G9", "Inclusion and exclusion criteria", "Set in the protocol and applied to every source." },
            new[] { "G10", "Selection with the same care for every source", $"Every source screened twice; κ = {(input.Screening.Kappa is double k ? k.ToString("0.00", CultureInfo.InvariantCulture) : "not computed")}; {Count(input.Screening.Sources.Count(x => x.Uncertain), "decision")} flagged as uncertain; {Num(input.Screening.Sources.Count(x => x.Reviewed))} looked at by the reviewer." },
            new[] { "G11", "Quality of the sources", $"Table 7 checklist, threshold {input.Quality.Threshold} of {input.Quality.MaxPoints} points; every point backed by a quote." },
            new[] { "G12", "Data extraction and systematic map", $"Map version {fixedMap.Number} with {Count(fixedMap.Attributes.Count, "attribute")}, fixed before extraction; every value backed by a quote." },
            new[] { "G13", "Synthesis", $"Qualitative coding into {Count(input.Synthesis.Themes.Count, "theme")}; evidence counted in code; opinion pieces with the critical questions." },
            new[] { "G14", "Reporting for the audience", "This report, written in code from the run's files, with its source repository; the paper and its summary for practitioners are written from the same files." },
        };
        section.Blocks.Add(ReportBlock.Table(new[] { "Guideline", "What it asks", "In this review" }, rows));
        return section;
    }

    private static ReportSection Limits(MultivocalReportInput input)
    {
        var section = new ReportSection { Heading = "9. Limits" };
        var limits = new List<string>
        {
            "A language model screened the sources, scored their quality, proposed the map, extracted the values and named the themes. Its answers were checked in code: every value, point and quotation rests on a quote found in the source's kept page text, and every count in this report was made in code. Whether a quote supports its value was checked by the model too, once per source, and the values it judged not supported were left out; that check is a model's judgement, not a check in code.",
            "Grey literature is not peer reviewed. Each theme says which outlet tiers it rests on, and a theme resting on 3rd-tier sources only (such as blog posts and code repositories) is labelled as such.",
            "Only robots.txt was checked before a page was fetched; other terms of a site cannot be read by code.",
        };
        section.Blocks.Add(ReportBlock.List(limits));
        return section;
    }

    private static ReportSection Repository(MultivocalReportInput input, IReadOnlyDictionary<string, int> numbers)
    {
        var section = new ReportSection { Heading = "Source repository" };
        var records = input.Ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Record);
        var points = input.Quality.Sources.GroupBy(q => q.Address).ToDictionary(g => g.Key, g => g.First().Points);
        var pages = input.Pages.Pages.GroupBy(p => MultivocalSearcher.AddressKey(p.Url)).ToDictionary(g => g.Key, g => g.First());
        section.Blocks.Add(ReportBlock.Paragraph(
            "Every source the results rest on, numbered as in the findings' ids (finding F3.2 comes from source S3). The access date is when the page's text was kept; the Wayback Machine link points to the capture nearest that date, which may differ from the text that was kept."));
        section.Blocks.Add(ReportBlock.Table(new[] { "Source", "Title", "Kind", "Tier", "Quality", "Address", "Accessed", "Wayback Machine" },
            input.Extraction.Sources.Where(s => numbers.ContainsKey(s.Address)).Select(s =>
            {
                string kind = records.TryGetValue(s.Address, out var r) ? r.Kind : "";
                var page = pages.GetValueOrDefault(s.Address);
                string url = PrismaReviewEngine.IsWebAddress(s.Url) ? s.Url : "";
                return new[]
                {
                    $"S{numbers[s.Address]}", s.Title, MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == kind).Label ?? kind,
                    Ordinal(MultivocalSynthesiser.TierOf(kind)), $"{points.GetValueOrDefault(s.Address).ToString("0.#", CultureInfo.InvariantCulture)} of {input.Quality.MaxPoints}",
                    url, page != null ? page.AccessedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "", page != null && PrismaReviewEngine.IsWebAddress(page.WaybackUrl) ? page.WaybackUrl : "",
                };
            })));
        return section;
    }
}
