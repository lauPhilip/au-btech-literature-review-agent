using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The systematic review (PRISMA 2020), and its rapid and living variants. Its runs are done by
/// <see cref="PrismaReviewEngine"/>; the PRISMA-only files move into this folder step by step.
/// </summary>
public sealed class SystematicModule : IReviewModule
{
    public string Key => ReviewMethod.SystematicKey;
    public string Name => "Systematic review";
    public string RoutePrefix => "/review";

    /// <summary>The ledger and audit files of a systematic review, in the order they appear in the archive.</summary>
    public IReadOnlyList<string> ArchiveFiles { get; } = new[]
    {
        RunHeader.FileName, RunStore.ProtocolFile, "transparent-process.json", "prisma-report.json", "llm-calls.json", "extraction.json",
        "citation-audit.json", RunStore.NotesFile, "thematic-codebook.json", "run-metrics.json", "peer-review-feedback.json",
        "stylistic-transformation-ledger.json", "grounded-outline.txt",
    };

    public IReadOnlyList<ReviewMethod> Cards { get; } = new[]
    {
        new ReviewMethod(
            ReviewMethod.SystematicKey,
            "Systematic Literature Review",
            "PRISMA 2020",
            "Page et al. (2021), The PRISMA 2020 statement, BMJ 372:n71",
            "https://www.prisma-statement.org/prisma-2020",
            "Answers a focused question from peer-reviewed studies, by a plan written before the search.",
            new[] { "Protocol written first", "Two independent screenings", "Quality appraisal (MMAT)", "Every citation checked against the paper" },
            Available: true),
        new ReviewMethod(
            "rapid",
            "Rapid Review",
            "Cochrane rapid review guidance",
            "Garritty et al. (2024), Updated recommendations for the Cochrane rapid review methods guidance for rapid reviews of effectiveness, BMJ 384:e076335",
            "https://doi.org/10.1136/bmj-2023-076335",
            "A systematic review made faster by simplifying chosen steps, with every shortcut stated in the report.",
            new[] { "Protocol written first", "Shortcuts chosen and recorded", "Every citation checked against the paper" },
            Available: false),
        new ReviewMethod(
            "living",
            "Living Systematic Review",
            "PRISMA 2020, kept up to date",
            "Elliott et al. (2017), Living systematic review: 1. Introduction, the why, what, when, and how, Journal of Clinical Epidemiology 91",
            "https://doi.org/10.1016/j.jclinepi.2017.08.010",
            "A systematic review that searches again on a schedule and adds new studies as they are published.",
            new[] { "Search rerun on a schedule", "Each update recorded and dated", "Every citation checked against the paper" },
            Available: false),
    };
}
