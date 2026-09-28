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
        DateTime runDateUtc,
        int identifiedViaCitations = 0,
        bool citationChaining = false)
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

        if (citationChaining)
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
        sb.Append($"Duplicates were removed across all sources by identifier, DOI and normalised title ({duplicatesRemoved} removed)");
        sb.Append(cappedBeyondMax > 0 ? $", and {cappedBeyondMax} further record{(cappedBeyondMax == 1 ? " was" : "s were")} dropped by the per-source cap. " : ". ");
        sb.Append("Every record removed at this stage is listed with its reason in the run ledger. ");
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
            sb.Append($"Each remaining record was screened twice by a language model ({model}, temperature 0) with two independent prompts that work through the criteria in a different order, using its title, authors, date, venue and abstract as returned by the source. ");
            sb.Append($"The two screenings agreed on {agreed} of {stats.DualScreened} records");
            sb.Append(stats.ScreeningKappa is double k ? $" (Cohen's kappa = {k.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}); " : "; ");
            sb.Append(stats.ScreeningDisagreements == 0
                ? "there were no disagreements to resolve. "
                : $"the {stats.ScreeningDisagreements} disagreement{(stats.ScreeningDisagreements == 1 ? " was" : "s were")} resolved by inclusion and flagged for human attention. ");
        }
        else
        {
            sb.Append($"Each remaining record was screened once by a language model ({model}, temperature 0) against the eligibility criteria, using its title, authors, date, venue and abstract as returned by the source. ");
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

    /// <summary>
    /// PRISMA items 9-11 (data collection, data items, study risk of bias): rendered from the extraction and
    /// MMAT appraisal the run actually produced, instead of a fixed statement.
    /// </summary>
    public static string DataAndAppraisal(IReadOnlyList<StudyExtraction> extractions, string? modelName = null)
    {
        if (extractions.Count == 0)
            return "No studies were included, so no data were extracted and no quality appraisal was carried out.";
        string model = string.IsNullOrWhiteSpace(modelName) ? ScreeningModelName : modelName;
        int failed = extractions.Count(e => e.Error != null);
        int fullText = extractions.Count(e => e.EvidenceBasis == "full text");
        var appraised = extractions.Where(e => e.Error == null && e.Appraisal.Count > 0).ToList();
        int notEmpirical = extractions.Count(e => e.Error == null && e.AppraisalCategory == "not_empirical");
        int answers = appraised.Sum(e => e.Appraisal.Count);
        int cantTell = appraised.Sum(e => e.Appraisal.Count(a => a.Answer == "cant_tell"));
        var values = extractions.Where(e => e.Error == null)
            .SelectMany(e => new[] { e.Method, e.Sample, e.Limitations }.Concat(e.KeyFindings))
            .Where(v => !v.Value.Equals("not reported", StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new StringBuilder();
        sb.Append($"For each included study, a language model ({model}, temperature 0) extracted the study type, method, sample, up to three key findings and the stated limitations ");
        sb.Append($"from the full text where it could be retrieved ({fullText} of {extractions.Count} studies) and otherwise from the abstract. ");
        sb.Append("Every extracted value had to come with a verbatim quote, which was checked against the paper's text in code");
        sb.Append(values.Count > 0 ? $"; {values.Count(v => v.QuoteVerified)} of {values.Count} quotes were found. " : ". ");
        sb.Append("Study quality was appraised with the Mixed Methods Appraisal Tool (MMAT, 2018): the model chose the study category and answered its five criteria with yes, no or can't tell, each backed by a quote; an answer whose quote could not be found in the text was changed to can't tell. ");
        sb.Append($"{appraised.Count} stud{(appraised.Count == 1 ? "y was" : "ies were")} appraised ({cantTell} of {answers} answers were can't tell)");
        sb.Append(notEmpirical > 0 ? $", and {notEmpirical} {(notEmpirical == 1 ? "was" : "were")} classified as non-empirical (for example position papers), to which MMAT does not apply. " : ". ");
        if (failed > 0) sb.Append($"Extraction failed for {failed} stud{(failed == 1 ? "y" : "ies")}. ");
        sb.Append("MMAT is not used to compute an overall score. The appraisal was done by a language model without a second human appraiser, so it should be checked before use (see extraction.json).");
        return sb.ToString();
    }

    /// <summary>PRISMA item 24 (registration and protocol).</summary>
    public static string Protocol(string? protocolSha256, DateTime createdUtc)
    {
        if (string.IsNullOrWhiteSpace(protocolSha256))
            return "No protocol was recorded for this run, and the review was not registered.";
        return $"The review was not registered. Its protocol (search query, objective, eligibility criteria, sources, limits and screening set-up) was written to protocol.md on {createdUtc:yyyy-MM-dd} (UTC) before any search was run, with SHA-256 fingerprint {protocolSha256[..16]}...; the search strings added by the language model are appended to it as a dated amendment. The file is part of the run archive.";
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
