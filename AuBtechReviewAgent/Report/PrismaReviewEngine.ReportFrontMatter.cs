using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>The front matter, artifact and closing sections of a systematic report, and the facts about the run they may use.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>Facts about the included set, so the abstract cannot call preprints "peer-reviewed studies".</summary>
    private static string IncludedFacts(ReviewState finalState)
    {
        var venueBreakdown = finalState.SynthesizedRecords
            .GroupBy(r => r.VenueType)
            .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")
            .ToList();
        return $"{finalState.SynthesizedRecords.Count} included record(s)" +
            (venueBreakdown.Count > 0 ? $" ({string.Join(", ", venueBreakdown)})" : "") +
            $"; the peer-reviewed-only filter was {(finalState.PeerReviewOnlyToggle ? "ON" : "OFF")}.";
    }

    private static string ScreeningFacts(ReviewState finalState) => finalState.Stats.HumanReviewed > 0
        ? $"a language model screened the records and a human reviewer then checked {finalState.Stats.HumanReviewed} decisions and changed {finalState.Stats.HumanOverrides}. Do not claim any other human involvement."
        : "done by a language model only; do not claim that any human screened, verified or reviewed records during the run.";

    /// <summary>PRISMA items 5 to 8, written in code from the run's configuration and ledger.</summary>
    private sealed record SystematicMethods(string Eligibility, string Sources, string Search, string Selection);

    /// <summary>
    /// PRISMA Items 5-8 describe what the run did, so they are rendered from the run's own configuration
    /// and ledger rather than written by the model (see MethodsSectionWriter for why).
    /// </summary>
    private SystematicMethods MethodsFromLedger(ReviewState finalState, string query, string inc, string exc, List<string> effectivePerspectives)
    {
        var selectedNames = finalState.SelectedSources.Select(SourceCatalog.DisplayNameFor).ToList();
        var unavailableNames = finalState.UnavailableSources.Select(SourceCatalog.DisplayNameFor).ToList();
        string methodsEligibility = MethodsSectionWriter.Eligibility(inc, exc, finalState.PeerReviewOnlyToggle);
        string methodsSources = MethodsSectionWriter.InformationSources(selectedNames, unavailableNames, finalState.SearchLogs, finalState.Timestamp,
            finalState.Stats.IdentifiedViaCitations, finalState.CitationChainingRequested, finalState.SeedChaining);
        string methodsSearch = MethodsSectionWriter.SearchStrategy(query, effectivePerspectives, finalState.MaxResultsPerSource,
            finalState.Stats.DuplicatesRemoved, finalState.Stats.CappedBeyondMaxResults, finalState.YearFrom, finalState.YearTo, finalState.Stats.OutsideDateRange,
            finalState.SearchStringsReviewed, finalState.SearchStringYields);
        string methodsSelection = MethodsSectionWriter.SelectionProcess(finalState.PeerReviewOnlyToggle, finalState.Stats, finalState.HumanScreeningReviewRequested, finalState.HumanScreeningReviewOutcome, Llm.DisplayNameFor("screening"));
        if (ExclusionReasons.Sentence(finalState.Phases.Screening) is { } exclusionSentence)
            methodsSelection += " " + exclusionSentence;
        return new SystematicMethods(methodsEligibility, methodsSources, methodsSearch, methodsSelection);
    }

    /// <summary>
    /// The title, abstract, rationale and objectives, the artifact and the closing sections, all written from the
    /// finished results. The front matter is written after the closing sections and gets the answers to the research
    /// questions and the conclusion as checked text, so the abstract states the review's reading as they do (issue #446). The front matter is written by the step every kind of review shares (Synthesis/ReviewWriter.cs):
    /// the abstract may only use numbers counted in code or stated in the results, and the rationale's citations are
    /// checked later like every other sentence. The first draft's versions are kept only for when that step fails.
    /// </summary>
    private async Task WriteFrontArtifactAndClosingAsync(ReportRun run)
    {
        var chat = run.Chat;
        var finalState = run.State;
        var thematic = run.Thematic;
        bool useThematic = run.UseThematic;
        var sections = run.Sections;
        string query = run.Query, explicitObjective = run.Objective, referenceListMapping = run.ReferenceList;
        string includedFacts = run.IncludedFacts, screeningFacts = run.ScreeningFacts;
        string synthesisProse = run.SynthesisProse, discussionText = run.DiscussionText;
        int referenceCount = run.ReferenceCount;

        ReportProgress(run.SessionId, finalState, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 5, 1), "Title, abstract and introduction");
        var resultSections = sections.Count > 0 ? (IReadOnlyList<SynthesisSection>)sections
            : new[] { new SynthesisSection { Field = "synthesisResultsItem", Heading = "Results", Text = synthesisProse } };
        // The artifact and the closing sections read the finished sections and neither reads the other, so they run at the
        // same time; the front matter (then its copy-edit) waits for the closing sections, whose answers and conclusion it
        // is given (issue #446). Results are used in the same order as before.
        async Task<(FrontMatter? Front, ReportDraft? Draft, (string Text, StyleDeltaLog Delta) Abstract, (string Text, StyleDeltaLog Delta) Rationale, (string Text, StyleDeltaLog Delta) Objectives)> FrontAndStyleAsync(Task<ClosingSections?> closingTask)
        {
            var closing = await closingTask;
            var written = await ReviewWriter.WriteFrontMatterAsync(chat, WritingProfile.Systematic, query, explicitObjective,
                FrontMatterFacts(finalState, useThematic ? thematic!.Codebook : null, includedFacts, screeningFacts) + EvidenceBaseFacts(useThematic ? thematic!.Evidence : null),
                resultSections, discussionText, referenceListMapping, referenceCount, message =>
                {
                    _log.LogWarning("{Message}", SanitizeLogMessage(message));
                    if (thematic != null) lock (thematic.Codebook) (thematic.Codebook.WritingNotes ??= new()).Add(message);
                },
                abstractRule: text => AbstractCountsProblem(text, finalState.Stats) ?? AbstractUnplacedProblem(text, finalState.Stats.Included, useThematic ? thematic!.Codebook.Uncoded.Count : 0),
                questions: finalState.ResearchQuestions,
                answers: closing?.Answers ?? thematic?.Answers, conclusion: closing?.Conclusion,
                weakSources: SystematicEvidence.WeakSources(finalState.Extractions ?? new List<StudyExtraction>()));
            // The first draft is written only now, and only when the front-matter step failed.
            var draft = written == null ? (await run.Fallback.Value).Draft : null;
            using (LlmStage.Begin("style"))
            {
                // Three separate copy-edits, sent together. Objectives written in code from the research questions are not copy-edited.
                var abstractTask = StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Abstract", written?.Abstract ?? draft!.Abstract);
                var rationaleTask = StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Rationale", written?.Rationale ?? draft!.Rationale);
                var objectivesTask = ReviewWriter.ObjectivesFromQuestions(finalState.ResearchQuestions) is { } codeObjectives
                    ? Task.FromResult((codeObjectives, new StyleDeltaLog("Objectives", codeObjectives, codeObjectives, Applied: false, Note: "Written in code from the research questions in the protocol; not sent through the stylistic pass.")))
                    : StylisticRefinerUtility.RefineAcademicProseAsync(chat, "Objectives", written?.Objectives ?? draft!.Objectives);
                await Task.WhenAll(abstractTask, rationaleTask, objectivesTask);
                return (written, draft, await abstractTask, await rationaleTask, await objectivesTask);
            }
        }

        string artifactKind = ArtifactKinds.Normalize(finalState.ArtifactKind);
        async Task<ReviewArtifact?> ArtifactAsync()
        {
            if (artifactKind == ArtifactKinds.None) return null;
            ReportProgress(run.SessionId, finalState, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 6, 0), "Building the artifact");
            string results = sections.Count > 0
                ? string.Join("\n\n", sections.Select(x => $"THEME: {x.Heading}\n{x.Text}")) + $"\n\nDISCUSSION:\n{discussionText}"
                : $"{synthesisProse}\n\nDISCUSSION:\n{discussionText}";
            using (LlmStage.Begin("artifact"))
                return await ArtifactBuilder.BuildAsync(chat, artifactKind, finalState.SynthesisTargetDirective ?? "",
                    explicitObjective, results, referenceListMapping, referenceCount);
        }

        // The limitations of the evidence, the implications, the future research and the conclusion (PRISMA 23b, 23d)
        // read the same finished sections and the answers written with the discussion, so they are written beside the artifact.
        async Task<ClosingSections?> ClosingAsync() => useThematic && thematic!.Evidence != null
            ? await ReviewWriter.WriteClosingAsync(chat, WritingProfile.Systematic, query, explicitObjective,
                FrontMatterFacts(finalState, thematic.Codebook, includedFacts, screeningFacts), resultSections, discussionText,
                thematic.Evidence, referenceCount, message =>
                {
                    // On file with the discussion's notes, so a figure sent back or kept is in thematic-codebook.json (issue #452).
                    _log.LogWarning("{Message}", SanitizeLogMessage(message));
                    lock (thematic.Codebook) (thematic.Codebook.WritingNotes ??= new()).Add(message);
                },
                finalState.ResearchQuestions, thematic.Codebook.Unanswered?.Select(u => u.Question).ToList(), thematic.Answers)
            : null;

        var artifactTask = ArtifactAsync();
        var closingTask = ClosingAsync();
        var frontTask = FrontAndStyleAsync(closingTask);
        await Task.WhenAll(frontTask, artifactTask, closingTask); // a failure of any is thrown here, after all have finished
        run.Closing = await closingTask;
        var (front, draft, styledAbstract, styledRationale, styledObjectives) = await frontTask;
        run.Artifact = await artifactTask;

        string frontNote = front == null ? "The front-matter step failed; the first draft's text was used." : "";
        string titleText = front?.Title ?? draft!.Title;
        run.Title = CleanTitle(titleText, query);
        run.Deltas.Add(new StyleDeltaLog("Title", titleText, run.Title, Applied: false, Note: "Title is not sent through the stylistic pass." + (frontNote.Length > 0 ? " " + frontNote : "")));
        run.Deltas.Add(styledAbstract.Delta);
        run.Deltas.Add(styledRationale.Delta);
        run.Deltas.Add(styledObjectives.Delta);
        run.Abstract = styledAbstract.Text;
        run.Rationale = styledRationale.Text;
        run.Objectives = styledObjectives.Text;
        if (string.IsNullOrWhiteSpace(run.Abstract)) run.Abstract = front?.Abstract ?? draft!.Abstract;
        if (string.IsNullOrWhiteSpace(run.Rationale)) run.Rationale = front?.Rationale ?? draft!.Rationale;
        if (string.IsNullOrWhiteSpace(run.Objectives)) run.Objectives = front?.Objectives ?? draft!.Objectives;
        run.Rationale = ReviewWriter.CitedSentencesOnly(run.Rationale, out var uncited);
        if (uncited != null) run.Deltas.Add(uncited);
    }

    /// <summary>What the front matter may say about the run, counted in code from the ledger.</summary>
    private static string FrontMatterFacts(ReviewState state, ThematicCodebook? book, string includedFacts, string screeningFacts)
    {
        var s = state.Stats;
        var lines = new List<string>
        {
            $"Records identified: {s.TotalIdentified}; duplicates removed: {s.DuplicatesRemoved}{OtherRemovals(state)}; records screened: {s.Screened}; included: {s.Included}; excluded: {s.Excluded}.",
            $"Included set: {includedFacts}",
            $"Screening: {screeningFacts}",
            $"Full text was read for {s.FullTextRetrieved} of the {s.Included} included studies; the others were read from their abstracts.",
        };
        if (book != null) lines.Add($"Thematic synthesis: {book.Themes.Count} theme{(book.Themes.Count == 1 ? "" : "s")} from {book.Codes.Count} code{(book.Codes.Count == 1 ? "" : "s")}.");
        if (book is { Uncoded.Count: > 0 }) lines.Add(UnplacedFact(book.Uncoded.Count));
        if (RemovalsBeforeScreening(s).Any(r => r.Label != "duplicates"))
            lines.Add("Wherever both the records identified and the records screened are given, every removal between them is given too, as listed above.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The facts about the evidence base for the front matter (issue #146): the introduction names the design of the
    /// studies it cites, so it gets each study's design and what it was read from, as the discussion does.
    /// </summary>
    private static string EvidenceBaseFacts(ReviewEvidence? evidence) =>
        string.IsNullOrWhiteSpace(evidence?.BaseFacts) ? ""
            : $"\nEvidence base (from the data extraction; name the design of a cited study only as given here): {evidence.BaseFacts}";

    /// <summary>The records removed before screening for a reason other than being a duplicate, as the flow diagram counts them (issue #118).</summary>
    private static string OtherRemovals(ReviewState state)
    {
        var s = state.Stats;
        string range = state.YearFrom > 0 && state.YearTo > 0 ? $" ({state.YearFrom} to {state.YearTo})" : "";
        var parts = new List<string>();
        if (s.OutsideDateRange > 0) parts.Add($"removed because their publication year was outside the year range{range}: {s.OutsideDateRange}");
        if (s.CappedBeyondMaxResults > 0) parts.Add($"removed over the per-source cap: {s.CappedBeyondMaxResults}");
        if (s.ScreeningErrors > 0) parts.Add($"could not be screened: {s.ScreeningErrors}");
        return string.Concat(parts.Select(p => "; " + p));
    }

    /// <summary>Every count of records removed between identification and screening that is above zero, as the flow diagram has them.</summary>
    private static List<(string Label, int Count)> RemovalsBeforeScreening(ReviewStats s) =>
        new List<(string Label, int Count)>
        {
            ("duplicates", s.DuplicatesRemoved),
            ("the records outside the year range", s.OutsideDateRange),
            ("the records over the per-source cap", s.CappedBeyondMaxResults),
            ("the records that could not be screened", s.ScreeningErrors),
        }.Where(r => r.Count > 0).ToList();

    /// <summary>
    /// Null when the abstract's record counts add up; otherwise what it leaves out. An abstract that gives both the
    /// records identified and the records screened must give every removal between them too, so a reader can follow
    /// the numbers to the flow diagram (issue #118). An abstract that gives neither, or only one, is left alone.
    /// </summary>
    public static string? AbstractCountsProblem(string abstractText, ReviewStats s)
    {
        if (s.TotalIdentified == s.Screened) return null;
        var numbers = ReviewWriter.NumbersIn(abstractText);
        if (!numbers.Contains(s.TotalIdentified.ToString()) || !numbers.Contains(s.Screened.ToString())) return null;
        var missing = RemovalsBeforeScreening(s).Where(r => !numbers.Contains(r.Count.ToString())).ToList();
        return missing.Count == 0 ? null
            : $"the abstract gives the {s.TotalIdentified} records identified and the {s.Screened} screened but leaves out " +
              $"{string.Join(" and ", missing.Select(r => $"{r.Label} ({r.Count})"))}; give every removal between them, as the facts list them.";
    }

    /// <summary>The fact line on the included studies the themes leave out (issue #447), for the front matter and the closing sections.</summary>
    public static string UnplacedFact(int unplaced) =>
        $"Studies placed in no theme: {unplaced} (the themes rest on the others; an abstract that describes the themes or gives the included studies says so).";

    private static readonly Regex ThemeWord = new(@"\bthem(?:e|es|atic)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Null when the abstract does not hide the studies placed in no theme; otherwise why it does (issue #447). An abstract
    /// that speaks of the themes or gives the number of included studies, when some studies are in no theme, must give
    /// that number too, in digits or in words, so it cannot read as if every included study were synthesised.
    /// </summary>
    public static string? AbstractUnplacedProblem(string abstractText, int included, int unplaced)
    {
        if (unplaced <= 0 || string.IsNullOrWhiteSpace(abstractText)) return null;
        var numbers = ReviewWriter.CountsIn(abstractText);
        if (!ThemeWord.IsMatch(abstractText) && !numbers.Contains(included.ToString())) return null;
        return numbers.Contains(unplaced.ToString()) ? null
            : $"the abstract describes the themes or the {included} included studies but not the {unplaced} placed in no theme; say how many studies the themes leave out, as the facts give it.";
    }

    /// <summary>
    /// Keeps the title a title: first line only, no trailing full stop, and if the model returned a
    /// paragraph (or nothing) fall back to a neutral title built from the query.
    /// </summary>
    public static string CleanTitle(string rawTitle, string query)
    {
        string t = (rawTitle ?? "").Split('\n')[0].Trim().TrimEnd('.');
        int words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool looksLikeParagraph = Regex.Matches(t, @"[.!?]\s+[A-Z]").Count > 0 || words > 30;
        if (string.IsNullOrWhiteSpace(t) || t == "Extraction failed" || looksLikeParagraph)
            return $"{query.Trim()}: A Systematic Literature Review";
        return t;
    }
}
