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
/// A card is declared by the module that runs it (<see cref="IReviewModule.Cards"/>), so a card cannot exist without
/// code behind it. Today only the PRISMA 2020 systematic review runs; the others are shown on the start page as
/// "coming soon", and switching one on means setting <see cref="Available"/> once its module supports it.
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
    // The order of the cards on the start page. Each card is declared by the module that runs it.
    private static readonly string[] StartPageOrder = { ReviewMethod.SystematicKey, "multivocal", "rapid", "living", "grey" };

    public static readonly IReadOnlyList<ReviewMethod> All = ReviewModules.All
        .SelectMany(m => m.Cards)
        .OrderBy(c => Array.IndexOf(StartPageOrder, c.Key) is var i and >= 0 ? i : int.MaxValue)
        .ToList();

    public static ReviewMethod Default => All.First(m => m.Key == ReviewMethod.SystematicKey);

    /// <summary>The method with this key, or null.</summary>
    public static ReviewMethod? Find(string? key) =>
        All.FirstOrDefault(m => m.Key.Equals(key?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>The method with this key when it can be run; otherwise the default (an older link or an unknown key).</summary>
    public static ReviewMethod AvailableOrDefault(string? key) => Find(key) is { Available: true } m ? m : Default;
}
