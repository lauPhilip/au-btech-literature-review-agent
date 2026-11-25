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
    /// Loads the persisted report + synthesized records for a session straight off disk and builds the
    /// workspace archive from them. Lets the download be served from a plain HTTP endpoint (Program.cs)
    /// instead of round-tripping the whole zip through the Blazor Server SignalR connection, which has a
    /// small default max message size and would silently fail once real source PDFs are included.
    /// </summary>
    public byte[] GenerateWorkspaceArchiveFromDisk(Guid sessionId)
    {
        var state = LoadState(sessionId);
        if (state == null) return Array.Empty<byte>();

        var report = new PrismaReport();
        string reportPath = GetReportFilePath(sessionId);
        if (File.Exists(reportPath))
        {
            try
            {
                string content = File.ReadAllText(reportPath);
                report = JsonSerializer.Deserialize<PrismaReport>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PrismaReport();
            }
            catch (Exception ex)
            {
                // The archive is still built from the ledger; only the report section is missing.
                _log.LogWarning("Archive: could not read prisma-report.json: {Message}", SanitizeLogMessage(ex.Message));
            }
        }

        // The reviewer's checked edits (report-edits.json) go into main.tex; a run without edits is written as before.
        report = ReportEdits.Apply(report, LoadReportEdits(sessionId));
        return GenerateWorkspaceArchive(sessionId, report, state.SynthesizedRecords, state);
    }

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

    // NOTE: historically named GenerateManuscriptPdf, but it never compiled a PDF - it always returned a
    // zip bundle (LaTeX source + JSON ledger + source PDFs). Renamed to match what it actually produces.
    public byte[] GenerateWorkspaceArchive(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records, ReviewState? state = null)
    {
        // The LaTeX below interpolates decimals (pie-slice angles, axis limits). On a machine with a Danish
        // (or any comma-decimal) culture those came out as "51,43", which breaks the TikZ syntax, so the
        // document is always written with the invariant culture.
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return BuildWorkspaceArchive(sessionId, report, records, state);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private byte[] BuildWorkspaceArchive(Guid sessionId, PrismaReport report, List<IncludedPaperMetricRow> records, ReviewState? state)
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
        // The reviewer's research questions come first in the method, as published reviews list them.
        var questions = state?.ResearchQuestions ?? new List<string>();
        if (questions.Count > 0)
            methods.Insert(0, new PaperSection("Research Questions", "", string.Join("", questions.Select((q, i) =>
                $"\\noindent\\textbf{{RQ{i + 1}.}} {EscapeLatexText(q)}\\par{Environment.NewLine}"))));

        string dataLatex = DataFiguresLatex(records, state);
        string appendixLatex = AppendixLatex(records, state);

        var resultsLatex = new List<string>();
        if (state?.ThematicSynthesis is { Themes.Count: > 0 } book && (report.SynthesisSections?.Count ?? 0) > 0)
            resultsLatex.Add(EvidenceMapTable(book));
        if (report.Artifact is { Error: null } artifact)
            resultsLatex.Add(ArtifactLatex(artifact));
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

        // The declarations in the order and with the headings of the report page (issue #169).
        var declarations = new List<PaperSection>
        {
            new("Registration and Protocol", string.IsNullOrWhiteSpace(report.ProtocolItem) ? NoProtocolText : report.ProtocolItem),
            new("Support & Funding", report.SupportItem ?? ""),
            new("Availability of Data and Code", "", LatexBreaks.Allow((report.AvailabilityItem ?? "").Replace("_", @"\_"))),
        };

        var paper = new ReviewPaper
        {
            KindLabel = "AI-GENERATED SYSTEMATIC LITERATURE REVIEW",
            Title = (report.TitleItem ?? "Systematic Review Manuscript").Replace("[Source Context Anchor 1]", "").Trim(),
            ProtocolHash = string.IsNullOrWhiteSpace(report.ProtocolHash) ? "not recorded" : report.ProtocolHash,
            GeneratedAt = report.GeneratedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
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
            Summary = ConclusionSection(report),
            Declarations = declarations,
            References = records.Select(r => r.ApaCitation).ToList(),
            AppendixLatex = appendixLatex,
        };

        // The files only a systematic review generates; the core (Report/RunArchive.cs) adds the run folder's
        // files, the source texts and answers, and manifest.json with the SHA-256 of everything.
        var generated = new List<(string Path, byte[] Content)>();
        void AddText(string name, string content) => generated.Add((name, new UTF8Encoding(false).GetBytes(content)));

        AddText("main.tex", PaperLatex.Build(paper));
        AddText("references.bib", BibliographyExporter.ToBibTeX(records));
        AddText("references.ris", BibliographyExporter.ToRis(records));
        if (state != null)
            AddText("screened.ris", BibliographyExporter.ToScreenedRis(state.Phases.Screening,
                records.GroupBy(r => r.PaperId).ToDictionary(g => g.Key, g => g.First().ReferenceNumber)));

        var module = ReviewModules.Find(ReviewMethod.SystematicKey)!;
        return RunArchive.Build(sessionId, GetWorkspaceFolderPath(sessionId), generated, module.ArchiveFiles,
            state?.ProtocolHash, state?.RunSettings?.Model);
    }

    /// <summary>
    /// The Conclusion: the answer to each research question ("RQ1. question" then its answer), then the conclusion text.
    /// Null when the run wrote no conclusion.
    /// </summary>
    private PaperSection? ConclusionSection(PrismaReport report)
    {
        if (string.IsNullOrWhiteSpace(report.ConclusionItem)) return null;
        if (report.ResearchAnswers is not { Count: > 0 } answers) return new PaperSection("Conclusion", report.ConclusionItem);
        var latex = new StringBuilder();
        foreach (var a in answers)
            latex.AppendLine($"\\noindent\\textbf{{RQ{a.Number}. {EscapeLatexText(a.Question)}}} {EscapeLatexText(a.Answer)}\\par\\smallskip");
        latex.AppendLine(EscapeLatexText(report.ConclusionItem));
        return new PaperSection("Conclusion", report.ConclusionItem, latex.ToString());
    }
}
