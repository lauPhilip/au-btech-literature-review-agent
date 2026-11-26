using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>The tables of the systematic review's main.tex: the evidence map, the artifact and the appendix.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>What the declarations say for a run made before protocols were written, on the report page and in main.tex.</summary>
    public const string NoProtocolText = "No protocol was recorded for this run (it was made before protocols were written).";

    /// <summary>The key to Table A.3, printed in the note under the table.</summary>
    public const string AppraisalTableKey =
        "Columns 1 to 5 are the five MMAT 2018 criteria for the study's category, answered Yes, No or ? (can't tell). Met is the number answered Yes; it is not an overall score.";

    /// <summary>
    /// The tables of the included studies, in the appendix after the references as Tables A.1 to A.3 (issue #139).
    /// Section 3 keeps its text, flow diagram and charts and points to them (AppendixPointer).
    /// </summary>
    private string AppendixLatex(List<IncludedPaperMetricRow> records, ReviewState? state)
    {
        var ab = new StringBuilder();
        ab.AppendLine(@"\small");
        ab.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.6\hsize}X >{\hsize=1.2\hsize}X >{\hsize=1.0\hsize}X >{\hsize=0.5\hsize}X >{\hsize=1.7\hsize}X}");
        // One line break, as in A.2 and A.3: a second one added an empty row under the title (issue #145).
        ab.AppendLine(@"\multicolumn{5}{l}{\textbf{Table A.1: Included studies}} \\");
        ab.AppendLine(@"\toprule");
        ab.AppendLine(@"\textbf{Paper Name} & \textbf{Executive Summary} & \textbf{Inclusion Rationale} & \textbf{Source} & \textbf{Citation (APA 7th)} \\");
        ab.AppendLine(@"\midrule");
        foreach (var row in records)
        {
            string title = EscapeLatexText(row.Title);
            string summary = EscapeLatexText(row.Summary);
            string rationale = EscapeLatexText(row.InclusionRationale);
            // The database by its display name, as on the paper page (issue #133).
            string source = EscapeLatexText(CategoryLabel(row.Category) + "; " + VenueRank(row));
            string citation = EscapeLatexText(row.ApaCitation);
            ab.AppendLine($"{title} & {summary} & {rationale} & {source} & {citation} \\\\ \\hline");
        }
        ab.AppendLine(@"\end{tabularx}");
        ab.AppendLine(@"\vspace{4pt}\noindent\small ");
        ab.AppendLine(@"Table A.1 lists each included study with a short summary, the reason it was included, the database it came from with its venue's rank, and its reference.");
        ab.AppendLine(EscapeLatexText(VenueRankNote));
        int nextTable = 2;
        if (state?.Extractions is { Count: > 0 } extractions)
        {
            ab.AppendLine(@"\vspace{10pt}");
            ab.Append(ExtractionTables(extractions));
            nextTable += extractions.Any(MethodsSectionWriter.ShowInAppraisalTable) ? 2 : 1;
        }
        if (state?.SeedChaining is { Seeds.Count: > 0 } seeds)
        {
            if (nextTable == 2) ab.AppendLine(@"\vspace{10pt}"); // the extraction tables end with this space already
            ab.Append(SeedStudiesTable.Latex(seeds, $"A.{nextTable}", EscapeLatexText));
        }
        return ab.ToString();
    }

    /// <summary>The note under Table A.2 when it lists a secondary study.</summary>
    public const string SecondaryNote =
        "A secondary study is a review of other studies (a systematic, scoping or literature review, a meta-analysis or a literature survey). It summarises the work of others and adds no new evidence, so an answer that rests on it is marked.";

    /// <summary>The note under Table A.1 on the venue ranks.</summary>
    public const string VenueRankNote =
        "The rank is the venue's SCImago Journal Rank (SJR) quartile, matched on the exact title. A venue marked not in SJR was not found in the SJR data: it may be a conference, a preprint server or an unlisted journal, and its standing was not checked. The Directory of Open Access Journals (DOAJ) was not checked.";

    /// <summary>The venue's SJR quartile as Table A.1 prints it ("SJR Q1"), or "not in SJR" (a preprint says so).</summary>
    public static string VenueRank(IncludedPaperMetricRow row) =>
        System.Text.RegularExpressions.Regex.IsMatch(row.Quartile ?? "", "^Q[1-4]$") ? $"SJR {row.Quartile}"
            : row.VenueType == "Preprints" ? "preprint, not in SJR" : "not in SJR";

    /// <summary>The artifact as a TikZ figure, a table or a list, outside the two-column layout.</summary>
    private string ArtifactLatex(ReviewArtifact artifact)
    {
        var sb = new StringBuilder();
        string title = EscapeLatexText(string.IsNullOrWhiteSpace(artifact.Title) ? "Artifact" : artifact.Title);
        sb.AppendLine(@"\vspace{6pt}");
        if (artifact.IsDiagram)
        {
            sb.AppendLine(@"\begin{figure}[H]");
            sb.AppendLine(@"\centering");
            sb.AppendLine(@"\resizebox{\linewidth}{!}{");
            sb.Append(ArtifactBuilder.ToTikz(artifact, EscapeLatexText));
            sb.AppendLine(@"}");
            sb.AppendLine($"\\caption{{{title}. {EscapeLatexText(artifact.Caption)}}}");
            sb.AppendLine(@"\end{figure}");
        }
        else if (artifact.Kind == ArtifactKinds.Table && artifact.Columns.Count > 0)
        {
            string spec = string.Join(" ", artifact.Columns.Select(_ => "X"));
            sb.AppendLine($"\\begin{{tabularx}}{{\\textwidth}}{{{spec}}}");
            sb.AppendLine($"\\multicolumn{{{artifact.Columns.Count}}}{{l}}{{\\textbf{{Table 4.2: {title}}}}} \\\\");
            sb.AppendLine(@"\toprule");
            sb.AppendLine(string.Join(" & ", artifact.Columns.Select(c => $"\\textbf{{{EscapeLatexText(c)}}}")) + @" \\");
            sb.AppendLine(@"\midrule");
            foreach (var row in artifact.Rows)
                sb.AppendLine(string.Join(" & ", row.Select(EscapeLatexText)) + @" \\ \hline");
            sb.AppendLine(@"\end{tabularx}");
            if (!string.IsNullOrWhiteSpace(artifact.Caption))
                sb.AppendLine(@"\vspace{2pt}\noindent\small " + EscapeLatexText(artifact.Caption) + @"\normalsize");
        }
        else
        {
            sb.AppendLine($"\\noindent\\textbf{{{title}}}");
            if (!string.IsNullOrWhiteSpace(artifact.Caption)) sb.AppendLine(@"\par\noindent " + EscapeLatexText(artifact.Caption));
            sb.AppendLine(@"\begin{itemize}");
            foreach (var bullet in artifact.Bullets) sb.AppendLine(@"\item " + EscapeLatexText(bullet));
            sb.AppendLine(@"\end{itemize}");
        }
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    /// <summary>Evidence map: which studies contribute to which theme of the thematic synthesis.</summary>
    private string EvidenceMapTable(ThematicCodebook book)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"\vspace{6pt}");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.9\hsize}X >{\hsize=1.4\hsize}X >{\hsize=0.7\hsize}X}");
        sb.AppendLine(@"\multicolumn{3}{l}{\textbf{Table 4.1: Evidence Map of the Thematic Synthesis}} \\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Theme} & \textbf{Description} & \textbf{Studies} \\");
        sb.AppendLine(@"\midrule");
        foreach (var t in book.Themes)
            sb.AppendLine($"{EscapeLatexText(t.Name)} & {EscapeLatexText(t.Description)} & {string.Join(", ", t.Studies.Select(r => $"{{[}}{r}{{]}}"))} \\\\ \\hline");
        sb.AppendLine(@"\end{tabularx}");
        var notes = new List<string>();
        if (book.SecondCoding?.Kappa is double k)
            notes.Add($"Second coding by a second prompt of the same model: {Agreement.Figures(book.SecondCoding.Agreements, book.SecondCoding.CellsCompared, k)} study-theme decisions; this measures robustness to the prompt, not inter-rater reliability.");
        if (book.Uncoded.Count > 0)
            notes.Add("Not placed in a theme: " + string.Join("; ", book.Uncoded.Select(u => $"[{u.Reference}] {u.Reason.TrimEnd('.')}")) + ".");
        if (notes.Count > 0)
            sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize " + EscapeLatexText(string.Join(" ", notes)) + @"\normalsize");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }

    /// <summary>Table A.2 (extracted data, with a mark where the supporting quote was found) and Table A.3 (MMAT), in the appendix.</summary>
    /// <remarks>The appendix is set in \small; each note returns to \small, not \normalsize, so A.3 matches A.1 and A.2.</remarks>
    private string ExtractionTables(IReadOnlyList<StudyExtraction> extractions)
    {
        static bool Unverified(EvidencedValue v) => !v.QuoteVerified && !v.Value.Equals("not reported", StringComparison.OrdinalIgnoreCase);
        static bool HasUnverifiedValue(StudyExtraction e) => e.Error == null && new[] { e.Method, e.Sample }.Concat(e.KeyFindings).Any(Unverified);
        string V(EvidencedValue v) => EscapeLatexText(v.Value) + (Unverified(v) ? "$^{\\dagger}$" : "");
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.25\hsize}X >{\hsize=0.8\hsize}X >{\hsize=1.1\hsize}X >{\hsize=0.9\hsize}X >{\hsize=1.95\hsize}X}");
        sb.AppendLine(@"\multicolumn{5}{l}{\textbf{Table A.2: Extracted Study Data}} \\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Ref.} & \textbf{Study type} & \textbf{Method} & \textbf{Sample} & \textbf{Key findings} \\");
        sb.AppendLine(@"\midrule");
        foreach (var e in extractions)
        {
            if (e.Error != null)
            {
                sb.AppendLine($"{{[}}{e.ReferenceNumber}{{]}} & \\multicolumn{{4}}{{l}}{{Extraction failed.}} \\\\ \\hline");
                continue;
            }
            string findings = string.Join(" ", e.KeyFindings.Select((f, i) => $"({i + 1}) {V(f)}"));
            string type = SecondaryStudy.KindOf(e) is { } kind ? $"{e.StudyType} (secondary study: {kind})" : e.StudyType;
            sb.AppendLine($"{{[}}{e.ReferenceNumber}{{]}} & {EscapeLatexText(type)} & {V(e.Method)} & {V(e.Sample)} & {findings} \\\\ \\hline");
        }
        sb.AppendLine(@"\end{tabularx}");
        // The note explains the dagger only when a value in the table carries one (issue #168).
        if (extractions.Any(HasUnverifiedValue))
            sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize $^{\dagger}$ The supporting quote given by the model was not found in the paper's text; treat the value with caution.\small");
        if (extractions.Any(e => SecondaryStudy.KindOf(e) != null))
            sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize " + EscapeLatexText(SecondaryNote) + @"\small");
        sb.AppendLine(@"\vspace{10pt}");

        string A(string answer) => answer switch { "yes" => "Yes", "no" => "No", _ => "?" };
        var appraised = extractions.Where(MethodsSectionWriter.ShowInAppraisalTable).ToList();
        if (appraised.Count > 0)
        {
            sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.4\hsize}X >{\hsize=1.6\hsize}X c c c c c c}");
            // A short title: the long one with the key ran 154 pt past the text block and squeezed the Ref and
            // Category columns. The key is in the note under the table (issue #145).
            sb.AppendLine(@"\multicolumn{8}{l}{\textbf{Table A.3: Quality Appraisal (MMAT 2018)}} \\");
            sb.AppendLine(@"\toprule");
            sb.AppendLine(@"\textbf{Ref.} & \textbf{Category} & \textbf{1} & \textbf{2} & \textbf{3} & \textbf{4} & \textbf{5} & \textbf{Met} \\");
            sb.AppendLine(@"\midrule");
            foreach (var e in appraised)
            {
                string category = e.Error != null ? "not appraised" : e.AppraisalCategory.Replace('_', ' ');
                var cells = Enumerable.Range(0, 5).Select(i => i < e.Appraisal.Count ? A(e.Appraisal[i].Answer) : "--");
                string met = e.Appraisal.Count == 0 ? "--" : $"{e.AppraisalYes}/{e.Appraisal.Count}";
                sb.AppendLine($"{{[}}{e.ReferenceNumber}{{]}} & {EscapeLatexText(category)} & {string.Join(" & ", cells)} & {met} \\\\");
            }
            sb.AppendLine(@"\bottomrule");
            sb.AppendLine(@"\end{tabularx}");
        }
        string notAppraised = MethodsSectionWriter.NotAppraisedNote(extractions, "--");
        string note = string.Join(" ", new[] { appraised.Count > 0 ? AppraisalTableKey : "", notAppraised }.Where(n => n.Length > 0));
        if (note.Length > 0)
            sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize " + EscapeLatexText(note.TrimEnd('.')) + @".\small");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }
}
