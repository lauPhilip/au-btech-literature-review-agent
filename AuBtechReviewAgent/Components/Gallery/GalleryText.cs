using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The gallery's words that more than one page shows: the consent text the owner reads before saying yes, and the rule
/// for duplicates on the gallery page. They describe what Runs/GalleryStore.cs and GallerySnapshot.cs do; change them
/// together.
/// </summary>
public static class GalleryText
{
    /// <summary>What the owner agrees to, one plain sentence a point.</summary>
    public static IReadOnlyList<string> Consent(int retentionDays) => new[]
    {
        "The report is published as it prints: its text, figures, tables and references, and the short quotes from the sources it cites. Your notes are not included.",
        "Your topic and research questions will be visible, in the report and on its card in the gallery.",
        "The gallery is public. Anyone can open it on this site without a link, and search engines may find it.",
        $"The report names its run, so for the rest of the run's {retentionDays} days anyone could also open the run itself.",
        "You can withdraw it at any time, here or on its gallery page, from this browser.",
        "If you add another run of the same review later, its report replaces this one.",
    };

    /// <summary>The rule for duplicates, as the gallery page explains it.</summary>
    public const string SameReviewRule =
        "Each review is in the gallery once. Two runs are the same review when they are the same kind of review with the " +
        "same topic and research questions, ignoring capitals, punctuation and spacing. When an owner adds a newer run of " +
        "the same review, its report replaces the older one, and the card says when it was updated.";
}
