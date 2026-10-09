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

/// <summary>One part of the paper's results: a theme section the model wrote, or a question answered in code.</summary>
public sealed class PaperResult
{
    public string Question { get; set; } = "";
    public string QuestionText { get; set; } = "";

    /// <summary>"theme" (written by the model from the theme's evidence, then checked) or "code" (counted in code).</summary>
    public string Kind { get; set; } = "theme";

    /// <summary>The field the citation check names it by ("synthesis-2"), or "answer-RQ2" for an answer in code.</summary>
    public string Field { get; set; } = "";
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>The note under a theme in code: what it rests on and the tensions between its sources.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// The written paper of a multivocal run (mlr-paper.json), before and after the citation check: the title, abstract,
/// rationale and objectives, the results in question order, the discussion and the summary for practitioners, with
/// the outline, the coverage of each theme, the peer review and the style pass on file. The method, the data section,
/// the answers in code, the theme notes and the references are not stored: they are written in code from the run's
/// files whenever the paper is shown (<see cref="MultivocalPaperParts"/>, <see cref="GreyReferences"/>).
/// </summary>
public sealed class MultivocalPaperFile
{
    public Guid RunId { get; set; }
    public DateTime WrittenUtc { get; set; }
    public string Model { get; set; } = "";
    public string Title { get; set; } = "";
    public string Abstract { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string Objectives { get; set; } = "";
    public List<PaperResult> Results { get; set; } = new();
    public string Discussion { get; set; } = "";
    public string? SummaryLead { get; set; }
    public List<string> SummaryItems { get; set; } = new();
    public List<ThemeCoverage> Coverage { get; set; } = new();
    public PeerReviewLog? PeerReview { get; set; }
    public List<StyleDeltaLog> Style { get; set; } = new();

    /// <summary>What did not go as planned while writing, in words, such as a step that fell back.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>When the citation check finished, and its counts after the repair; null before the check.</summary>
    public DateTime? CheckedUtc { get; set; }
    public CitationSupportSummary? Checks { get; set; }
}

/// <summary>A source the paper does not cite, and why.</summary>
public sealed record UncitedSource(int Reference, string Reason);

/// <summary>The citation check of a multivocal paper (citation-audit.json), with the same parts as the systematic review's.</summary>
public sealed class MultivocalCitationAudit
{
    public int ReferenceListSize { get; set; }
    public int InvalidMarkersStripped { get; set; }
    public List<StrippedCitation> Details { get; set; } = new();
    public CitationSupportSummary? InitialSupportSummary { get; set; }
    public CitationSupportSummary? SupportSummary { get; set; }
    public List<CitationRepair> Repairs { get; set; } = new();
    public int SourcesCited { get; set; }
    public List<FormattingChange> FormattingChanges { get; set; } = new();
    public List<UncitedSource> NotCited { get; set; } = new();
    public List<CitationSupportResult> SupportChecks { get; set; } = new();
}

/// <summary>
/// The two steps that turn a synthesised multivocal run into a paper (MLR G-8), through the writing and checking
/// every kind of review shares. Writing: the grounded outline, one cited section per theme with its fill pass, the
/// discussion, the automated peer review with one revision, the summary for practitioners, and last the title,
/// abstract, rationale and objectives with the style pass; questions about frequency or existence are answered in
/// code and never written by the model. Checking: every cited sentence is checked against the kept page text of the
/// source it cites, with the same verdicts as the systematic review, a second check of the weak ones and one repair.
/// The run goes from "Synthesised" through "Writing" to "Written", then through "Checking" to "Checked".
/// </summary>
public sealed class MultivocalWriter
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

    public MultivocalWriter(RunStore runs, MultivocalReporter reporter, MultivocalPages pages, Func<IChatCompletionService> chat, string model = "", int parallelism = 4,
        ReviewCache? cache = null)
    {
        _cache = cache ?? ReviewCache.Disabled;
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
            "(a) what the themes together say about the goal, (b) the state of the practice: where the 1st-tier sources (such as white papers and documentation) and the 3rd-tier sources (such as blog posts) agree or differ, (c) gaps the sources leave open and directions for research and practice, (d) implications for practitioners, (e) the limitations of the evidence (grey literature, not peer reviewed, pages read as kept on the access date) and of this review (automated screening, extraction and synthesis by a language model).",
    };

    /// <summary>The facts about the review the abstract may use, counted in code.</summary>
    public static string FrontMatterFacts(MultivocalReportInput input)
    {
        var f = MultivocalPaperParts.Flow(input);
        var plan = input.Planned.Plan;
        return $"Kind of review: {plan.ReviewKind} following the Garousi et al. (2019) guidelines. " +
               $"Grey sources found: {f.Found} from {f.Searches} searches. Screened twice: {f.Screened}; included: {f.Included}; excluded: {f.Excluded}. " +
               $"Passed the quality threshold of {input.Quality.Threshold} of {input.Quality.MaxPoints} points: {f.Passed}. Extracted against the systematic map: {f.Extracted}. " +
               $"Findings: {f.Findings}. Themes: {f.Themes}. Research questions: {plan.Numbered().Count}.";
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

    /// <summary>The writing itself, from the run's files: no file is read or written here, so tests can call it directly.</summary>
    public static async Task<(MultivocalPaperFile File, string Outline)> WritePaperAsync(IChatCompletionService chat, MultivocalReportInput input,
        Func<PageSnapshot, string?> readText, string model, int parallelism, IProgress<string>? progress = null)
    {
        var plan = input.Planned.Plan;
        var (evidence, questionOfTheme) = MultivocalEvidence.Build(input, readText);
        var profile = Profile(plan);
        int referenceCount = evidence.Sources.Count;
        string goal = string.IsNullOrWhiteSpace(plan.Goal) ? plan.Topic : plan.Goal;
        string referenceLines = evidence.ReferenceLines();
        var file = new MultivocalPaperFile { RunId = input.Planned.RunId, WrittenUtc = DateTime.UtcNow, Model = model };
        void Warn(string message)
        {
            file.Notes.Add(message);
            _log.LogWarning("{Message}", message.Replace('\r', ' ').Replace('\n', ' '));
        }
        void Step(int number, string what) => progress?.Report($"{number} of {WritingSteps} writing steps · {what}");

        // 1. The grounded outline: the claims each source supports, before any prose.
        Step(1, "the grounded outline");
        string excerpts = string.Join("\n", evidence.Sources.Select(s => evidence.SourceBlock(s.Reference, null, excerpts: 2)));
        string outline;
        using (LlmStage.Begin("outline"))
            outline = await ReviewWriter.WriteOutlineAsync(chat, profile, plan.Topic, goal, excerpts, referenceLines, Warn);

        // 2. One cited section per theme of the questions the model writes about, each with its fill pass.
        Step(2, $"{evidence.Themes.Count} theme section{(evidence.Themes.Count == 1 ? "" : "s")}");
        var (sections, coverage) = await ReviewWriter.WriteThemesAsync(chat, profile, goal, evidence, referenceCount, parallelism,
            (done, total) => progress?.Report($"2 of {WritingSteps} writing steps · theme sections: {done} of {total} written"), Warn);
        file.Coverage = coverage;

        // The results in question order: each question's theme sections, or its answer in code.
        var sectionsOf = sections.GroupBy(s => questionOfTheme.GetValueOrDefault(s.ThemeId, "")).ToDictionary(g => g.Key, g => g.ToList());
        var themeByName = evidence.Themes.ToDictionary(t => t.Id);
        foreach (var (number, question) in plan.Numbered())
        {
            if (MultivocalPaperParts.AnsweredInCode(question.Type))
            {
                var answer = MultivocalPaperParts.CodeAnswer(input, number, question);
                file.Results.Add(new PaperResult { Question = number, QuestionText = question.Text, Kind = "code", Field = $"answer-{number}", Heading = answer.Heading, Text = answer.Text });
                continue;
            }
            var written = sectionsOf.GetValueOrDefault(number) ?? new List<SynthesisSection>();
            if (written.Count == 0)
            {
                // Said in code, so the paper never leaves a question out without saying why.
                file.Results.Add(new PaperResult
                {
                    Question = number, QuestionText = question.Text, Kind = "code", Field = $"answer-{number}", Heading = $"{number}: {question.Text}",
                    Text = input.Synthesis.Unanswered.Contains(number)
                        ? "No extracted source states anything this question asks about, so the review cannot answer it from these sources."
                        : "The sources' findings on this question formed no theme that could be written up, so the review does not answer it.",
                });
                continue;
            }
            foreach (var section in written)
            {
                var evidenceTheme = themeByName.GetValueOrDefault(section.ThemeId);
                var theme = input.Synthesis.Themes.FirstOrDefault(t => t.Question == number && t.Name == evidenceTheme?.Name);
                file.Results.Add(new PaperResult
                {
                    Question = number, QuestionText = question.Text, Kind = "theme", Field = section.Field, Heading = section.Heading, Text = section.Text,
                    Note = theme == null ? null : MultivocalPaperParts.ThemeNote(input, theme),
                });
            }
        }
        var forDiscussion = file.Results.Select(r => new SynthesisSection { Field = r.Field, Heading = r.Heading, Text = r.Text }).ToList();

        // 3. The discussion, from all the results.
        Step(3, "the discussion");
        string discussion;
        using (LlmStage.Begin("discussion"))
            discussion = await ReviewWriter.WriteDiscussionAsync(chat, profile, plan.Topic, goal, forDiscussion, evidence, referenceCount, Warn);

        // 4. The automated peer review of the theme sections and the discussion, with one revision.
        Step(4, "the automated peer review");
        if (sections.Count > 0 && discussion.Length > 0)
        {
            using (LlmStage.Begin("automated-peer-review"))
                (file.PeerReview, discussion) = await ReviewWriter.PeerReviewAsync(chat, profile, sections, discussion, evidence, referenceCount, parallelism, Warn);
            foreach (var result in file.Results.Where(r => r.Kind == "theme"))
                result.Text = sections.FirstOrDefault(s => s.Field == result.Field)?.Text ?? result.Text;
            forDiscussion = file.Results.Select(r => new SynthesisSection { Field = r.Field, Heading = r.Heading, Text = r.Text }).ToList();
        }
        file.Discussion = discussion;

        // 5. The summary for practitioners (G14), from the checked-to-be results and the discussion only.
        Step(5, "the summary for practitioners");
        var summary = await ReviewWriter.WritePractitionerSummaryAsync(chat, profile, plan.Topic, goal, MultivocalReporter.Audience(plan.Audience),
            forDiscussion, discussion, referenceLines, referenceCount, Warn);
        file.SummaryLead = summary?.Lead;
        file.SummaryItems = summary?.Items.ToList() ?? new List<string>();

        // 6. Last, the title, abstract, rationale and objectives, with the numbers counted in code; then the style pass.
        Step(6, "the title, abstract and introduction");
        var front = await ReviewWriter.WriteFrontMatterAsync(chat, profile, plan.Topic, goal, FrontMatterFacts(input), forDiscussion, discussion,
            referenceLines, referenceCount, Warn);
        file.Title = front?.Title ?? $"{plan.Topic}: a {plan.ReviewKind}";
        file.Style.Add(new StyleDeltaLog("Title", file.Title, file.Title, Applied: false, Note: "The title is not sent through the style pass." + (front == null ? " The front-matter step failed; the title is the topic and the kind of review." : "")));
        using (LlmStage.Begin("style"))
        {
            async Task<string> Refine(string field, string? text)
            {
                if (string.IsNullOrWhiteSpace(text)) return "";
                var (refined, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, field, text);
                file.Style.Add(delta);
                return string.IsNullOrWhiteSpace(refined) ? text : refined;
            }
            file.Abstract = await Refine("Abstract", front?.Abstract);
            file.Rationale = await Refine("Rationale", front?.Rationale);
            file.Objectives = await Refine("Objectives", front?.Objectives);
        }
        if (front == null) file.Objectives = $"The review set out to {LowerFirst(goal.TrimEnd('.'))}.";
        return (file, outline);
    }

    // ---------- Checking ----------

    /// <summary>Checks every citation of a written paper against the kept page texts and repairs the rejected ones once.</summary>
    public async Task<MultivocalPaperFile> CheckAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can check its paper.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run's paper is being checked already.");
        string folder = _runs.FolderOf(runId);
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage is not (StageWritten or StageChecking)) throw new InvalidOperationException("This run's paper has been checked already.");
            var input = _reporter.LoadInput(runId) ?? throw new InvalidOperationException("Synthesise the run first.");
            var file = Load(runId) ?? throw new InvalidOperationException("Write the paper first.");
            await RunHeader.WriteAsync(folder, header with { Stage = StageChecking });

            var audit = await CheckPaperAsync(_chat(), input, file, page => _pages.ReadText(runId, page), _parallelism, progress, new CheckCache(_cache, _model));
            await SafeFile.WriteAllTextAsync(Path.Join(folder, AuditFile), JsonSerializer.Serialize(audit, Json));
            await SafeFile.WriteAllTextAsync(Path.Join(folder, PaperFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageChecked });
            return file;
        }
        catch
        {
            if (_runs.LoadHeader(runId) is { Stage: StageChecking } current)
                await RunHeader.WriteAsync(folder, current with { Stage = StageWritten });
            throw;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    /// <summary>The field names the check gives the paper's parts.</summary>
    public const string RationaleField = "rationale";
    public const string DiscussionField = "discussion";
    public const string SummaryField = "summary";

    /// <summary>
    /// The check itself, on the paper in place: the prose is cleaned of Markdown, citations outside the reference list
    /// are stripped, and every cited sentence goes through the shared grounding check against the kept page text of
    /// the source it cites. A source whose kept text is missing, or no longer matches its fingerprint, is unverifiable:
    /// its search summary is never used as evidence. The answers in code are not checked; they are counted, not written.
    /// </summary>
    public static async Task<MultivocalCitationAudit> CheckPaperAsync(IChatCompletionService chat, MultivocalReportInput input, MultivocalPaperFile file,
        Func<PageSnapshot, string?> readText, int parallelism, IProgress<string>? progress = null, CheckCache? cache = null)
    {
        var (evidence, _) = MultivocalEvidence.Build(input, readText);
        int referenceCount = evidence.Sources.Count;
        var audit = new MultivocalCitationAudit { ReferenceListSize = referenceCount };

        string Plain(string text, string field)
        {
            var (clean, changes) = ProseCleaner.Clean(text, field);
            audit.FormattingChanges.AddRange(changes);
            return clean;
        }
        var fields = new List<(string Field, string Text)> { (RationaleField, Plain(file.Rationale, RationaleField)) };
        fields.AddRange(file.Results.Where(r => r.Kind == "theme").Select(r => (r.Field, Plain(r.Text, r.Field))));
        fields.Add((DiscussionField, Plain(file.Discussion, DiscussionField)));
        if (file.SummaryItems.Count > 0) fields.Add((SummaryField, string.Join("\n", file.SummaryItems.Select(i => Plain(i, SummaryField)))));
        for (int i = 0; i < fields.Count; i++)
        {
            var validation = CitationValidator.ValidateAndStrip(fields[i].Text, referenceCount, fields[i].Field);
            fields[i] = (fields[i].Field, validation.CleanedText);
            audit.Details.AddRange(validation.Stripped);
        }
        audit.InvalidMarkersStripped = audit.Details.Count;

        // The kept page text only: no summary as a stand-in, and no extraction quotes for a source whose kept text is
        // missing or changed, so such a source is unverifiable rather than judged on what was once read from it.
        var sources = evidence.Sources.Where(s => s.Text != null).Select(s => s.Text! with { Abstract = "" }).ToList();
        var withText = sources.Where(s => s.HasFullText).Select(s => s.ReferenceNumber).ToHashSet();
        var quotes = evidence.VerifiedQuotes().Where(q => withText.Contains(q.Key)).ToDictionary(q => q.Key, q => q.Value);
        var grounding = await Grounding.CheckAndRepairAsync(chat, fields, sources, quotes, referenceCount,
            new GroundingOptions(parallelism, SecondCheck: true, Repair: true, Review: input.Planned.Plan.ReviewKind, Cache: cache),
            checking: count => progress?.Report($"Checking {count} citations against the kept pages"),
            checkProgress: (second, done, total) => progress?.Report($"{(second ? "Second check" : "First check")}: {done} of {total} sources"),
            repairing: count => progress?.Report($"Repairing {count} citations and checking them again"));

        var text = grounding.Fields.ToDictionary(f => f.Field, f => f.Text);
        file.Rationale = text.GetValueOrDefault(RationaleField, file.Rationale);
        foreach (var result in file.Results.Where(r => r.Kind == "theme"))
            result.Text = text.GetValueOrDefault(result.Field, result.Text);
        file.Discussion = text.GetValueOrDefault(DiscussionField, file.Discussion);
        if (text.TryGetValue(SummaryField, out var summary))
        {
            var items = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (items.Count > 0) file.SummaryItems = items;
        }

        var cited = new SortedSet<int>(grounding.Fields.SelectMany(f => ThematicSynthesis.CitedIn(f.Text)).Where(r => r >= 1 && r <= referenceCount));
        audit.SourcesCited = cited.Count;
        for (int reference = 1; reference <= referenceCount; reference++)
        {
            if (cited.Contains(reference)) continue;
            bool inTheme = evidence.Themes.Any(t => t.Sources.Contains(reference));
            audit.NotCited.Add(new UncitedSource(reference, inTheme
                ? "in a theme, but not cited after the fill pass, the peer review and the citation repair"
                : "in no theme the model writes about; its findings answer a question in code or were left out"));
        }
        audit.InitialSupportSummary = grounding.BeforeRepair;
        audit.SupportSummary = CitationSupportSummary.From(grounding.Checks);
        audit.Repairs = grounding.Repairs;
        audit.SupportChecks = grounding.Checks;
        file.CheckedUtc = DateTime.UtcNow;
        file.Checks = audit.SupportSummary;
        return audit;
    }

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
