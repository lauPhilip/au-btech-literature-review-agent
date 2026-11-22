using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpecMatrix = AuBtechReviewAgent.Components.Pages.SpecMatrix;

namespace AuBtechReviewAgent;

// The finished report as data: the same files the Review output page reads, put in the page's order. Nothing is
// written or asked of the model here; text is cleaned of its markers exactly as the page cleans it (ScrubAnchors).
public sealed partial class ReviewApi
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public ApiResult<ReviewReport> Report(Guid runId)
    {
        var header = _engine.LoadHeader(runId);
        if (header == null) return ApiProblems.NoRun();
        if (header.Module != ReviewMethod.SystematicKey) return ApiProblems.NotSystematic();
        var state = _engine.LoadState(runId);
        string stage = state?.Stats.ProcessingStage ?? PrismaReviewEngine.StageQueued;
        if (state == null || stage != PrismaReviewEngine.StageComplete) return ApiProblems.NotReady(stage);

        string folder = _engine.Store.FolderOf(runId);
        var report = Read<PrismaReport>(Path.Join(folder, "prisma-report.json"));
        if (report == null) return ApiProblems.NotReady(stage);

        var references = ReferencesOf(state);
        var links = references.GroupBy(r => r.Number).ToDictionary(g => g.Key, g => g.First().Link);
        var citations = ChecksOf(folder)
            .Select(c => new ReportCitation(c.Reference, c.Field, c.SentenceIndex, c.Sentence, c.Verdict,
                c.QuoteVerified && !string.IsNullOrWhiteSpace(c.Quote) ? c.Quote : null, c.QuoteVerified,
                c.EvidenceBasis, c.Reason, links.GetValueOrDefault(c.Reference)))
            .ToList();

        var sections = ReportOutline.Systematic(report)
            .Select(entry => new ReportSection(entry.Id, entry.Label, entry.Level, SectionText(entry.Id, report, state, references)))
            .ToList();

        return ApiResult<ReviewReport>.Ok(new ReviewReport(runId, SpecMatrix.ScrubAnchors(report.TitleItem),
            SpecMatrix.ScrubAnchors(report.AbstractItem), sections, citations, references, PrismaOf(state),
            state.RunSettings?.AppVersion ?? "", string.IsNullOrEmpty(report.ProtocolHash) ? state.ProtocolHash : report.ProtocolHash,
            state.ProtocolSha256, state.CompletedUtc));
    }

    /// <summary>The text of one section, from the fields the page shows in it, in the page's order.</summary>
    private static string SectionText(string id, PrismaReport r, ReviewState state, IReadOnlyList<ReportReference> references)
    {
        for (int k = 0; k < r.SynthesisSections.Count; k++)
            if (id == ReportOutline.ThemeId(k)) return Join(r.SynthesisSections[k].Text);
        var questions = state.ResearchQuestions ?? new List<string>();
        return id switch
        {
            ReportOutline.AbstractId => Join(r.AbstractItem),
            ReportOutline.IntroductionId => Join(new[] { r.RationaleItem, r.ObjectivesItem }
                .Concat(questions.Select((q, i) => $"RQ{i + 1}. {q}")).ToArray()),
            ReportOutline.MethodologyId => Join(r.EligibilityItem, r.SourcesItem, r.SearchStrategyItem, r.SelectionProcessItem,
                r.BiasAssessmentItem, r.SynthesisMethodsItem),
            ReportOutline.ResultsId => Join(r.SynthesisResultsItem, r.CoverageSummary),
            ReportOutline.DiscussionId => Join(r.DiscussionItem, r.EvidenceLimitationsItem, r.ReviewLimitationsItem,
                r.ImplicationsItem, r.FutureResearchItem),
            ReportOutline.ConclusionId => Join(new[] { r.ConclusionItem }
                .Concat(r.ResearchAnswers.Select(a => $"RQ{a.Number}. {a.Question}\n{a.Answer}")).ToArray()),
            ReportOutline.DeclarationsId => Join(r.SupportItem, r.AvailabilityItem, r.ProtocolItem, r.CitationCheckSummary),
            ReportOutline.ReferencesId => string.Join("\n", references.Select(x => $"[{x.Number}] {x.Citation}")),
            // The included studies and the appendix are tables on the page: their data is in references and prisma.
            _ => "",
        };
    }

    private static string Join(params string?[] parts) => string.Join("\n\n",
        parts.Select(p => SpecMatrix.ScrubAnchors(p ?? "")).Where(p => p.Length > 0));

    /// <summary>The included studies in reference order, as the page numbers them, with a link only to a web address.</summary>
    private static List<ReportReference> ReferencesOf(ReviewState state) => state.SynthesizedRecords
        .OrderBy(r => r.ReferenceNumber)
        .Select((r, i) =>
        {
            string? doi = PrismaReviewEngine.NormalizeDoi(r.Doi);
            string? link = doi != null ? "https://doi.org/" + doi : PrismaReviewEngine.IsWebAddress(r.Url) ? r.Url : null;
            return new ReportReference(r.ReferenceNumber > 0 ? r.ReferenceNumber : i + 1, r.ApaCitation.Trim().TrimEnd('.'), doi, link);
        })
        .ToList();

    private static List<CitationSupportResult> ChecksOf(string folder)
    {
        string path = Path.Join(folder, "citation-audit.json");
        if (!File.Exists(path)) return new();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("SupportChecks", out var checks)
                ? checks.Deserialize<List<CitationSupportResult>>(ReadOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private static ReportPrismaCounts PrismaOf(ReviewState state)
    {
        var c = PrismaFlowCounts.From(state);
        return new ReportPrismaCounts(c.Identified, c.IdentifiedBySource.Select(s => new SourceCount(s.Source, s.Count)).ToList(),
            c.IdentifiedViaCitations, c.DuplicatesRemoved, c.OutsideDateRange, c.CappedBeyondMax, c.Screened, c.ScreeningErrors,
            c.ExcludedPeerReview, c.ExcludedAtScreening, c.ExcludedByReason.Select(r => new ReasonCount(r.Reason, r.Count)).ToList(),
            c.Included, c.FullTextRetrieved, c.AbstractOnly);
    }
}
