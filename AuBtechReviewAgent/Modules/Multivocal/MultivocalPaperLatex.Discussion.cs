using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

// The discussion of a multivocal paper (issues #460, #461): the answers to the research questions in one place, the
// discussion that builds on them, the parts after it, and the conclusion; a part that could not be written is said in code.
public static partial class MultivocalPaper
{
    /// <summary>The discussion's subsections, in the order the page and main.tex show them; a part not written is left out.</summary>
    public static List<PaperSection> DiscussionParts(MultivocalReportInput input, MultivocalPaperFile paper, MultivocalCitationAudit? audit)
    {
        var parts = new List<PaperSection>();
        if (paper.Answers.Count > 0) parts.Add(AnswersSection(paper));
        parts.Add(new PaperSection("Interpretation", string.IsNullOrWhiteSpace(paper.Discussion) ? MultivocalWriter.DiscussionUnwrittenText : paper.Discussion));
        void Part(string heading, string text) { if (!string.IsNullOrWhiteSpace(text)) parts.Add(new PaperSection(heading, text)); }
        Part(LimitationsHeading, paper.Limitations);
        parts.Add(new PaperSection(ReviewLimitationsHeading, ReviewLimitations(input)));
        Part(ImplicationsHeading, paper.Implications);
        Part(FutureResearchHeading, paper.FutureResearch);
        if (audit?.SupportSummary != null) parts.Add(new PaperSection("Automated citation check", CheckNote(audit)));
        return parts;
    }

    public const string AnswersHeading = "Answers to the research questions";
    public const string LimitationsHeading = "Limitations of the evidence";
    public const string ReviewLimitationsHeading = "Limitations of the review";
    public const string ImplicationsHeading = "Implications for practitioners";
    public const string FutureResearchHeading = "Implications for researchers";

    /// <summary>Each question, then its answer: the model's, checked with the discussion, or the one written in code.</summary>
    public static PaperSection AnswersSection(MultivocalPaperFile paper)
    {
        var text = new StringBuilder();
        var latex = new StringBuilder();
        foreach (var a in paper.Answers)
        {
            text.Append($"{a.Question}. {a.QuestionText} {a.Text}\n\n");
            latex.AppendLine($@"\noindent\textbf{{{PaperLatex.Escape(a.Question)}. {PaperLatex.Escape(a.QuestionText)}}} {MultivocalPaperParts.Prose(a.Text)}\par\smallskip");
        }
        return new PaperSection(AnswersHeading, text.ToString().Trim(), latex.ToString());
    }

    /// <summary>
    /// The results as the paper shows them: a question answered in code is shown once, by its answer in code, even when
    /// the paper file holds theme sections for it. A paper written before issue #458 answered a relationship question
    /// with themes that grouped every outcome; its answer is now the pairing in code.
    /// </summary>
    public static List<PaperResult> ShownResults(MultivocalReportInput input, MultivocalPaperFile paper)
    {
        var questions = input.Planned.Plan.Numbered().ToDictionary(q => q.Number, q => q.Question);
        var shown = new List<PaperResult>();
        foreach (var r in paper.Results)
        {
            if (!questions.TryGetValue(r.Question, out var q) || !MultivocalPaperParts.AnsweredInCode(q.Type)) { shown.Add(r); continue; }
            if (shown.Any(s => s.Question == r.Question)) continue;
            shown.Add(r.Kind == "code" ? r : new PaperResult
            {
                Question = r.Question, QuestionText = q.Text, Kind = "code", Field = $"answer-{r.Question}", Heading = $"{r.Question}: {q.Text}",
                Text = MultivocalPaperParts.CodeAnswerLead(input, r.Question, q),
            });
        }
        return shown;
    }

    /// <summary>The conclusion, when it was written; null otherwise (the discussion's text in code says why).</summary>
    public static PaperSection? ConclusionSection(MultivocalPaperFile paper) =>
        string.IsNullOrWhiteSpace(paper.Conclusion) ? null : new PaperSection("Conclusion", paper.Conclusion);

    /// <summary>
    /// The limitations of how the review was done, in code (issue #460): what the model did, what the grey sources are,
    /// and what the academic sources were read from. The writing prompts say this is written separately, so the model's
    /// limitations of the evidence leave it out.
    /// </summary>
    public static string ReviewLimitations(MultivocalReportInput input)
    {
        var text = new StringBuilder(
            "A language model screened the sources, scored their quality, proposed the systematic map, extracted the values, formed the themes and wrote the text; " +
            "every value and citation counts only with a quote found in the kept text, but the model's judgements were not all checked by a human reviewer. " +
            "The grey sources are not peer reviewed, and each was read as its page was kept on the access date; a page may have changed since.");
        if (input.Ledger.HasFormalPool)
        {
            var formal = MultivocalPools.For(true, input.Ledger, input.Screening, input.Quality, input.Extraction);
            if (formal.AbstractOnly > 0)
                text.Append(CultureInfo.InvariantCulture, $" Of the {formal.Extracted} academic sources extracted, {formal.AbstractOnly} were read from the abstract alone, as no open full text was found, so their findings rest on what the abstract says.");
        }
        return text.ToString();
    }

    /// <summary>The written parts the model wrote in this paper, for the statement on the use of AI: only what was written.</summary>
    public static string WrittenParts(MultivocalPaperFile paper)
    {
        var parts = new List<string> { "the theme sections" };
        if (!string.IsNullOrWhiteSpace(paper.Discussion))
            parts.Add(paper.Answers.Any(a => a.Kind == "written") ? "the answers to the research questions and the discussion" : "the discussion");
        if (!string.IsNullOrWhiteSpace(paper.Conclusion)) parts.Add("the limitations of the evidence, the implications and the conclusion");
        if (paper.SummaryItems.Count > 0) parts.Add("the summary for practitioners");
        parts.Add("the title, abstract and introduction");
        return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }
}
