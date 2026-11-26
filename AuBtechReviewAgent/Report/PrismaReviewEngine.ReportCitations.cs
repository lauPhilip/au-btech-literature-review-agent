using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>The citation check of a systematic report, its coverage of the included studies, and the files the report is written to.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// The sentence of the citation check about citations in a theme subsection to a study not coded under that
    /// theme, singular or plural by the count (issue #119).
    /// </summary>
    public static string OutsideThemeSentence(int count) => count == 1
        ? "1 citation in a theme subsection refers to a study not coded under that theme and is flagged in the audit."
        : $"{count} citations in a theme subsection refer to a study not coded under that theme and are flagged in the audit.";

    private sealed record CitationCheck(List<(string Field, string Text)> Fields, List<StrippedCitation> Strips, List<CitationSupportResult> Checks,
        CitationSupportSummary Initial, CitationSupportSummary Summary, List<CitationRepair> Repairs, List<RemovedCitation> Removed,
        List<RepairSenseCheck> Sense);

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
        // Repair: sentences whose citation was rejected are rewritten from the cited paper's evidence (or lose that
        // citation) and checked again; a second pass removes what is still not supported (issue #135). Before and after go into citation-audit.json. The check
        // and the repair are shared by every kind of review (Verification/Grounding.cs).
        var grounding = await Grounding.CheckAndRepairAsync(chat, fields, papersForCheck, verifiedFindings, referenceCount,
            new GroundingOptions(Llm.ParallelismFor("citation-check"), Synthesis.SecondCitationCheck, Synthesis.RepairCitations),
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
        finalState.Stats.CitationsRepaired = repairs.Count(r => r.Pass == 1 && r.Action is ("rewrite" or "drop_citation" or "delete"));

        // Theme consistency: a subsection citing a study that was not coded under its theme is flagged (not removed).
        var themeOfField = sections.ToDictionary(s => s.Field, s => thematic?.Codebook.Themes.FirstOrDefault(t => t.Id == s.ThemeId));
        foreach (var check in supportChecks)
        {
            if (themeOfField.TryGetValue(check.Field, out var theme) && theme != null && !theme.Studies.Contains(check.Reference))
                check.ThemeNote = $"Study [{check.Reference}] was not coded under the theme \"{theme.Name}\".";
        }
        finalState.Stats.CitationsOutsideTheme = supportChecks.Count(c => c.ThemeNote != null);

        var supportSummary = CitationSupportSummary.From(supportChecks, grounding.Removed.Count);
        finalState.Stats.CitationsRemoved = supportSummary.Removed;
        finalState.Stats.CitationsChecked = supportSummary.Checked;
        finalState.Stats.CitationsSupported = supportSummary.Supported;
        finalState.Stats.CitationsPartiallySupported = supportSummary.PartiallySupported;
        finalState.Stats.CitationsNotSupported = supportSummary.NotSupported;
        finalState.Stats.CitationsUnverifiable = supportSummary.Unverifiable;
        finalState.Stats.CitationsInsufficientEvidence = supportSummary.InsufficientEvidence;
        finalState.Stats.CitationsSecondChecked = supportSummary.SecondChecked;
        finalState.Stats.CitationsUpgradedBySecondCheck = supportSummary.UpgradedBySecondCheck;
        return new CitationCheck(fields, citationStrips, supportChecks, initialSummary, supportSummary, repairs, grounding.Removed, grounding.Sense ?? new());
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
        // The coverage sentence says what "the synthesis and discussion" cite, so it counts only the results and the
        // discussion; a study cited only in the rationale or the closing sections is not synthesised (issue #447).
        var resultFields = run.Sections.Select(x => x.Field).Append("synthesisResultsItem").Append("discussionItem").ToHashSet(StringComparer.Ordinal);
        var citedStudies = new SortedSet<int>(fields.Where(f => resultFields.Contains(f.Field)).SelectMany(f => ThematicSynthesis.CitedIn(f.Text)).Where(r => r >= 1 && r <= referenceCount));
        var notCited = new List<UncodedStudy>();
        for (int refNo = 1; refNo <= referenceCount; refNo++)
        {
            if (citedStudies.Contains(refNo)) continue;
            var uncodedReason = thematic?.Codebook.Uncoded.FirstOrDefault(u => u.Reference == refNo)?.Reason;
            var theme = useThematic ? thematic!.Codebook.Themes.FirstOrDefault(t => t.Studies.Contains(refNo)) : null;
            notCited.Add(new UncodedStudy(refNo,
                uncodedReason != null ? $"not coded: {uncodedReason.TrimEnd('.')}" :
                theme != null && theme.Studies.Count > ReviewWriter.CiteEveryUpTo ? $"coded under \"{theme.Name}\", a theme of {theme.Studies.Count} studies synthesised from those that report its claims most directly; not cited after peer review and citation repair" :
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
        string fullSynthesisField = textOf["synthesisResultsItem"] + run.DraftIfWritten?.MermaidBlock + run.DraftIfWritten?.TikzBlock;
        string cleanDiscussionValidated = textOf["discussionItem"];
        foreach (var s in sections) s.Text = textOf[s.Field];
        await FinishArtifactAsync(run, textOf);

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
                RemovedCitations = check.Removed,
                SenseChecks = check.Sense,
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

        var secondary = SecondaryStudy.In(finalState.Extractions);
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
                    ? $" The {ArtifactKinds.All.First(k => k.Key == artifact.Requested).Label.ToLowerInvariant()} requested by the reviewer was built by the model from the finished results only, and the elements in it that cite a study were checked like the sentences of the report." + ArtifactLinks.MethodsSentence(artifact)
                    : $" The requested artifact could not be built: {artifact.Error}"),
            Artifact = artifact,
            CoverageSummary = coverage.Sentence,
            DiscussionItem = cleanDiscussionValidated,
            EvidenceLimitationsItem = closing == null ? "" : textOf["evidenceLimitationsItem"],
            ReviewLimitationsItem = (MethodsSectionWriter.ReviewProcessLimitations(finalState, Llm.DisplayNameFor("screening"), Llm.DisplayName) + " " +
                SearchAdequacy.LimitationSentence(finalState)).Trim(),
            ImplicationsItem = closing == null ? "" : textOf["implicationsItem"],
            FutureResearchItem = closing == null ? "" : textOf["futureResearchItem"],
            ConclusionItem = closing == null ? "" : textOf["conclusionItem"],
            ResearchAnswers = closing?.Answers == null ? new List<ResearchAnswer>() : finalState.ResearchQuestions
                .Select((question, i) => new ResearchAnswer { Number = i + 1, Question = question, Answer = textOf[$"researchAnswer{i + 1}"],
                    Note = SecondaryStudy.AnswerNote(textOf[$"researchAnswer{i + 1}"], secondary) })
                .ToList(),
            // Items 9-11 from the extraction and MMAT appraisal the run actually produced.
            // The references were completed before they were numbered; that step is part of the data collection (rules 2 and 3).
            BiasAssessmentItem = (MethodsSectionWriter.DataAndAppraisal(finalState.Extractions ?? new List<StudyExtraction>(), Llm.DisplayName) + " " +
                ReferenceEnrichmentText.Sentence(ReferenceEnrichmentText.Load(GetWorkspaceFolderPath(sessionId)), "included study", "included studies") + " " +
                SecondaryStudy.MethodsSentence(secondary)).Trim(),
            ProtocolItem = MethodsSectionWriter.Protocol(finalState.ProtocolSha256, finalState.Timestamp, finalState.ResearchQuestions is { Count: > 0 }),
            SupportItem = _supportStatement,
            ProtocolHash = finalState.ProtocolHash,
            CitationCheckSummary = checkSentence,
            AvailabilityItem = $"The code of the tool is openly available at {ToolVersion.RepositoryUrl}. {ToolVersion.PaperSentence(ToolVersion.Current)} The run archive contains the protocol, the raw source responses, the screening ledger, the extraction and appraisal data, the thematic codebook, every model call's fingerprint and a manifest.json with the SHA-256 fingerprint of every file. The run archive to share, made for a public repository such as Zenodo or OSF, leaves out the downloaded papers without an open licence, the raw answers of licensed databases and the model call log, holds no abstracts from licensed databases and shortens long quotes; its deposit.json lists every file left out or changed with its SHA-256 in the full archive, which the review's owner keeps. Journal quartiles are taken from the SCImago Journal Rank (SJR) data (https://www.scimagojr.com)."
        };

        string ledgerPath = Path.Join(GetWorkspaceFolderPath(sessionId), "stylistic-transformation-ledger.json");
        await File.WriteAllTextAsync(ledgerPath, JsonSerializer.Serialize(run.Deltas, new JsonSerializerOptions { WriteIndented = true }));

        await File.WriteAllTextAsync(GetReportFilePath(sessionId), JsonSerializer.Serialize(reportObj, new JsonSerializerOptions { WriteIndented = true }));
    }
}
