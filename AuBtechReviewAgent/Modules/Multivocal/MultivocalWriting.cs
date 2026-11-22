using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The two steps that turn a synthesised multivocal run into a paper (MLR G-8), through the writing and checking
/// every kind of review shares. Writing: the grounded outline, one cited section per theme with its fill pass, the
/// discussion, the automated peer review with one revision, the summary for practitioners, and last the title,
/// abstract, rationale and objectives with the style pass; questions about frequency or existence are answered in
/// code and never written by the model. Checking: every cited sentence is checked against the kept page text of the
/// source it cites, with the same verdicts as the systematic review, a second check of the weak ones and one repair.
/// The run goes from "Synthesised" through "Writing" to "Written", then through "Checking" to "Checked".
/// </summary>
public sealed partial class MultivocalWriter
{
    public const string PaperFile = "mlr-paper.json";
    public const string AuditFile = "citation-audit.json";
    public const string OutlineFile = "grounded-outline.txt";
    public const string PeerReviewFile = "peer-review-feedback.json";
    public const string StageWriting = "Writing";
    public const string StageWritten = "Written";
    public const string StageChecking = "Checking";
    public const string StageChecked = "Checked";

    /// <summary>How many writing steps the progress line counts.</summary>
    public const int WritingSteps = 6;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<Guid, bool> Running = new();
    private static ILogger _log => AppLog.For("AuBtechReviewAgent.MultivocalWriter");

    private readonly RunStore _runs;
    private readonly MultivocalReporter _reporter;
    private readonly MultivocalPages _pages;
    private readonly Func<IChatCompletionService> _chat;
    private readonly string _model;
    private readonly int _parallelism;

    private readonly ReviewCache _cache;
    private readonly RunMetricsStore _metrics;

    public MultivocalWriter(RunStore runs, MultivocalReporter reporter, MultivocalPages pages, Func<IChatCompletionService> chat, string model = "", int parallelism = 4,
        ReviewCache? cache = null, RunMetricsStore? metrics = null)
    {
        _cache = cache ?? ReviewCache.Disabled;
        _metrics = metrics ?? RunMetricsStore.Disabled;
        _runs = runs;
        _reporter = reporter;
        _pages = pages;
        _chat = chat;
        _model = model;
        _parallelism = Math.Max(1, parallelism);
    }

    /// <summary>Whether a run at this stage has its synthesis, so its report and evidence can be read.</summary>
    public static bool IsSynthesised(string? stage) =>
        stage is MultivocalSynthesiser.StageSynthesised or StageWriting or StageWritten or StageChecking or StageChecked;

    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    public MultivocalPaperFile? Load(Guid runId) => Read<MultivocalPaperFile>(Path.Join(_runs.FolderOf(runId), PaperFile));

    public MultivocalCitationAudit? LoadAudit(Guid runId) => Read<MultivocalCitationAudit>(Path.Join(_runs.FolderOf(runId), AuditFile));

    /// <summary>
    /// Writes run-metrics.json of a checked run from its files and its call log, and adds it to the metrics store;
    /// null before the check. Never fails: the metrics are a by-product of the run.
    /// </summary>
    public async Task<RunMetrics?> WriteMetricsAsync(Guid runId)
    {
        try
        {
            var input = _reporter.LoadInput(runId);
            var paper = Load(runId);
            if (input == null || paper?.CheckedUtc == null) return null;
            string folder = _runs.FolderOf(runId);
            var calls = MultivocalRunLog.LoadCalls(folder)?.Calls ?? new List<LlmCallRecord>();
            var metrics = MultivocalRunLog.BuildMetrics(input, paper, LoadAudit(runId), calls);
            await SafeFile.WriteAllTextAsync(Path.Join(folder, MultivocalRunLog.MetricsFile), JsonSerializer.Serialize(metrics, Json));
            _metrics.Append(metrics);
            return metrics;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogWarning("Run {RunId}: run metrics not written: {Message}", runId, ex.Message);
            return null;
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>How the shared writing prompts name a grey literature review and its parts.</summary>
    public static WritingProfile Profile(MultivocalPlan plan) => new()
    {
        Review = $"a {plan.ReviewKind} following the Garousi et al. (2019) guidelines",
        ReviewShort = $"a {plan.ReviewKind}",
        ResultsSection = "the Results section",
        DiscussionSection = "the Discussion section",
        Source = "source",
        Sources = "sources",
        BaseFactsFrom = "counted in code from the run's files",
        SourceTexts = "excerpts from the kept page texts of the included sources",
        DiscussionParts =
            "(a) what the themes together say about the goal, (b) the state of the practice: where the 1st-tier sources (such as white papers and documentation) and the 3rd-tier sources (such as blog posts) agree or differ, (c) gaps the sources leave open and directions for research and practice, (d) implications for practitioners, (e) in one paragraph, the limitations of the evidence (grey literature, not peer reviewed, pages read as kept on the access date) and of this review (automated screening, extraction and synthesis by a language model).",
    };

    /// <summary>The kind of a result whose theme section the model could not write.</summary>
    public const string UnwrittenKind = "unwritten";

    /// <summary>What the paper says, in code, in place of a theme section that could not be written.</summary>
    public const string UnwrittenText =
        "The section on this theme could not be written: the language model's answer failed the checks in code twice. " +
        "The theme's quotations are listed in the data appendix.";

    /// <summary>The facts about the review the abstract may use, counted in code.</summary>
    public static string FrontMatterFacts(MultivocalReportInput input, int? sectionsWritten = null)
    {
        var f = MultivocalPaperParts.Flow(input);
        var plan = input.Planned.Plan;
        var inCode = plan.Numbered().Where(q => MultivocalPaperParts.AnsweredInCode(q.Question.Type)).Select(q => q.Number).ToHashSet();
        int themed = input.Synthesis.Themes.Count(t => !inCode.Contains(t.Question));
        int written = Math.Min(sectionsWritten ?? themed, themed);
        return $"Kind of review: {plan.ReviewKind} following the Garousi et al. (2019) guidelines. " +
               $"Grey sources found by the searches: {f.Found}, from {f.Searches} searches. Screened twice against the criteria: {f.Screened}. " +
               $"Included at screening, before the quality check: {f.Included}; excluded at screening: {f.Excluded}. " +
               $"Passed the quality check ({input.Quality.Threshold} of {input.Quality.MaxPoints} points or more) and kept for the synthesis: {f.Passed}; below the threshold: {f.BelowThreshold}; not assessed (page not readable): {f.NotAssessed}. " +
               $"Extracted against the systematic map: {f.Extracted}. Findings: {f.Findings}. Themes written up as result sections: {written}" +
               (written < themed ? $"; {themed - written} more {(themed - written == 1 ? "theme" : "themes")} could not be written up and {(themed - written == 1 ? "is" : "are")} listed with {(themed - written == 1 ? "its" : "their")} sources only" : "") +
               (themed == f.Themes ? ". " : $" (of {f.Themes} themes formed in the synthesis; the others belong to questions answered by counting, shown as tables). ") +
               $"Research questions: {plan.Numbered().Count}. " +
               "Use each number only with the meaning given here: the sources the results rest on are the ones kept for the synthesis, not the ones included at screening.";
    }

    // ---------- The paper as main.tex, and the archive ----------

    /// <summary>The paper of a checked run as main.tex, built in code from its files each time; null before the check.</summary>
    public string? MainTex(Guid runId)
    {
        var input = _reporter.LoadInput(runId);
        var paper = Load(runId);
        if (input == null || paper?.CheckedUtc == null) return null;
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { return PaperLatex.Build(MultivocalPaper.Build(input, paper, LoadAudit(runId))); }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>
    /// The archive of a multivocal run: main.tex when the paper is checked, then the module's files and the core
    /// files of the run folder, the raw search answers, and manifest.json with the SHA-256 of everything
    /// (<see cref="RunArchive.Build"/>). Empty when the run is not a multivocal run.
    /// </summary>
    public byte[] Archive(Guid runId)
    {
        var header = _runs.LoadHeader(runId);
        var module = ReviewModules.Find("multivocal");
        if (header == null || module == null || ReviewModules.ElsewhereFor(header, module.Key) != null) return Array.Empty<byte>();
        var generated = new List<(string Path, byte[] Content)>();
        if (MainTex(runId) is { } tex) generated.Add((MultivocalPaper.MainTexFile, new System.Text.UTF8Encoding(false).GetBytes(tex)));
        var planned = _reporter.LoadInput(runId)?.Planned;
        return RunArchive.Build(runId, _runs.FolderOf(runId), generated, module.ArchiveFiles, planned?.ProtocolSha256, Load(runId)?.Model);
    }

    // ---------- Writing ----------

    /// <summary>Writes the paper of a synthesised run. Needs the run's edit key.</summary>
    public async Task<MultivocalPaperFile> WriteAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can write its paper.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run's paper is being written already.");
        string folder = _runs.FolderOf(runId);
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage is not (MultivocalSynthesiser.StageSynthesised or StageWriting)) throw new InvalidOperationException("This run's paper has been written already.");
            var input = _reporter.LoadInput(runId) ?? throw new InvalidOperationException("Synthesise the run first.");
            await RunHeader.WriteAsync(folder, header with { Stage = StageWriting });

            var paper = await WritePaperAsync(_chat(), input, page => _pages.ReadText(runId, page), _model, _parallelism, progress);
            if (!string.IsNullOrWhiteSpace(paper.Outline)) await SafeFile.WriteAllTextAsync(Path.Join(folder, OutlineFile), paper.Outline);
            if (paper.File.PeerReview != null) await SafeFile.WriteAllTextAsync(Path.Join(folder, PeerReviewFile), JsonSerializer.Serialize(paper.File.PeerReview, Json));
            await SafeFile.WriteAllTextAsync(Path.Join(folder, PaperFile), JsonSerializer.Serialize(paper.File, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageWritten });
            return paper.File;
        }
        catch
        {
            // A failed writing step leaves the run synthesised, so it can be tried again.
            if (_runs.LoadHeader(runId) is { Stage: StageWriting } current)
                await RunHeader.WriteAsync(folder, current with { Stage = MultivocalSynthesiser.StageSynthesised });
            throw;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }
}
