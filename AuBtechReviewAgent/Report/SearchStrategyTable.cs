using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// The full search strategy per source (PRISMA 2020 item 7), as an appendix table written in code from the run's search
/// log: every search string as it was sent to each database, the day, the records the database returned and whether the
/// search worked. The note under the table says how each database's API receives the string and which limits applied.
/// Citation chaining is summarised in the note, since its look-ups are by DOI rather than by search string.
/// </summary>
public static class SearchStrategyTable
{
    /// <summary>
    /// How each source's API receives the search string, keyed by the source name in the search log. Written from the
    /// request each class in Sources/ builds; a new source without an entry is described as receiving the string as it is.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> HowSent = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["arXiv API"] = "arXiv searched all fields for the string (search_query=all:)",
        ["ScienceDirect API"] = "Scopus searched all fields for the words of the string joined with AND (query=ALL())",
        ["IEEE Xplore API"] = "IEEE Xplore received the string as its query text (querytext)",
        ["OpenAlex"] = "OpenAlex received the string as its search parameter (search)",
        ["Semantic Scholar"] = "Semantic Scholar received the string as its relevance search (query)",
        ["Crossref"] = "Crossref received the string as a bibliographic query (query.bibliographic), limited to journal articles, proceedings articles and posted content",
    };

    /// <summary>True for a log entry of citation chaining rather than a database search.</summary>
    public static bool IsChaining(PlatformSearchLog log) =>
        log.SourceName.EndsWith("(citation chaining)", StringComparison.Ordinal) || log.SourceName.EndsWith("(seed chaining)", StringComparison.Ordinal);

    /// <summary>The table as LaTeX, with the label <paramref name="label"/> (for example "tab:A.4"); empty when no database was searched.</summary>
    /// <param name="logs">The run's search log, in the order the searches ran.</param>
    /// <param name="maxResultsPerSource">The per-source cap of records that go on to screening.</param>
    /// <param name="searchStrings">How many search strings the run used.</param>
    /// <param name="yearFrom">The first year of the year range, or 0 when there was none.</param>
    /// <param name="yearTo">The last year of the year range, or 0 when there was none.</param>
    /// <param name="peerReviewOnly">Whether the peer-reviewed-only filter was on.</param>
    public static string Latex(IReadOnlyList<PlatformSearchLog> logs, string label, Func<string, string> escape,
        int maxResultsPerSource, int searchStrings, int yearFrom = 0, int yearTo = 0, bool peerReviewOnly = false)
    {
        var searches = logs.Where(l => !IsChaining(l)).ToList();
        if (searches.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.6\hsize}Y >{\hsize=1.9\hsize}Y c r >{\hsize=0.5\hsize}Y}");
        sb.Append(PaperLatex.TableHead("Search strategy per source", label, @"\textbf{Source} & \textbf{Search string as sent} & \textbf{Searched} & \textbf{Records} & \textbf{Status}"));
        foreach (var log in searches)
            sb.AppendLine($"{escape(log.SourceName)} & {escape(log.QueryUsed)} & {Day(log.Timestamp)} & {log.PapersFound.ToString(CultureInfo.InvariantCulture)} & {Status(log)}\\\\ \\addlinespace");
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}");
        sb.AppendLine(@"\vspace{2pt}\noindent{\scriptsize " + escape(Note(logs, maxResultsPerSource, searchStrings, yearFrom, yearTo, peerReviewOnly)) + @"\par}");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    /// <summary>The note under the table: how each source received the string, the limits, chaining and the saved answers.</summary>
    public static string Note(IReadOnlyList<PlatformSearchLog> logs, int maxResultsPerSource, int searchStrings, int yearFrom = 0, int yearTo = 0, bool peerReviewOnly = false)
    {
        var inv = CultureInfo.InvariantCulture;
        var searches = logs.Where(l => !IsChaining(l)).ToList();
        var parts = new List<string>();
        foreach (string source in searches.Select(l => l.SourceName).Distinct())
            parts.Add((HowSent.TryGetValue(source, out var how) ? how : $"{source} received the string as it is") + ". ");
        if (maxResultsPerSource > 0)
        {
            int strings = Math.Max(1, searchStrings);
            int perString = Math.Max(1, (int)Math.Ceiling(maxResultsPerSource / (double)strings));
            parts.Add(string.Create(inv, $"Each search asked for at most {perString} record{(perString == 1 ? "" : "s")}, and a source was not searched again once it had returned {maxResultsPerSource} distinct records. "));
        }
        parts.Add(yearFrom > 0 && yearTo > 0
            ? string.Create(inv, $"No filter was sent to the databases; records published outside {yearFrom}-{yearTo} were removed after retrieval. ")
            : "No date, language or document-type filter was sent to the databases. ");
        if (peerReviewOnly) parts.Add("Records classified as not peer reviewed were removed before screening. ");
        if (searches.Any(l => l.FromCache)) parts.Add("Reused marks an answer taken from an earlier identical search. ");
        var chaining = logs.Where(IsChaining).ToList();
        if (chaining.Count > 0)
            parts.Add(string.Create(inv, $"Citation chaining looked up the references of and citations to {chaining.Count} included record{(chaining.Count == 1 ? "" : "s")} by DOI in OpenAlex ({chaining.Sum(l => l.PapersFound)} records returned); each look-up is in the run ledger. "));
        if (searches.Any(l => !string.IsNullOrEmpty(l.RawResponseFile)))
            parts.Add("The raw answer of every search is in SourceResponses/ in the run archive, with its SHA-256 fingerprint in the run ledger.");
        return string.Concat(parts).Trim();
    }

    private static string Day(string timestamp) =>
        timestamp.Length >= 10 && DateTime.TryParseExact(timestamp[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? timestamp[..10] : "--";

    private static string Status(PlatformSearchLog log)
    {
        string status = log.Status.StartsWith("Completed", StringComparison.OrdinalIgnoreCase) ? "Completed" : "Failed";
        return log.FromCache ? status + " (reused)" : status;
    }
}
