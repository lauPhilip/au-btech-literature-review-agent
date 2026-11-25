using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>The methods items about the included studies (9-11, 23c, 24), the included set and the notes on the MMAT table.</summary>
public static partial class MethodsSectionWriter
{
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
        sb.Append($"For each included study, a language model {ModelInBrackets(model, "temperature 0")} extracted the study type, method, sample, up to three key findings and the stated limitations ");
        sb.Append($"from the full text where it could be retrieved ({fullText} of {extractions.Count} studies) and otherwise from the abstract. ");
        if (extractions.Any(e => e.KeyFindings.Any(f => f.Question != null)))
            sb.Append($"The passages were chosen with the words of the research questions weighted above the generic ones, and each key finding names the research question it bears on (checked in code against the questions); up to {StudyExtractor.ExtraFindings} further findings were kept for a study only when they answered a question none of its first three answered. ");
        int reused = extractions.Count(e => e.FromCache == true);
        if (reused > 0)
            sb.Append($"{reused} extraction{(reused == 1 ? " was" : "s were")} reused from an earlier identical one (same text, research questions, model and prompt version), marked in extraction.json. ");
        sb.Append("Every extracted value had to come with a verbatim quote, which was checked against the paper's text in code");
        sb.Append(values.Count > 0 ? $"; {values.Count(v => v.QuoteVerified)} of {values.Count} quotes were found. " : ". ");
        sb.Append("Study quality was appraised with the Mixed Methods Appraisal Tool (MMAT, 2018): the model chose the study category and answered its five criteria with yes, no or can't tell, each backed by a quote; an answer whose quote could not be found in the text was changed to can't tell. ");
        sb.Append($"{appraised.Count} stud{(appraised.Count == 1 ? "y was" : "ies were")} appraised ({cantTell} of {answers} answers were can't tell)");
        sb.Append(notEmpirical > 0 ? $", and {notEmpirical} {(notEmpirical == 1 ? "was" : "were")} classified as non-empirical (for example position papers), to which MMAT does not apply. " : ". ");
        int noText = extractions.Count(e => e.AppraisalCategory == StudyExtractor.NotAppraised);
        if (noText > 0)
            sb.Append($"{noText} stud{(noText == 1 ? "y had" : "ies had")} neither an abstract nor a full text, so nothing was extracted from {(noText == 1 ? "it and it was" : "them and they were")} not appraised. ");
        if (failed > 0) sb.Append($"Extraction failed for {failed} stud{(failed == 1 ? "y" : "ies")}. ");
        sb.Append("MMAT is not used to compute an overall score. The appraisal was done by a language model without a second human appraiser, so it should be checked before use (see extraction.json).");
        return sb.ToString();
    }

    /// <summary>
    /// The limitations of the review processes (PRISMA 2020 item 23c), written from what the run did: who screened, whether
    /// a person checked the decisions, how many included studies were read from their abstracts only, the search limits and
    /// the sources that could not be searched. The model never writes this: it describes the method (AGENTS.md rule 2).
    /// </summary>
    public static string ReviewProcessLimitations(ReviewState state, string screeningModel, string writingModel)
    {
        var s = state.Stats;
        var sb = new StringBuilder("This review has limits of its own. ");
        sb.Append($"The records were screened by the language model {screeningModel}");
        if (state.DualScreeningRequested && s.DualScreened > 0) sb.Append(", twice with independent prompts");
        sb.Append(state.HumanScreeningReviewRequested && s.HumanReviewed > 0
            ? $", and a human reviewer checked {s.HumanReviewed} of the decisions. "
            : ", and no human reviewer checked the decisions within the tool. ");
        if (s.Included > 0)
        {
            int abstractOnly = Math.Clamp(s.Included - s.FullTextRetrieved, 0, s.Included);
            sb.Append(abstractOnly == 0
                ? (s.Included == 1 ? "The included study was read in full text. " : $"All {s.Included} included studies were read in full text. ")
                : abstractOnly == s.Included
                    ? (s.Included == 1 ? "No open-access full text was found for the included study, so it was read from its abstract only. "
                                       : $"No open-access full text was found for any of the {s.Included} included studies, so all were read from their abstracts only. ")
                    : $"Of the {s.Included} included studies, {abstractOnly} {(abstractOnly == 1 ? "was" : "were")} read from {(abstractOnly == 1 ? "its abstract" : "their abstracts")} only, because no open-access full text was found. ");
        }
        if (state.MaxResultsPerSource > 0)
            sb.Append($"The search took at most {state.MaxResultsPerSource} record{(state.MaxResultsPerSource == 1 ? "" : "s")} per source");
        else
            sb.Append("The search covered the selected sources");
        if (state.YearFrom > 0 && state.YearTo > 0) sb.Append($", published from {state.YearFrom} to {state.YearTo}");
        sb.Append(". ");
        var unavailable = state.UnavailableSources.Select(SourceCatalog.DisplayNameFor).ToList();
        if (unavailable.Count > 0)
            sb.Append($"{string.Join(" and ", unavailable)} could not be searched. ");
        sb.Append($"The extraction, the synthesis and the text were written by the language model {writingModel}; every cited sentence was checked against its source, but the review should be read by a person before it is relied on.");
        return sb.ToString();
    }

    /// <summary>
    /// One paragraph describing the included set (years, sources, venue types), built only from the run's
    /// records. Used by the Review Output page and main.tex, replacing a fixed sentence that claimed trends.
    /// </summary>
    public static string IncludedSet(IReadOnlyList<IncludedPaperMetricRow> records)
    {
        if (records.Count == 0) return "No papers were included, so there is no data to describe.";
        var years = records.Where(r => r.Year > 0).Select(r => r.Year).ToList();
        int noYear = records.Count - years.Count;
        string span = years.Count == 0 ? "None of the included papers has a publication year in its metadata."
            : years.Min() == years.Max() ? $"All dated papers were published in {years.Min()}."
            : $"The {records.Count} included papers were published between {years.Min()} and {years.Max()}, most of them in {years.GroupBy(y => y).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key}.";
        if (noYear > 0) span += $" {noYear} ha{(noYear == 1 ? "s" : "ve")} no year in the source metadata.";
        string sources = string.Join(", ", records
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "Other" : r.Category)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} from {PrismaReviewEngine.CategoryLabel(g.Key)}"));
        int journals = records.Count(r => r.VenueType == "Journals");
        int transactions = records.Count(r => r.VenueType == "Transactions");
        int conferences = records.Count(r => r.VenueType is "Conferences" or "Proceedings");
        int other = records.Count - journals - transactions - conferences;
        return $"{span} By the source that first returned them: {sources}. " +
               $"By venue type: {journals} journal, {transactions} transactions, {conferences} conference and {other} preprint or unknown.";
    }

    /// <summary>
    /// The sentences in section 3 that point to the tables in the appendix (issue #139), so the results still lead
    /// the reader to the study characteristics and the risk of bias (PRISMA 2020 items 17 and 18). Table A.2 is
    /// named only when data was extracted, and Table A.3 only when at least one study has a row in the MMAT table.
    /// Empty when no study was included.
    /// </summary>
    public static string AppendixPointer(int includedStudies, IReadOnlyCollection<StudyExtraction>? extractions)
    {
        if (includedStudies == 0) return "";
        string text = "Table A.1 in the appendix lists each included study.";
        if (extractions is not { Count: > 0 }) return text;
        return text + (extractions.Any(ShowInAppraisalTable)
            ? " Table A.2 gives the data extracted from each study, and Table A.3 the quality appraisal of each study with MMAT 2018."
            : " Table A.2 gives the data extracted from each study.");
    }

    /// <summary>
    /// Removes Markdown emphasis the model sometimes writes (**bold**, *italic*), which would otherwise show
    /// as literal asterisks on the page and break the LaTeX. The words are kept.
    /// </summary>
    public static string StripMarkdownEmphasis(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string t = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "$1", System.Text.RegularExpressions.RegexOptions.Singleline);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"__(.+?)__", "$1");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<![\w*])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![\w*])", "$1");
        return t;
    }

    /// <summary>
    /// PRISMA item 24 (registration and protocol). The list of what the protocol holds names the research questions
    /// when the run has them (issue #121).
    /// </summary>
    public static string Protocol(string? protocolSha256, DateTime createdUtc, bool hasResearchQuestions = false)
    {
        if (string.IsNullOrWhiteSpace(protocolSha256))
            return "No protocol was recorded for this run, and the review was not registered.";
        string questions = hasResearchQuestions ? "research questions, " : "";
        return $"The review was not registered. Its protocol (search query, objective, {questions}eligibility criteria, sources, limits and screening set-up) was written to protocol.md on {createdUtc:yyyy-MM-dd} (UTC) before any search was run, with SHA-256 fingerprint {protocolSha256[..16]}...; the extra search strings are appended to it as a dated amendment. The file is part of the run archive.";
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

    /// <summary>
    /// Whether a study gets a row in the MMAT table. Studies classified as non-empirical have no MMAT answers,
    /// so they are left out of the table and named in <see cref="NotAppraisedNote"/> instead; studies whose
    /// extraction failed stay in the table, because that failure is something a reader should see.
    /// </summary>
    public static bool ShowInAppraisalTable(StudyExtraction e) =>
        e.Error != null || (e.AppraisalCategory != "not_empirical" && e.AppraisalCategory != StudyExtractor.NotAppraised);

    /// <summary>
    /// The note naming the studies left out of the MMAT table, e.g. "[1], [4]-[5] and [10] were classified
    /// as non-empirical ...", with a second sentence for studies that had no text to appraise. Empty when every
    /// study is in the table.
    /// </summary>
    public static string NotAppraisedNote(IEnumerable<StudyExtraction> extractions, string rangeDash = "\u2013")
    {
        var left = extractions.Where(e => !ShowInAppraisalTable(e)).ToList();
        var nonEmpirical = left.Where(e => e.AppraisalCategory == "not_empirical").Select(e => e.ReferenceNumber).Distinct().OrderBy(n => n).ToList();
        var noText = left.Where(e => e.AppraisalCategory == StudyExtractor.NotAppraised).Select(e => e.ReferenceNumber).Distinct().OrderBy(n => n).ToList();
        var sentences = new List<string>();
        if (nonEmpirical.Count > 0)
            sentences.Add($"{Studies(nonEmpirical, rangeDash)} {(nonEmpirical.Count == 1 ? "was" : "were")} classified as non-empirical (for example reviews or position papers), to which MMAT does not apply.");
        // Issue #148: a study with only a title is not called non-empirical; it was not appraised at all.
        if (noText.Count > 0)
            sentences.Add($"{Studies(noText, rangeDash)} {(noText.Count == 1 ? "was" : "were")} not appraised, because neither an abstract nor a full text was available.");
        return sentences.Count == 0 ? "" : "Not shown: " + string.Join(" ", sentences);
    }

    /// <summary>"9 studies ([1], [4], [5] and [13]-[16])".</summary>
    private static string Studies(List<int> refs, string rangeDash)
    {
        var parts = new List<string>();
        for (int i = 0; i < refs.Count; i++)
        {
            int start = refs[i];
            while (i + 1 < refs.Count && refs[i + 1] == refs[i] + 1) i++;
            int end = refs[i];
            // Two neighbours are two items, so the list ends "[12] and [13]", never "and [12], [13]" (issue #131).
            if (end == start + 1) parts.AddRange(new[] { $"[{start}]", $"[{end}]" });
            else parts.Add(end == start ? $"[{start}]" : $"[{start}]{rangeDash}[{end}]");
        }
        string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return $"{refs.Count} {(refs.Count == 1 ? "study" : "studies")} ({list})";
    }
}
