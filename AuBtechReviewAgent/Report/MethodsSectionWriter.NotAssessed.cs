namespace AuBtechReviewAgent;

/// <summary>The plain statement on the PRISMA 2020 methods items the tool does not carry out (items 12 to 14).</summary>
public static partial class MethodsSectionWriter
{
    /// <summary>The heading of the statement in main.tex, which the PRISMA checklist names.</summary>
    public const string NotAssessedHeading = "What This Review Did Not Assess";

    /// <summary>
    /// What the review did not do, said plainly so a reader does not look for it: effect measures, a statistical synthesis,
    /// heterogeneity and reporting bias. The sensitivity check is named only when the run rated the certainty of its answers.
    /// </summary>
    /// <param name="sensitivityCheck">True when the run has a certainty rating, whose sensitivity check is item 13f.</param>
    public static string NotAssessed(bool sensitivityCheck) =>
        "This review did not compute effect measures (PRISMA 2020 item 12). " +
        "The findings were synthesised in words, as themes, without a meta-analysis, so no effect sizes were pooled, no statistical heterogeneity was explored and no subgroup analysis or meta-regression was run (items 13d and 13e). " +
        (sensitivityCheck
            ? "The only sensitivity analysis counts the studies each answer still rests on without the weakest evidence; it is described under Certainty of the Evidence (item 13f). "
            : "No sensitivity analysis was run (item 13f). ") +
        "The risk of bias due to missing results, such as unpublished studies or selectively reported outcomes, was not assessed (item 14), so reporting bias cannot be ruled out. " +
        "The authors should add these assessments if their question needs them.";
}
