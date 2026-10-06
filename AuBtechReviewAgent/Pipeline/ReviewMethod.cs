using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// A kind of literature review, done by its own recognised method. Every method shares TraceableAI's core (every
/// claim grounded in a quote from a source, every decision on file); what differs is which sources count, which
/// stages run and which reporting standard the report follows. Only methods with a published reporting guideline
/// are listed, so each one stands for a real, citable method.
///
/// Today the engine implements one method, the PRISMA 2020 systematic review. The others are shown on the start
/// page as "coming soon"; switching one on means setting <see cref="Available"/> once the engine supports it.
/// </summary>
public sealed record ReviewMethod(
    string Key,
    string Name,
    string Standard,
    string StandardReference,
    string StandardUrl,
    string Summary,
    IReadOnlyList<string> Highlights,
    bool Available)
{
    /// <summary>The method a review uses when none is given (and the one every older run used).</summary>
    public const string SystematicKey = "systematic";
}

public static class ReviewMethods
{
    public static readonly IReadOnlyList<ReviewMethod> All = new[]
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
            "multivocal",
            "Multivocal Literature Review",
            "Garousi et al. guidelines",
            "Garousi, Felderer & Mäntylä (2019), Guidelines for including grey literature and conducting multivocal literature reviews, IST 106",
            "https://doi.org/10.1016/j.infsof.2018.09.006",
            "Adds grey literature, such as reports, documentation and practitioner articles, to the academic studies.",
            new[] { "Academic and grey sources", "Quality check for sources that are not peer reviewed", "Every citation checked against the source" },
            Available: false),
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
        new ReviewMethod(
            "grey",
            "Grey Literature Review",
            "Garousi et al. guidelines",
            "Garousi, Felderer & Mäntylä (2019), Guidelines for including grey literature and conducting multivocal literature reviews, IST 106",
            "https://doi.org/10.1016/j.infsof.2018.09.006",
            "Reviews only grey literature, such as reports, documentation and practitioner articles, where research is scarce.",
            new[] { "Grey sources only", "Quality check for sources that are not peer reviewed", "Every citation checked against the source" },
            Available: false),
    };

    public static ReviewMethod Default => All.First(m => m.Key == ReviewMethod.SystematicKey);

    /// <summary>The method with this key, or null.</summary>
    public static ReviewMethod? Find(string? key) =>
        All.FirstOrDefault(m => m.Key.Equals(key?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>The method with this key when it can be run; otherwise the default (an older link or an unknown key).</summary>
    public static ReviewMethod AvailableOrDefault(string? key) => Find(key) is { Available: true } m ? m : Default;
}
