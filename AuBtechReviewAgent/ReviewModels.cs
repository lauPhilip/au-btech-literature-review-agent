using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

public record AcademicPaper(
    string Id, 
    string Title, 
    string Abstract, 
    string PublishedDate, 
    List<string> Authors,
    string JournalSource
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
    string BriefSummary
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
}

public record StyleDeltaLog(string FieldName, string OriginalText, string RefinedText);

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
}