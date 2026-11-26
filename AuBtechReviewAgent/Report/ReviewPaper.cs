using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// One section of a paper: a heading and its plain text, which the LaTeX builder escapes. A section whose text is
/// already LaTeX, built in code, carries it in <see cref="Latex"/> instead.
/// </summary>
public sealed record PaperSection(string Heading, string Text, string? Latex = null);

/// <summary>
/// The paper of a review, in one shape for every kind of review (module design M9). The shared parts are the same
/// for all of them: the title, abstract, introduction, results by theme, discussion, declarations and references.
/// What a module does differently goes into its own slots: its method sections, its data figures and tables, the
/// full-width results blocks (an evidence map, an artifact) and the notes after the discussion. Everything a module
/// puts in a LaTeX slot is drawn in code, never by the model.
/// </summary>
public sealed class ReviewPaper
{
    /// <summary>The line above the title, e.g. "AI-GENERATED SYSTEMATIC LITERATURE REVIEW".</summary>
    public string KindLabel { get; init; } = "";
    public string Title { get; init; } = "";
    public string Affiliation { get; init; } = "Department of Business Development and Technology, Aarhus University";
    public string ProtocolHash { get; init; } = "not recorded";
    public string GeneratedAt { get; init; } = "";
    public string Abstract { get; init; } = "";
    public string Rationale { get; init; } = "";
    public string Objectives { get; init; } = "";

    /// <summary>The method, one subsection per part, written in code from the run's ledger.</summary>
    public List<PaperSection> Methods { get; init; } = new();

    /// <summary>The data and collection section: its opening text, then its figures and tables as LaTeX built in code.</summary>
    public string DataHeading { get; init; } = "Data & Collection Metrics";
    public string DataText { get; init; } = "";
    public string DataLatex { get; init; } = "";

    /// <summary>The results: an overview, one subsection per theme, a note on which sources are cited, and full-width blocks.</summary>
    public string ResultsHeading { get; init; } = "Results & Synthesis";
    public string ResultsText { get; init; } = "";

    /// <summary>A heading over the results' opening text, so the first theme gets the same number as on the report page
    /// ("4.1 Overview of the synthesis", then 4.2 for the first theme, issue #169); none when null.</summary>
    public string? ResultsOverviewHeading { get; init; }
    public List<PaperSection> Themes { get; init; } = new();
    public string? CoverageNote { get; init; }
    public List<string> ResultsLatex { get; init; } = new();

    public string Discussion { get; init; } = "";

    /// <summary>A heading over the discussion's opening text ("General interpretation"), as on the report page; none when null.</summary>
    public string? DiscussionOpeningHeading { get; init; }

    /// <summary>Subsections after the discussion, such as the automated citation check.</summary>
    public List<PaperSection> DiscussionNotes { get; init; } = new();

    /// <summary>
    /// A section of its own after the discussion, when the module has one: the multivocal review's summary for
    /// practitioners (G14). The systematic review has none.
    /// </summary>
    public PaperSection? Summary { get; init; }

    /// <summary>A conclusion after <see cref="Summary"/>, when the module writes one apart (the multivocal review); the systematic review's is its <see cref="Summary"/>.</summary>
    public PaperSection? Conclusion { get; init; }

    /// <summary>Support, registration and protocol, availability.</summary>
    public List<PaperSection> Declarations { get; init; } = new();

    /// <summary>The reference list, in reference-number order, as plain text.</summary>
    public List<string> References { get; init; } = new();

    /// <summary>
    /// Tables that come after the references, under the heading "Appendix", as LaTeX built in code: the systematic
    /// review's tables of the included studies (Tables A.1 to A.3, issue #139). Empty means no appendix.
    /// </summary>
    public string AppendixLatex { get; init; } = "";

    /// <summary>
    /// The citation keys of <see cref="References"/>, in the same order: the keys of the run's references.bib, so a
    /// reader can swap the typeset list for BibTeX and keep every \cite. Empty, or of another length, means "ref1", "ref2", ...
    /// </summary>
    public List<string> ReferenceKeys { get; init; } = new();

    /// <summary>The front page's facts (the print front page, #525): the author line, keywords, run and tool version; none in an old test.</summary>
    public ArticleInfo? Article { get; init; }
}
