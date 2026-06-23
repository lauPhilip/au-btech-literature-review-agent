using System;

namespace AuBtechReviewAgent;

public class UiStateContainer
{
    // Holds the UI dashboard variables across tab switches
    public string SearchQuery { get; set; } = "Agentic AI software frameworks";
    public string InclusionCriteria { get; set; } = "Must focus on loop execution and agent architecture.";
    public string ExclusionCriteria { get; set; } = "Exclude agronomy or commercial marketing studies.";
    public string ReviewObjective { get; set; } = "To evaluate the current state of agentic orchestration loops and identify common design patterns regarding fault-tolerance and system safety.";
    public DateTime DateFrom { get; set; } = new DateTime(2020, 01, 01);
    public DateTime DateTo { get; set; } = DateTime.Today;
    public bool RequirePeerReview { get; set; } = false; // Maps directly to UI toggle check

    public string RawJsonLogs { get; set; } = "{\n  \"status\": \"Awaiting execution...\",\n  \"loopState\": \"Idle\"\n}";
    
    public int TotalIdentified { get; set; } = 0;
    public int ScreenedCount { get; set; } = 0;
    public int ExcludedCount { get; set; } = 0;
    public int IncludedCount { get; set; } = 0;
    
    // NEW: Dashboard tracking counts mapped from engine progress loops
    public int PassedPeerReviewCount { get; set; } = 0;
    public int FailedPeerReviewCount { get; set; } = 0;

    public bool IsSearching { get; set; } = false;
}