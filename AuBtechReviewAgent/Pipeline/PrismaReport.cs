using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

public class PrismaReport
{
    public string GeneratedAt { get; set; } = "";
    
    // SECTION 1: TITLE & ABSTRACT
    public string TitleItem { get; set; } = "Pending...";            // Item 1
    public string AbstractItem { get; set; } = "Pending...";         // Item 2
    
    // SECTION 2: INTRODUCTION
    public string RationaleItem { get; set; } = "Pending...";        // Item 3
    public string ObjectivesItem { get; set; } = "Pending...";       // Item 4
    
    // SECTION 3: METHODS
    public string EligibilityItem { get; set; } = "Pending...";      // Item 5
    public string SourcesItem { get; set; } = "Pending...";          // Item 6
    public string SearchStrategyItem { get; set; } = "Pending...";   // Item 7
    public string SelectionProcessItem { get; set; } = "Pending..."; // Item 8
    public string BiasAssessmentItem { get; set; } = "Pending...";   // Item 11
    
    // SECTION 4: RESULTS & DISCUSSION
    public string SynthesisResultsItem { get; set; } = string.Empty; // Item 20a
    public string DiscussionItem { get; set; } = "Pending...";       // Item 23a
    // The parts after the discussion (empty in runs made before they were written): the limitations of the evidence (23b,
    // written by the model and citation-checked), the limitations of the review processes (23c, written in code), the
    // implications for practice and policy and the directions for future research (23d), and the conclusion.
    public string EvidenceLimitationsItem { get; set; } = "";        // Item 23b
    public string ReviewLimitationsItem { get; set; } = "";          // Item 23c
    public string ImplicationsItem { get; set; } = "";               // Item 23d
    public string FutureResearchItem { get; set; } = "";             // Item 23d
    public string ConclusionItem { get; set; } = "";

    // The reviewer's research questions with the answer to each, in order (empty when the run had none); every answer
    // is checked against its sources like the conclusion.
    public List<ResearchAnswer> ResearchAnswers { get; set; } = new();
    
    // SECTION 5: OTHER INFORMATION
    public string SupportItem { get; set; } = "Pending...";          // Item 25
    public string AvailabilityItem { get; set; } = "Pending...";     // Item 27

    // Fingerprint of the run's protocol, printed in the LaTeX header.
    public string ProtocolHash { get; set; } = "";

    // One-paragraph summary of the automated citation support check.
    public string CitationCheckSummary { get; set; } = "";

    // PRISMA item 24: where the protocol is and when it was written.
    public string ProtocolItem { get; set; } = "";

    // PRISMA item 13d: how the findings were synthesised (written in code from the thematic codebook).
    public string SynthesisMethodsItem { get; set; } = "";

    // One subsection per theme of the thematic synthesis, shown after SynthesisResultsItem (the overview).
    // Empty for runs made before thematic synthesis, or when it fell back to a single synthesis text.
    public List<SynthesisSection> SynthesisSections { get; set; } = new();

    // How many of the included studies the synthesis and discussion cite, and which ones they do not (and why).
    public string CoverageSummary { get; set; } = "";

    // The artifact the reviewer asked for (diagram, table or list), built from the results; null when none was asked for.
    public ReviewArtifact? Artifact { get; set; }
}

/// <summary>
/// One theme subsection of the synthesis. <see cref="Field"/> is the name the citation check records for its
/// sentences ("synthesis-1", "synthesis-2", ...), so a clicked [n] finds its verdict.
/// </summary>
public class SynthesisSection
{
    public string Field { get; set; } = "";
    public string ThemeId { get; set; } = "";
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>One research question of a review and the answer the closing step wrote to it, after the citation check.</summary>
public class ResearchAnswer
{
    public int Number { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";

    /// <summary>Written in code when the answer cites a secondary study (<see cref="SecondaryStudy.AnswerNote"/>); null otherwise.</summary>
    public string? Note { get; set; }
}

// Applied=false means the rewrite was rejected by a guard (for example it changed the length far
// beyond a copy-edit) and the original text was kept; Note says why. PromptVersion is the language edit's prompt
// (StylisticRefinerUtility.PromptVersion); empty for a part not sent to it and in runs made before it was recorded.
public record StyleDeltaLog(string FieldName, string OriginalText, string RefinedText, bool Applied = true, string Note = "", string PromptVersion = "");

// One issue raised by the automated peer reviewer against a generated section.
public record PeerReviewComment(string Section, string Severity, string Issue, string Suggestion);

// The full, transparent record of the peer-review pass over the synthesis and discussion
// sections: what the reviewer flagged, and the text before and after the revision. Written
// to peer-review-feedback.json in the run's workspace so the improvement is auditable.
public class PeerReviewLog
{
    public string GeneratedAt { get; set; } = "";
    public string Verdict { get; set; } = "";
    public List<PeerReviewComment> Comments { get; set; } = new();
    public string SynthesisBefore { get; set; } = "";
    public string SynthesisAfter { get; set; } = "";
    public string DiscussionBefore { get; set; } = "";
    public string DiscussionAfter { get; set; } = "";

    // Thematic synthesis: the review is applied per subsection; each revision is listed with whether it was kept.
    public List<SectionRevision> SectionRevisions { get; set; } = new();
}

/// <summary>A peer-review or coverage revision of one section, and whether the guard in code accepted it.</summary>
public record SectionRevision(string Section, string Before, string After, bool Applied, string Note);
