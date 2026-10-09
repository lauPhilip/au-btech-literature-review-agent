using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>One block of a report section: a paragraph, a quotation, a list or a table.</summary>
public sealed class ReportBlock
{
    /// <summary>"paragraph", "quote", "list" or "table".</summary>
    public string Kind { get; init; } = "paragraph";
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = Array.Empty<IReadOnlyList<string>>();

    public static ReportBlock Paragraph(string text) => new() { Kind = "paragraph", Text = text };
    public static ReportBlock Quote(string text) => new() { Kind = "quote", Text = text };
    public static ReportBlock List(IEnumerable<string> items) => new() { Kind = "list", Items = items.ToList() };
    public static ReportBlock Table(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows) =>
        new() { Kind = "table", Headers = headers.ToList(), Rows = rows.Select(r => (IReadOnlyList<string>)r.ToList()).ToList() };
}

/// <summary>A section of the report, with its heading level (2 or 3).</summary>
public sealed class ReportSection
{
    public string Heading { get; init; } = "";
    public int Level { get; init; } = 2;
    public List<ReportBlock> Blocks { get; } = new();
}

/// <summary>
/// The report of a multivocal run (MLR block G, G14), as data: the page shows it and <see cref="ToMarkdown"/> writes it
/// to multivocal-report.md for the archive. Every sentence is written in code from the run's files, never by the
/// model; the only model text in it is the themes' names and descriptions, which say so, and every quotation is a
/// finding whose quote was found in the kept page text.
/// </summary>
public sealed class MultivocalReport
{
    public string Title { get; init; } = "";

    /// <summary>The lines under the title: run, dates, method, protocol fingerprint.</summary>
    public List<string> Meta { get; } = new();
    public List<ReportSection> Sections { get; } = new();

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Title}").AppendLine();
        foreach (var line in Meta) sb.AppendLine($"- {line}");
        sb.AppendLine();
        foreach (var section in Sections)
        {
            sb.AppendLine($"{new string('#', section.Level)} {section.Heading}").AppendLine();
            foreach (var block in section.Blocks)
            {
                switch (block.Kind)
                {
                    case "quote":
                        sb.AppendLine($"> {block.Text}");
                        break;
                    case "list":
                        foreach (var item in block.Items) sb.AppendLine($"- {item}");
                        break;
                    case "table":
                        sb.AppendLine($"| {string.Join(" | ", block.Headers.Select(Cell))} |");
                        sb.AppendLine($"|{string.Concat(block.Headers.Select(_ => "---|"))}");
                        foreach (var row in block.Rows) sb.AppendLine($"| {string.Join(" | ", row.Select(Cell))} |");
                        break;
                    default:
                        sb.AppendLine(block.Text);
                        break;
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>Everything the report is written from: the run's files, loaded.</summary>
public sealed record MultivocalReportInput(
    PlannedRun Planned,
    MultivocalLedger Ledger,
    MultivocalScreeningFile Screening,
    MultivocalPagesFile Pages,
    MultivocalQualityFile Quality,
    MultivocalMapFile Map,
    MultivocalExtractionFile Extraction,
    MultivocalSynthesisFile Synthesis);

/// <summary>
/// Writes the report of a synthesised multivocal run, in code, following the guidelines' phases (planning, conducting,
/// reporting): the methods from the run's files, a table of how each guideline G1–G14 was followed, the results per
/// research question with what each theme rests on, the limits, and the source repository (G14). Writing it changes
/// nothing in the run, so it can be written again; it is saved as multivocal-report.md, which is in the archive.
/// </summary>
public sealed class MultivocalReporter
{
    public const string ReportFile = "multivocal-report.md";

    /// <summary>How many quotations a theme shows in the report, each from another source.</summary>
    public const int QuotesPerTheme = 3;

    private readonly RunStore _runs;
    private readonly MultivocalPlanner _planner;
    private readonly MultivocalSearcher _searcher;
    private readonly MultivocalScreener _screener;
    private readonly MultivocalPages _pages;
    private readonly MultivocalQualityAssessor _quality;
    private readonly MultivocalMapper _mapper;
    private readonly MultivocalExtractor _extractor;
    private readonly MultivocalSynthesiser _synthesiser;

    /// <summary>A reporter that reads the run's files through the services that wrote them.</summary>
    public MultivocalReporter(RunStore runs, MultivocalPlanner planner, MultivocalSearcher searcher, MultivocalScreener screener, MultivocalPages pages,
        MultivocalQualityAssessor quality, MultivocalMapper mapper, MultivocalExtractor extractor, MultivocalSynthesiser synthesiser)
    {
        _runs = runs;
        _planner = planner;
        _searcher = searcher;
        _screener = screener;
        _pages = pages;
        _quality = quality;
        _mapper = mapper;
        _extractor = extractor;
        _synthesiser = synthesiser;
    }

    /// <summary>The report of a synthesised run, or null when the run is not synthesised.</summary>
    public MultivocalReport? Build(Guid runId)
    {
        var input = LoadInput(runId);
        return input == null ? null : Write(input);
    }

    /// <summary>Writes the report to the run folder (multivocal-report.md). Needs the run's edit key.</summary>
    public async Task<MultivocalReport> SaveAsync(Guid runId, string? editKey)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can write its report.");
        var report = Build(runId) ?? throw new InvalidOperationException("Synthesise the run first.");
        await SafeFile.WriteAllTextAsync(Path.Join(_runs.FolderOf(runId), ReportFile), report.ToMarkdown());
        return report;
    }

    /// <summary>
    /// The run's references in code (G-6): "bib" or "ris" for the sources the results rest on, once the run is
    /// extracted, or "screened" for every screened source, once it is screened; null otherwise.
    /// </summary>
    public string? ExportReferences(Guid runId, string format)
    {
        var ledger = _searcher.LoadLedger(runId);
        var screening = _screener.Load(runId);
        if (ledger == null || screening == null) return null;
        var extraction = _extractor.Load(runId);
        if (format == "screened") return GreyReferences.ToScreenedRis(ledger, screening, extraction == null ? null : SourceNumbers(extraction));
        if (extraction == null) return null;
        var references = GreyReferences.Build(ledger, _pages.Load(runId), extraction);
        return format == "ris" ? GreyReferences.ToRis(references) : format == "bib" ? GreyReferences.ToBibTeX(references) : null;
    }

    /// <summary>The saved report, or null when it has not been written.</summary>
    public string? ReadSaved(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), ReportFile);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private MultivocalReportInput? LoadInput(Guid runId)
    {
        if (_runs.LoadHeader(runId)?.Stage != MultivocalSynthesiser.StageSynthesised) return null;
        var planned = _planner.Load(runId);
        var ledger = _searcher.LoadLedger(runId);
        var screening = _screener.Load(runId);
        var quality = _quality.Load(runId);
        var map = _mapper.Load(runId);
        var extraction = _extractor.Load(runId);
        var synthesis = _synthesiser.Load(runId);
        if (planned == null || ledger == null || screening == null || quality == null || map == null || extraction == null || synthesis == null) return null;
        return new MultivocalReportInput(planned, ledger, screening, _pages.Load(runId), quality, map, extraction, synthesis);
    }

    /// <summary>The report, written in code from the run's files.</summary>
    public static MultivocalReport Write(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        string kind = plan.ReviewKind;
        var report = new MultivocalReport { Title = $"{plan.Topic}: a {kind}" };
        report.Meta.Add($"Run {input.Planned.RunId}, planned {input.Planned.CreatedUtc:yyyy-MM-dd}, synthesised {input.Synthesis.SynthesisedUtc:yyyy-MM-dd} (UTC)");
        report.Meta.Add($"Method: {MultivocalGuidelines.Reference}");
        report.Meta.Add($"Protocol fingerprint (SHA-256): {input.Planned.ProtocolSha256}");
        if (input.Synthesis.Model.Length > 0) report.Meta.Add($"Language model: {input.Synthesis.Model}");

        var sources = SourceNumbers(input.Extraction);
        report.Sections.Add(Planning(input));
        report.Sections.Add(Search(input));
        report.Sections.Add(Selection(input));
        report.Sections.Add(QualitySection(input));
        report.Sections.Add(MapAndExtraction(input));
        report.Sections.Add(SynthesisMethod(input));
        report.Sections.AddRange(Results(input, sources));
        report.Sections.Add(Guidelines(input));
        report.Sections.Add(Limits(input));
        report.Sections.Add(Repository(input, sources));
        return report;
    }

    /// <summary>
    /// The number of each source in the source repository ("S3"), in the order of the extraction; it is the number in
    /// its findings' ids ("F3.2"), so a finding can always be traced to its source.
    /// </summary>
    public static IReadOnlyDictionary<string, int> SourceNumbers(MultivocalExtractionFile extraction) => extraction.Sources
        .Where(s => s.Error == null)
        .Select((s, i) => (s.Address, Number: i + 1))
        .ToDictionary(x => x.Address, x => x.Number);

    private static ReportSection Planning(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        var section = new ReportSection { Heading = "1. Planning (G1–G5)" };
        int yes = plan.IncludeGrey.Count(a => a == true);
        section.Blocks.Add(ReportBlock.Paragraph(
            $"The review's goal was: {Sentence(plan.Goal)} It was written for {Audience(plan.Audience)}. " +
            $"Whether to include grey literature was decided with the seven questions of the guidelines' Table 4 (G3): {yes} of {MultivocalGuidelines.IncludeGreyQuestions.Count} were answered \"yes\". " +
            $"The protocol was written on {input.Planned.CreatedUtc:yyyy-MM-dd} (UTC), before any search, and its SHA-256 fingerprint is recorded with the plan (G1)."));
        section.Blocks.Add(ReportBlock.Table(new[] { "Question", "Text", "Type (G5)" },
            plan.Numbered().Select(x => new[] { x.Number, x.Question.Text, MultivocalGuidelines.FindType(x.Question.Type)?.Name ?? "not set" })));
        return section;
    }

    private static ReportSection Search(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        var ledger = input.Ledger;
        var section = new ReportSection { Heading = "2. Search (G6–G8)" };
        var ran = ledger.Searches.Where(s => s.Error == null).ToList();
        string kinds = Join(plan.GreyTypes.Select(k => MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == k).Label ?? k).Select(Lower), "; ");
        section.Blocks.Add(ReportBlock.Paragraph(
            $"The review covered {kinds} (G6). On {ledger.SearchedUtc:yyyy-MM-dd} (UTC), {Count(ran.Count, "search", "searches")} ran across {Count(ran.Select(s => s.Source).Distinct().Count(), "source")} " +
            $"with {Count(plan.EffectiveGreySearchStrings.Count, "search string")} ({Join(plan.EffectiveGreySearchStrings.Select(t => $"\"{t}\""))}) and found {Count(ledger.Sources.Count, "source")} (G7). " +
            $"Each raw answer was saved as received, with its SHA-256 fingerprint. Stopping rule (G8): {Sentence(ledger.StoppingApplied)}"));
        section.Blocks.Add(ReportBlock.Table(new[] { "Source", "Search string", "Returned", "New" },
            ledger.Searches.Select(s => new[] { MultivocalGuidelines.SearchLabel(s.Source), s.SearchString, s.Error == null ? Num(s.Returned) : $"failed: {s.Error}", Num(s.New) })));
        var notDone = new List<string>();
        notDone.AddRange(ledger.Skipped.Select(s => $"{MultivocalGuidelines.SearchLabel(s.Source)}: not run ({Lower(s.Reason.TrimEnd('.'))})."));
        notDone.Add("No general web search engine was used: none offers a search API that is free and allows its results to be kept, which a traceable review needs.");
        notDone.Add("Practitioners and authors were not contacted for sources; only sources found by the searches were reviewed.");
        section.Blocks.Add(ReportBlock.Paragraph("Not done, and why:"));
        section.Blocks.Add(ReportBlock.List(notDone));
        return section;
    }

    private static ReportSection Selection(MultivocalReportInput input)
    {
        var s = input.Screening;
        var section = new ReportSection { Heading = "3. Selection (G9, G10)" };
        int reviewed = s.Sources.Count(x => x.Reviewed);
        int changed = s.Sources.Count(x => x.Reviewed && x.ModelDecision != null && x.ModelDecision != x.Decision);
        string kappa = s.Kappa is double k ? $" The two screenings agreed with Cohen's κ = {k.ToString("0.00", CultureInfo.InvariantCulture)}." : "";
        section.Blocks.Add(ReportBlock.Paragraph(
            $"Each of the {Count(s.Sources.Count, "source")} found was screened twice by the language model against the protocol's criteria, independently and with the steps in a different order (G9, G10).{kappa} " +
            $"{Num(s.Count("Included"))} were included, {Num(s.Count("Excluded"))} excluded and {Num(s.Count("Duplicate"))} set aside as duplicates of a source with the same title" +
            $"{(s.Count("Error") > 0 ? $"; {Num(s.Count("Error"))} could not be screened" : "")}. " +
            $"The reviewer looked at {Count(reviewed, "decision")} and changed {Num(changed)}; the model's decision stays on file next to the reviewer's."));
        section.Blocks.Add(ReportBlock.Paragraph($"Inclusion criteria: {Sentence(s.InclusionCriteria)} Exclusion criteria: {(s.ExclusionCriteria.Trim().Length > 0 ? Sentence(s.ExclusionCriteria) : "none.")}"));
        var reasons = s.Sources.Where(x => x.Decision == "Excluded")
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ExclusionReason) ? "No reason given" : x.ExclusionReason!.Trim())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new[] { g.Key, Num(g.Count()) }).ToList();
        if (reasons.Count > 0) section.Blocks.Add(ReportBlock.Table(new[] { "Reason for exclusion", "Sources" }, reasons));
        return section;
    }

    private static ReportSection QualitySection(MultivocalReportInput input)
    {
        var q = input.Quality;
        var section = new ReportSection { Heading = "4. Quality (G11)" };
        var passed = q.Sources.Where(x => x.Outcome == "Passed").ToList();
        int opinions = q.Sources.Count(x => x.Argument != null);
        section.Blocks.Add(ReportBlock.Paragraph(
            $"Every included source was scored on the {q.MaxPoints} items of the quality checklist in the guidelines' Table 7, each 1, 0.5 or 0 points, with a threshold of {q.Threshold} points. " +
            $"The language model answered the items that need reading the source, and every point it gave rests on a quote found in the kept text of the source's page; the date, the impact and the outlet type were decided in code (impact: {Lower(q.ImpactRule.TrimEnd('.'))}). " +
            $"{Num(passed.Count)} sources passed, {Num(q.Count("Below threshold"))} scored below the threshold and {Num(q.Count("Not assessed"))} could not be assessed{(q.Count("Not assessed") > 0 ? ", most often because their page could not be read" : "")}." +
            (opinions > 0 ? $" {Count(opinions, "source")} {(opinions == 1 ? "was" : "were")} recorded as an opinion piece, with the critical questions for an expert opinion (G13)." : "")));
        if (passed.Count > 0)
            section.Blocks.Add(ReportBlock.Paragraph(
                $"Of the sources that passed, {TierCount(passed, input, 1)} came from 1st-tier outlets, {TierCount(passed, input, 2)} from 2nd-tier and {TierCount(passed, input, 3)} from 3rd-tier outlets (checklist item 8.1); " +
                $"their mean score was {passed.Average(x => x.Points).ToString("0.#", CultureInfo.InvariantCulture)} of {q.MaxPoints} points."));
        return section;
    }

    private static int TierCount(IEnumerable<GreyQuality> sources, MultivocalReportInput input, int tier)
    {
        var kinds = input.Ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Record.Kind);
        return sources.Count(s => MultivocalSynthesiser.TierOf(kinds.GetValueOrDefault(s.Address, "")) == tier);
    }

    private static ReportSection MapAndExtraction(MultivocalReportInput input)
    {
        var map = input.Map;
        var section = new ReportSection { Heading = "5. Systematic map and extraction (G12)" };
        var fixedMap = map.Versions.FirstOrDefault(v => v.Number == map.FixedVersion) ?? map.Latest;
        int edited = map.Versions.Count(v => v.By == "reviewer");
        int extracted = input.Extraction.Sources.Count(s => s.Error == null);
        int failed = input.Extraction.Sources.Count - extracted;
        section.Blocks.Add(ReportBlock.Paragraph(
            $"The language model proposed the attributes of the systematic map from the research questions, with values generalised over {Count(map.SourcesSeen, "source")} that passed the quality check. " +
            $"The reviewer {(edited > 0 ? $"edited it {Count(edited, "time")}" : "kept it as proposed")}, and version {fixedMap.Number} was fixed before any source was extracted. " +
            $"{Num(extracted)} sources were extracted against it{(failed > 0 ? $" and {Num(failed)} could not be" : "")}; every value rests on a quote found in the kept text of the source's page, and what a page does not state was recorded as not stated. " +
            "The kind of each source, whether it is grey or formal literature, its site, date and quality points were recorded in code."));
        section.Blocks.Add(ReportBlock.Table(new[] { "Attribute", "Question", "Values" },
            fixedMap.Attributes.Select(a => new[] { a.Name, a.Question, a.Open ? "open (named by each source)" : string.Join(", ", a.Values) })));
        return section;
    }

    private static ReportSection SynthesisMethod(MultivocalReportInput input)
    {
        var s = input.Synthesis;
        var section = new ReportSection { Heading = "6. Synthesis (G13)" };
        int notCoded = s.NotCoded.Sum(n => n.Findings.Count);
        section.Blocks.Add(ReportBlock.Paragraph(
            $"Each extracted value with its quote became a finding; {Count(s.Findings.Count, "finding")} remained after every quote was checked again against the kept page text, never the live page, and checked by the language model for whether it supports its value" +
            (s.Unsupported.Count > 0 ? $" ({Count(s.Unsupported.Count, "value")} left out as not supported)" : "") + ". " +
            "For each research question, the findings were grouped in code by attribute and value, the map's values serving as the first codes; the language model then named the themes, placed the values in them, and named the tensions between sources within a theme. " +
            (s.SecondPlacements.Any(a => a.Kappa != null)
                ? $"A second, independent placement of the values in the other order agreed with the first with Cohen's κ = {string.Join(", ", s.SecondPlacements.Where(a => a.Kappa != null).Select(a => $"{a.Kappa!.Value.ToString("0.00", CultureInfo.InvariantCulture)} for {a.Question}"))}; it did not change the themes. "
                : "") +
            $"The review produced {Count(s.Themes.Count, "theme")}. What each theme rests on (how many sources, their outlet tiers and quality points, grey or formal literature) was counted in code, never by the model." +
            (notCoded > 0 ? $" {Count(notCoded, "finding")} had the map's \"Other\" value; they are listed under their question as not coded by the map, not themed." : "") +
            (s.Unassigned.Count > 0 ? $" {Count(s.Unassigned.Count, "value")} fit no theme; they are listed with the reason." : "")));
        if (s.Notes.Count > 0)
        {
            section.Blocks.Add(ReportBlock.Paragraph("Notes recorded by the synthesis:"));
            section.Blocks.Add(ReportBlock.List(s.Notes));
        }
        return section;
    }

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

    /// <summary>The findings a theme shows: one per source, from the highest outlet tier and quality first.</summary>
    public static IReadOnlyList<SynthesisFinding> Representative(IEnumerable<SynthesisFinding> findings) => findings
        .GroupBy(f => f.Address)
        .Select(g => g.First())
        .OrderBy(f => f.Tier).ThenByDescending(f => f.QualityPoints).ThenBy(f => f.Id, StringComparer.Ordinal)
        .Take(QuotesPerTheme)
        .ToList();

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
            new[] { "G10", "Selection with the same care for every source", $"Every source screened twice; κ = {(input.Screening.Kappa is double k ? k.ToString("0.00", CultureInfo.InvariantCulture) : "not computed")}; flagged decisions looked at by the reviewer." },
            new[] { "G11", "Quality of the sources", $"Table 7 checklist, threshold {input.Quality.Threshold} of {input.Quality.MaxPoints} points; every point backed by a quote." },
            new[] { "G12", "Data extraction and systematic map", $"Map version {fixedMap.Number} with {Count(fixedMap.Attributes.Count, "attribute")}, fixed before extraction; every value backed by a quote." },
            new[] { "G13", "Synthesis", $"Qualitative coding into {Count(input.Synthesis.Themes.Count, "theme")}; evidence counted in code; opinion pieces with the critical questions." },
            new[] { "G14", "Reporting for the audience", "This report, written in code from the run's files, with its source repository. A summary for practitioners is still to come." },
        };
        section.Blocks.Add(ReportBlock.Table(new[] { "Guideline", "What it asks", "In this review" }, rows));
        return section;
    }

    private static ReportSection Limits(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        var section = new ReportSection { Heading = "9. Limits" };
        var limits = new List<string>
        {
            "A language model screened the sources, scored their quality, proposed the map, extracted the values and named the themes. Its answers were checked in code: every value, point and quotation rests on a quote found in the source's kept page text, and every count in this report was made in code. Whether a quote supports its value as well as it appears was not checked by code.",
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

    private static string Cite(IEnumerable<string> addresses, IReadOnlyDictionary<string, int> numbers)
    {
        var cited = addresses.Where(numbers.ContainsKey).Select(a => numbers[a]).Distinct().OrderBy(n => n).ToList();
        return cited.Count == 0 ? "" : $"[{string.Join(", ", cited.Select(n => $"S{n}"))}]";
    }

    private static string Audience(ReviewAudience? audience) => audience switch
    {
        ReviewAudience.Researchers => "researchers",
        ReviewAudience.Practitioners => "practitioners",
        ReviewAudience.Both => "researchers and practitioners",
        _ => "an audience that was not set",
    };

    private static string Ordinal(int tier) => tier switch { 1 => "1st", 2 => "2nd", _ => "3rd" };
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Count(int n, string noun, string? plural = null) => $"{Num(n)} {(n == 1 ? noun : plural ?? noun + "s")}";
    private static string Upper(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    private static string Lower(string text) => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    private static string Sentence(string text)
    {
        string t = text.Trim();
        return t.Length == 0 ? "not given." : t.EndsWith('.') || t.EndsWith('?') || t.EndsWith('!') ? t : t + ".";
    }

    private static string Join(IEnumerable<string> items, string separator = ", ")
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(separator, list.Take(list.Count - 1)) + (separator == ", " ? " and " : $"{separator}and ") + list[^1],
        };
    }
}

internal static class ReportSectionExtensions
{
    public static ReportSection With(this ReportSection section, ReportBlock block)
    {
        section.Blocks.Add(block);
        return section;
    }
}
