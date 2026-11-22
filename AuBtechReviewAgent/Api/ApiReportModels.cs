using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// What the Review output page shows, as data. Everything is read from the run's files (prisma-report.json,
/// citation-audit.json and the ledger), which the run checked in code; nothing is written for the API.
/// </summary>
public sealed record ReviewReport(
    Guid Id,
    string Title,
    string Abstract,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyList<ReportCitation> Citations,
    IReadOnlyList<ReportReference> References,
    ReportPrismaCounts Prisma,
    string AppVersion,
    string ProtocolFingerprint,
    string? ProtocolSha256,
    DateTime? CompletedUtc);

/// <summary>One section of the report, in the order of <see cref="ReportOutline.Systematic"/>; Level 2 is a theme under section 4.</summary>
public sealed record ReportSection(string Id, string Heading, int Level, string Text);

/// <summary>
/// One citation and its check: the sentence, the reference number, the verdict and the passage it was checked
/// against. The quote is given only when it was found word for word in the source (QuoteVerified).
/// </summary>
public sealed record ReportCitation(
    int Reference,
    string Section,
    int SentenceIndex,
    string Sentence,
    string Verdict,
    string? Quote,
    bool QuoteVerified,
    string EvidenceBasis,
    string Reason,
    string? SourceLink);

/// <summary>One entry of the reference list: its number, the APA citation, the DOI and a web address, when there is one.</summary>
public sealed record ReportReference(int Number, string Citation, string? Doi, string? Link);

public sealed record SourceCount(string Source, int Count);

public sealed record ReasonCount(string Reason, int Count);

/// <summary>The counts of the PRISMA 2020 flow diagram (<see cref="PrismaFlowCounts"/>).</summary>
public sealed record ReportPrismaCounts(
    int Identified,
    IReadOnlyList<SourceCount> IdentifiedBySource,
    int IdentifiedViaCitations,
    int DuplicatesRemoved,
    int OutsideDateRange,
    int CappedBeyondMax,
    int Screened,
    int ScreeningErrors,
    int ExcludedPeerReview,
    int ExcludedAtScreening,
    IReadOnlyList<ReasonCount> ExcludedByReason,
    int Included,
    int FullTextRetrieved,
    int AbstractOnly);
