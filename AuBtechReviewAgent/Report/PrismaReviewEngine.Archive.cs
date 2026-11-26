using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;
/// <summary>The downloadable archive: main.tex, bibliography, ledgers, manifest and source files.</summary>
public partial class PrismaReviewEngine
{
    private static string EscapeLatexText(string input) => PaperLatex.Escape(input);

    /// <summary>
    /// references.bib / references.ris (the included studies) or screened.ris (every screened record, tagged with
    /// its decision) for a run, or null if the run does not exist.
    /// </summary>
    public string? ExportReferences(Guid sessionId, string format)
    {
        var state = LoadState(sessionId);
        if (state == null) return null;
        return format switch
        {
            "ris" => BibliographyExporter.ToRis(state.SynthesizedRecords),
            "screened" => BibliographyExporter.ToScreenedRis(state.Phases.Screening,
                state.SynthesizedRecords.GroupBy(r => r.PaperId).ToDictionary(g => g.Key, g => g.First().ReferenceNumber)),
            _ => BibliographyExporter.ToBibTeX(state.SynthesizedRecords),
        };
    }

    /// <summary>The files only a systematic review generates for its archive, main.tex first. Called with the invariant culture.</summary>
    private List<(string Path, byte[] Content)> BuildGeneratedFiles(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records, ReviewState? state)
    {
        // main.tex is built in memory and written straight into the zip. It used to go through a temporary
        // manuscript_*.tex file in the project folder, and every download first deleted all such files - so
        // two people downloading at the same moment could delete each other's main.tex.

        // Table A.1 and the bibliography must follow the reference numbers the synthesis cites.
        // (Older runs have ReferenceNumber = 0 everywhere; OrderBy is stable, so they keep their order.)
        records = records.OrderBy(r => r.ReferenceNumber).ToList();

        string rawSynthesis = report.SynthesisResultsItem ?? "";
        string textSynthesisOnly = Regex.Replace(rawSynthesis, @"\[MERMAID_START\].*?\[MERMAID_END\]", "", RegexOptions.Singleline);
        textSynthesisOnly = Regex.Replace(textSynthesisOnly, @"\[TIKZ_START\].*?\[TIKZ_END\]", "", RegexOptions.Singleline).Trim();

        string tikzDiagramCode = "";
        var tikzMatch = Regex.Match(rawSynthesis, @"\[TIKZ_START\](.*?)\[TIKZ_END\]", RegexOptions.Singleline);
        if (tikzMatch.Success) {
            tikzDiagramCode = tikzMatch.Groups[1].Value.Trim();
            // "-->" is not a TikZ arrow tip (it made every generated main.tex fail to compile); only repair the
            // mangled "-¿" some model outputs contain.
            tikzDiagramCode = tikzDiagramCode.Replace("-¿", "->");
        }

        // The systematic review's own parts of the shared paper (Report/ReviewPaper.cs): its PRISMA method items,
        // its data figures and tables, and the evidence map and artifact. All of it is drawn in code.
        var methods = new List<PaperSection>
        {
            new("Eligibility Criteria", report.EligibilityItem ?? ""),
            new("Information Sources", report.SourcesItem ?? ""),
            new("Search Execution", report.SearchStrategyItem ?? ""),
            new("Selection Automation", report.SelectionProcessItem ?? ""),
            new("Data Collection and Quality Appraisal", report.BiasAssessmentItem ?? ""),
        };
        if (!string.IsNullOrWhiteSpace(report.SynthesisMethodsItem)) methods.Add(new("Synthesis Methods", report.SynthesisMethodsItem));
        // The certainty of each answer is rated in code from the run's files (PRISMA 2020 items 15 and 22).
        var certainty = SystematicCertainty.Assess(report, state?.ThematicSynthesis, state?.Extractions);
        if (certainty != null) methods.Add(new("Certainty of the Evidence", certainty.Method));
        // PRISMA 2020 items 12 to 14: what the tool does not do, said plainly in code.
        methods.Add(new(MethodsSectionWriter.NotAssessedHeading, MethodsSectionWriter.NotAssessed(certainty != null)));
        // The reviewer's research questions come first in the method, as published reviews list them.
        var questions = state?.ResearchQuestions ?? new List<string>();
        if (questions.Count > 0)
            methods.Insert(0, new PaperSection("Research Questions", "", string.Join("", questions.Select((q, i) =>
                $"\\noindent\\textbf{{RQ{i + 1}.}} {EscapeLatexText(q)}\\par{Environment.NewLine}"))));

        string dataLatex = DataFiguresLatex(records, state);
        string appendixLatex = AppendixLatex(records, state, out var appendixTables);

        var resultsLatex = new List<string>();
        if (state?.ThematicSynthesis is { Themes.Count: > 0 } book && (report.SynthesisSections?.Count ?? 0) > 0)
            resultsLatex.Add(EvidenceMapTable(book));
        if (report.Artifact is { Error: null } artifact)
            resultsLatex.Add(ArtifactLatex(artifact));
        // The certainty table is numbered by LaTeX after the evidence map and a table artifact.
        if (certainty != null) resultsLatex.Add(CertaintyLatex.Table(certainty));
        if (!string.IsNullOrEmpty(tikzDiagramCode))
            resultsLatex.Add(string.Join("", new[]
            {
                @"\begin{figure}[H]", @"\centering", @"\resizebox{\linewidth}{!}{", tikzDiagramCode, @"}",
                @"\caption{Grounded Architectural Synthesis System Model Diagram}", @"\end{figure}",
            }.Select(line => line + Environment.NewLine)));

        // PRISMA 2020 items 23b to 23d after the general interpretation (23a), as published reviews give them their own headings.
        var discussionNotes = new List<PaperSection>();
        void Note(string heading, string? text) { if (!string.IsNullOrWhiteSpace(text)) discussionNotes.Add(new(heading, text)); }
        Note("Limitations of the Evidence", report.EvidenceLimitationsItem);
        Note("Limitations of the Review Process", report.ReviewLimitationsItem);
        Note("Implications for Practice and Policy", report.ImplicationsItem);
        Note("Directions for Future Research", report.FutureResearchItem);
        if (!string.IsNullOrWhiteSpace(report.CitationCheckSummary)) discussionNotes.Add(new("Automated Citation Check", report.CitationCheckSummary));
        if (!string.IsNullOrWhiteSpace(state?.HumanScreeningReviewOutcome)) discussionNotes.Add(new("Human Screening Review", state!.HumanScreeningReviewOutcome!));

        // The declarations in the order and with the headings of the report page (issue #169), then the statement on AI
        // use (7.4 on the page) and the declarations only the authors can make (the publisher audit, batch A).
        var declarations = new List<PaperSection>
        {
            new("Registration and Protocol", string.IsNullOrWhiteSpace(report.ProtocolItem) ? NoProtocolText : report.ProtocolItem),
            new("Support & Funding", PaperDeclarations.Funding(report.SupportItem)),
            new("Availability of Data and Code", report.AvailabilityItem ?? ""),
        };
        string aiMethods = "";
        if (state != null)
        {
            aiMethods = AiUseStatement.Methods(state, sessionId, LoadReportEdits(sessionId), report.Artifact);
            declarations.Add(new("Use of Artificial Intelligence", aiMethods));
        }
        declarations.AddRange(PaperDeclarations.Placeholders());
        if (state != null)
            appendixLatex += PaperDeclarations.PublisherVariantsLatex(AiUseStatement.Used(state), AiUseStatement.Purpose, AiUseStatement.Short(state), aiMethods);

        string title = (report.TitleItem ?? "Systematic Review Manuscript").Replace("[Source Context Anchor 1]", "").Trim();
        // The paper ends with the PRISMA 2020 checklist, written in code from what this paper holds.
        appendixLatex += PrismaChecklist.Latex(PrismaChecklist.Rows(new PrismaChecklistFacts
        {
            Title = title,
            IsPilot = SearchAdequacy.IsPilot(state),
            HasResearchQuestions = questions.Count > 0,
            HasSynthesisMethods = !string.IsNullOrWhiteSpace(report.SynthesisMethodsItem),
            HasCertainty = certainty != null,
            IncludedStudies = records.Count,
            HasEvidenceMap = state?.ThematicSynthesis is { Themes.Count: > 0 } && (report.SynthesisSections?.Count ?? 0) > 0,
            ExtractionTable = appendixTables.Extraction,
            AppraisalTable = appendixTables.Appraisal,
            SearchTable = appendixTables.Search,
            HasProtocol = !string.IsNullOrWhiteSpace(report.ProtocolItem),
            FundingDeclared = PaperDeclarations.Funding(report.SupportItem) != PaperDeclarations.ToComplete,
            HasEvidenceLimitations = !string.IsNullOrWhiteSpace(report.EvidenceLimitationsItem),
            HasReviewLimitations = !string.IsNullOrWhiteSpace(report.ReviewLimitationsItem),
            HasImplications = !string.IsNullOrWhiteSpace(report.ImplicationsItem),
            HasFutureResearch = !string.IsNullOrWhiteSpace(report.FutureResearchItem),
        }), $"tab:{appendixTables.Next}", EscapeLatexText);
        string bibtex = BibliographyExporter.ToBibTeX(records);
        bool hasProtocol = File.Exists(Path.Join(GetWorkspaceFolderPath(sessionId), "protocol.md"));
        var paper = new ReviewPaper
        {
            KindLabel = SearchAdequacy.IsPilot(state) ? "AI-GENERATED PILOT SYSTEMATIC SEARCH" : "AI-GENERATED SYSTEMATIC LITERATURE REVIEW",
            Title = title,
            ProtocolHash = string.IsNullOrWhiteSpace(report.ProtocolHash) ? "not recorded" : report.ProtocolHash,
            Keywords = state == null ? Array.Empty<string>() : ArticleKeywords.From(state.SearchQuery,
                state.SearchPerspectives.Count > 0 ? state.SearchPerspectives : new List<string> { state.SearchQuery },
                state.ThematicSynthesis?.Themes.Select(t => t.Name)),
            GeneratedAt = report.GeneratedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            Models = AiMarking.Clean(new[] { state?.RunSettings?.Model, state?.RunSettings?.FastModel }),
            Abstract = report.AbstractItem ?? "",
            Rationale = report.RationaleItem ?? "",
            Objectives = report.ObjectivesItem ?? "",
            Methods = methods,
            DataHeading = "Characteristics of the Included Studies",
            DataText = (MethodsSectionWriter.IncludedSet(records) + " " + MethodsSectionWriter.AppendixPointer(records.Count, state?.Extractions)).Trim(),
            DataLatex = dataLatex,
            ResultsText = textSynthesisOnly,
            ResultsOverviewHeading = "Overview of the Synthesis",
            Themes = (report.SynthesisSections ?? new List<SynthesisSection>()).Select(x => new PaperSection(x.Heading, x.Text)).ToList(),
            CoverageNote = report.CoverageSummary,
            ResultsLatex = resultsLatex,
            Discussion = report.DiscussionItem ?? "",
            DiscussionOpeningHeading = "General Interpretation",
            DiscussionNotes = discussionNotes,
            Summary = ConclusionSection(report, certainty),
            Declarations = declarations,
            References = records.Select(r => r.ApaCitation).ToList(),
            AppendixLatex = appendixLatex,
            ReferenceKeys = PaperLatex.BibKeys(bibtex),
            Article = ArticleInfo.ForSystematic(sessionId, state, report, title, report.AbstractItem ?? "", hasProtocol),
        };

        // The files only a systematic review generates; the core (Report/RunArchive.cs) adds the run folder's
        // files, the source texts and answers, and manifest.json with the SHA-256 of everything.
        var generated = new List<(string Path, byte[] Content)>();
        void AddText(string name, string content) => generated.Add((name, new UTF8Encoding(false).GetBytes(content)));

        AddText("main.tex", PaperLatex.Build(paper));
        AddText("references.bib", bibtex);
        if (certainty != null) AddText(CertaintyFile.FileName, JsonSerializer.Serialize(certainty, new JsonSerializerOptions { WriteIndented = true }));
        AddText("references.ris", BibliographyExporter.ToRis(records));
        if (state != null)
            AddText("screened.ris", BibliographyExporter.ToScreenedRis(state.Phases.Screening,
                records.GroupBy(r => r.PaperId).ToDictionary(g => g.Key, g => g.First().ReferenceNumber)));
        return generated;
    }

    /// <summary>
    /// The Conclusion: the answer to each research question ("RQ1. question" then its answer), then the conclusion text.
    /// Null when the run wrote no conclusion.
    /// </summary>
    private PaperSection? ConclusionSection(PrismaReport report, CertaintyFile? certainty = null)
    {
        if (string.IsNullOrWhiteSpace(report.ConclusionItem)) return null;
        if (report.ResearchAnswers is not { Count: > 0 } answers) return new PaperSection("Conclusion", report.ConclusionItem);
        var latex = new StringBuilder();
        foreach (var a in answers)
        {
            // The certainty phrase follows the answer, e.g. "(low certainty: 3 studies, two read from the abstract only)".
            string phrase = certainty?.Answers.FirstOrDefault(c => c.Number == a.Number)?.Phrase is { } p ? " " + EscapeLatexText(p) : "";
            latex.AppendLine($"\\noindent\\textbf{{RQ{a.Number}. {EscapeLatexText(a.Question)}}} {EscapeLatexText(a.Answer)}{phrase}\\par\\smallskip");
            // Written in code when the answer cites a secondary study.
            if (!string.IsNullOrWhiteSpace(a.Note)) latex.AppendLine($"\\noindent{{\\small\\itshape {EscapeLatexText(a.Note)}}}\\par\\smallskip");
        }
        // Whether the answers hold without the weakest evidence, in plain sentences (the sensitivity check).
        if (certainty != null && EvidenceCertainty.Summary(certainty.Answers, certainty.Rule) is { Length: > 0 } held)
            latex.AppendLine($"\\noindent {EscapeLatexText(held)}\\par\\smallskip");
        latex.AppendLine(EscapeLatexText(report.ConclusionItem));
        return new PaperSection("Conclusion", report.ConclusionItem, latex.ToString());
    }
}
