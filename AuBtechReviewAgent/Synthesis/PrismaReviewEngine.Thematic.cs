using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// Thematic synthesis stage: codes the included studies from their verified findings, groups the codes into
/// themes, then hands the evidence to the writing stages every kind of review shares (<see cref="ReviewWriter"/>):
/// one cited subsection per theme from that theme's studies only, a fill pass so every study of a theme is cited,
/// the discussion from the subsections and the automated peer review. Cited sentences the support check rejects are
/// repaired later. Every step is recorded (thematic-codebook.json, peer-review-feedback.json,
/// citation-audit.json) and every model answer is checked in code before it is used.
/// </summary>
public partial class PrismaReviewEngine
{
    /// <summary>Thematic synthesis, dual coding and citation repair settings ("Synthesis" section).</summary>
    public SynthesisOptions Synthesis { get; init; } = new();

    private sealed class ThematicResult
    {
        public ThematicCodebook Codebook { get; init; } = new();
        public List<SynthesisSection> Sections { get; } = new();
        public string Discussion { get; set; } = "";
        public PeerReviewLog? PeerReview { get; set; }
    }

    /// <summary>
    /// Runs coding, codebook, optional second coding, theme subsections with the coverage fill pass, the
    /// discussion and the per-section peer review. Returns a result without sections when the synthesis has
    /// to fall back to the single-pass writer (the reason is in <see cref="ThematicCodebook.FallbackReason"/>).
    /// </summary>
    private async Task<ThematicResult> RunThematicSynthesisAsync(Guid runId,
        IChatCompletionService chat, string query, string objective, ReviewState state,
        IReadOnlyList<ReferencedPaper> papers, int referenceCount)
    {
        var book = new ThematicCodebook();
        var result = new ThematicResult { Codebook = book };
        var extractions = state.Extractions ?? new List<StudyExtraction>();
        string goal = string.IsNullOrWhiteSpace(objective) ? query : objective;

        // 1. Coding
        List<ThematicCode> codes;
        using (LlmStage.Begin("thematic-coding"))
        {
            ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 1, 0), $"Coding {papers.Count} studies");
            var (c, uncoded) = await ThematicSynthesis.CodeStudiesAsync(chat, goal, papers, extractions,
                Synthesis.CodingBatchSize, Llm.ScreeningParallelism,
                (done, total) => ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 1, (double)done / total), $"Coding studies: {Of(done, total, "batches")}"));
            codes = c;
            book.Codes = codes;
            book.Uncoded.AddRange(uncoded);
        }
        if (codes.Count == 0)
        {
            book.FallbackReason = "no study could be coded";
            return result;
        }

        // 2. Codebook
        try
        {
            ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 2, 0), $"Grouping {codes.Count} codes into themes");
            using (LlmStage.Begin("thematic-codebook"))
            {
                var (themes, unplaced) = await ThematicSynthesis.BuildCodebookAsync(chat, goal, codes, Math.Max(2, Synthesis.MaxThemes));
                book.Themes = themes;
                book.Uncoded.AddRange(unplaced);
                book.Uncoded = book.Uncoded.OrderBy(u => u.Reference).ToList();
            }
        }
        catch (Exception ex)
        {
            book.FallbackReason = $"the codebook could not be built ({SanitizeLogMessage(ex.Message)})";
            _log.LogWarning("Thematic codebook failed, falling back to the single-pass synthesis: {Message}", SanitizeLogMessage(ex.Message));
            return result;
        }
        if (book.Themes.Count == 0)
        {
            book.FallbackReason = "the codebook had no themes";
            return result;
        }

        // 3. Second, independent coding (reported, does not change the themes)
        if (Synthesis.DualCoding)
        {
            using (LlmStage.Begin("thematic-coding-second"))
                book.SecondCoding = await ThematicSynthesis.SecondCodingAsync(chat, book.Themes, codes);
        }

        // 4. One subsection per theme, each with its own coverage fill pass (shared by every kind of review:
        //    Synthesis/ReviewWriter.cs). Written in parallel, assembled in theme order.
        var evidence = SystematicEvidence.Build(book, state.SynthesizedRecords, papers, extractions);
        var profile = WritingProfile.Systematic;
        void Warn(string message) => _log.LogWarning("{Message}", SanitizeLogMessage(message));
        ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 3, 0), $"Writing {book.Themes.Count} theme subsections");
        var (sections, coverage) = await ReviewWriter.WriteThemesAsync(chat, profile, goal, evidence, referenceCount, Llm.ScreeningParallelism,
            (n, total) => ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 3, (double)n / total), $"Theme subsections: {Of(n, total, "written")}"),
            Warn);
        book.Coverage.AddRange(coverage);
        result.Sections.AddRange(sections);
        if (result.Sections.Count == 0)
        {
            book.FallbackReason = "no theme subsection could be written";
            return result;
        }

        // 5. Discussion from the subsections
        ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 4, 0), "Writing the discussion");
        using (LlmStage.Begin("discussion"))
            result.Discussion = await ReviewWriter.WriteDiscussionAsync(chat, profile, query, goal, result.Sections, evidence, referenceCount, Warn);

        // 6. Peer review, per section, with the same coverage guard as the fill pass
        ReportProgress(runId, state, RunProgress.Synthesis, RunProgress.Phase(RunProgress.SynthesisPhases, 5, 0), "Automated peer review");
        using (LlmStage.Begin("automated-peer-review"))
            (result.PeerReview, result.Discussion) = await ReviewWriter.PeerReviewAsync(chat, profile, result.Sections, result.Discussion, evidence, referenceCount, Llm.ScreeningParallelism, Warn);

        return result;
    }

    // ─── Citation repair (in Verification/CitationRepairer.cs, shared by every kind of review) ───────────────

    /// <summary>Removes one reference number from the citation markers of a sentence (see <see cref="CitationRepairer.RemoveReference"/>).</summary>
    public static string RemoveReference(string sentence, int reference) => CitationRepairer.RemoveReference(sentence, reference);

    /// <summary>Should the repair pass look at this verdict? (see <see cref="CitationRepairer.NeedsRepair"/>).</summary>
    public static bool NeedsRepair(CitationSupportResult r) => CitationRepairer.NeedsRepair(r);
}
