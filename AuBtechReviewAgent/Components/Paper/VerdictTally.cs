using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// How many citations got each of the five verdicts, for the stacked verdict bar (<c>Components/Paper/VerdictBar.razor</c>),
/// and how many the second repair pass removed (issue #234), counted on their own so none disappears silently.
/// The Done card and the report both count from the citation audit with the reader's edits applied (<see cref="ForRun"/>),
/// so they never disagree (issue: the run page said 109 citations checked, the report 113); the run's figures are the
/// fall-back for a run without an audit file.
/// </summary>
public readonly record struct VerdictTally(int Supported, int Partly, int NotSupported, int AbstractOnly, int Unverifiable, int Removed = 0)
{
    public int Total => Supported + Partly + NotSupported + AbstractOnly + Unverifiable + Removed;

    /// <summary>The citations that stand in the report with a verdict: every checked one but the removed, as the methods text counts them.</summary>
    public int Checked => Total - Removed;

    /// <summary>Partly or not supported: the citations a reader should look at.</summary>
    public int Flagged => Partly + NotSupported;

    public static VerdictTally From(ReviewStats s) => new(
        s.CitationsSupported, s.CitationsPartiallySupported, s.CitationsNotSupported,
        s.CitationsInsufficientEvidence, s.CitationsUnverifiable, s.CitationsRemoved);

    public static VerdictTally From(IEnumerable<CitationSupportResult> checks, int removed = 0)
    {
        var list = checks as IReadOnlyCollection<CitationSupportResult> ?? checks.ToList();
        int Count(string verdict) => list.Count(c => c.Verdict == verdict);
        return new(
            Count(CitationSupportChecker.Supported), Count(CitationSupportChecker.Partial), Count(CitationSupportChecker.NotSupported),
            Count(CitationSupportChecker.InsufficientEvidence), list.Count - Count(CitationSupportChecker.Supported) - Count(CitationSupportChecker.Partial)
                - Count(CitationSupportChecker.NotSupported) - Count(CitationSupportChecker.InsufficientEvidence), removed);
    }

    /// <summary>
    /// The counts of a run, read from citation-audit.json in its folder with the edit log applied (the same checks the report
    /// shows), or from the run's figures when the folder has no readable audit.
    /// </summary>
    public static VerdictTally ForRun(string folder, ReviewStats? fallback)
    {
        string path = Path.Join(folder, "citation-audit.json");
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("SupportChecks", out var checks))
                {
                    var audit = JsonSerializer.Deserialize<List<CitationSupportResult>>(checks.GetRawText()) ?? new();
                    int removed = doc.RootElement.TryGetProperty("RemovedCitations", out var gone) && gone.ValueKind == JsonValueKind.Array ? gone.GetArrayLength() : 0;
                    return From(ReportEdits.Checks(audit, ReportEditLog.Load(folder)), removed);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* fall back to the run's figures */ }
        }
        return fallback == null ? default : From(fallback);
    }

    /// <summary>The bar as one sentence, for screen readers; abstract only, unverifiable and removed are named only when present.</summary>
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
        if (Removed > 0) parts.Add($"{Removed} removed");
        return $"Of {total ?? Total} citations: " + string.Join(", ", parts) + ".";
    }
}
