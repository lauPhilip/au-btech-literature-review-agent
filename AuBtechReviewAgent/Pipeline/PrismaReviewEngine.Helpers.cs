using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

public partial class PrismaReviewEngine
{
    /// <summary>
    /// For each gateway in SourceCatalog: can it be queried with the server keys plus the user's own keys?
    /// Used by the dashboard to grey out sources that have no key.
    /// </summary>
    public IReadOnlyDictionary<string, bool> GetSourceAvailability(UserApiKeys? userKeys)
    {
        var keys = ResolveKeys(userKeys);
        return SourceCatalog.All.ToDictionary(
            s => s.Key,
            s => s.KeyKind switch
            {
                SourceKeyKind.None => true,
                SourceKeyKind.Elsevier => SourceCatalog.IsUsableKey(keys.Elsevier),
                SourceKeyKind.Ieee => SourceCatalog.IsUsableKey(keys.Ieee),
                _ => false
            });
    }

    private (string Mistral, string Elsevier, string? Ieee, string? Scholar) ResolveKeys(UserApiKeys? userKeys) => (
        !string.IsNullOrWhiteSpace(userKeys?.MistralApiKey) ? userKeys.MistralApiKey : _globalMistralKey,
        !string.IsNullOrWhiteSpace(userKeys?.ElsevierApiKey) ? userKeys.ElsevierApiKey : _globalElsevierKey,
        !string.IsNullOrWhiteSpace(userKeys?.IeeeApiKey) ? userKeys.IeeeApiKey : _globalIeeeKey,
        !string.IsNullOrWhiteSpace(userKeys?.ScholarApiKey) ? userKeys.ScholarApiKey : _globalScholarKey);

    private string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        // Line breaks are removed so a crafted value cannot add fake lines to the log (log forging).
        string oneLine = message.Replace("\r", " ").Replace("\n", " ");
        return Regex.Replace(oneLine, @"([a-zA-Z0-9]{24,64})", "[REDACTED_API_CREDENTIAL]");
    }

    /// <summary>Lower-case title with punctuation and spacing removed, for duplicate detection.</summary>
    public static string NormalizeTitle(string? title) =>
        Regex.Replace(Regex.Replace((title ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " "), @"\s+", " ").Trim();

    /// <summary>Bare lower-case DOI ("10.x/y"), or null when there is no usable DOI.</summary>
    public static string? NormalizeDoi(string? doi)
    {
        if (string.IsNullOrWhiteSpace(doi)) return null;
        string d = Regex.Replace(doi.Trim(), @"^(https?://(dx\.)?doi\.org/|doi:)", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        return Regex.IsMatch(d, @"^10\.\d{4,9}/\S+$") ? d : null;
    }

    /// <summary>Chart category and platform label for a paper, from the prefix of its source id.</summary>
    public static (string Category, string Platform) SourceOfPaper(string paperId) => paperId switch
    {
        _ when paperId.StartsWith("SCOPUS_ID:") => ("ScienceDirect", "Scopus API"),
        _ when paperId.StartsWith("IEEE_") => ("IEEE", "IEEE Xplore API"),
        _ when paperId.StartsWith("OPENALEX_") => ("OpenAlex", "OpenAlex API"),
        _ when paperId.StartsWith("S2_") => ("SemanticScholar", "Semantic Scholar API"),
        _ when paperId.StartsWith("CROSSREF_") => ("Crossref", "Crossref API"),
        _ when paperId.StartsWith("SCHOLAR_") => ("Scholar", "Google Scholar API"),      // runs from before OpenAlex
        _ when paperId.StartsWith("RG_") => ("ResearchGate", "ResearchGate API"),         // runs from before OpenAlex
        _ => ("Arxiv", "arXiv API"),
    };

    public static string CategoryLabel(string category) => category switch
    {
        "Arxiv" => "arXiv",
        "ScienceDirect" => "ScienceDirect",
        "IEEE" => "IEEE Xplore",
        "SemanticScholar" => "Semantic Scholar",
        "Scholar" => "Google Scholar",
        _ => category,
    };

    private static string ClassifyVenueFromCitation(string? citation)
    {
        string c = citation ?? "";
        if (c.Contains("Proceedings of", StringComparison.OrdinalIgnoreCase) || c.Contains("Conference", StringComparison.OrdinalIgnoreCase)) return "Conferences";
        if (c.Contains("Transactions on", StringComparison.OrdinalIgnoreCase)) return "Transactions";
        if (c.Contains("Journal of", StringComparison.OrdinalIgnoreCase)) return "Journals";
        return "Preprints";
    }

    private static int ExtractYearFromCitation(string? citation)
    {
        if (string.IsNullOrEmpty(citation)) return 2026;
        var yearMatch = Regex.Match(citation, @"\((20\d{2})\)");
        return yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int yr) ? yr : 2026;
    }
}
