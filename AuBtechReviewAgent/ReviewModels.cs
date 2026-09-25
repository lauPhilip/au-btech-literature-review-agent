using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

public record AcademicPaper(
    string Id, 
    string Title, 
    string Abstract, 
    string PublishedDate, 
    List<string> Authors,
    string JournalSource,
    string? Doi = null,   // Bare DOI (e.g. "10.1016/j.epsr.2025.109876") when the source API returns one
    string? Url = null    // Landing-page URL used when no DOI is available
);

public class ReviewState
{
    public string ReviewId { get; set; } = Guid.NewGuid().ToString();
    public string SearchQuery { get; set; } = "";
    public bool PeerReviewOnlyToggle { get; set; } 
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ReviewStats Stats { get; set; } = new();
    public string SynthesisTargetDirective { get; set; } = string.Empty;
    public ReviewPhases Phases { get; set; } = new();
    public List<PlatformSearchLog> SearchLogs { get; set; } = new();
    public List<IncludedPaperMetricRow> SynthesizedRecords { get; set; } = new();

    // STORM-style multi-perspective search: the primary query plus LLM-generated variant phrasings
    // actually issued against every source, kept here so the run is auditable end-to-end.
    public List<string> SearchPerspectives { get; set; } = new();

    // Which gateways the user ticked for this run, and which ticked gateways could not be queried
    // because no API key was available. Feeds the deterministic PRISMA Item 6 text.
    public List<string> SelectedSources { get; set; } = new();
    public List<string> UnavailableSources { get; set; } = new();

    // The hard per-source result cap used for this run (PRISMA Item 7 detail).
    public int MaxResultsPerSource { get; set; }

    // Publication-year window applied after retrieval (0 = no limit).
    public int YearFrom { get; set; }
    public int YearTo { get; set; }

    // Short SHA-256 fingerprint of the run's protocol (query, criteria, perspectives, sources, cap).
    public string ProtocolHash { get; set; } = string.Empty;
}
    
public class IncludedPaperMetricRow
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ApaCitation { get; set; } = string.Empty;
    public string SourcePlatform { get; set; } = string.Empty; // "arXiv API" or "ScienceDirect API"
    public int Year { get; set; } = 2026;
    public string VenueType { get; set; } = "Other"; // "Journals", "Conferences", "Preprints", etc.
    public string InclusionRationale { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Quartile { get; set; } = "N/A";          // e.g., "Q1", "Q2", "Q3", "Q4"
    public string ConferenceRating { get; set; } = "N/A";  // e.g., "A*", "A", "B"

    // 1-based position in the run's reference list. The [n] markers in the synthesis/discussion, the
    // extraction table and the LaTeX bibliography all use this same number.
    public int ReferenceNumber { get; set; }
}
public class ReviewStats 
{ 
    public int TotalIdentified { get; set; } = 0; 
    public int Screened { get; set; } = 0; 
    public int Excluded { get; set; } = 0; 
    public int Included { get; set; } = 0; 
    public string ProcessingStage { get; set; } = "Idle";
    public int PassedPeerReviewCheck { get; set; }  // Exactly X papers
    public int FailedPeerReviewCheck { get; set; }  // Exactly Y papers
    public int DuplicatesRemoved { get; set; }      // Cross-perspective duplicate hits collapsed before screening
    public int CappedBeyondMaxResults { get; set; } // Candidates discarded by the hard per-source maxResults cap
    public int InvalidCitationsStripped { get; set; } // Out-of-range [n] markers removed from generated prose for traceability
    public int OutsideDateRange { get; set; }       // Records dropped because their publication year fell outside the chosen range
}

public class ReviewPhases 
{ 
    public List<ScreeningLog> Screening { get; set; } = new(); 
}

public record ScreeningLog(
    string PaperId, 
    string Title, 
    string Decision, 
    string Reasoning, 
    string ApaCitation, 
    string BriefSummary,
    // Metadata taken straight from the source API (not from the language model), so the citation,
    // venue type, year and quartile lookup can all be traced back to what the database returned.
    string VenueType = "",
    string VenueName = "",
    int Year = 0
);

// ─── PLATFORM SEARCH METRIC DATA CONTAINER ──────────────────────────
public class PlatformSearchLog
{
    public string SourceName { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int PapersFound { get; set; }
    public string ErrorMessage { get; set; } = "None";
    public string QueryUsed { get; set; } = string.Empty; // Which search-perspective phrasing produced this pass
}

// Applied=false means the rewrite was rejected by a guard (for example it changed the length far
// beyond a copy-edit) and the original text was kept; Note says why.
public record StyleDeltaLog(string FieldName, string OriginalText, string RefinedText, bool Applied = true, string Note = "");

// ─── LLM PEER-REVIEW AUDIT TRAIL ────────────────────────────────────
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
}

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
    
    // SECTION 5: OTHER INFORMATION
    public string SupportItem { get; set; } = "Pending...";          // Item 25
    public string AvailabilityItem { get; set; } = "Pending...";     // Item 27

    // Fingerprint of the run's protocol, printed in the LaTeX header.
    public string ProtocolHash { get; set; } = "";
}