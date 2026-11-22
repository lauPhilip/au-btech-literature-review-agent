using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;
/// <summary>Synthesis stage: full texts, extraction and appraisal, outline, cited sections, peer review and the report.</summary>
public partial class PrismaReviewEngine
{

    /// <summary>Test hook: replaces the full-text download (default: arXiv, the source's open-access link, then Unpaywall).</summary>
    public Func<AcademicPaper, string, Task<FullTextResult>>? FullTextFetcher { get; init; }

    /// <summary>
    /// Downloads and chunks the full text of every included paper, a few at a time. Where each text came
    /// from (or why there is none) is written to the ledger.
    /// </summary>
    private async Task RetrieveFullTextsAsync(RunContext ctx)
    {
        var included = ctx.State.Phases.Screening
            .Where(l => l.Decision == "Included" && ctx.Papers.ContainsKey(l.PaperId))
            .Select(l => ctx.Papers[l.PaperId])
            .ToList();

        using var throttle = new SemaphoreSlim(4);
        var downloads = included.Select(async paper =>
        {
            await throttle.WaitAsync();
            try
            {
                return FullTextFetcher != null
                    ? await FullTextFetcher(paper, ctx.Workspace)
                    : await DocumentRAGUtility.IngestAndChunkPaperAsync(paper, ctx.Workspace, OpenSources.ContactEmail);
            }
            catch (Exception ex)
            {
                return new FullTextResult(Array.Empty<DocumentChunk>(), $"none (download failed: {SanitizeLogMessage(ex.Message)})", null);
            }
            finally { throttle.Release(); }
        }).ToList();

        ReportProgress(ctx.RunId, ctx.State, RunProgress.FullText, 0, $"Fetching open-access full texts of {included.Count} studies");
        for (int i = 0; i < included.Count; i++)
        {
            var result = await downloads[i];
            ReportProgress(ctx.RunId, ctx.State, RunProgress.FullText, (double)(i + 1) / included.Count, Of(i + 1, included.Count, "papers"));
            ctx.Chunks[included[i].Id] = result.Chunks.ToList();
            ctx.State.FullTextSources[included[i].Id] = result.Source;
            if (IsWebAddress(result.Url)) ctx.State.FullTextUrls[included[i].Id] = result.Url!;
            if (result.Chunks.Count > 0) ctx.State.Stats.FullTextRetrieved++;
        }
        await PublishAsync(ctx);
    }

    /// <summary>
    /// Structured data extraction and MMAT appraisal for every included study, a few at a time. Written to
    /// extraction.json and the ledger; each value carries a quote that was checked against the paper's text.
    /// </summary>
    private async Task ExtractStudiesAsync(RunContext ctx, IReadOnlyList<ReferencedPaper> papers)
    {
        using var throttle = new SemaphoreSlim(Math.Max(1, Llm.ScreeningParallelism));
        int extracted = 0;
        ReportProgress(ctx.RunId, ctx.State, RunProgress.Extraction, 0, $"Extracting and appraising {papers.Count} studies");
        var work = papers.Select(async p =>
        {
            await throttle.WaitAsync();
            try
            {
                StudyExtraction result;
                using (LlmStage.Begin("extraction"))
                    result = await StudyExtractor.ExtractAsync(ctx.Chat, p);
                int n = Interlocked.Increment(ref extracted);
                ReportProgress(ctx.RunId, ctx.State, RunProgress.Extraction, (double)n / papers.Count, Of(n, papers.Count, "studies"));
                return result;
            }
            finally { throttle.Release(); }
        }).ToList();
        ctx.State.Extractions = (await Task.WhenAll(work)).OrderBy(e => e.ReferenceNumber).ToList();
        try
        {
            await File.WriteAllTextAsync(Path.Join(ctx.Workspace, "extraction.json"),
                JsonSerializer.Serialize(ctx.State.Extractions, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning("Run {RunId}: extraction.json not written: {Message}", ctx.RunId, ex.Message); }
        await SaveStateAsync(ctx.RunId, ctx.State);
    }

    private async Task SynthesizeAsync(RunContext ctx)
    {
        var reviewState = ctx.State;
        var request = ctx.Request;

        // One ordering for the whole report. The reference list shown to the model, the [n] markers it
        // writes, the extraction table and the LaTeX bibliography are all numbered from this list.
        var includedPapers = reviewState.Phases.Screening
            .Where(p => p.Decision.Equals("Included", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ApaCitation ?? "", ApaCitationBuilder.ReferenceOrder)
            .ThenBy(p => p.PaperId, StringComparer.Ordinal)
            .ToList();

        reviewState.SynthesizedRecords.Clear();
        var referencedPapers = new List<ReferencedPaper>();
        for (int i = 0; i < includedPapers.Count; i++)
        {
            var log = includedPapers[i];
            reviewState.SynthesizedRecords.Add(BuildRecord(log, i + 1));

            ctx.Papers.TryGetValue(log.PaperId, out var paper);
            ctx.Chunks.TryGetValue(log.PaperId, out var chunks);
            referencedPapers.Add(new ReferencedPaper(i + 1, log.PaperId, log.Title,
                paper?.Abstract ?? log.Abstract ?? "", (IReadOnlyList<DocumentChunk>?)chunks ?? Array.Empty<DocumentChunk>()));
        }
        await SaveStateAsync(ctx.RunId, reviewState);

        await ExtractStudiesAsync(ctx, referencedPapers);

        var sbReferences = new StringBuilder();
        for (int i = 0; i < includedPapers.Count; i++)
        {
            sbReferences.AppendLine($"[{i + 1}] - {includedPapers[i].ApaCitation}");
        }

        // About two excerpts per study (at least 40, at most 120), so a large review does not leave most
        // studies with a single excerpt or none.
        string groundedContext = GroundingContextBuilder.Build(referencedPapers, $"{request.Query} {request.Objective}",
            Math.Clamp(2 * referencedPapers.Count, 40, 120));

        await GeneratePrismaChecklistReportWithRAGAsync(ctx.RunId, ctx.Chat, request.Query, request.Objective,
            inc: request.Inclusion, exc: request.Exclusion, groundedContext, sbReferences.ToString(), reviewState,
            reviewState.SearchPerspectives, referenceCount: includedPapers.Count, referencedPapers);
    }

    private static IncludedPaperMetricRow BuildRecord(ScreeningLog log, int referenceNumber)
    {
        // Venue type, venue name and year come from the source metadata (ApaCitationBuilder). Runs from
        // before that change only have the citation string, so fall back to parsing it.
        string venue = !string.IsNullOrEmpty(log.VenueType) ? log.VenueType : ClassifyVenueFromCitation(log.ApaCitation);
        int parsedYear = log.Year > 0 ? log.Year : ExtractYearFromCitation(log.ApaCitation);

        var (cleanCategory, platformFull) = SourceOfPaper(log.PaperId);

        // Quartile only from an exact Scimago title match; no default.
        string quartile = "N/A";
        if (venue is "Journals" or "Transactions" && !string.IsNullOrWhiteSpace(log.VenueName))
        {
            quartile = JournalRankingMatcher.LookupQuartile(log.VenueName, parsedYear);
        }


        return new IncludedPaperMetricRow
        {
            Title = log.Title,
            Summary = log.BriefSummary,
            ApaCitation = log.ApaCitation ?? "",
            SourcePlatform = platformFull,
            Year = parsedYear,
            VenueType = venue,
            InclusionRationale = log.HumanReviewed && log.ModelDecision != null
                ? $"Included by the human reviewer (model decision: {log.ModelDecision}).{(log.HumanNote != null ? " Note: " + log.HumanNote : "")}"
                : log.Reasoning,
            Category = cleanCategory,
            Quartile = quartile,
            ConferenceRating = "N/A", // no conference-ranking data source is wired in, so no rating is claimed
            ReferenceNumber = referenceNumber,
            PaperId = log.PaperId,
            Authors = log.Authors ?? new List<string>(),
            VenueName = log.VenueName,
            Doi = log.Doi,
            Url = log.Url,
        };
    }

    private sealed record CitationCheck(List<(string Field, string Text)> Fields, List<StrippedCitation> Strips, List<CitationSupportResult> Checks,
        CitationSupportSummary Initial, CitationSupportSummary Summary, List<CitationRepair> Repairs);

    private sealed record CoverageResult(Dictionary<string, string> TextOf, SortedSet<int> Cited, List<UncodedStudy> NotCited, string Sentence);

    /// <summary>
    /// Every cited text of the report as plain prose, its citation markers checked against the reference list, its
    /// citations checked against the cited papers and repaired once, and the counts written to the run's stats.
    /// </summary>
    private async Task<CitationCheck> CheckCitationsAsync(ReportRun run)
    {
        var sessionId = run.SessionId;
        var chat = run.Chat;
        var finalState = run.State;
        var thematic = run.Thematic;
        var sections = run.Sections;
        var closing = run.Closing;
        var artifact = run.Artifact;
        var papersForCheck = run.Papers;
        int referenceCount = run.ReferenceCount;
        string cleanRationale = run.Rationale, synthesisProse = run.SynthesisProse, discussionText = run.DiscussionText;
        string Plain(string text, string field) => run.Plain(text, field);

        // The model sometimes writes Markdown (headings, lists, tables, **emphasis**); turn it back into plain
        // prose before the citation check, so the checked sentences are exactly the ones shown on the page
        // and in main.tex. Every change is listed under FormattingChanges in citation-audit.json.
        // Fields are kept in report order: the synthesis overview, the theme subsections, the discussion.
        // The artifact the reviewer asked for was built from the finished results above; its cited elements go
        // through the same range check, support check and repair as the prose.
        var fields = new List<(string Field, string Text)> { ("rationaleItem", Plain(cleanRationale, "rationaleItem")), ("synthesisResultsItem", Plain(synthesisProse, "synthesisResultsItem")) };
        fields.AddRange(sections.Select(s => (s.Field, Plain(s.Text, s.Field))));
        fields.Add(("discussionItem", Plain(discussionText, "discussionItem")));
        // The closing sections are checked like the discussion; the limitations of the review processes are written in
        // code and cite nothing, so they are not checked.
        if (closing != null)
        {
            fields.Add(("evidenceLimitationsItem", Plain(closing.EvidenceLimitations, "evidenceLimitationsItem")));
            fields.Add(("implicationsItem", Plain(closing.Implications, "implicationsItem")));
            fields.Add(("futureResearchItem", Plain(closing.FutureResearch, "futureResearchItem")));
            fields.Add(("conclusionItem", Plain(closing.Conclusion, "conclusionItem")));
            // One field per answer to a research question, so each is checked and repaired on its own.
            for (int i = 0; i < (closing.Answers?.Count ?? 0); i++)
                fields.Add(($"researchAnswer{i + 1}", Plain(closing.Answers![i], $"researchAnswer{i + 1}")));
        }
        if (artifact is { Error: null }) fields.AddRange(artifact.CitableTexts().ToList());

        // STORM-style traceability guardrail: every [n] citation marker the model writes must resolve to
        // an entry that actually exists in the run's reference list. The model occasionally invents or
        // miscounts numbers (e.g. citing [56] against a 53-entry list); strip those instead of shipping
        // a report whose citations don't trace back to a real source, and log every removal to an
        // on-disk audit artifact so the discrepancy itself stays visible rather than silently vanishing.
        // The stripping logic lives in CitationValidator so it can be unit-tested in isolation.
        var citationStrips = new List<StrippedCitation>();
        for (int i = 0; i < fields.Count; i++)
        {
            var validation = CitationValidator.ValidateAndStrip(fields[i].Text, referenceCount, fields[i].Field);
            fields[i] = (fields[i].Field, validation.CleanedText);
            citationStrips.AddRange(validation.Stripped);
        }
        finalState.Stats.InvalidCitationsStripped += citationStrips.Count;

        // Citation SUPPORT check: does the cited paper actually say what the sentence claims? The range
        // check above cannot catch a valid-looking [n] that points at the wrong paper. The verified
        // extraction quotes are given to the check as evidence too: they are what the synthesis was written from.
        var verifiedFindings = (finalState.Extractions ?? new List<StudyExtraction>())
            .ToDictionary(e => e.ReferenceNumber, e => (IReadOnlyList<string>)ThematicSynthesis.VerifiedFindings(e).Select(f => f.Finding.Quote).ToList());
        // Repair: sentences whose citation was rejected are rewritten once from the cited paper's evidence
        // (or lose that citation) and checked again. Before and after go into citation-audit.json. The check
        // and the repair are shared by every kind of review (Verification/Grounding.cs).
        var grounding = await Grounding.CheckAndRepairAsync(chat, fields, papersForCheck, verifiedFindings, referenceCount,
            new GroundingOptions(Llm.ScreeningParallelism, Synthesis.SecondCitationCheck, Synthesis.RepairCitations),
            checking: count => ReportProgress(sessionId, finalState, RunProgress.Checking, 0, $"Checking {count} citations"),
            checkProgress: (second, done, total) => ReportProgress(sessionId, finalState, RunProgress.Checking,
                RunProgress.Phase(RunProgress.CheckingPhases, second ? 1 : 0, (double)done / total),
                second ? $"Second check: {Of(done, total, "papers")}" : $"First check: {Of(done, total, "papers")}"),
            repairing: count => ReportProgress(sessionId, finalState, RunProgress.Checking, RunProgress.Phase(RunProgress.CheckingPhases, 2, 0),
                $"Repairing {count} citations and checking them again"));
        fields = grounding.Fields;
        var supportChecks = grounding.Checks;
        var initialSummary = grounding.BeforeRepair;
        var repairs = grounding.Repairs;
        finalState.Stats.CitationsRepaired = repairs.Count(r => r.Action is "rewrite" or "drop_citation" or "delete");

        // Theme consistency: a subsection citing a study that was not coded under its theme is flagged (not removed).
        var themeOfField = sections.ToDictionary(s => s.Field, s => thematic?.Codebook.Themes.FirstOrDefault(t => t.Id == s.ThemeId));
        foreach (var check in supportChecks)
        {
            if (themeOfField.TryGetValue(check.Field, out var theme) && theme != null && !theme.Studies.Contains(check.Reference))
                check.ThemeNote = $"Study [{check.Reference}] was not coded under the theme \"{theme.Name}\".";
        }
        finalState.Stats.CitationsOutsideTheme = supportChecks.Count(c => c.ThemeNote != null);

        var supportSummary = CitationSupportSummary.From(supportChecks);
        finalState.Stats.CitationsChecked = supportSummary.Checked;
        finalState.Stats.CitationsSupported = supportSummary.Supported;
        finalState.Stats.CitationsPartiallySupported = supportSummary.PartiallySupported;
        finalState.Stats.CitationsNotSupported = supportSummary.NotSupported;
        finalState.Stats.CitationsUnverifiable = supportSummary.Unverifiable;
        finalState.Stats.CitationsInsufficientEvidence = supportSummary.InsufficientEvidence;
        finalState.Stats.CitationsSecondChecked = supportSummary.SecondChecked;
        finalState.Stats.CitationsUpgradedBySecondCheck = supportSummary.UpgradedBySecondCheck;
        return new CitationCheck(fields, citationStrips, supportChecks, initialSummary, supportSummary, repairs);
    }

    /// <summary>Which included studies the synthesis and discussion cite, and why the others are not cited; saved with the codebook.</summary>
    private async Task<CoverageResult> RecordCoverageAsync(ReportRun run, List<(string Field, string Text)> fields)
    {
        var sessionId = run.SessionId;
        var finalState = run.State;
        var thematic = run.Thematic;
        bool useThematic = run.UseThematic;
        int referenceCount = run.ReferenceCount;

        var textOf = fields.ToDictionary(f => f.Field, f => f.Text);
        var citedStudies = new SortedSet<int>(fields.SelectMany(f => ThematicSynthesis.CitedIn(f.Text)).Where(r => r >= 1 && r <= referenceCount));
        var notCited = new List<UncodedStudy>();
        for (int refNo = 1; refNo <= referenceCount; refNo++)
        {
            if (citedStudies.Contains(refNo)) continue;
            var uncodedReason = thematic?.Codebook.Uncoded.FirstOrDefault(u => u.Reference == refNo)?.Reason;
            var theme = useThematic ? thematic!.Codebook.Themes.FirstOrDefault(t => t.Studies.Contains(refNo)) : null;
            notCited.Add(new UncodedStudy(refNo,
                uncodedReason != null ? $"not coded: {uncodedReason.TrimEnd('.')}" :
                theme != null ? $"coded under \"{theme.Name}\" but not cited after the fill pass, peer review and citation repair" :
                "not cited by the synthesis"));
        }
        if (thematic != null)
        {
            thematic.Codebook.NotCited = notCited;
            foreach (var cov in thematic.Codebook.Coverage)
            {
                if (!textOf.TryGetValue(cov.Field, out var finalText)) continue;
                cov.CitedFinal = ThematicSynthesis.CitedIn(finalText).Intersect(cov.Studies).ToList();
                cov.NotCited = cov.Studies.Except(cov.CitedFinal).ToList();
            }
            finalState.Stats.ThemesIdentified = useThematic ? thematic.Codebook.Themes.Count : 0;
            finalState.Stats.StudiesCoded = useThematic ? thematic.Codebook.CodedStudies.Count() : 0;
            finalState.Stats.CodingKappa = useThematic ? thematic.Codebook.SecondCoding?.Kappa : null;
            try
            {
                await File.WriteAllTextAsync(Path.Join(GetWorkspaceFolderPath(sessionId), "thematic-codebook.json"),
                    JsonSerializer.Serialize(thematic.Codebook, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not save thematic-codebook.json: {Message}", SanitizeLogMessage(ex.Message));
            }
        }
        finalState.Stats.StudiesCitedInSynthesis = citedStudies.Count;
        string coverageSentence = ThematicSynthesis.CoverageSentence(citedStudies.Count, referenceCount, notCited);
        return new CoverageResult(textOf, citedStudies, notCited, coverageSentence);
    }

    /// <summary>The checked texts put back in place, then citation-audit.json, the style ledger and prisma-report.json.</summary>
    private async Task WriteReportFilesAsync(ReportRun run, CitationCheck check, CoverageResult coverage)
    {
        var sessionId = run.SessionId;
        var finalState = run.State;
        var thematic = run.Thematic;
        bool useThematic = run.UseThematic;
        var sections = run.Sections;
        var closing = run.Closing;
        var artifact = run.Artifact;
        int referenceCount = run.ReferenceCount;
        var textOf = coverage.TextOf;
        var (citationStrips, supportChecks, initialSummary, supportSummary, repairs) = (check.Strips, check.Checks, check.Initial, check.Summary, check.Repairs);
        string Plain(string text, string field) => run.Plain(text, field);

        // Re-append the isolated diagram blocks to the overview field.
        string fullSynthesisField = textOf["synthesisResultsItem"] + run.Draft.MermaidBlock + run.Draft.TikzBlock;
        string cleanDiscussionValidated = textOf["discussionItem"];
        foreach (var s in sections) s.Text = textOf[s.Field];
        if (artifact is { Error: null })
        {
            foreach (var (field, _) in artifact.CitableTexts().ToList()) artifact.SetText(field, textOf[field]);
            artifact.Compact();
            artifact.Mermaid = ArtifactBuilder.ToMermaid(artifact);
        }

        try
        {
            string auditPath = Path.Join(GetWorkspaceFolderPath(sessionId), "citation-audit.json");
            var auditPayload = new
            {
                ReferenceListSize = referenceCount,
                InvalidMarkersStripped = citationStrips.Count,
                Details = citationStrips,
                InitialSupportSummary = initialSummary,
                SupportSummary = supportSummary,
                Repairs = repairs,
                StudiesCited = coverage.Cited.Count,
                FormattingChanges = run.FormattingChanges,
                NotCited = coverage.NotCited,
                SupportChecks = supportChecks
            };
            await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(auditPayload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not save citation-audit.json: {Message}", SanitizeLogMessage(ex.Message));
        }

        string checkSentence = supportSummary.ToSentence();
        if (repairs.Count > 0)
            checkSentence += $" Before this final count, {initialSummary.NotSupported} citation{(initialSummary.NotSupported == 1 ? " was" : "s were")} judged not supported and {initialSummary.PartiallySupported} partially supported; {finalState.Stats.CitationsRepaired} cited sentence{(finalState.Stats.CitationsRepaired == 1 ? " was" : "s were")} rewritten or had the citation removed once and checked again (listed under Repairs in citation-audit.json).";
        if (finalState.Stats.CitationsOutsideTheme > 0)
            checkSentence += " " + OutsideThemeSentence(finalState.Stats.CitationsOutsideTheme);

        var reportObj = new PrismaReport
        {
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            TitleItem = run.Title,
            AbstractItem = Plain(run.Abstract, "abstractItem"),
            RationaleItem = textOf["rationaleItem"],
            ObjectivesItem = run.Objectives,
            EligibilityItem = run.Methods.Eligibility,
            SourcesItem = run.Methods.Sources,
            SearchStrategyItem = run.Methods.Search,
            SelectionProcessItem = run.Methods.Selection,
            SynthesisResultsItem = fullSynthesisField,
            SynthesisSections = sections,
            SynthesisMethodsItem = ThematicSynthesis.MethodsText(useThematic ? thematic!.Codebook : thematic?.Codebook is { } fb ? new ThematicCodebook { FallbackReason = fb.FallbackReason } : null, Llm.DisplayName, Synthesis.RepairCitations, Llm.DisplayNameFor("citation-check"))
                + (artifact == null ? "" : artifact.Error == null
                    ? $" The {ArtifactKinds.All.First(k => k.Key == artifact.Requested).Label.ToLowerInvariant()} requested by the reviewer was built by the model from the finished results only, and the elements in it that cite a study were checked like the sentences of the report."
                    : $" The requested artifact could not be built: {artifact.Error}"),
            Artifact = artifact,
            CoverageSummary = coverage.Sentence,
            DiscussionItem = cleanDiscussionValidated,
            EvidenceLimitationsItem = closing == null ? "" : textOf["evidenceLimitationsItem"],
            ReviewLimitationsItem = MethodsSectionWriter.ReviewProcessLimitations(finalState, Llm.DisplayNameFor("screening"), Llm.DisplayName),
            ImplicationsItem = closing == null ? "" : textOf["implicationsItem"],
            FutureResearchItem = closing == null ? "" : textOf["futureResearchItem"],
            ConclusionItem = closing == null ? "" : textOf["conclusionItem"],
            ResearchAnswers = closing?.Answers == null ? new List<ResearchAnswer>() : finalState.ResearchQuestions
                .Select((question, i) => new ResearchAnswer { Number = i + 1, Question = question, Answer = textOf[$"researchAnswer{i + 1}"] })
                .ToList(),
            // Items 9-11 from the extraction and MMAT appraisal the run actually produced.
            BiasAssessmentItem = MethodsSectionWriter.DataAndAppraisal(finalState.Extractions ?? new List<StudyExtraction>(), Llm.DisplayName),
            ProtocolItem = MethodsSectionWriter.Protocol(finalState.ProtocolSha256, finalState.Timestamp, finalState.ResearchQuestions is { Count: > 0 }),
            SupportItem = _supportStatement,
            ProtocolHash = finalState.ProtocolHash,
            CitationCheckSummary = checkSentence,
            AvailabilityItem = $"The code of the tool is openly available at {ToolVersion.RepositoryUrl}. {ToolVersion.PaperSentence(ToolVersion.Current)} The run archive contains the protocol, the raw source responses, the screening ledger, the extraction and appraisal data, the thematic codebook, every model call's fingerprint and a manifest.json with the SHA-256 fingerprint of every file. Journal quartiles are taken from the SCImago Journal Rank (SJR) data (https://www.scimagojr.com)."
        };

        // 3. ARCHIVE THE AUDIT DELTA LEDGER DIRECTLY INTO THE ACTIVE WORKSPACE STORE SUBFOLDER
        string ledgerPath = Path.Join(GetWorkspaceFolderPath(sessionId), "stylistic-transformation-ledger.json");
        await File.WriteAllTextAsync(ledgerPath, JsonSerializer.Serialize(run.Deltas, new JsonSerializerOptions { WriteIndented = true }));

        // 4. WRITE THE MAIN CONSOLIDATED REPORT
        await File.WriteAllTextAsync(GetReportFilePath(sessionId), JsonSerializer.Serialize(reportObj, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The sentence of the citation check about citations in a theme subsection to a study not coded under that
    /// theme, singular or plural by the count (issue #119).
    /// </summary>
    public static string OutsideThemeSentence(int count) => count == 1
        ? "1 citation in a theme subsection refers to a study not coded under that theme and is flagged in the audit."
        : $"{count} citations in a theme subsection refer to a study not coded under that theme and are flagged in the audit.";

    /// <summary>The thematic synthesis, or a result that names why it failed; never throws, so it can run beside other steps.</summary>
    private async Task<ThematicResult> ThematicOrFallbackAsync(Guid sessionId, IChatCompletionService chat, string query, string explicitObjective,
        ReviewState finalState, IReadOnlyList<ReferencedPaper> papers, int referenceCount)
    {
        try
        {
            return await RunThematicSynthesisAsync(sessionId, chat, query, explicitObjective, finalState, papers, referenceCount);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Thematic synthesis failed, falling back to the single-pass synthesis: {Message}", SanitizeLogMessage(ex.Message));
            return new ThematicResult { Codebook = new ThematicCodebook { FallbackReason = $"the thematic synthesis failed ({SanitizeLogMessage(ex.Message)})" } };
        }
    }
}
