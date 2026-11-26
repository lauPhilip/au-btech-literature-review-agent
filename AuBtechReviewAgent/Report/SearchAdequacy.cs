using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The search-adequacy guard. A run that took fewer than <see cref="MinRecordsPerSource"/> records per source, or
/// identified fewer than <see cref="MinRecordsIdentified"/> records, is a pilot search: it cannot claim to have found
/// the literature. The limitations and the abstract facts say so in code, and the title and the kicker on the front
/// page call it a pilot systematic search rather than a systematic literature review.
/// </summary>
public static class SearchAdequacy
{
    /// <summary>Under this per-source cap the search is a pilot, whatever it found.</summary>
    public const int MinRecordsPerSource = 50;

    /// <summary>Under this many records identified the search is a pilot, whatever the cap.</summary>
    public const int MinRecordsIdentified = 100;

    /// <summary>The kicker of a pilot run, in place of "Systematic review".</summary>
    public const string PilotKind = "Pilot systematic search";

    /// <summary>True when the run's search was too small to stand for the literature.</summary>
    public static bool IsPilot(ReviewState? state) =>
        state != null && ((state.MaxResultsPerSource > 0 && state.MaxResultsPerSource < MinRecordsPerSource)
                          || state.Stats.TotalIdentified < MinRecordsIdentified);

    /// <summary>Why the run is a pilot, counted from the ledger; empty when it is not.</summary>
    public static string Reason(ReviewState? state) => ReasonText(state, false);

    // The limitations paragraph already says "The search took at most N records per source" (MethodsSectionWriter), so
    // its pilot sentence names the cap in other words instead of saying it twice.
    private static string ReasonText(ReviewState? state, bool capAlreadySaid)
    {
        if (!IsPilot(state)) return "";
        var s = state!;
        var parts = new System.Collections.Generic.List<string>();
        if (s.MaxResultsPerSource > 0 && s.MaxResultsPerSource < MinRecordsPerSource)
            parts.Add(capAlreadySaid
                ? $"was capped below the {MinRecordsPerSource} records per source that a full search needs"
                : $"took at most {s.MaxResultsPerSource} record{(s.MaxResultsPerSource == 1 ? "" : "s")} per source");
        if (s.Stats.TotalIdentified < MinRecordsIdentified)
            parts.Add($"identified {s.Stats.TotalIdentified} record{(s.Stats.TotalIdentified == 1 ? "" : "s")}");
        return string.Join(" and ", parts);
    }

    /// <summary>The limitations sentence of a pilot run; empty otherwise.</summary>
    public static string LimitationSentence(ReviewState? state) => IsPilot(state)
        ? $"This was a pilot run, not a comprehensive search: the search {ReasonText(state, true)}, so relevant studies are likely to be missing and the findings show what a small sample of the literature says, not the state of the field. "
        : "";

    /// <summary>The fact line for the abstract of a pilot run; empty otherwise.</summary>
    public static string AbstractFact(ReviewState? state) => IsPilot(state)
        ? $"Search scope: this was a pilot run, not a comprehensive search (the search {Reason(state)}). The abstract must say that it reports a pilot search, not a comprehensive one, and must not call the review comprehensive or systematic without the word pilot."
        : "";

    // "A Systematic Literature Review", "a systematic review", "Systematic Mapping Review".
    private static readonly Regex ReviewClaim = new(@"\b(?:(?:an?\s+)?pilot\s+|an?\s+)?systematic\s+(?:literature\s+|mapping\s+)?review\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The title of a pilot run: "a systematic (literature) review" becomes "a pilot systematic search", in the title's
    /// own case. Any other title, and every title of a run that is not a pilot, is returned as it was.
    /// </summary>
    public static string Title(string title, ReviewState? state)
    {
        if (!IsPilot(state) || string.IsNullOrWhiteSpace(title)) return title;
        return ReviewClaim.Replace(title, m =>
        {
            // A "pilot" the model already wrote in front of the claim is taken in, so it is not said twice.
            bool article = Regex.IsMatch(m.Value, @"^an?\s", RegexOptions.IgnoreCase);
            bool titleCase = Regex.IsMatch(m.Value, @"\bSystematic\b");
            string phrase = titleCase ? "Pilot Systematic Search" : "pilot systematic search";
            if (!article) return phrase;
            return (char.IsUpper(m.Value[0]) ? "A " : "a ") + phrase;
        });
    }

    /// <summary>The kicker on the front page: "Systematic review", or <see cref="PilotKind"/> for a pilot run.</summary>
    public static string Kind(ReviewState? state) => IsPilot(state) ? PilotKind : "Systematic review";
}
