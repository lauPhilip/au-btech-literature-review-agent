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
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ReviewStats Stats { get; set; } = new();
    public ReviewPhases Phases { get; set; } = new();
}

public class ReviewStats 
{ 
    public int TotalIdentified { get; set; } = 0; 
    public int Screened { get; set; } = 0; 
    public int Excluded { get; set; } = 0; 
    public int Included { get; set; } = 0; 
    public string ProcessingStage { get; set; } = "Screening";
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

public class PrismaReport
{
    public string GeneratedAt { get; set; } = "";
    
    // SECTION 1: TITLE & ABSTRACT
    public string TitleItem { get; set; } = "Pending...";            // Item 1 [cite: 7]
    public string AbstractItem { get; set; } = "Pending...";         // Item 2 [cite: 7]
    
    // SECTION 2: INTRODUCTION
    public string RationaleItem { get; set; } = "Pending...";        // Item 3 [cite: 7]
    public string ObjectivesItem { get; set; } = "Pending...";       // Item 4 [cite: 7]
    
    // SECTION 3: METHODS
    public string EligibilityItem { get; set; } = "Pending...";      // Item 5 [cite: 7]
    public string SourcesItem { get; set; } = "Pending...";          // Item 6 [cite: 8]
    public string SearchStrategyItem { get; set; } = "Pending...";   // Item 7 [cite: 8]
    public string SelectionProcessItem { get; set; } = "Pending..."; // Item 8 [cite: 9]
    public string BiasAssessmentItem { get; set; } = "Pending...";   // Item 11 [cite: 10]
    
    // SECTION 4: RESULTS & DISCUSSION
    public string SynthesisResultsItem { get; set; } = "Pending..."; // Item 20a [cite: 13]
    public string DiscussionItem { get; set; } = "Pending...";       // Item 23a [cite: 16]
    
    // SECTION 5: OTHER INFORMATION
    public string SupportItem { get; set; } = "Pending...";          // Item 25 [cite: 18]
    public string AvailabilityItem { get; set; } = "Pending...";     // Item 27 [cite: 18]
}