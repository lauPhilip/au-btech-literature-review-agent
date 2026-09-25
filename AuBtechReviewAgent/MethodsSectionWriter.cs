using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// Writes the PRISMA methods items (5 Eligibility, 6 Information sources, 7 Search strategy, 8 Selection
/// process) from what the run actually did, instead of asking the language model to describe it.
/// The model used to get these wrong in ways a reader cannot spot: it listed databases that were never
/// queried, called preprints "peer-reviewed" with the filter switched off, and described "a researcher
/// manually reviewed borderline cases" in a pipeline with no human step. These are facts about the run,
/// so they are rendered from the run's own configuration and ledger.
/// </summary>
public static class MethodsSectionWriter
{
    public const string ScreeningModelName = "Mistral Large (mistral-large-latest)";

    public static string Eligibility(string inclusion, string exclusion, bool peerReviewOnly)
    {
        var sb = new StringBuilder();
        sb.Append($"Records were eligible if they met the inclusion criterion: \"{inclusion.Trim()}\". ");
        sb.Append($"Records were excluded if they met the exclusion criterion: \"{exclusion.Trim()}\". ");
        sb.Append(peerReviewOnly
            ? "The peer-reviewed-only filter was enabled, so records classified as preprints or working papers were excluded before screening."
            : "The peer-reviewed-only filter was disabled, so preprints and other non-peer-reviewed records were eligible.");
        return sb.ToString();
    }

    public static string InformationSources(
        IReadOnlyList<string> selectedSourceNames,
        IReadOnlyList<string> unavailableSourceNames,
        IReadOnlyList<PlatformSearchLog> searchLogs,
        DateTime runDateUtc)
    {
        var sb = new StringBuilder();
        var queried = searchLogs.Select(l => l.SourceName).Distinct().ToList();

        if (queried.Count == 0)
        {
            sb.Append("No bibliographic source was queried in this run. ");
        }
        else
        {
            sb.Append($"The following sources were searched on {runDateUtc:yyyy-MM-dd} (UTC): {JoinNatural(selectedSourceNames.Except(unavailableSourceNames).ToList())}. ");
        }

        if (unavailableSourceNames.Count > 0)
        {
            sb.Append($"{JoinNatural(unavailableSourceNames.ToList())} {(unavailableSourceNames.Count == 1 ? "was" : "were")} selected but could not be searched because no API key was configured. ");
        }

        var faulted = searchLogs
            .Where(l => !l.Status.StartsWith("Completed", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.SourceName).Distinct().ToList();
        if (faulted.Count > 0)
        {
            sb.Append($"At least one query to {JoinNatural(faulted)} failed; the failed requests are listed in the run ledger (transparent-process.json). ");
        }

        sb.Append("No other databases, registers, websites or reference lists were consulted.");
        return sb.ToString().Trim();
    }

    public static string SearchStrategy(
        string primaryQuery,
        IReadOnlyList<string> perspectives,
        int maxResultsPerSource,
        int duplicatesRemoved,
        int cappedBeyondMax,
        int yearFrom = 0,
        int yearTo = 0,
        int outsideDateRange = 0)
    {
        var sb = new StringBuilder();
        sb.Append($"The primary search string was \"{primaryQuery.Trim()}\". ");

        var extra = perspectives.Skip(1).ToList();
        if (extra.Count > 0)
        {
            sb.Append($"To improve recall, the language model proposed {extra.Count} additional search string{(extra.Count == 1 ? "" : "s")}, each of which was also run against every source: ");
            sb.Append(string.Join("; ", extra.Select(p => $"\"{p.Trim()}\"")));
            sb.Append(". ");
        }
        else
        {
            sb.Append("No additional search strings were used. ");
        }

        sb.Append($"Each source contributed at most {maxResultsPerSource} record{(maxResultsPerSource == 1 ? "" : "s")} in total across all search strings. ");
        sb.Append($"Within each source, duplicates were removed by identifier and normalised title ({duplicatesRemoved} removed)");
        sb.Append(cappedBeyondMax > 0 ? $", and {cappedBeyondMax} further record{(cappedBeyondMax == 1 ? " was" : "s were")} dropped by the per-source cap. " : ". ");
        if (yearFrom > 0 && yearTo > 0)
        {
            sb.Append($"Records with a publication year outside {yearFrom}-{yearTo} were removed after retrieval ({outsideDateRange} removed); records with no year in the source metadata were kept. ");
            sb.Append("No language or document-type limits were applied.");
        }
        else
        {
            sb.Append("No date, language or document-type limits were applied.");
        }
        return sb.ToString();
    }

    public static string SelectionProcess(bool peerReviewOnly, ReviewStats stats)
    {
        var sb = new StringBuilder();
        if (peerReviewOnly)
        {
            sb.Append($"Before screening, records were checked for peer-review status using source metadata and, where that was inconclusive, a language-model classification ({stats.PassedPeerReviewCheck} passed, {stats.FailedPeerReviewCheck} excluded). ");
        }
        sb.Append($"Each remaining record was screened once by a language model ({ScreeningModelName}) against the eligibility criteria, using its title, authors, date, venue and abstract as returned by the source. ");
        sb.Append("Full texts were retrieved only for included records and were not used for the screening decision. ");
        sb.Append($"Of {stats.Screened} records screened, {stats.Included} were included and {stats.Excluded} excluded. ");
        sb.Append("No human reviewer took part in screening during the run. Every decision and its rationale is recorded in the run ledger (transparent-process.json) so that a human reviewer can verify it.");
        return sb.ToString();
    }

    /// <summary>
    /// Short fingerprint of everything that defines the run's protocol. Two runs with the same hash used
    /// the same query, criteria, search strings, sources, cap and filter. Replaces a hard-coded
    /// "Protocol Hash: cc584090" that was printed on every report regardless of the run.
    /// </summary>
    public static string ComputeProtocolHash(
        string query, string objective, string inclusion, string exclusion,
        IEnumerable<string> perspectives, IEnumerable<string> selectedSourceKeys,
        int maxResultsPerSource, bool peerReviewOnly)
    {
        string canonical = string.Join("\n", new[]
        {
            query.Trim(), objective.Trim(), inclusion.Trim(), exclusion.Trim(),
            string.Join("|", perspectives.Select(p => p.Trim())),
            string.Join("|", selectedSourceKeys.OrderBy(k => k, StringComparer.Ordinal)),
            maxResultsPerSource.ToString(), peerReviewOnly ? "peer-reviewed-only" : "all",
        });
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static string JoinNatural(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1],
    };
}
