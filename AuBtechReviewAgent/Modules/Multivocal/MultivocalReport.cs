using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// Writes the report of a synthesised multivocal run, in code, following the guidelines' phases (planning, conducting,
/// reporting): the methods from the run's files, a table of how each guideline G1–G14 was followed, the results per
/// research question with what each theme rests on, the limits, and the source repository (G14). Writing it changes
/// nothing in the run, so it can be written again; it is saved as multivocal-report.md, which is in the archive.
/// </summary>
public sealed partial class MultivocalReporter
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
        return input == null ? null : Write(input, LoadPaper(runId));
    }

    /// <summary>The run's written paper (mlr-paper.json), for the models it names; null before it is written or when it cannot be read.</summary>
    private MultivocalPaperFile? LoadPaper(Guid runId)
    {
        string path = Path.Join(_runs.FolderOf(runId), MultivocalWriter.PaperFile);
        if (!File.Exists(path)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<MultivocalPaperFile>(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException) { return null; }
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

    /// <summary>The run's files, loaded, once the run is synthesised; null before that or when a file is missing.</summary>
    public MultivocalReportInput? LoadInput(Guid runId)
    {
        if (!MultivocalWriter.IsSynthesised(_runs.LoadHeader(runId)?.Stage)) return null;
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

    /// <summary>The report, written in code from the run's files; <paramref name="paper"/>, once written, names the citation checks' model.</summary>
    public static MultivocalReport Write(MultivocalReportInput input, MultivocalPaperFile? paper = null)
    {
        var plan = input.Planned.Plan;
        string kind = plan.ReviewKind;
        var report = new MultivocalReport { Title = $"{plan.Topic}: a {kind}" };
        report.Meta.Add($"Run {input.Planned.RunId}, planned {input.Planned.CreatedUtc:yyyy-MM-dd}, synthesised {input.Synthesis.SynthesisedUtc:yyyy-MM-dd} (UTC)");
        report.Meta.Add($"Method: {MultivocalGuidelines.Reference}");
        report.Meta.Add($"Protocol fingerprint (SHA-256): {input.Planned.ProtocolSha256}");
        if (MultivocalPaper.ModelsLine(input, paper) is { } models) report.Meta.Add(models);

        var sources = SourceNumbers(input.Extraction);
        report.Sections.AddRange(MethodSections(input));
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

    private static string Cite(IEnumerable<string> addresses, IReadOnlyDictionary<string, int> numbers)
    {
        var cited = addresses.Where(numbers.ContainsKey).Select(a => numbers[a]).Distinct().OrderBy(n => n).ToList();
        return cited.Count == 0 ? "" : $"[{string.Join(", ", cited.Select(n => $"S{n}"))}]";
    }

    public static string Audience(ReviewAudience? audience) => audience switch
    {
        ReviewAudience.Researchers => "researchers",
        ReviewAudience.Practitioners => "practitioners",
        ReviewAudience.Both => "researchers and practitioners",
        _ => "an audience that was not set",
    };

    private static string Ordinal(int tier) => tier switch { 1 => "1st", 2 => "2nd", _ => "3rd" };
    private static string KindLabel(string key) => MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == key).Label ?? key;

    /// <summary>An exclusion reason in words: the screening's fixed groups by their label, anything else as written.</summary>
    private static string ReasonLabel(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "No reason given";
        string key = reason.Trim();
        bool fixedGroup = ExclusionReasons.ModelKeys.Contains(key) || key is ExclusionReasons.ByReviewer or ExclusionReasons.NotPeerReviewed;
        return fixedGroup ? ExclusionReasons.Label(key) : key == ExclusionReasons.Unspecified ? "No reason given" : key;
    }

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
