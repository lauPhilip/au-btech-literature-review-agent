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
            "scoping",
            "Scoping Review",
            "PRISMA-ScR",
            "Tricco et al. (2018), PRISMA Extension for Scoping Reviews, Annals of Internal Medicine 169(7)",
            "https://www.prisma-statement.org/scoping",
            "Maps what has been studied and where the gaps are, instead of judging how good the studies are.",
            new[] { "Protocol written first", "A charting table instead of quality appraisal", "Every citation checked against the paper" },
            Available: false),
    };

    public static ReviewMethod Default => All.First(m => m.Key == ReviewMethod.SystematicKey);

    /// <summary>The method with this key, or null.</summary>
    public static ReviewMethod? Find(string? key) =>
        All.FirstOrDefault(m => m.Key.Equals(key?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>The method with this key when it can be run; otherwise the default (an older link or an unknown key).</summary>
    public static ReviewMethod AvailableOrDefault(string? key) => Find(key) is { Available: true } m ? m : Default;
}
