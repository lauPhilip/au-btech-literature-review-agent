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
        // Ragged-right columns (Y): justified text in a narrow column left gaps between words.
        ab.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.6\hsize}Y >{\hsize=1.2\hsize}Y >{\hsize=1.0\hsize}Y >{\hsize=0.5\hsize}Y >{\hsize=1.7\hsize}Y}");
        ab.Append(PaperLatex.TableHead("Included studies", "tab:A.1", @"\textbf{Paper Name} & \textbf{Executive Summary} & \textbf{Inclusion Rationale} & \textbf{Source} & \textbf{Citation (APA 7th)}"));
        foreach (var row in records)
        {
            string title = EscapeLatexText(row.Title)
                + (string.IsNullOrEmpty(row.IntegrityNote) ? "" : $"\\newline\\textit{{{EscapeLatexText(row.IntegrityNote)}}}");
            string summary = EscapeLatexText(row.Summary);
            string rationale = EscapeLatexText(row.InclusionRationale);
            // The database by its display name, as on the paper page (issue #133).
            string source = EscapeLatexText(CategoryLabel(row.Category));
            string citation = EscapeLatexText(row.ApaCitation);
            // No space before the row's end: chktex reads "Agents}. \\" as a full stop inside a sentence.
            ab.AppendLine($"{title} & {summary} & {rationale} & {source} & {citation}\\\\ \\addlinespace");
        }
        ab.AppendLine(@"\bottomrule");
        ab.AppendLine(@"\end{tabularx}");
        ab.AppendLine(@"\vspace{4pt}\noindent Table~\ref{tab:A.1} lists each included study with a short summary, the reason it was included, the database it came from and its reference.\par");
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
            ab.Append(SeedStudiesTable.Latex(seeds, $"tab:A.{nextTable}", EscapeLatexText));
        }
        return ab.ToString();
    }

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
            sb.AppendLine($"\\caption{{{title}. {EscapeLatexText(artifact.Caption)}}}\\label{{fig:artifact}}");
            sb.AppendLine(@"\end{figure}");
        }
        else if (artifact.Kind == ArtifactKinds.Table && artifact.Columns.Count > 0)
        {
            string spec = string.Join(" ", artifact.Columns.Select(_ => "Y"));
            sb.AppendLine($"\\begin{{tabularx}}{{\\textwidth}}{{{spec}}}");
            sb.Append(PaperLatex.TableHead(title, "tab:artifact", string.Join(" & ", artifact.Columns.Select(c => $"\\textbf{{{EscapeLatexText(c)}}}"))));
            foreach (var row in artifact.Rows)
                sb.AppendLine(string.Join(" & ", row.Select(EscapeLatexText)) + @" \\ \addlinespace");
            sb.AppendLine(@"\bottomrule");
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

    // The reference numbers as one citation, which Build resolves to the references' keys.
    // Braced, since it starts a table cell: chktex asks for a tie before a bare \cite.
    private static string Cite(IEnumerable<int> numbers) => @"{\cite{" + string.Join(",", numbers.Select(n => "ref" + n)) + "}}";

    /// <summary>Evidence map: which studies contribute to which theme of the thematic synthesis.</summary>
    private string EvidenceMapTable(ThematicCodebook book)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"\vspace{6pt}");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.9\hsize}Y >{\hsize=1.4\hsize}Y >{\hsize=0.7\hsize}Y}");
        sb.Append(PaperLatex.TableHead("Evidence Map of the Thematic Synthesis", "tab:evidence-map", @"\textbf{Theme} & \textbf{Description} & \textbf{Studies}"));
        foreach (var t in book.Themes)
            sb.AppendLine($"{EscapeLatexText(t.Name)} & {EscapeLatexText(t.Description)} & {Cite(t.Studies)} \\\\ \\addlinespace");
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}");
        var notes = new List<string>();
        if (book.SecondCoding?.Kappa is double k)
            notes.Add($"Second, independent coding: Cohen's kappa = {k.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} over {book.SecondCoding.CellsCompared} study-theme decisions.");
        if (book.Uncoded.Count > 0)
            notes.Add("Not placed in a theme: " + string.Join("; ", book.Uncoded.Select(u => $"[{u.Reference}] {u.Reason.TrimEnd('.')}")) + ".");
        if (notes.Count > 0)
            sb.AppendLine(@"\vspace{2pt}\noindent{\scriptsize " + EscapeLatexText(string.Join(" ", notes)) + @"\par}");
        // The text after the map was set in \normalsize since the note switched back to it; it still is.
        if (notes.Count > 0) sb.AppendLine(@"\normalsize");
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
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.25\hsize}Y >{\hsize=0.8\hsize}Y >{\hsize=1.1\hsize}Y >{\hsize=0.9\hsize}Y >{\hsize=1.95\hsize}Y}");
        sb.Append(PaperLatex.TableHead("Extracted Study Data", "tab:A.2", @"\textbf{Ref.} & \textbf{Study type} & \textbf{Method} & \textbf{Sample} & \textbf{Key findings}"));
        foreach (var e in extractions)
        {
            if (e.Error != null)
            {
                sb.AppendLine($"{Cite(new[] { e.ReferenceNumber })} & \\multicolumn{{4}}{{l}}{{Extraction failed.}} \\\\ \\addlinespace");
                continue;
            }
            string findings = string.Join(" ", e.KeyFindings.Select((f, i) => $"({i + 1}) {V(f)}"));
            sb.AppendLine($"{Cite(new[] { e.ReferenceNumber })} & {EscapeLatexText(e.StudyType)} & {V(e.Method)} & {V(e.Sample)} & {findings} \\\\ \\addlinespace");
        }
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}");
        // The note explains the dagger only when a value in the table carries one (issue #168).
        if (extractions.Any(HasUnverifiedValue))
            sb.AppendLine(@"\vspace{2pt}\noindent{\scriptsize $^{\dagger}$ The supporting quote given by the model was not found in the paper's text; treat the value with caution.\par}");
        sb.AppendLine(@"\vspace{10pt}");

        string A(string answer) => answer switch { "yes" => "Yes", "no" => "No", _ => "{?}" };
        var appraised = extractions.Where(MethodsSectionWriter.ShowInAppraisalTable).ToList();
        if (appraised.Count > 0)
        {
            sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.4\hsize}Y >{\hsize=1.6\hsize}Y c c c c c c}");
            // A short title; the key is in the note under the table (issue #145).
            sb.AppendLine(PaperLatex.TableHead("Quality Appraisal (MMAT 2018)", "tab:A.3", @"\textbf{Ref.} & \textbf{Category} & \textbf{1} & \textbf{2} & \textbf{3} & \textbf{4} & \textbf{5} & \textbf{Met}").TrimEnd());
            foreach (var e in appraised)
            {
                string category = e.Error != null ? "not appraised" : e.AppraisalCategory.Replace('_', ' ');
                var cells = Enumerable.Range(0, 5).Select(i => i < e.Appraisal.Count ? A(e.Appraisal[i].Answer) : "--");
                string met = e.Appraisal.Count == 0 ? "--" : $"{e.AppraisalYes}/{e.Appraisal.Count}";
                sb.AppendLine($"{Cite(new[] { e.ReferenceNumber })} & {EscapeLatexText(category)} & {string.Join(" & ", cells)} & {met} \\\\");
            }
            sb.AppendLine(@"\bottomrule");
            sb.AppendLine(@"\end{tabularx}");
        }
        string notAppraised = MethodsSectionWriter.NotAppraisedNote(extractions, "--");
        string note = string.Join(" ", new[] { appraised.Count > 0 ? AppraisalTableKey : "", notAppraised }.Where(n => n.Length > 0));
        if (note.Length > 0)
            sb.AppendLine(@"\vspace{2pt}\noindent{\scriptsize " + EscapeLatexText(note.TrimEnd('.')) + @".\par}");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }
}
