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

    /// <summary>
    /// "theme" (written by the model from the theme's evidence, then checked), "code" (counted in code) or "unwritten"
    /// (a theme whose section the model could not write; said in code, with what the theme rests on).
    /// </summary>
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
    public List<RemovedCitation> RemovedCitations { get; set; } = new();
    public List<RepairSenseCheck> SenseChecks { get; set; } = new();
    public int SourcesCited { get; set; }
    public List<FormattingChange> FormattingChanges { get; set; } = new();
    public List<UncitedSource> NotCited { get; set; } = new();
    public List<CitationSupportResult> SupportChecks { get; set; } = new();
}
