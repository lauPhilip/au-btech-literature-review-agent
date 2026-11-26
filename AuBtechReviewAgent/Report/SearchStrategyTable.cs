using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// The full search strategy of a systematic review as an appendix table (PRISMA 2020 item 7; PRISMA-S items 8, 13
/// and 15): every search the run sent, by source, with the search string, how the source's interface received it,
/// when it was run, how many records came back and the file holding the source's raw answer. Written in code from the
/// run's search log, never by the model.
/// </summary>
public static class SearchStrategyTable
{
    /// <summary>
    /// The request parameter each source adapter puts the search string in (Sources/). The search string itself is sent
    /// as written, with no field tags, Boolean operators or filters added. SearchStrategyTableTests checks each adapter
    /// still builds its request this way.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SentAs = new Dictionary<string, string>
    {
        ["arXiv API"] = "search_query=all:",
        ["ScienceDirect API"] = "query=ALL(...)",
        ["IEEE Xplore API"] = "querytext=",
        ["OpenAlex"] = "search=",
        ["Semantic Scholar"] = "query=",
        ["Crossref"] = "query.bibliographic=",
    };

    /// <summary>The table, or an empty string when the run has no search log.</summary>
    /// <param name="state">The run's ledger.</param>
    /// <param name="tableName">The table's name in the appendix, such as "A.4".</param>
    /// <param name="escape">Escapes text for LaTeX; search strings come from the reviewer or the model, so they are data.</param>
    public static string Latex(ReviewState state, string tableName, Func<string, string> escape)
    {
        if (state.SearchLogs.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.8\hsize}X >{\hsize=1.5\hsize}X >{\hsize=0.8\hsize}X >{\hsize=0.9\hsize}X c >{\hsize=1.0\hsize}X}");
        sb.AppendLine($"\\multicolumn{{6}}{{l}}{{\\textbf{{Table {tableName}: Search Strategy per Source}}}} \\\\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Source} & \textbf{Search string} & \textbf{Sent as} & \textbf{Searched (UTC)} & \textbf{Records} & \textbf{Raw answer} \\");
        sb.AppendLine(@"\midrule");
        foreach (var log in state.SearchLogs)
        {
            string sentAs = SentAs.TryGetValue(log.SourceName, out var parameter) ? escape(parameter) : "--";
            string when = log.FromCache ? escape(log.Timestamp) + " (cached answer)" : escape(log.Timestamp);
            string records = log.Status.StartsWith("Completed", StringComparison.Ordinal) ? log.PapersFound.ToString() : "failed";
            string raw = string.IsNullOrWhiteSpace(log.RawResponseFile) ? "--" : escape(log.RawResponseFile);
            sb.AppendLine($"{escape(log.SourceName)} & {escape(log.QueryUsed)} & {sentAs} & {when} & {records} & {raw} \\\\ \\hline");
        }
        sb.AppendLine(@"\end{tabularx}");
        sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize " + escape(Note(state)) + @"\small");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    /// <summary>The note under the table: the limits applied in the request and after it, and the strings not sent.</summary>
    public static string Note(ReviewState state)
    {
        // The same split as the search (PrismaReviewEngine.Search.cs): the per-source limit shared across the strings.
        int strings = Math.Max(1, state.SearchPerspectives.Count);
        int perRequest = Math.Max(1, (int)Math.Ceiling(state.MaxResultsPerSource / (double)strings));
        var parts = new List<string>
        {
            $"Each search string was sent as written in the request parameter shown under Sent as, asking for at most {perRequest} record{(perRequest == 1 ? "" : "s")} (the limit of {state.MaxResultsPerSource} per source shared across {strings} search string{(strings == 1 ? "" : "s")}); no field tags, Boolean operators or search filters were added.",
        };
        parts.Add(state.YearFrom > 0 || state.YearTo > 0
            ? $"The publication-year limit ({YearRange(state)}) was applied in code to the records returned, not in the request."
            : "No publication-year limit was applied.");
        parts.Add(state.PeerReviewOnlyToggle
            ? "The peer-reviewed-only filter was applied in code to the records returned."
            : "No peer-review, language or document-type limit was applied.");
        var notSent = state.SearchStringYields.Where(y => y.Searches == 0).Select(y => $"\"{y.SearchString}\"").ToList();
        if (notSent.Count > 0)
            parts.Add($"Not sent to any source, because the limit per source was reached first: {string.Join("; ", notSent)}.");
        if (state.SearchLogs.Any(l => l.FromCache))
            parts.Add("A cached answer was reused from an earlier identical search; the date shown is when this run used it.");
        parts.Add("Each raw answer is in the run archive under the name shown, with its SHA-256 fingerprint in transparent-process.json.");
        return string.Join(" ", parts);
    }

    private static string YearRange(ReviewState state) =>
        state.YearFrom > 0 && state.YearTo > 0 ? $"{state.YearFrom}–{state.YearTo}"
        : state.YearFrom > 0 ? $"from {state.YearFrom}" : $"up to {state.YearTo}";
}
