using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// The paper of a checked multivocal run in the shared shape (<see cref="ReviewPaper"/>, MLR G-8): the written parts
/// from mlr-paper.json, and everything else written in code from the run's files when it is built: the method by
/// the guidelines' phases, the data section, the answers in code, the notes under the themes, the citation check, the
/// declarations, the statement on the use of AI and the references. <see cref="PaperLatex.Build"/> writes it as
/// main.tex; the paper page shows the same parts.
/// </summary>
public static partial class MultivocalPaper
{
    public const string MainTexFile = "main.tex";

    public static string KindLabel(MultivocalPlan plan) =>
        plan.UsesFormalPool ? "AI-GENERATED MULTIVOCAL LITERATURE REVIEW" : "AI-GENERATED GREY LITERATURE REVIEW";

    /// <summary>
    /// The objectives: written in code from the research questions since issue #461, and so given as they are; in a paper
    /// written before, the model's objectives followed by the research questions from the plan, in code.
    /// </summary>
    public static string Objectives(MultivocalPlan plan, string written)
    {
        if (written.TrimStart().StartsWith("The review set out to answer", System.StringComparison.Ordinal)) return written.Trim();
        var questions = plan.Numbered().Select(q => $"{q.Number}: {q.Question.Text.Trim().TrimEnd('?')}?");
        return $"{written.Trim()} The research questions were {string.Join("; ", questions)}".Trim();
    }

    /// <summary>The paper, ready for <see cref="PaperLatex.Build"/>. The LaTeX slots need the invariant culture, which the caller sets.</summary>
    public static ReviewPaper Build(MultivocalReportInput input, MultivocalPaperFile paper, MultivocalCitationAudit? audit)
    {
        var plan = input.Planned.Plan;
        var themes = new List<PaperSection>();
        var shown = ShownResults(input, paper);
        foreach (var result in shown)
        {
            if (result.Kind == "theme")
            {
                string latex = MultivocalPaperParts.Prose(result.Text) + "\n\n";
                if (!string.IsNullOrWhiteSpace(result.Note)) latex += @"\par\smallskip\noindent{\small\textit{" + MultivocalPaperParts.Prose(result.Note) + "}}\n";
                themes.Add(new PaperSection($"{result.Question}: {result.Heading}", result.Text + (result.Note == null ? "" : "\n\n" + result.Note), latex));
                continue;
            }
            var question = plan.Numbered().FirstOrDefault(q => q.Number == result.Question);
            themes.Add(question.Question != null && MultivocalPaperParts.AnsweredInCode(question.Question.Type)
                ? MultivocalPaperParts.CodeAnswer(input, question.Number, question.Question)
                : new PaperSection(result.Heading, result.Text, MultivocalPaperParts.Prose(result.Text)));
        }


        PaperSection? summary = null;
        if (paper.SummaryItems.Count > 0)
        {
            var latex = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(paper.SummaryLead)) latex.AppendLine(MultivocalPaperParts.Prose(paper.SummaryLead));
            latex.AppendLine(@"\begin{itemize}");
            foreach (var item in paper.SummaryItems) latex.AppendLine(@"\item " + MultivocalPaperParts.Prose(item));
            latex.AppendLine(@"\end{itemize}");
            summary = new PaperSection("Summary for practitioners", $"{paper.SummaryLead}\n\n{string.Join("\n", paper.SummaryItems)}".Trim(), latex.ToString());
        }

        return new ReviewPaper
        {
            KindLabel = KindLabel(plan),
            Title = paper.Title,
            ProtocolHash = string.IsNullOrWhiteSpace(input.Planned.ProtocolSha256) ? "not recorded" : input.Planned.ProtocolSha256,
            GeneratedAt = paper.WrittenUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            Models = MultivocalArticleInfo.ModelsOf(input, paper),
            Abstract = paper.Abstract,
            Rationale = paper.Rationale,
            Objectives = Objectives(plan, paper.Objectives),
            Methods = MultivocalPaperParts.Methods(input),
            DataHeading = "Data and the flow of sources",
            DataText = MultivocalPaperParts.DataText(input),
            DataLatex = MultivocalPaperParts.DataLatex(input),
            ResultsHeading = "Results",
            ResultsText = ResultsText(new MultivocalPaperFile { Results = shown }),
            Themes = themes,
            // The discussion is in its parts, so the answers come first (issue #461).
            Discussion = "",
            DiscussionNotes = DiscussionParts(input, paper, audit),
            Summary = summary,
            Conclusion = ConclusionSection(paper),
            Declarations = Declarations(input, paper),
            References = GreyReferences.Build(input.Ledger, input.Pages, input.Extraction).Select(GreyReferences.Line).ToList(),
        };
    }

    /// <summary>The opening of the results, in code: how each kind of answer was made.</summary>
    public static string ResultsText(MultivocalPaperFile paper)
    {
        int themes = paper.Results.Count(r => r.Kind == "theme");
        int unwritten = paper.Results.Count(r => r.Kind == MultivocalWriter.UnwrittenKind);
        return $"The results answer each research question in turn. The {themes} theme section{(themes == 1 ? " was" : "s were")} written by the language model from the findings of {(themes == 1 ? "its theme" : "each theme")} only, and every citation in {(themes == 1 ? "it" : "them")} was checked against the kept page of the source it cites; " +
               "the note under each theme, on what it rests on, and the answers to questions about frequency, existence or a relationship are counted in code." +
               (unwritten == 0 ? "" : $" The section{(unwritten == 1 ? "" : "s")} on {unwritten} more theme{(unwritten == 1 ? "" : "s")} could not be written; {(unwritten == 1 ? "it is" : "they are")} kept in {(unwritten == 1 ? "its" : "their")} place with what {(unwritten == 1 ? "it rests" : "they rest")} on.");
    }

    /// <summary>The citation check in words, in code, with the multivocal review's evidence named as it is.</summary>
    public static string CheckNote(MultivocalCitationAudit audit)
    {
        var s = audit.SupportSummary!;
        var inv = CultureInfo.InvariantCulture;
        int removed = audit.RemovedCitations.Count;
        if (s.Checked == 0 && removed == 0) return "No inline citations were available for the automated check.";
        var text = new StringBuilder();
        text.Append(inv, $"An automated check compared the part of each cited sentence attributed to a source with the text of that source's page as it was kept on the access date. Of {s.Checked} citations checked, ");
        text.Append(inv, $"{s.Supported} were judged supported, {s.PartiallySupported} partly supported and {s.NotSupported} not supported; ");
        text.Append(inv, $"{s.Unverifiable} could not be verified, most often because the model's quote was not found word for word in the kept text, or because a source's kept text was missing or had changed");
        if (s.InsufficientEvidence > 0) text.Append(inv, $", and {s.InsufficientEvidence} could not be judged from the text that was checked");
        text.Append(". A supporting verdict counts only when its quote is found verbatim in the kept text. ");
        int firstPass = audit.Repairs.Count(r => r.Pass == 1), secondPass = audit.Repairs.Count(r => r.Pass == 2);
        if (audit.InitialSupportSummary is { } initial && audit.Repairs.Count > 0)
        {
            text.Append(inv, $"Before the repair, {initial.NotSupported} citations were judged not supported and {initial.PartiallySupported} partly supported; {firstPass} cited sentences were rewritten or lost a citation and were checked again");
            if (secondPass > 0) text.Append(inv, $", and {secondPass} that were still partly supported were rewritten a second time and checked again. ");
            else text.Append(". ");
        }
        if (removed > 0)
            text.Append(inv, $"{removed} {(removed == 1 ? "citation that was still not supported after the repair was" : "citations that were still not supported after the repair were")} removed, with the claim when no other citation backed it; they are not in the counts above and are listed in citation-audit.json. ");
        if (audit.InvalidMarkersStripped > 0)
            text.Append(inv, $"{audit.InvalidMarkersStripped} citation markers that pointed outside the reference list were removed. ");
        text.Append("Every verdict, with the quoted evidence, is listed in citation-audit.json; these are model judgements and should be spot-checked by a human reviewer.");
        return text.ToString();
    }

    /// <summary>The declarations, in code: the use of AI, the protocol, and where the code and the archive are.</summary>
    public static List<PaperSection> Declarations(MultivocalReportInput input, MultivocalPaperFile paper) => new()
    {
        new PaperSection("Use of artificial intelligence", AiUse(input, paper)),
        new PaperSection("Registration and protocol",
            $"The review was not registered. Its protocol (need and audience, the decision to include grey literature, research questions, kinds of grey literature, searches and search strings, stopping rule, criteria and quality threshold) was written on {input.Planned.CreatedUtc:yyyy-MM-dd} (UTC), before any search; its SHA-256 fingerprint begins {Short(input.Planned.ProtocolSha256)}. The file is part of the run archive."),
        new PaperSection("Availability",
            ($"The code of the tool is openly available at {ToolVersion.RepositoryUrl}. {VersionSentence(paper.AppVersion)} The run archive contains the protocol, the raw search answers, every screening decision and quality score with its quotes, the systematic map, the extraction, the synthesis, the written paper, the citation audit and a manifest.json with the SHA-256 fingerprint of every file. The kept page texts are fingerprinted but not redistributed.")),
    };

    /// <summary>The run's own version without the commit (#468), or "unknown" when its files record none.</summary>
    public static string VersionNumber(string? version) => ToolVersion.Number(version) is { Length: > 0 } number ? number : "unknown";

    /// <summary>The availability sentence on the run's own version; a run whose files record none says so.</summary>
    public static string VersionSentence(string? version) =>
        ToolVersion.Number(version).Length > 0 ? ToolVersion.PaperSentence(version) : "The run's files do not record which version of the tool made it.";

    /// <summary>The statement on the use of AI, written in code from what the run did.</summary>
    public static string AiUse(MultivocalReportInput input, MultivocalPaperFile paper)
    {
        var inv = CultureInfo.InvariantCulture;
        var others = OtherModels(input, paper);
        string model = string.IsNullOrWhiteSpace(paper.Model) ? "a large language model"
            : others.Count == 0 ? $"the large language model {paper.Model}"
            : $"the large language models {paper.Model} and {JoinList(others.Select(o => o.Model).ToList())}";
        string subject = others.Count == 0 ? "The model was" : "The models were";
        var text = new StringBuilder();
        text.Append(inv, $"This review was conducted with TraceableAI (version {VersionNumber(paper.AppVersion)}), an open-source tool, following the multivocal literature review guidelines of Garousi et al. (2019), with {model}, in {paper.WrittenUtc.ToString("MMMM yyyy", inv)} (run {input.Planned.RunId}). ");
        if (others.Count > 0)
            text.Append($"Of these, {string.Join("; ", others.Select(o => $"{o.Model} answered {JoinList(o.Tasks)}"))}, and {paper.Model} did every other step that used a model. ");
        text.Append("The protocol was written by the authors and fingerprinted before the search. ");
        if (input.Ledger.HasFormalPool)
        {
            text.Append(MultivocalFormalText.AiUseTasks(input, subject, WrittenParts(paper)));
            text.Append(MultivocalFormalText.AiUseKappa(input));
            text.Append(MultivocalFormalText.AiUseSeeds(input));
        }
        else
        {
            text.Append(subject).Append(" used for ").Append(MultivocalScreeningText.AiUseTask(input.Screening, "the grey sources", "the criteria")).Append(", scoring their quality on the guidelines' checklist, proposing the systematic map, extracting each source's values, checking that each value's quote supports it, grouping the findings into themes, writing ").Append(WrittenParts(paper)).Append(", and checking each citation against the kept page of the source it cites. ");
            text.Append("Every value, quality point and citation verdict counts only with a quote found verbatim in the kept page text; the method, the counts, the figures, the answers to questions about frequency, existence or a relationship and the references were written in code. ");
            text.Append(MultivocalScreeningText.AiUseAgreement(input.Screening, bothPools: false));
        }
        int reviewed = input.Screening.Sources.Count(s => s.Reviewed);
        text.Append(reviewed > 0 ? string.Create(inv, $"A human reviewer checked {reviewed} screening decisions within the tool. ") : "Screening decisions were not checked by a human reviewer within the tool. ");
        var kept = input.Pages.Pages.Where(p => !string.IsNullOrEmpty(p.TextFile)).ToList();
        // Only a strong flag is counted as one; a page that only names prompts is a mention (#472, lost in a merge and put back).
        var withHits = kept.Where(p => p.InjectionFlags is { Count: > 0 }).ToList();
        int flagged = withHits.Where(p => PromptSafety.AnyStrong(p.InjectionFlags)).Select(p => MultivocalSearcher.AddressKey(p.Url)).Distinct().Count();
        int mentioned = withHits.Select(p => MultivocalSearcher.AddressKey(p.Url)).Distinct().Count() - flagged;
        text.Append(AiUseStatement.FlaggedSources(flagged, kept.Select(p => MultivocalSearcher.AddressKey(p.Url)).Distinct().Count(), "kept text", "source", "sources", mentioned));
        text.Append("The authors reviewed the output and take full responsibility for the content.");
        return text.ToString();
    }

    /// <summary>
    /// The models other than the paper's that answered a step (Llm:FastModel), each with what it did, from the run's
    /// files: the screening file names the screenings' model and the paper file the citation checks' model.
    /// </summary>
    private static List<(string Model, List<string> Tasks)> OtherModels(MultivocalReportInput input, MultivocalPaperFile paper) =>
        OtherModels(input, paper.Model, paper.CheckModel);

    private static List<(string Model, List<string> Tasks)> OtherModels(MultivocalReportInput input, string mainModel, string? checkModel)
    {
        var steps = new List<(string Model, string Task)>();
        string main = mainModel.Trim();
        if (main.Length == 0) return new();
        string screening = input.Screening.Model.Trim();
        if (screening.Length > 0 && screening != main) steps.Add((screening, input.Screening.AuditSeed == null || MultivocalSecondScreening.Count(input.Screening, null).AllTwice ? "both screenings of every source" : "every screening, first and second"));
        string check = checkModel?.Trim() ?? "";
        if (check.Length > 0 && check != main) steps.Add((check, "the citation checks"));
        return steps.GroupBy(s => s.Model, StringComparer.Ordinal).Select(g => (g.Key, g.Select(s => s.Task).ToList())).ToList();
    }

    /// <summary>
    /// The data appendix's line on the models (#484), in the words of the statement on AI use: one model, or each model
    /// other than the main one with what it answered. The main model is the paper's, or the synthesis's before the paper
    /// is written; the citation checks' model is known only from the paper's file. Null when no model is on file.
    /// </summary>
    public static string? ModelsLine(MultivocalReportInput input, MultivocalPaperFile? paper)
    {
        string main = (paper?.Model is { Length: > 0 } written ? written : input.Synthesis.Model).Trim();
        if (main.Length == 0) return null;
        var others = OtherModels(input, main, paper?.CheckModel);
        return others.Count == 0
            ? $"Language model: {main}"
            : $"Language models: {string.Join("; ", others.Select(o => $"{o.Model} answered {JoinList(o.Tasks)}"))}, and {main} did every other step that used a model";
    }

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static string Short(string sha) => string.IsNullOrWhiteSpace(sha) ? "(not recorded)" : sha.Length > 16 ? sha[..16] : sha;
}
