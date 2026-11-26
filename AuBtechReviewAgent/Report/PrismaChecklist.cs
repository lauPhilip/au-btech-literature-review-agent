using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// What the PRISMA 2020 checklist of a systematic review's main.tex is built from: the headings the paper has, the names
/// of its tables and figure, and what the run did. Everything comes from the run's ledger and the paper built in code.
/// </summary>
public sealed record PrismaChecklistContext
{
    /// <summary>Every section and subsection heading of the paper; a heading missing here makes its items "Missing".</summary>
    public IReadOnlyCollection<string> Headings { get; init; } = Array.Empty<string>();
    public bool TitleSaysSystematicReview { get; init; }
    public bool HasResearchQuestions { get; init; }
    public string? FlowFigureLabel { get; init; }
    public string? IncludedTable { get; init; }
    public string? ExtractionTable { get; init; }
    public string? AppraisalTable { get; init; }
    public string? EvidenceMapTable { get; init; }
    public string? SearchTable { get; init; }

    /// <summary>The number of the results' table of certainty per answer; null when no answer was rated.</summary>
    public string? CertaintyTable { get; init; }
    public bool HumanScreeningReview { get; init; }
    public bool HasProtocol { get; init; }
    public bool CompetingInterestsDeclared { get; init; }
}

/// <summary>
/// The PRISMA 2020 checklist as an appendix table of main.tex (Page et al., 2021): each item with the section, table or
/// figure that reports it, and an honest status. Items the tool does not do are marked "Not done" or "Not applicable"
/// with the reason; nothing is claimed that the run did not do (AGENTS.md rule 2).
/// </summary>
public static class PrismaChecklist
{
    public const string NotAssessedHeading = "Effect Measures and Reporting Bias";

    /// <summary>
    /// The methods text on PRISMA 2020 items 12, 13b, 13e and 14, which the tool does not do. The certainty of each
    /// answer (items 15 and 22) is rated in code when the review has research questions; otherwise this says it is not.
    /// </summary>
    public static string NotAssessedText(bool certaintyRated) =>
        "The synthesis is thematic, so no effect measures were computed and no statistical synthesis (meta-analysis) was done; " +
        "the preparation of summary statistics and the exploration of heterogeneity therefore do not apply. " +
        "The risk of bias due to missing results (reporting bias) was not assessed. " +
        (certaintyRated
            ? "The certainty of each answer and its sensitivity check are reported under Certainty of the Evidence."
            : "The review has no research questions, so the certainty of the evidence was not rated and no sensitivity check was made.") +
        " The findings should be read with that in mind.";

    public sealed record Row(string Item, string Topic, string Where, string Status);

    /// <summary>The rows of the checklist for one paper.</summary>
    public static List<Row> Rows(PrismaChecklistContext c)
    {
        var headings = new HashSet<string>(c.Headings, StringComparer.Ordinal);
        // Each place is named, with its number from \ref, so the reader finds it by name and by number.
        string S(params string[] names) => string.Join("; ", names.Where(headings.Contains).Select(n => $"{PaperLatex.Escape(n)} ({LatexLabels.Ref(n)})"));
        bool Has(string name) => headings.Contains(name);
        string Join(params string?[] parts) => string.Join("; ", parts.Where(p => !string.IsNullOrEmpty(p)));
        string T(string? table, string name) => table == null ? "" : $"Table {table} ({name})";
        string F() => c.FlowFigureLabel == null ? "" : $"PRISMA flow diagram (Fig.~\\ref{{{c.FlowFigureLabel}}})";
        string When(bool reported, string status) => reported ? status : "Missing";
        const string notDone = "Not done", notApplicable = "Not applicable: thematic synthesis, no effect estimates";
        const string noQuestions = "Not done: the review has no research questions to rate";
        const string Certainty = "Certainty of the Evidence", CertaintyName = "Certainty of the Evidence for Each Answer";
        bool rated = c.CertaintyTable != null;

        string screening = c.HumanScreeningReview
            ? "Reported: two independent model screenings, then a human check of the decisions"
            : "Reported: two independent model screenings; no human screener (single automated reviewer)";
        return new List<Row>
        {
            new("1", "Title", "Title", c.TitleSaysSystematicReview ? "Reported" : "Partly: the line above the title names the review type; the title does not"),
            new("2", "Abstract", "Abstract", "Partly: written by the model; not checked against PRISMA 2020 for Abstracts"),
            new("3", "Rationale", S("Rationale"), When(Has("Rationale"), "Reported")),
            new("4", "Objectives", S("Objectives", "Research Questions"), When(Has("Objectives"), "Reported")),
            new("5", "Eligibility criteria", S("Eligibility Criteria"), When(Has("Eligibility Criteria"), "Reported")),
            new("6", "Information sources", S("Information Sources"), When(Has("Information Sources"), "Reported, with the date searched")),
            new("7", "Search strategy", Join(S("Search Execution"), T(c.SearchTable, "Search Strategy per Source")), When(c.SearchTable != null, "Reported: every search string, source, date and count")),
            new("8", "Selection process", S("Selection Automation"), When(Has("Selection Automation"), screening)),
            new("9", "Data collection process", S("Data Collection and Quality Appraisal"), When(Has("Data Collection and Quality Appraisal"), "Reported: one model, every value backed by a checked quote; no second extractor")),
            new("10a", "Data items: outcomes", Join(S("Data Collection and Quality Appraisal"), T(c.ExtractionTable, "Extracted Study Data")), When(c.ExtractionTable != null, "Partly: findings extracted as text; no outcome domains or time points defined")),
            new("10b", "Data items: other variables", Join(S("Data Collection and Quality Appraisal"), T(c.ExtractionTable, "Extracted Study Data")), When(c.ExtractionTable != null, "Reported: study type, method, sample, limitations")),
            new("11", "Study risk of bias assessment", S("Data Collection and Quality Appraisal"), When(c.AppraisalTable != null, "Reported: MMAT 2018 by one model; no second appraiser")),
            new("12", "Effect measures", S(NotAssessedHeading), notApplicable),
            new("13a", "Synthesis: eligibility", S("Synthesis Methods"), When(Has("Synthesis Methods"), "Reported: coded studies are grouped into themes")),
            new("13b", "Synthesis: data preparation", S(NotAssessedHeading), notApplicable),
            new("13c", "Synthesis: tabulation", Join(S("Synthesis Methods"), T(c.EvidenceMapTable, "Evidence Map of the Thematic Synthesis")), When(c.EvidenceMapTable != null, "Reported: evidence map of studies by theme")),
            new("13d", "Synthesis: methods", S("Synthesis Methods"), When(Has("Synthesis Methods"), "Reported: thematic synthesis (Thomas and Harden, 2008)")),
            new("13e", "Synthesis: heterogeneity", S(NotAssessedHeading), notApplicable),
            new("13f", "Synthesis: sensitivity analyses", rated ? Join(S(Certainty), T(c.CertaintyTable, CertaintyName)) : S(NotAssessedHeading), rated ? "Reported: each answer checked without the studies read from the abstract only and without those failing most MMAT criteria" : noQuestions),
            new("14", "Reporting bias assessment", S(NotAssessedHeading), notDone),
            new("15", "Certainty assessment", rated ? S(Certainty) : S(NotAssessedHeading), rated ? "Reported: a rule in code inspired by GRADE-CERQual; an automated approximation, not a formal assessment by people" : noQuestions),
            new("16a", "Results of the search", Join(S("Characteristics of the Included Studies"), F()), When(c.FlowFigureLabel != null, "Reported: flow diagram with counts")),
            new("16b", "Excluded studies", F(), When(c.FlowFigureLabel != null, "Partly: exclusions counted by reason; full texts were read only for included studies, so no study was excluded at full text; each excluded record is in screened.ris")),
            new("17", "Study characteristics", Join(T(c.IncludedTable, "Included studies"), T(c.ExtractionTable, "Extracted Study Data")), When(c.ExtractionTable != null, "Reported: type, method, sample and the text read")),
            new("18", "Risk of bias in studies", T(c.AppraisalTable, "Quality Appraisal (MMAT 2018)"), When(c.AppraisalTable != null, "Reported: MMAT answers per study")),
            new("19", "Results of individual studies", T(c.ExtractionTable, "Extracted Study Data"), When(c.ExtractionTable != null, "Partly: key findings as text; no effect estimates")),
            new("20a", "Results of syntheses: studies", Join(S("Results & Synthesis"), T(c.EvidenceMapTable, "Evidence Map of the Thematic Synthesis")), When(c.EvidenceMapTable != null, "Partly: the studies behind each theme are listed; their appraisal is not summarised per theme")),
            new("20b", "Results of statistical syntheses", S(NotAssessedHeading), notApplicable),
            new("20c", "Results: heterogeneity", S(NotAssessedHeading), notApplicable),
            new("20d", "Results: sensitivity analyses", rated ? Join(T(c.CertaintyTable, CertaintyName), S("Conclusion")) : S(NotAssessedHeading), rated ? "Reported: whether each answer holds without the weakest studies" : noQuestions),
            new("21", "Reporting biases", S(NotAssessedHeading), notDone),
            new("22", "Certainty of evidence", rated ? Join(T(c.CertaintyTable, CertaintyName), S("Conclusion")) : S(NotAssessedHeading), rated ? "Reported: the certainty of each answer with its reasons" : noQuestions),
            new("23a", "Discussion: interpretation", S("General Interpretation"), When(Has("General Interpretation"), "Reported")),
            new("23b", "Discussion: limitations of the evidence", S("Limitations of the Evidence"), When(Has("Limitations of the Evidence"), "Reported")),
            new("23c", "Discussion: limitations of the review", S("Limitations of the Review Process"), When(Has("Limitations of the Review Process"), "Reported")),
            new("23d", "Discussion: implications", S("Implications for Practice and Policy", "Directions for Future Research"), When(Has("Implications for Practice and Policy") || Has("Directions for Future Research"), "Reported")),
            new("24a", "Registration", S("Registration and Protocol"), When(Has("Registration and Protocol"), "Reported: not registered")),
            new("24b", "Protocol", S("Registration and Protocol"), When(Has("Registration and Protocol"), c.HasProtocol ? "Reported: protocol.md with its fingerprint" : "Not done: run made before protocols were written")),
            new("24c", "Amendments to the protocol", S("Registration and Protocol"), When(Has("Registration and Protocol"), c.HasProtocol ? "Reported: dated amendments in protocol.md" : "Not done")),
            new("25", "Support", S("Support & Funding"), When(Has("Support & Funding"), "Reported")),
            new("26", "Competing interests", S("Competing Interests"), When(Has("Competing Interests"), c.CompetingInterestsDeclared ? "Reported" : "Partly: not reported by the reviewer")),
            new("27", "Availability of data, code and other materials", S("Availability of Data and Code"), When(Has("Availability of Data and Code"), "Reported: code repository and run archive")),
        };
    }

    /// <summary>The checklist as a compact LaTeX table; the topics and statuses are fixed text, written here.</summary>
    public static string Latex(PrismaChecklistContext context, string tableName, Func<string, string> escape)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"{\scriptsize");
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{>{\hsize=0.2\hsize}X >{\hsize=0.95\hsize}X >{\hsize=0.75\hsize}X >{\hsize=2.1\hsize}X}");
        sb.AppendLine($"\\multicolumn{{4}}{{l}}{{\\textbf{{\\small Table {tableName}: PRISMA 2020 Checklist}}}} \\\\");
        sb.AppendLine(@"\toprule");
        sb.AppendLine(@"\textbf{Item} & \textbf{Topic} & \textbf{Reported in} & \textbf{Status} \\");
        sb.AppendLine(@"\midrule");
        foreach (var row in Rows(context))
            sb.AppendLine($"{row.Item} & {escape(row.Topic)} & {(row.Where.Length == 0 ? "--" : row.Where)} & {escape(row.Status)} \\\\");
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}}");
        sb.AppendLine(@"\vspace{2pt}\noindent\scriptsize " + escape(
            "Items and wording follow the PRISMA 2020 checklist (Page et al., 2021). The status is written in code from what the run did: Not done means the tool does not do it; Missing means the run has no section for it.") + @"\small");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }
}
