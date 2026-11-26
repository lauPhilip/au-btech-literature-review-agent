using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The report page and main.tex give a theme the same name (issue #165) and their sections the same numbers,
/// headings and order (issue #169).
/// </summary>
public class SectionNumbersTests
{
    [Fact]
    public void TheOverviewAndGeneralInterpretationHeadingsNumberTheSectionsAsOnThePage()
    {
        string tex = PaperLatex.Build(new ReviewPaper
        {
            Title = "T",
            ResultsText = "Overview text.",
            ResultsOverviewHeading = "Overview of the Synthesis",
            Themes = { new PaperSection("Recovery", "Theme text.") },
            Discussion = "Discussion text.",
            DiscussionOpeningHeading = "General Interpretation",
            DiscussionNotes = { new PaperSection("Limitations of the Evidence", "Limits.") },
        });

        int results = tex.IndexOf(@"\section{Results \& Synthesis}", StringComparison.Ordinal);
        int overview = tex.IndexOf(@"\subsection{Overview of the Synthesis}", StringComparison.Ordinal);
        int theme = tex.IndexOf(@"\subsection{Recovery}", StringComparison.Ordinal);
        int discussion = tex.IndexOf(@"\section{Discussion}", StringComparison.Ordinal);
        int general = tex.IndexOf(@"\subsection{General Interpretation}", StringComparison.Ordinal);
        int limits = tex.IndexOf(@"\subsection{Limitations of the Evidence}", StringComparison.Ordinal);

        // 4.1 is the overview, so the first theme is 4.2; 5.1 is the general interpretation, so the limitations are 5.2.
        Assert.True(results < overview && overview < tex.IndexOf("Overview text.", StringComparison.Ordinal) && overview < theme);
        Assert.True(discussion < general && general < tex.IndexOf("Discussion text.", StringComparison.Ordinal) && general < limits);
    }

    [Fact]
    public void WithoutTheHeadingsTheSharedShapeIsUnchanged()
    {
        string tex = PaperLatex.Build(new ReviewPaper { Title = "T", ResultsText = "Overview text.", Discussion = "Discussion text." });

        Assert.DoesNotContain(@"\subsection{Overview", tex);
        Assert.DoesNotContain(@"\subsection{General", tex);
        Assert.Contains("\\section{Discussion}\\label{sec:discussion}\n\nDiscussion text.".Replace("\n", Environment.NewLine), tex);
    }

    [Fact]
    public async Task AThemeIsHeadedByItsCodebookNameWhateverTheModelSuggests()
    {
        var evidence = new ReviewEvidence
        {
            SourceLabel = "SOURCE",
            Sources = new[]
            {
                new EvidenceSource { Reference = 1, ReferenceLine = "Ana (2025). Keeping context short. blog.example.org", Findings = new[] { new EvidenceFinding("F1.1", "Summarisation", "We keep a running summary.") } },
            },
            Themes = new[] { new EvidenceTheme { Id = "T3", Name = "Engagement and Evaluation Methods", Description = "How engagement is measured.", Sources = new[] { 1 } } },
        };
        var profile = new WritingProfile { Review = "a grey literature review", ReviewShort = "a grey literature review", ResultsSection = "the results", Source = "source", Sources = "sources" };
        var model = new FakeChatService()
            .Returns("""{"heading":"Engagement Patterns and Evaluation Methods","text":"Sources keep a running summary [1]."}""");

        var (section, _) = await ReviewWriter.WriteThemeAsync(model, profile, "Map the practices", evidence, evidence.Themes[0], 1, referenceCount: 1);

        Assert.Equal("Engagement and Evaluation Methods", section!.Heading);
    }
}
