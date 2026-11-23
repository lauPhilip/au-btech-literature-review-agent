using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AuBtechReviewAgent;

public class ReviewState
{
    public string ReviewId { get; set; } = Guid.NewGuid().ToString();
    public string SearchQuery { get; set; } = "";
    public bool PeerReviewOnlyToggle { get; set; } 
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ReviewStats Stats { get; set; } = new();
    public string SynthesisTargetDirective { get; set; } = string.Empty;

    // The rest of what the reviewer entered, so a later run can start from the same settings.
    public string ReviewObjective { get; set; } = "";
    /// <summary>The reviewer's research questions, as checked by ReviewInputGuard (empty when the run had none).</summary>
    public List<string> ResearchQuestions { get; set; } = new();
    public string InclusionCriteria { get; set; } = "";
    public string ExclusionCriteria { get; set; } = "";
    public string ArtifactKind { get; set; } = "";
    public ReviewPhases Phases { get; set; } = new();
    public List<PlatformSearchLog> SearchLogs { get; set; } = new();
    public List<IncludedPaperMetricRow> SynthesizedRecords { get; set; } = new();

    // STORM-style multi-perspective search: the primary query plus LLM-generated variant phrasings
    // actually issued against every source, kept here so the run is auditable end-to-end.
    public List<string> SearchPerspectives { get; set; } = new();

    // The kind of review this run is (see ReviewMethods). Older runs have none and were systematic reviews.
    public string Method { get; set; } = ReviewMethod.SystematicKey;

    // True when the reviewer saw and approved (or edited) the extra search strings before the run.
    public bool SearchStringsReviewed { get; set; }

    // Per search string, in the order they were run: records retrieved and how many no earlier string had found.
    public List<SearchStringYield> SearchStringYields { get; set; } = new();

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

    // The address of each included paper's open-access full text, when one was downloaded (linked from the report).
    public Dictionary<string, string> FullTextUrls { get; set; } = new();

    // Instruction-like phrases in an included paper's full text (#366); null, and left out of the ledger, when none
    // was found. The text goes on to extraction and the citation check, so the run view asks the reviewer to look.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SourceTextFlags>? FullTextFlags { get; set; }

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

    /// <summary>The seed step of citation chaining: the rule, the seeds with their counts and ranks, and what they found.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SeedChainingRecord? SeedChaining { get; set; }

    // True when the user asked to confirm the screening decisions before the write-up.
    public bool HumanScreeningReviewRequested { get; set; }
    public string? HumanScreeningReviewOutcome { get; set; }

    // Model, app version, temperatures and prompt fingerprints for this run (details in llm-calls.json).
    public RunSettingsRecord? RunSettings { get; set; }

    // Instruction-like phrases found in the reviewer's own query, objective or criteria (ReviewInputGuard);
    // the reviewer confirmed them before the run, and protocol.md lists them.
    public List<string> InputFlags { get; set; } = new();

    // Thematic synthesis: codes, themes, coverage and (optionally) the second coding (also thematic-codebook.json).
    public ThematicCodebook? ThematicSynthesis { get; set; }

    // Set when the run stopped early ("Failed", "Interrupted" or "Cancelled" stage), so a returning user sees why.
    public string? FailureMessage { get; set; }
    public DateTime? CompletedUtc { get; set; }

    // Set only when the owner cancelled the run: when they asked, and the reason they gave. Left out of the
    // ledger otherwise, so a run that is not cancelled is written exactly as before.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CancelledUtc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CancelReason { get; set; }
}

public class ReviewStats 
{ 
    public int TotalIdentified { get; set; } = 0; 
    public int Screened { get; set; } = 0; 
    public int Excluded { get; set; } = 0; 
    public int Included { get; set; } = 0; 
    public string ProcessingStage { get; set; } = "Idle";
    public int PassedPeerReviewCheck { get; set; }
    public int FailedPeerReviewCheck { get; set; }
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
    public int IdentifiedViaSeedChaining { get; set; } // ...of which found by building out from the most-cited included studies
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
    public int CitationsRemoved { get; set; }       // Citations the second repair pass removed as still not supported; not in CitationsChecked
    public int CitationsOutsideTheme { get; set; }  // Citations in a theme subsection of a study not coded under that theme
    public int CitationsInsufficientEvidence { get; set; } // Abstract-only papers whose abstract does not mention the attributed claim
    public int CitationsSecondChecked { get; set; } // Citations given the independent second check
    public int CitationsUpgradedBySecondCheck { get; set; } // ...whose verdict the second check raised with a verified quote

    // Live progress for the dashboard (see RunProgress): the steps of this run, the current one, how far it
    // is (0 to 1) and a short detail such as "12 of 40 records".
    public List<string> ProgressPlan { get; set; } = new();
    public string ProgressStep { get; set; } = "";
    public double ProgressFraction { get; set; }
    public string ProgressDetail { get; set; } = "";
    /// <summary>What the step is working on right now, for the live line under a running review's title (LiveActivity):
    /// a database and its search string, or the paper whose full text is read. Shown live only; never written to the ledger.</summary>
    [JsonIgnore] public string ProgressSubject { get; set; } = "";
    public DateTime? RunStartedUtc { get; set; }
}

public class ReviewPhases 
{ 
    public List<ScreeningLog> Screening { get; set; } = new(); 
}

/// <summary>
/// The instruction-like phrases found in one included paper's full text (#366), and what was taken out of that text
/// because a reader never sees it (#369). <see cref="Flags"/> is empty when only hidden text was removed.
/// </summary>
public sealed record SourceTextFlags(string PaperId, string Title, List<SourceFlag> Flags,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    HiddenTextRemoved? Removed = null);
