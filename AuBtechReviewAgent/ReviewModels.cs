using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

public record AcademicPaper(
    string Id, 
    string Title, 
    string Abstract, 
    string PublishedDate, 
    List<string> Authors
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
    string Citation, 
    string Publication, 
    string BriefSummary
);