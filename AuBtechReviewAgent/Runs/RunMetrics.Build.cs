using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

public partial class RunMetrics
{
    /// <summary>Builds the metrics of a completed run from its ledger, report, citation audit and call log.</summary>
    public static RunMetrics Build(Guid runId, ReviewState state, PrismaReport? report,
        IReadOnlyList<CitationSupportResult> checks, CitationSupportSummary? initial, int strippedMarkers,
        IReadOnlyList<LlmCallRecord> calls, IReadOnlyDictionary<string, string> settings)
    {
        var m = new RunMetrics
        {
            RunId = runId.ToString("N"),
            CompletedUtc = (state.CompletedUtc ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            AppVersion = state.RunSettings?.AppVersion ?? RecordingChatCompletionService.AppVersion,
            Model = state.RunSettings?.Model ?? "",
            QueryHash = Sha(state.SearchQuery ?? ""),
            Settings = settings.ToDictionary(kv => kv.Key, kv => kv.Value),
            ReviewType = "systematic",
        };
        m.ConfigFingerprint = Sha(string.Join("|", new[] { m.Model, m.AppVersion }.Concat(settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))));

        var s = state.Stats;
        m.Identified = s.TotalIdentified;
        m.DuplicatesRemoved = s.DuplicatesRemoved;
        m.Screened = s.Screened;
        m.Included = s.Included;
        m.ScreeningKappa = s.ScreeningKappa;
        m.ScreeningKappaBasis = s.ScreeningKappa == null ? null : "all records";
        m.UncertainDecisions = s.UncertainDecisions;
        m.FullTextRetrieved = s.FullTextRetrieved;
        m.FullTextShare = s.Included == 0 ? 0 : Math.Round((double)s.FullTextRetrieved / s.Included, 4);

        var extractions = state.Extractions ?? new List<StudyExtraction>();
        var findings = extractions.Where(e => e.Error == null).SelectMany(e => e.KeyFindings)
            .Where(f => !f.Value.Equals("not reported", StringComparison.OrdinalIgnoreCase)).ToList();
        m.KeyFindings = findings.Count;
        m.VerifiedFindingShare = findings.Count == 0 ? 0 : Math.Round((double)findings.Count(f => f.QuoteVerified) / findings.Count, 4);
        m.ExtractionErrors = extractions.Count(e => e.Error != null);

        var book = state.ThematicSynthesis;
        m.ThematicSynthesisUsed = (report?.SynthesisSections?.Count ?? 0) > 0;
        m.FallbackReason = book?.FallbackReason;
        if (book != null)
        {
            m.Themes = m.ThematicSynthesisUsed ? book.Themes.Count : 0;
            m.Codes = book.Codes.Count;
            m.AnchoredCodeShare = book.Codes.Count == 0 ? 0 : Math.Round((double)book.Codes.Count(c => c.Anchor != "unanchored") / book.Codes.Count, 4);
            m.StudiesCoded = book.CodedStudies.Count();
            m.StudiesUncoded = book.Uncoded.Count;
            m.CodingKappa = book.SecondCoding?.Kappa;
        }

        int referenceCount = state.SynthesizedRecords.Count;
        m.StudiesCited = s.StudiesCitedInSynthesis;
        m.CoverageShare = referenceCount == 0 ? 0 : Math.Round((double)m.StudiesCited / referenceCount, 4);

        m.CitationsFinal = VerdictCounts.From(checks.Select(c => c.Verdict));
        if (initial != null)
        {
            m.CitationsInitial = new VerdictCounts
            {
                Total = initial.Checked, Supported = initial.Supported, PartiallySupported = initial.PartiallySupported,
                NotSupported = initial.NotSupported, InsufficientEvidence = initial.InsufficientEvidence, Unverifiable = initial.Unverifiable,
            };
        }
        m.CitationsBySection = checks.GroupBy(c => c.Field.StartsWith("synthesis", StringComparison.Ordinal) ? "synthesis" : c.Field == "discussionItem" ? "discussion" : c.Field)
            .ToDictionary(g => g.Key, g => VerdictCounts.From(g.Select(c => c.Verdict)));
        m.CitationsByEvidence = checks.GroupBy(c => string.IsNullOrEmpty(c.EvidenceBasis) ? "none" : c.EvidenceBasis)
            .ToDictionary(g => g.Key, g => VerdictCounts.From(g.Select(c => c.Verdict)));
        m.CitationsByPapersInSentence = checks.GroupBy(c => Math.Min(4, CitationSupportChecker.ReferencesIn(c.Sentence).Count) switch { >= 4 => "4+", var n => n.ToString() })
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => VerdictCounts.From(g.Select(c => c.Verdict)));
        m.SecondChecked = checks.Count(c => c.SecondVerdict != null);
        m.SecondCheckAgreed = checks.Count(c => c.SecondVerdict != null && c.SecondVerdict == c.FirstVerdict);
        m.UpgradedBySecondCheck = checks.Count(c => c.ResolvedBy == "second check");
        m.Repaired = s.CitationsRepaired;
        m.Removed = s.CitationsRemoved;
        m.OutsideTheme = s.CitationsOutsideTheme;
        m.InvalidMarkersStripped = strippedMarkers;

        if (report != null)
        {
            string overview = Regex.Replace(report.SynthesisResultsItem ?? "", @"\[(MERMAID|TIKZ)_START\].*?\[(MERMAID|TIKZ)_END\]", "", RegexOptions.Singleline);
            var synthesisTexts = new[] { overview }.Concat((report.SynthesisSections ?? new()).Select(x => x.Text)).ToList();
            m.SynthesisWords = synthesisTexts.Sum(Words);
            m.DiscussionWords = Words(report.DiscussionItem);
            var sentences = synthesisTexts.Append(report.DiscussionItem ?? "").SelectMany(CitationSupportChecker.SplitSentences).ToList();
            m.Sentences = sentences.Count;
            var refsPerSentence = sentences.Select(x => CitationSupportChecker.ReferencesIn(x).Count).ToList();
            int cited = refsPerSentence.Count(n => n > 0);
            m.CitedSentenceShare = sentences.Count == 0 ? 0 : Math.Round((double)cited / sentences.Count, 4);
            m.MultiPaperSentenceShare = cited == 0 ? 0 : Math.Round((double)refsPerSentence.Count(n => n > 1) / cited, 4);
            int words = m.SynthesisWords + m.DiscussionWords;
            m.CitationsPer100Words = words == 0 ? 0 : Math.Round(refsPerSentence.Sum() * 100.0 / words, 2);
        }

        m.AddCost(calls);
        if (state.CompletedUtc is DateTime done)
            m.DurationMinutes = Math.Round((done - state.Timestamp).TotalMinutes, 1);
        return m;
    }
}
