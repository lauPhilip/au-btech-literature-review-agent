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
public static partial class MethodsSectionWriter
{
    public const string ScreeningModelName = "Mistral Large (mistral-large-latest)";

    public static string Eligibility(string inclusion, string exclusion, bool peerReviewOnly)
    {
        var sb = new StringBuilder();
        sb.Append($"Records were eligible if they met the inclusion criterion: \"{inclusion.Trim().TrimEnd('.')}\". ");
        sb.Append($"Records were excluded if they met the exclusion criterion: \"{exclusion.Trim().TrimEnd('.')}\". ");
        sb.Append(peerReviewOnly
            ? "The peer-reviewed-only filter was enabled, so records classified as preprints or working papers were excluded before screening."
            : "The peer-reviewed-only filter was disabled, so preprints and other non-peer-reviewed records were eligible.");
        return sb.ToString();
    }

    public static string InformationSources(
        IReadOnlyList<string> selectedSourceNames,
        IReadOnlyList<string> unavailableSourceNames,
        IReadOnlyList<PlatformSearchLog> searchLogs,
        DateTime runDateUtc,
        int identifiedViaCitations = 0,
        bool citationChaining = false,
        SeedChainingRecord? seeds = null)
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

        if (citationChaining && seeds != null)
        {
            sb.Append("In addition, one round of citation chaining was run through OpenAlex, in both directions: the works a study cites and the works citing it. ");
            sb.Append(SeedChaining(seeds));
            int other = Math.Max(0, identifiedViaCitations - seeds.Identified);
            sb.Append($"For every {(seeds.Seeds.Count > 0 ? "other " : "")}included record with a DOI, up to a fixed number of the works it cites and of the works citing it were retrieved ({other} record{(other == 1 ? "" : "s")} identified this way). ");
            sb.Append("All records found by citation chaining were screened in the same way. ");
            sb.Append("No other databases, registers or websites were consulted.");
        }
        else if (citationChaining)
        {
            sb.Append($"In addition, one round of citation chaining was run through OpenAlex: for every included record with a DOI, up to a fixed number of the works it cites and of the works citing it were retrieved ({identifiedViaCitations} record{(identifiedViaCitations == 1 ? "" : "s")} identified this way) and screened in the same way. ");
            sb.Append("No other databases, registers or websites were consulted.");
        }
        else
        {
            sb.Append("No other databases, registers, websites or reference lists were consulted.");
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// The sentences on the seed step of citation chaining, shared by every kind of review, from the seed record on file:
    /// how the seeds were ranked, that code chose them from included records only, and what building out kept. Empty
    /// without a seed record.
    /// </summary>
    public static string SeedChaining(SeedChainingRecord? seeds, string records = "included record")
    {
        if (seeds == null) return "";
        string Plural(int n, string one) => n == 1 ? one : one.EndsWith('y') ? one[..^1] + "ies" : one + "s";
        if (seeds.Seeds.Count == 0)
            return seeds.WithoutCount > 0
                ? $"No seed studies were chosen, because none of the {seeds.WithoutCount} {Plural(seeds.WithoutCount, records)} with a DOI had a citation count and a year in its database's metadata. "
                : $"No seed studies were chosen, because no {records} had a DOI. ";
        var sb = new StringBuilder();
        int n = seeds.Seeds.Count;
        sb.Append($"First, the {(n == 1 ? "" : n + " ")}{Plural(n, records)} with the most citations per year {(n == 1 ? "was" : "were")} taken as seed {Plural(n, "study")}. ");
        sb.Append($"Citations per year are the citation count in the database's metadata on the day of the search divided by the years from publication to {seeds.ReferenceYear}, the publication year counting as one, so a recent study could compete with an older one; ties went to more citations, then to the newer study. ");
        sb.Append("The seeds were chosen in code from these counts, not by a language model, and only from records already included at screening, so a highly cited but irrelevant paper could not become a seed. ");
        if (seeds.WithoutCount > 0)
            sb.Append($"Of the {records}s with a DOI, {seeds.WithoutCount} had no citation count or year and could not be ranked. ");
        string kept = seeds.PerDirection == 1 ? "the one with the most citations per year in each direction was" : $"the {seeds.PerDirection} with the most citations per year in each direction were";
        sb.Append($"For each seed, up to {seeds.PoolPerDirection} of the works it cites and up to {seeds.PoolPerDirection} of the most cited works citing it were retrieved from OpenAlex and ranked the same way, and {kept} kept ({seeds.Identified} record{(seeds.Identified == 1 ? "" : "s")} identified this way). ");
        int failed = seeds.Seeds.Count(x => x.Error != null);
        if (failed > 0)
            sb.Append($"The look-up failed for {failed} seed{(failed == 1 ? "" : "s")}, which {(failed == 1 ? "was" : "were")} chained like the other included records instead. ");
        sb.Append("The appendix lists the seeds. ");
        return sb.ToString();
    }

    public static string SearchStrategy(
        string primaryQuery,
        IReadOnlyList<string> perspectives,
        int maxResultsPerSource,
        int duplicatesRemoved,
        int cappedBeyondMax,
        int yearFrom = 0,
        int yearTo = 0,
        int outsideDateRange = 0,
        bool reviewedByReviewer = false,
        IReadOnlyList<SearchStringYield>? yields = null)
    {
        var sb = new StringBuilder();
        sb.Append($"The primary search string was \"{primaryQuery.Trim()}\". ");

        var extra = perspectives.Skip(1).ToList();
        if (extra.Count > 0)
        {
            sb.Append(reviewedByReviewer
                ? $"To improve recall, {extra.Count} additional search string{(extra.Count == 1 ? " was" : "s were")} proposed by the language model and checked and approved (or edited) by the reviewer before the search, each run against every source: "
                : $"To improve recall, the language model proposed {extra.Count} additional search string{(extra.Count == 1 ? "" : "s")}, each of which was also run against every source: ");
            sb.Append(string.Join("; ", extra.Select(p => $"\"{p.Trim()}\"")));
            sb.Append(". ");
            if (yields != null && SearchSaturation.Hint(yields) is { } hint)
                sb.Append("Taken in order, the search strings added " + string.Join(", ", yields.Where(y => y.Searches > 0).Select(y => y.New)) +
                          " records not found by an earlier string" + (hint.Contains("close to saturation", StringComparison.Ordinal)
                              ? ", so the search was close to saturation for these phrasings. "
                              : ", so the last phrasing still added new records. "));
        }
        else
        {
            sb.Append("No additional search strings were used. ");
        }

        // The cap counts records that go on to screening, after duplicates and the year filter, so a source can
        // show more records identified in the flow diagram than the cap (issue #150).
        bool yearFilter = yearFrom > 0 && yearTo > 0;
        sb.Append($"At most {maxResultsPerSource} record{(maxResultsPerSource == 1 ? "" : "s")} from each source went on to screening, across all search strings. ");
        sb.Append(yearFilter
            ? "Duplicates and records outside the year range did not count towards this limit, so a source can show more records identified in the flow diagram. "
            : "Duplicates did not count towards this limit, so a source can show more records identified in the flow diagram. ");
        sb.Append($"Duplicates were removed across all sources by identifier, DOI and normalised title ({duplicatesRemoved} removed)");
        sb.Append(cappedBeyondMax > 0 ? $", and {cappedBeyondMax} further record{(cappedBeyondMax == 1 ? " was" : "s were")} dropped by the per-source cap. " : ". ");
        sb.Append("Every record removed at this stage is listed with its reason in the run ledger. ");
        if (yearFilter)
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

    /// <summary>
    /// A model's name in brackets for the methods text, with any further details (and <paramref name="details"/>)
    /// after commas, so a name that already carries brackets is not bracketed twice: "mistral-small-latest (Mistral
    /// API)" with "temperature 0" becomes "(mistral-small-latest, Mistral API, temperature 0)" (issue #120).
    /// </summary>
    public static string ModelInBrackets(string model, string? details = null)
    {
        string name = (model ?? "").Trim();
        var parts = System.Text.RegularExpressions.Regex.Match(name, @"^(.+?)\s*\((.+)\)$");
        string inner = parts.Success ? $"{parts.Groups[1].Value.Trim()}, {parts.Groups[2].Value.Trim()}" : name;
        return string.IsNullOrWhiteSpace(details) ? $"({inner})" : $"({inner}, {details})";
    }

    /// <summary>
    /// A sentence for when the screenings agreed on most records but kappa is still low, as a reader would
    /// otherwise take it for a mistake (issue #122): when nearly every record gets the same decision, most of
    /// the agreement is expected by chance, which is what kappa takes away. Empty otherwise.
    /// </summary>
    public static string KappaNote(int agreed, int total, double? kappa)
    {
        if (kappa is not double k || total <= 0 || k >= 0.2 || agreed < 0.7 * total) return "";
        return "Kappa is low although most decisions agree, because nearly every record got the same decision, so most of that agreement could happen by chance. ";
    }

    public static string SelectionProcess(bool peerReviewOnly, ReviewStats stats, bool humanReviewRequested = false, string? humanReviewOutcome = null, string? modelName = null)
    {
        string model = string.IsNullOrWhiteSpace(modelName) ? ScreeningModelName : modelName;
        var sb = new StringBuilder();
        if (peerReviewOnly)
        {
            sb.Append($"Before screening, records were checked for peer-review status using source metadata and, where that was inconclusive, a language-model classification ({stats.PassedPeerReviewCheck} passed, {stats.FailedPeerReviewCheck} excluded). ");
        }
        if (stats.DualScreened > 0)
        {
            int agreed = stats.DualScreened - stats.ScreeningDisagreements;
            sb.Append($"Each remaining record was screened twice by a language model {ModelInBrackets(model, "temperature 0")} with two independent prompts that work through the criteria in a different order, using its title, authors, date, venue and abstract as returned by the source. ");
            sb.Append($"The two screenings agreed on {agreed} of {stats.DualScreened} records");
            sb.Append(stats.ScreeningKappa is double k ? $" (Cohen's kappa = {k.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}); " : "; ");
            sb.Append(stats.ScreeningDisagreements == 0
                ? "there were no disagreements to resolve. "
                : $"the {stats.ScreeningDisagreements} disagreement{(stats.ScreeningDisagreements == 1 ? " was" : "s were")} resolved by inclusion and flagged for human attention. ");
            sb.Append(KappaNote(agreed, stats.DualScreened, stats.ScreeningKappa));
        }
        else
        {
            sb.Append($"Each remaining record was screened once by a language model {ModelInBrackets(model, "temperature 0")} against the eligibility criteria, using its title, authors, date, venue and abstract as returned by the source. ");
        }
        sb.Append("Text taken from the records was passed to the model inside marked data blocks with the instruction to treat it as data, never as instructions");
        sb.Append(stats.InjectionSuspected > 0
            ? $"; {stats.InjectionSuspected} record{(stats.InjectionSuspected == 1 ? "" : "s")} contained instruction-like phrases and {(stats.InjectionSuspected == 1 ? "was" : "were")} flagged as uncertain. "
            : ". ");
        if (stats.UncertainDecisions > 0)
            sb.Append($"In total {stats.UncertainDecisions} decision{(stats.UncertainDecisions == 1 ? " was" : "s were")} flagged as uncertain (disagreement, low model confidence or suspected manipulation). ");
        if (stats.CacheHits > 0)
            sb.Append($"{stats.CacheHits} screening decision{(stats.CacheHits == 1 ? "" : "s")} or search response{(stats.CacheHits == 1 ? " was" : "s were")} reused from an earlier identical run (same record, criteria, model and prompt version); reuse is marked per record in the ledger. ");
        sb.Append("Full texts were retrieved only for included records and were not used for the screening decision. ");
        if (stats.ScreeningErrors > 0)
            sb.Append($"{stats.ScreeningErrors} record{(stats.ScreeningErrors == 1 ? "" : "s")} could not be screened because the model call failed; they are listed in the run ledger. ");

        if (humanReviewRequested && stats.HumanReviewed > 0)
        {
            sb.Append($"A human reviewer then checked {stats.HumanReviewed} of the model's screening decisions in the dashboard and changed {stats.HumanOverrides}; ");
            sb.Append("each change is recorded in the run ledger next to the model's original decision. ");
        }
        else if (humanReviewRequested)
        {
            sb.Append((humanReviewOutcome ?? "A human screening review was requested but not completed.") + " ");
        }

        sb.Append($"Of {stats.Screened} records screened, {stats.Included} were included and {stats.Excluded + stats.FailedPeerReviewCheck} excluded. ");
        if (!(humanReviewRequested && stats.HumanReviewed > 0))
            sb.Append("No human reviewer took part in screening during the run. ");
        sb.Append("Every decision and its rationale is recorded in the run ledger (transparent-process.json) so that a human reviewer can verify it.");
        return sb.ToString();
    }
}
