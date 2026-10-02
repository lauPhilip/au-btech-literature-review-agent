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
    string? Url = null,   // Landing-page URL used when no DOI is available
    string? PdfUrl = null // Direct link to a legal open-access PDF, when the source knows one
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

    // Where each included paper's full text came from (arXiv, open-access link, Unpaywall) or why there is none.
    public Dictionary<string, string> FullTextSources { get; set; } = new();

    // Data extraction and MMAT appraisal per included study (also written to extraction.json).
    public List<StudyExtraction> Extractions { get; set; } = new();

    // SHA-256 of protocol.md as written before the search.
    public string? ProtocolSha256 { get; set; }

    // Every record removed before screening (duplicate, outside the year range, over the per-source cap),
    // with the reason, so the funnel's numbers can be traced to actual papers.
    public List<RemovedRecord> RemovedBeforeScreening { get; set; } = new();

    // Options chosen for this run (see ReviewRequest).
    public bool DualScreeningRequested { get; set; }
    public bool CitationChainingRequested { get; set; }

    // True when the user asked to confirm the screening decisions before the write-up.
    public bool HumanScreeningReviewRequested { get; set; }
    public string? HumanScreeningReviewOutcome { get; set; }

    // Model, app version, temperatures and prompt fingerprints for this run (details in llm-calls.json).
    public RunSettingsRecord? RunSettings { get; set; }

    // Thematic synthesis: codes, themes, coverage and (optionally) the second coding (also thematic-codebook.json).
    public ThematicCodebook? ThematicSynthesis { get; set; }

    // Set when the run stopped early ("Failed" or "Interrupted" stage), so a returning user sees why.
    public string? FailureMessage { get; set; }
    public DateTime? CompletedUtc { get; set; }
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

    // Structured metadata for the BibTeX / RIS export.
    public string PaperId { get; set; } = string.Empty;
    public List<string> Authors { get; set; } = new();
    public string VenueName { get; set; } = string.Empty;
    public string? Doi { get; set; }
    public string? Url { get; set; }
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
    public int ScreeningErrors { get; set; }        // Records the model could not screen (call failed); not counted as screened
    public int HumanReviewed { get; set; }          // Screening decisions confirmed by a human reviewer
    public int HumanOverrides { get; set; }         // ...of which the reviewer changed the model's decision
    public int FullTextRetrieved { get; set; }      // Included papers whose full text could be read
    public int QueuePosition { get; set; }          // Place in the waiting line while all run slots are busy (0 = not waiting)
    public int CitationsChecked { get; set; }       // Automated citation support check (see citation-audit.json)
    public int CitationsSupported { get; set; }
    public int CitationsPartiallySupported { get; set; }
    public int CitationsNotSupported { get; set; }
    public int CitationsUnverifiable { get; set; }
    public int IdentifiedViaCitations { get; set; } // Subset of TotalIdentified found by citation chaining (PRISMA "other methods")
    public int DualScreened { get; set; }           // Records screened by both independent prompts
    public int ScreeningDisagreements { get; set; } // ...where the two prompts disagreed
    public double? ScreeningKappa { get; set; }     // Cohen's kappa between the two screenings
    public int UncertainDecisions { get; set; }     // Decisions flagged for human attention
    public int InjectionSuspected { get; set; }     // Records whose text contained instruction-like phrases
    public int CacheHits { get; set; }              // Screening decisions and search responses reused from the cache
    public int ThemesIdentified { get; set; }       // Thematic synthesis: descriptive themes in the codebook
    public int StudiesCoded { get; set; }           // ...included studies that received at least one code in a theme
    public int StudiesCitedInSynthesis { get; set; } // Included studies cited at least once in the synthesis or discussion
    public double? CodingKappa { get; set; }        // Cohen's kappa between the two theme assignments (dual coding only)
    public int CitationsRepaired { get; set; }      // Cited sentences rewritten by the repair pass after a failed support check
    public int CitationsOutsideTheme { get; set; }  // Citations in a theme subsection of a study not coded under that theme
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
    int Year = 0,
    List<string>? Authors = null,
    string? Doi = null,
    string? Url = null,
    string? Abstract = null,
    // Human screening review: the model's original decision is kept when a reviewer changes it.
    string? ModelDecision = null,
    bool HumanReviewed = false,
    string? HumanNote = null,
    // Dual screening: the second, independent screening prompt's decision and reasoning.
    string? SecondDecision = null,
    string? SecondReasoning = null,
    // high / medium / low, as reported by the screening model.
    string? Confidence = null,
    // True when the decision should get human attention first: the two screenings disagreed, confidence was
    // low, or the paper's text contained instruction-like phrases (possible prompt injection).
    bool Uncertain = false,
    List<string>? InjectionFlags = null,
    // True when the decision was reused from the screening cache (same paper, criteria, model and prompt).
    bool FromCache = false,
    // "database search" or "citation chaining".
    string Origin = "database search"
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
    public string? RawResponseFile { get; set; }          // SourceResponses/... file with the source's raw answer
    public string? RawResponseSha256 { get; set; }
    public bool FromCache { get; set; }                   // Response reused from the search cache (see CacheOptions)
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

    // Thematic synthesis: the review is applied per subsection; each revision is listed with whether it was kept.
    public List<SectionRevision> SectionRevisions { get; set; } = new();
}

/// <summary>A peer-review or coverage revision of one section, and whether the guard in code accepted it.</summary>
public record SectionRevision(string Section, string Before, string After, bool Applied, string Note);

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

/// <summary>Everything the user chose in the dashboard for one run.</summary>
public record ReviewRequest(
    string Query,
    string Objective,
    string Inclusion,
    string Exclusion,
    int MaxResultsPerSource,
    bool PeerReviewOnly = false,
    string SynthesisDirective = "",
    UserApiKeys? UserKeys = null,
    IReadOnlyCollection<string>? SelectedSources = null,
    int YearFrom = 0,
    int YearTo = 0,
    bool HumanScreeningReview = false,
    bool DualScreening = true,     // screen every record twice with two independent prompts
    bool CitationChaining = false  // add references and citing papers of included studies (OpenAlex)
);

/// <summary>A record that was found but removed before screening, and why (written to the run ledger).</summary>
public record RemovedRecord(
    string PaperId,
    string Title,
    string Source,
    string QueryUsed,
    int Year,
    string Reason,
    string? DuplicateOf = null,
    string? Doi = null)
{
    public const string Duplicate = "Duplicate";
    public const string OutsideYearRange = "Outside year range";
    public const string OverCap = "Over per-source cap";
}
