using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// How many citations got each of the five verdicts, for the stacked verdict bar (<c>Components/Paper/VerdictBar.razor</c>).
/// The Done card counts from the run's figures; the report counts from the citation audit, so a re-check shows at once.
/// </summary>
public readonly record struct VerdictTally(int Supported, int Partly, int NotSupported, int AbstractOnly, int Unverifiable)
{
    public int Total => Supported + Partly + NotSupported + AbstractOnly + Unverifiable;

    /// <summary>Partly or not supported: the citations a reader should look at.</summary>
    public int Flagged => Partly + NotSupported;

    public static VerdictTally From(ReviewStats s) => new(
        s.CitationsSupported, s.CitationsPartiallySupported, s.CitationsNotSupported,
        s.CitationsInsufficientEvidence, s.CitationsUnverifiable);

    public static VerdictTally From(IEnumerable<CitationSupportResult> checks)
    {
        var list = checks as IReadOnlyCollection<CitationSupportResult> ?? checks.ToList();
        int Count(string verdict) => list.Count(c => c.Verdict == verdict);
        return new(
            Count(CitationSupportChecker.Supported), Count(CitationSupportChecker.Partial), Count(CitationSupportChecker.NotSupported),
            Count(CitationSupportChecker.InsufficientEvidence), list.Count - Count(CitationSupportChecker.Supported) - Count(CitationSupportChecker.Partial)
                - Count(CitationSupportChecker.NotSupported) - Count(CitationSupportChecker.InsufficientEvidence));
    }

    /// <summary>The bar as one sentence, for screen readers; abstract only and unverifiable are named only when present.</summary>
    public string Sentence(int? total = null)
    {
        var parts = new List<string>
        {
            $"{Supported} supported",
            $"{Partly} partly supported",
            $"{NotSupported} not supported",
        };
        if (AbstractOnly > 0) parts.Add($"{AbstractOnly} abstract only");
        if (Unverifiable > 0) parts.Add($"{Unverifiable} could not be verified");
        return $"Of {total ?? Total} citations: " + string.Join(", ", parts) + ".";
    }
}
