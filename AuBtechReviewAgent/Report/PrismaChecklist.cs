using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>What a PRISMA 2020 checklist row says of an item: done, done in part, not done, not applicable, or left to the authors.</summary>
public enum ChecklistStatus { Reported, InPart, NotDone, NotApplicable, Authors }

/// <summary>One row of the PRISMA 2020 checklist: the item, its topic, where the paper reports it and an honest status with its reason.</summary>
public sealed record ChecklistRow(string Item, string Topic, string Where, ChecklistStatus Status, string Reason = "");

/// <summary>
/// What a systematic review's main.tex holds, as the checklist needs it: which optional parts are there and the names of
/// its appendix tables. Filled in code from the run's report and ledger (Report/PrismaReviewEngine.Archive.cs).
/// </summary>
public sealed record PrismaChecklistFacts
{
    public string Title { get; init; } = "";
    /// <summary>True for a pilot run, whose line above the title reads "AI-GENERATED PILOT SYSTEMATIC SEARCH".</summary>
    public bool IsPilot { get; init; }
    public bool HasResearchQuestions { get; init; }
    public bool HasSynthesisMethods { get; init; }
    public bool HasCertainty { get; init; }
    public int IncludedStudies { get; init; }
    public bool HasEvidenceMap { get; init; }
    /// <summary>The names of the appendix tables, such as "Table A.2", or null when the paper has none.</summary>
    public string? ExtractionTable { get; init; }
    public string? AppraisalTable { get; init; }
    public string? SearchTable { get; init; }
    public bool HasProtocol { get; init; }
    public bool FundingDeclared { get; init; }
    public bool HasEvidenceLimitations { get; init; }
    public bool HasReviewLimitations { get; init; }
    public bool HasImplications { get; init; }
    public bool HasFutureResearch { get; init; }
}

/// <summary>
/// The PRISMA 2020 checklist (Page et al., 2021) for a systematic review's main.tex, written in code: every item and
/// sub-item, the section, table or figure that reports it by name, and an honest status. An item the tool does not
/// carry out is marked so with the reason, never claimed; an item only the authors can give is left to them.
/// </summary>
public static class PrismaChecklist
{
    private const string NoStatistics = "the findings were synthesised in words, without a meta-analysis";

    // The names of the paper's figures and tables, which the LaTeX table links to their labels.
    private const string EvidenceMap = "Evidence Map of the Thematic Synthesis";
    private const string FlowDiagram = "PRISMA 2020 flow diagram";
    private const string CertaintyTable = "Certainty of the Evidence for Each Answer";
    private static readonly (string Name, string Label, string Kind)[] Linked =
    {
        (EvidenceMap, "tab:evidence-map", "Table"), (FlowDiagram, "fig:flow", "Figure"), (CertaintyTable, "tab:certainty", "Table"),
    };

    public static IReadOnlyList<ChecklistRow> Rows(PrismaChecklistFacts f)
    {
        var R = ChecklistStatus.Reported;
        string Method(string heading) => $"{heading} (Methodology)";
        string Discussion(string heading) => $"{heading} (Discussion)";
        string Declaration(string heading) => $"{heading} (Declarations)";
        string Tables(params string?[] names) => string.Join(", ", names.Where(n => !string.IsNullOrEmpty(n)));
        bool studies = f.IncludedStudies > 0;
        var none = new ChecklistRow("", "", "", ChecklistStatus.NotApplicable, "no study was included");
        ChecklistRow IfStudies(ChecklistRow row) => studies ? row : row with { Status = none.Status, Reason = none.Reason };
        string notAssessed = Method(MethodsSectionWriter.NotAssessedHeading);
        string certainty = Method("Certainty of the Evidence");
        var sensitivity = f.HasCertainty
            ? (ChecklistStatus.InPart, "a check of whether each answer holds without the weakest evidence, not a statistical sensitivity analysis")
            : (ChecklistStatus.NotDone, "no certainty rating, so no sensitivity check");

        return new List<ChecklistRow>
        {
            f.Title.Contains("systematic review", StringComparison.OrdinalIgnoreCase)
                ? new("1", "Title", "Title", R)
                : f.IsPilot
                    ? new("1", "Title", "Line above the title", ChecklistStatus.InPart, "the line above the title names the report a pilot systematic search, not a systematic review, because the search was too small; the title names it so too")
                    : new("1", "Title", "Line above the title", ChecklistStatus.InPart, "the line above the title names the report a systematic review; the title itself does not"),
            new("2", "Abstract", "Abstract", ChecklistStatus.InPart, "the abstract does not yet follow the PRISMA 2020 for Abstracts checklist"),
            new("3", "Rationale", "Rationale (Introduction)", R),
            new("4", "Objectives", f.HasResearchQuestions ? "Objectives (Introduction); Research Questions (Methodology)" : "Objectives (Introduction)", R),
            new("5", "Eligibility criteria", Method("Eligibility Criteria"), R),
            new("6", "Information sources", Method("Information Sources"), R),
            new("7", "Search strategy", Tables(Method("Search Execution"), f.SearchTable), f.SearchTable != null ? R : ChecklistStatus.InPart,
                f.SearchTable != null ? "" : "no database was searched, so there is no search table"),
            new("8", "Selection process", Method("Selection Automation"), R),
            IfStudies(new("9", "Data collection process", Method("Data Collection and Quality Appraisal"), R)),
            IfStudies(new("10a", "Data items: outcomes", Method("Data Collection and Quality Appraisal"), ChecklistStatus.InPart,
                "up to three key findings per study are extracted with quotes; no outcomes were defined in advance")),
            IfStudies(new("10b", "Data items: other variables", Method("Data Collection and Quality Appraisal"), R)),
            IfStudies(new("11", "Study risk of bias assessment", Method("Data Collection and Quality Appraisal"), R)),
            new("12", "Effect measures", notAssessed, ChecklistStatus.NotDone, NoStatistics),
            f.HasSynthesisMethods ? new("13a", "Synthesis: which studies in each synthesis", Method("Synthesis Methods"), R)
                                  : new("13a", "Synthesis: which studies in each synthesis", "", ChecklistStatus.NotDone, "the run wrote no synthesis"),
            new("13b", "Synthesis: preparing the data", notAssessed, ChecklistStatus.NotApplicable, "no statistics were prepared, so no data were converted or imputed"),
            f.HasEvidenceMap ? new("13c", "Synthesis: tabulating the results", EvidenceMap, R)
                             : new("13c", "Synthesis: tabulating the results", "", ChecklistStatus.NotDone, "the run has no themes to map"),
            f.HasSynthesisMethods ? new("13d", "Synthesis: methods", Tables(Method("Synthesis Methods"), notAssessed), R)
                                  : new("13d", "Synthesis: methods", notAssessed, ChecklistStatus.NotDone, "the run wrote no synthesis"),
            new("13e", "Synthesis: heterogeneity", notAssessed, ChecklistStatus.NotDone, NoStatistics),
            new("13f", "Synthesis: sensitivity analyses", f.HasCertainty ? certainty : notAssessed, sensitivity.Item1, sensitivity.Item2),
            new("14", "Reporting bias assessment", notAssessed, ChecklistStatus.NotDone, "the risk of bias due to missing results is not assessed"),
            f.HasCertainty ? new("15", "Certainty assessment", certainty, R)
                           : new("15", "Certainty assessment", "", ChecklistStatus.NotDone, "the run rated no answers"),
            new("16a", "Study selection", FlowDiagram + " in Characteristics of the Included Studies", R),
            new("16b", "Excluded studies that seemed eligible", "screened.ris in the run archive", ChecklistStatus.InPart,
                "these studies are not singled out; screened.ris lists every screened record with its decision and reason"),
            IfStudies(new("17", "Study characteristics", Tables("Characteristics of the Included Studies", "Table A.1", f.ExtractionTable), R)),
            IfStudies(f.AppraisalTable != null ? new("18", "Risk of bias in studies", f.AppraisalTable, R)
                                               : new("18", "Risk of bias in studies", "", ChecklistStatus.NotDone, "no study had an MMAT appraisal to show")),
            IfStudies(f.ExtractionTable != null
                ? new("19", "Results of individual studies", f.ExtractionTable, ChecklistStatus.InPart, "key findings with quotes; no summary statistics or effect estimates")
                : new("19", "Results of individual studies", "", ChecklistStatus.NotDone, "no data were extracted")),
            f.HasEvidenceMap ? new("20a", "Results of syntheses: contributing studies", "Results & Synthesis; " + EvidenceMap, R)
                             : new("20a", "Results of syntheses: contributing studies", "Results & Synthesis", ChecklistStatus.InPart, "the run has no themes to map"),
            new("20b", "Results of statistical syntheses", notAssessed, ChecklistStatus.NotDone, NoStatistics),
            new("20c", "Results: heterogeneity", notAssessed, ChecklistStatus.NotDone, NoStatistics),
            new("20d", "Results: sensitivity analyses", f.HasCertainty ? "Conclusion" : notAssessed, sensitivity.Item1, sensitivity.Item2),
            new("21", "Reporting biases", notAssessed, ChecklistStatus.NotDone, "the risk of bias due to missing results is not assessed"),
            f.HasCertainty ? new("22", "Certainty of evidence", CertaintyTable + " (Results & Synthesis); Conclusion", R)
                           : new("22", "Certainty of evidence", "", ChecklistStatus.NotDone, "the run rated no answers"),
            new("23a", "Discussion: interpretation", Discussion("General Interpretation"), R),
            Optional("23b", "Discussion: limitations of the evidence", Discussion("Limitations of the Evidence"), f.HasEvidenceLimitations),
            Optional("23c", "Discussion: limitations of the review", Discussion("Limitations of the Review Process"), f.HasReviewLimitations),
            Optional("23d", "Discussion: implications", Tables(f.HasImplications ? Discussion("Implications for Practice and Policy") : null,
                f.HasFutureResearch ? Discussion("Directions for Future Research") : null), f.HasImplications || f.HasFutureResearch),
            new("24a", "Registration", Declaration("Registration and Protocol"), ChecklistStatus.Authors, "the tool does not register a review"),
            f.HasProtocol ? new("24b", "Protocol", Tables(Declaration("Registration and Protocol"), "protocol.md in the run archive"), R)
                          : new("24b", "Protocol", Declaration("Registration and Protocol"), ChecklistStatus.NotDone, "no protocol was recorded for this run"),
            f.HasProtocol ? new("24c", "Amendments to the protocol", Declaration("Registration and Protocol"), R)
                          : new("24c", "Amendments to the protocol", "", ChecklistStatus.NotDone, "no protocol was recorded for this run"),
            f.FundingDeclared ? new("25", "Support", Declaration("Support & Funding"), R)
                              : new("25", "Support", Declaration("Support & Funding"), ChecklistStatus.Authors, "the run recorded no funding statement"),
            new("26", "Competing interests", Declaration("Competing Interests"), ChecklistStatus.Authors, "only the authors can declare them"),
            new("27", "Availability of data, code and other materials", Declaration("Availability of Data and Code"), R),
        };
    }

    private static ChecklistRow Optional(string item, string topic, string where, bool present) =>
        present ? new(item, topic, where, ChecklistStatus.Reported) : new(item, topic, "", ChecklistStatus.NotDone, "the run wrote no such section");

    /// <summary>The status as the table prints it, with its reason after a colon.</summary>
    public static string StatusText(ChecklistRow row)
    {
        string label = row.Status switch
        {
            ChecklistStatus.Reported => "Reported",
            ChecklistStatus.InPart => "In part",
            ChecklistStatus.NotDone => "Not done by the tool",
            ChecklistStatus.NotApplicable => "Not applicable",
            _ => "For the authors",
        };
        return string.IsNullOrEmpty(row.Reason) ? label : $"{label}: {row.Reason}";
    }

    /// <summary>
    /// The checklist as an appendix table in LaTeX, with the label <paramref name="label"/>, all text escaped. A named table
    /// or figure is linked to it ("Table A.4" to tab:A.4), so the reader can follow the reference.
    /// </summary>
    public static string Latex(IReadOnlyList<ChecklistRow> rows, string label, Func<string, string> escape)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{l >{\hsize=0.9\hsize}Y >{\hsize=1.0\hsize}Y >{\hsize=1.1\hsize}Y}");
        sb.Append(PaperLatex.TableHead("PRISMA 2020 checklist", label, @"\textbf{Item} & \textbf{Topic} & \textbf{Where reported} & \textbf{Status}"));
        foreach (var row in rows)
            sb.AppendLine($"{escape(row.Item)} & {escape(row.Topic)} & {(row.Where.Length == 0 ? "--" : Link(escape(row.Where)))} & {escape(StatusText(row))}\\\\ \\addlinespace");
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}");
        string note = "The checklist is written in code from what this paper holds. An item the tool does not carry out is marked as not done with the reason; "
            + "an item only the authors can give is left to them. Item numbers follow Page et al. (2021), The PRISMA 2020 statement, BMJ 372:n71.";
        sb.AppendLine(@"\vspace{2pt}\noindent{\scriptsize " + escape(note) + @"\par}");
        return sb.ToString();
    }

    private static string Link(string where)
    {
        string linked = System.Text.RegularExpressions.Regex.Replace(where, @"\bTable (A\.\d+)\b", @"Table~\ref{tab:$1}");
        foreach (var (name, label, kind) in Linked)
            linked = linked.Replace(name, $"{name} ({kind}~\\ref{{{label}}})", StringComparison.Ordinal);
        return linked;
    }
}
