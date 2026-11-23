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
    string? PdfUrl = null, // Direct link to a legal open-access PDF, when the source knows one
    int? CitedByCount = null // Times cited, as the source counted it on the day of the search; null when the source gives none
);

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
    string Origin = "database search",
    // For excluded records: the group of reasons (see ExclusionReasons), for the funnel and PRISMA item 16a.
    string? ExclusionReason = null,
    // Times cited, from the source's metadata on the day of the search; null when the source gave none.
    int? CitedByCount = null
);

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
    bool CitationChaining = false, // add references and citing papers of included studies (OpenAlex)
    string ArtifactKind = ArtifactKinds.ConceptMap, // what to build from the findings (see ArtifactKinds)
    // The extra search strings the reviewer saw and approved before the run (the primary query is not repeated
    // here). null: the model proposes them during the run; an empty list: only the primary query is searched.
    IReadOnlyList<string>? SearchStrings = null,
    // The kind of review (ReviewMethods); only available methods can be run.
    string Method = "systematic",
    // Optional research questions (at most ReviewInputGuard.MaxResearchQuestions), answered one by one in the conclusion.
    IReadOnlyList<string>? ResearchQuestions = null
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
