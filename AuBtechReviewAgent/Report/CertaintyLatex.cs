using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// The certainty of the evidence in main.tex, drawn in code from <see cref="CertaintyFile"/>: the table of every
/// answer's rating with its concerns per domain and its reasons, and the phrase printed after each answer. Any kind
/// of review that rates its answers can use it.
/// </summary>
public static class CertaintyLatex
{
    private static string Short(string domain) => domain switch
    {
        EvidenceCertainty.Limitations => "Limitations",
        EvidenceCertainty.AbstractOnly => "Abstract only",
        EvidenceCertainty.Adequacy => "Adequacy",
        _ => domain,
    };

    private static string Concern(string concern) => concern switch
    {
        EvidenceCertainty.Serious => "serious",
        EvidenceCertainty.NoConcern => "none",
        _ => concern,
    };

    /// <summary>The concerns of one rating, one domain a line ("Abstract only: serious").</summary>
    public static IEnumerable<string> ConcernLines(CertaintyAssessment a) => a.Domains.Select(d => $"{Short(d.Domain)}: {Concern(d.Concern)}");

    /// <summary>The reasons of one rating: where it started, each serious concern, and the sensitivity check.</summary>
    public static IEnumerable<string> ReasonLines(CertaintyAssessment a)
    {
        if (a.NotRated != null) { yield return a.NotRated; yield break; }
        yield return $"Starts at {a.Start}: {a.StartReason}";
        foreach (var d in a.Domains.Where(d => d.Concern == EvidenceCertainty.Serious)) yield return $"{d.Domain}: {d.Reason}";
        yield return $"Sensitivity: {a.SensitivitySentence}";
    }

    /// <summary>
    /// The results in plain sentences, then the table "Certainty of the Evidence for Each Answer" with its caption and
    /// the label tab:certainty; LaTeX numbers it among the results' tables.
    /// </summary>
    public static string Table(CertaintyFile file)
    {
        static string E(string s) => PaperLatex.Escape(s);
        var sb = new StringBuilder();
        string summary = EvidenceCertainty.Summary(file.Answers, file.Rule);
        if (summary.Length > 0) sb.AppendLine(E(summary)).AppendLine();
        sb.AppendLine(@"\vspace{6pt}");
        sb.AppendLine(@"{\scriptsize");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.25\hsize}Y >{\hsize=1.3\hsize}Y >{\hsize=0.35\hsize}Y >{\hsize=0.9\hsize}Y >{\hsize=0.45\hsize}Y >{\hsize=1.75\hsize}Y}");
        sb.Append(PaperLatex.TableHead(@"\normalsize Certainty of the Evidence for Each Answer", "tab:certainty",
            @"\textbf{RQ} & \textbf{Answer in brief} & \textbf{Studies} & \textbf{Concerns} & \textbf{Certainty} & \textbf{Reasons}"));
        foreach (var a in file.Answers)
        {
            string studies = a.Sources.Count == 0 ? "0" : $"{a.Sources.Count} (\\cite{{{string.Join(",", a.Sources.Select(r => "ref" + r))}}})";
            string concerns = a.NotRated != null ? "--" : string.Join(@" \newline ", ConcernLines(a).Select(E));
            string reasons = string.Join(@" \newline ", ReasonLines(a).Select(E));
            sb.AppendLine($"RQ{a.Number} & {E(a.AnswerInBrief)} & {studies} & {concerns} & {E(a.Overall ?? "not rated")} & {reasons} \\\\ \\addlinespace");
        }
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}");
        sb.AppendLine(@"\vspace{2pt}\noindent " + E($"Rated in code by a rule inspired by GRADE-CERQual and GRADE; an automated approximation, not a formal assessment by people. Thresholds and reasons: {CertaintyFile.FileName}.") + "}");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }
}
