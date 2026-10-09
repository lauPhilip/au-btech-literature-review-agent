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
public static class MultivocalPaper
{
    public const string MainTexFile = "main.tex";

    public static string KindLabel(MultivocalPlan plan) =>
        plan.UsesFormalPool ? "AI-GENERATED MULTIVOCAL LITERATURE REVIEW" : "AI-GENERATED GREY LITERATURE REVIEW";

    /// <summary>The objectives as written, followed by the research questions from the plan, in code.</summary>
    public static string Objectives(MultivocalPlan plan, string written)
    {
        var questions = plan.Numbered().Select(q => $"{q.Number}: {q.Question.Text.Trim().TrimEnd('?')}?");
        return $"{written.Trim()} The research questions were {string.Join("; ", questions)}".Trim();
    }

    /// <summary>The paper, ready for <see cref="PaperLatex.Build"/>. The LaTeX slots need the invariant culture, which the caller sets.</summary>
    public static ReviewPaper Build(MultivocalReportInput input, MultivocalPaperFile paper, MultivocalCitationAudit? audit)
    {
        var plan = input.Planned.Plan;
        var themes = new List<PaperSection>();
        foreach (var result in paper.Results)
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

        var notes = new List<PaperSection>();
        if (audit?.SupportSummary != null) notes.Add(new PaperSection("Automated citation check", CheckNote(audit)));

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
            Abstract = paper.Abstract,
            Rationale = paper.Rationale,
            Objectives = Objectives(plan, paper.Objectives),
            Methods = MultivocalPaperParts.Methods(input),
            DataHeading = "Data and the flow of sources",
            DataText = MultivocalPaperParts.DataText(input),
            DataLatex = MultivocalPaperParts.DataLatex(input),
            ResultsHeading = "Results",
            ResultsText = ResultsText(paper),
            Themes = themes,
            Discussion = paper.Discussion,
            DiscussionNotes = notes,
            Summary = summary,
            Declarations = Declarations(input, paper),
            References = GreyReferences.Build(input.Ledger, input.Pages, input.Extraction).Select(GreyReferences.Line).ToList(),
        };
    }

    /// <summary>The opening of the results, in code: how each kind of answer was made.</summary>
    public static string ResultsText(MultivocalPaperFile paper)
    {
        int themes = paper.Results.Count(r => r.Kind == "theme");
        return $"The results answer each research question in turn. The {themes} theme section{(themes == 1 ? " was" : "s were")} written by the language model from the findings of {(themes == 1 ? "its theme" : "each theme")} only, and every citation in {(themes == 1 ? "it" : "them")} was checked against the kept page of the source it cites; " +
               "the note under each theme, on what it rests on, and the answers to questions about frequency or existence are counted in code.";
    }

    /// <summary>The citation check in words, in code, with the multivocal review's evidence named as it is.</summary>
    public static string CheckNote(MultivocalCitationAudit audit)
    {
        var s = audit.SupportSummary!;
        var inv = CultureInfo.InvariantCulture;
        if (s.Checked == 0) return "No inline citations were available for the automated check.";
        var text = new StringBuilder();
        text.Append(inv, $"An automated check compared the part of each cited sentence attributed to a source with the text of that source's page as it was kept on the access date. Of {s.Checked} citations checked, ");
        text.Append(inv, $"{s.Supported} were judged supported, {s.PartiallySupported} partly supported and {s.NotSupported} not supported; ");
        text.Append(inv, $"{s.Unverifiable} could not be verified, most often because the model's quote was not found word for word in the kept text, or because a source's kept text was missing or had changed");
        if (s.InsufficientEvidence > 0) text.Append(inv, $", and {s.InsufficientEvidence} could not be judged from the text that was checked");
        text.Append(". A supporting verdict counts only when its quote is found verbatim in the kept text. ");
        if (audit.InitialSupportSummary is { } initial && audit.Repairs.Count > 0)
            text.Append(inv, $"Before the repair, {initial.NotSupported} citations were judged not supported and {initial.PartiallySupported} partly supported; {audit.Repairs.Count} cited sentences were rewritten or lost a citation once and were checked again. ");
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
            ($"The code of the tool (version {RecordingChatCompletionService.AppVersion}) is openly available at https://github.com/lauPhilip/au-btech-literature-review-agent. The run archive contains the protocol, the raw search answers, every screening decision and quality score with its quotes, the systematic map, the extraction, the synthesis, the written paper, the citation audit and a manifest.json with the SHA-256 fingerprint of every file. The kept page texts are fingerprinted but not redistributed.")),
    };

    /// <summary>The statement on the use of AI, written in code from what the run did.</summary>
    public static string AiUse(MultivocalReportInput input, MultivocalPaperFile paper)
    {
        var inv = CultureInfo.InvariantCulture;
        string model = string.IsNullOrWhiteSpace(paper.Model) ? "a large language model" : $"the large language model {paper.Model}";
        var text = new StringBuilder();
        text.Append(inv, $"This review was conducted with TraceableAI (version {RecordingChatCompletionService.AppVersion.Split('+')[0]}), an open-source tool, following the multivocal literature review guidelines of Garousi et al. (2019), with {model}, in {paper.WrittenUtc.ToString("MMMM yyyy", inv)} (run {input.Planned.RunId}). ");
        text.Append("The protocol was written by the authors and fingerprinted before the search. ");
        text.Append("The model was used for screening the grey sources twice against the criteria, scoring their quality on the guidelines' checklist, proposing the systematic map, extracting each source's values, checking that each value's quote supports it, grouping the findings into themes, writing the theme sections, the discussion, the summary for practitioners and the title, abstract and introduction, and checking each citation against the kept page of the source it cites. ");
        text.Append("Every value, quality point and citation verdict counts only with a quote found verbatim in the kept page text; the method, the counts, the figures, the answers to questions about frequency or existence and the references were written in code. ");
        if (input.Screening.Kappa is double k) text.Append(inv, $"The two screenings agreed with Cohen's kappa = {k:0.00}. ");
        int reviewed = input.Screening.Sources.Count(s => s.Reviewed);
        text.Append(reviewed > 0 ? string.Create(inv, $"A human reviewer checked {reviewed} screening decisions within the tool. ") : "Screening decisions were not checked by a human reviewer within the tool. ");
        text.Append("The authors reviewed the output and take full responsibility for the content.");
        return text.ToString();
    }

    private static string Short(string sha) => string.IsNullOrWhiteSpace(sha) ? "(not recorded)" : sha.Length > 16 ? sha[..16] : sha;
}
