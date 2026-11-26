using System;
using System.Collections.Generic;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// The declarations every paper carries that the tool cannot know, written in code as placeholders for the authors
/// (competing interests, author contributions, funding, the author line with ORCID iDs), the one it can (ethics: a
/// review of published sources has no participants), and the statement on AI use in the shapes publishers ask for.
/// Nothing here names the site's own department: whoever runs a review writes their own affiliation and funding.
/// </summary>
public static class PaperDeclarations
{
    /// <summary>The text of a declaration only the authors can make.</summary>
    public const string ToComplete = "To be completed by the authors.";

    /// <summary>The line under the title, in place of an affiliation the tool cannot know.</summary>
    public const string AuthorLine = "Author names, affiliations and ORCID iDs (0000-0000-0000-0000): to be completed by the authors";

    /// <summary>Who answers for the output. It does not claim that anyone has checked it.</summary>
    public const string Responsibility = "The person who ran this review is responsible for checking this output and for how it is used.";

    /// <summary>
    /// The funding text for the paper: what the run recorded, or the placeholder when it recorded none or recorded the
    /// old fallback, which told the reader how to set a configuration value.
    /// </summary>
    public static string Funding(string? recorded)
    {
        string text = (recorded ?? "").Trim();
        bool empty = text.Length == 0 || text == "Pending...";
        bool configHint = text.Contains("Report:SupportStatement", StringComparison.Ordinal)
            || text.StartsWith("No funding statement has been declared", StringComparison.Ordinal);
        return empty || configHint ? ToComplete : text;
    }

    /// <summary>Competing interests, ethics and author contributions, in that order.</summary>
    public static List<PaperSection> Placeholders() => new()
    {
        new("Competing Interests", ToComplete + " Declare any financial or personal relationship that could be seen to influence the work, or state that there is none."),
        new("Ethics Approval", "Not required; the review involved no human participants and used only published or publicly available sources."),
        new("Author Contributions", ToComplete + " Give each author's roles in the CRediT taxonomy, for example Conceptualization, Methodology, Investigation, Writing – original draft and Writing – review & editing."),
    };

    /// <summary>
    /// The statement on AI use in the three shapes publishers ask for, as an appendix in LaTeX: Elsevier's separate
    /// declaration, an acknowledgement for IEEE and ACM, and a paragraph for the methods (Springer Nature, Wiley).
    /// The authors copy the one their venue wants. All text is escaped; it is written in code from the run.
    /// </summary>
    /// <param name="used">What the authors used, e.g. "TraceableAI (version 0.5.1) with the large language model mistral-large-latest".</param>
    /// <param name="purpose">What for, e.g. "search for, screen and extract data from studies".</param>
    /// <param name="acknowledgement">The one-sentence version.</param>
    /// <param name="methods">The full statement.</param>
    public static string PublisherVariantsLatex(string used, string purpose, string acknowledgement, string methods)
    {
        string elsevier = $"During the preparation of this work the authors used {used} in order to {purpose}. {Responsibility}";
        var variants = new (string Heading, string Where, string Text)[]
        {
            ("Declaration of generative AI and AI-assisted technologies in the manuscript preparation process",
                "Elsevier asks for this as a separate declaration before the references.", elsevier),
            ("Acknowledgments", "IEEE and ACM ask for the disclosure in the acknowledgements.", acknowledgement),
            ("Methods", "Springer Nature and Wiley ask for the disclosure in the methods section.", methods),
        };
        var latex = new StringBuilder();
        latex.AppendLine(@"\subsection*{Statements on the Use of AI for Submission}");
        latex.AppendLine(PaperLatex.Escape("Publishers ask for the use of AI to be disclosed in different places. Each statement below is written in code from the run's record; copy the one your venue asks for, check it and edit it to fit."));
        foreach (var (heading, where, text) in variants)
        {
            latex.AppendLine(@"\paragraph{" + PaperLatex.Escape(heading) + "}");
            latex.AppendLine(@"\textit{" + PaperLatex.Escape(where) + @"} " + PaperLatex.Escape(text) + @"\par");
        }
        return latex.ToString();
    }
}
