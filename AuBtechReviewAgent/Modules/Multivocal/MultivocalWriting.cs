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
    private readonly string _checkModel;
    private readonly int _parallelism;

    private readonly ReviewCache _cache;
    private readonly RunMetricsStore _metrics;

    /// <param name="checkModel">The model that answers the citation checks when a fast model takes them (Llm:FastModel); empty: <paramref name="model"/>.</param>
    public MultivocalWriter(RunStore runs, MultivocalReporter reporter, MultivocalPages pages, Func<IChatCompletionService> chat, string model = "", int parallelism = 4,
        ReviewCache? cache = null, RunMetricsStore? metrics = null, string? checkModel = null)
    {
        _cache = cache ?? ReviewCache.Disabled;
        _metrics = metrics ?? RunMetricsStore.Disabled;
        _runs = runs;
        _reporter = reporter;
        _pages = pages;
        _chat = chat;
        _model = model;
        _checkModel = string.IsNullOrWhiteSpace(checkModel) ? model : checkModel.Trim();
        _parallelism = Math.Max(1, parallelism);
    }

    /// <summary>Whether a run at this stage has its synthesis, so its report and evidence can be read.</summary>
    public static bool IsSynthesised(string? stage) =>
        stage is MultivocalSynthesiser.StageSynthesised or StageWriting or StageWritten or StageChecking or StageChecked;

    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    /// <summary>The run's paper, with the version that wrote it taken from the run's own files when the paper predates the field.</summary>
    public MultivocalPaperFile? Load(Guid runId)
    {
        string folder = _runs.FolderOf(runId);
        var paper = Read<MultivocalPaperFile>(Path.Join(folder, PaperFile));
        if (paper != null && string.IsNullOrWhiteSpace(paper.AppVersion)) paper.AppVersion = RecordedVersion(folder);
        return paper;
    }

    /// <summary>The version recorded in a run's files: run-metrics.json, else llm-calls.json; null when neither records one.</summary>
    public static string? RecordedVersion(string folder)
    {
        string? fromMetrics = Read<RunMetrics>(Path.Join(folder, MultivocalRunLog.MetricsFile))?.AppVersion;
        if (!string.IsNullOrWhiteSpace(fromMetrics)) return fromMetrics;
        string? fromCalls = MultivocalRunLog.LoadCalls(folder)?.RunSettings?.AppVersion;
        return string.IsNullOrWhiteSpace(fromCalls) ? null : fromCalls;
    }

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
        LeanEvidence = true,
        QuestionIds = plan.Numbered().Select(q => q.Number).ToList(),
        // The limitations, implications and conclusion are written after the discussion (issue #461), as Garousi et al.
        // (2019) give them: threats to validity, and implications for practitioners and for researchers.
        DiscussionParts =
            "(a) what the answers and the themes together say about the goal, (b) the state of the practice: where the 1st-tier sources (such as white papers and documentation) and the 3rd-tier sources (such as blog posts)" +
            (plan.UsesFormalPool ? ", and the grey and the academic sources," : "") + " agree or differ, (c) how strong the evidence behind the main findings is. Do not write the limitations, the implications, the directions for research or a conclusion: they are separate sections written after this one.",
        LimitationsPart = "the limitations of the evidence (the threats to validity of Garousi et al. 2019)",
        ImplicationsPart = "the implications for practitioners (Garousi et al. 2019)",
        FutureResearchPart = "the implications for researchers: the directions for future research",
    };

    /// <summary>The kind of a result whose theme section the model could not write.</summary>
    public const string UnwrittenKind = "unwritten";

    /// <summary>What the paper says, in code, in place of a theme section that could not be written.</summary>
    public const string UnwrittenText =
        "The section on this theme could not be written: the language model's answer failed the checks in code twice. " +
        "The theme's quotations are listed in the data appendix.";

    /// <summary>The facts about the review the abstract may use, counted in code (<see cref="MultivocalFrontMatter.Facts"/>).</summary>
    public static string FrontMatterFacts(MultivocalReportInput input, int? sectionsWritten = null) => MultivocalFrontMatter.Facts(input, sectionsWritten);

    // ---------- The paper as main.tex, and the archive ----------

    /// <summary>The paper of a checked run as main.tex, built in code from its files each time; null before the check.</summary>
    public string? MainTex(Guid runId)
    {
        var input = _reporter.LoadInput(runId);
        var paper = Load(runId);
        if (input == null || paper?.CheckedUtc == null) return null;
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var article = MultivocalArticleInfo.For(input, paper, File.Exists(Path.Join(_runs.FolderOf(runId), "protocol.md")));
            return PaperLatex.Build(MultivocalPaper.Build(input, paper, LoadAudit(runId), article));
        }
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
