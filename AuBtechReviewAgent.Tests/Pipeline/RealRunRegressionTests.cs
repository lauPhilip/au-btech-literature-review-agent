using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Cases taken from a real run (2026-09-28, "Agentic AI software frameworks"): quotes the citation check
/// wrongly rejected, Markdown emphasis in the discussion, and a record screened without an abstract.
/// </summary>
public class RealRunRegressionTests
{
    private const string Aluri =
        "Agentic AI goes beyond AI-assisted programming, allowing AI systems to autonomously decompose their work, make decisions based on context, reason together, use tools dynamically, and learn continuously.";

    [Fact]
    public void QuoteShortenedWithAnEllipsisIsAcceptedWhenEveryPartIsInTheText()
    {
        Assert.True(CitationSupportChecker.QuoteOccursIn(
            "Agentic AI goes beyond AI-assisted programming, allowing AI systems to autonomously decompose their work... and use tools dynamically.", Aluri));
        Assert.True(CitationSupportChecker.QuoteOccursIn(
            "...proposing a reference-tracking safety controller to ensure that each agent’s output remains within a shrinking safety set",
            "while proposing a reference-tracking safety controller to ensure that each agent's output remains within a shrinking safety set in the control layer."));
    }

    [Fact]
    public void EllipsisPartsMustAppearInOrderAndBeMoreThanKeywords()
    {
        // Parts swapped round.
        Assert.False(CitationSupportChecker.QuoteOccursIn("use tools dynamically... Agentic AI goes beyond AI-assisted programming", Aluri));
        // Only short keywords around the ellipses.
        Assert.False(CitationSupportChecker.QuoteOccursIn("Agentic... tools... learn", Aluri));
    }

    [Fact]
    public void PdfLineBreakHyphensAndLigaturesDoNotBreakAQuote()
    {
        string pdfText = "In practice, differences in termi- nology and back-\nground between sub-communities have resulted in a certain degree of frag­mentation, with occasional reinvention of existing ideas under neighboring ﬁelds.";
        Assert.True(CitationSupportChecker.QuoteOccursIn(
            "differences in terminology and background between sub-communities have resulted in a certain degree of fragmentation", pdfText));
        Assert.True(CitationSupportChecker.QuoteOccursIn("reinvention of existing ideas under neighboring fields", pdfText));
    }

    [Fact]
    public void AParaphraseIsStillRejected()
    {
        const string abstract3 = "These challenges include translating natural language requirements into design models, resolving ambiguities, maintaining consistency within and between design models, and managing conflicts and inconsistencies when merging different versions of a design model.";
        Assert.False(CitationSupportChecker.QuoteOccursIn("refining conflict resolution mechanisms.", abstract3));
    }

    [Fact]
    public void MarkdownEmphasisIsRemovedButTheWordsStay()
    {
        string text = "Emerging patterns suggest a *proposed* convergence [1][3]. \n\n**Integration Challenges:** The trade-offs reveal limits [2].";
        string clean = MethodsSectionWriter.StripMarkdownEmphasis(text);
        Assert.Equal("Emerging patterns suggest a proposed convergence [1][3]. \n\nIntegration Challenges: The trade-offs reveal limits [2].", clean);
        // Multiplication signs and lone asterisks are left alone.
        Assert.Equal("3 * 4 and a*b", MethodsSectionWriter.StripMarkdownEmphasis("3 * 4 and a*b"));
        // After stripping, the heading starts its own sentence for the citation check.
        var sentences = CitationSupportChecker.SplitSentences(clean);
        Assert.Equal(2, sentences.Count);
        Assert.StartsWith("Integration Challenges:", sentences[1]);
    }

    [Fact]
    public void EligibilityTextHasNoDoubleFullStop()
    {
        string text = MethodsSectionWriter.Eligibility("Must focus on loop execution and agent architecture.", "Exclude agronomy.", false);
        Assert.Contains("\"Must focus on loop execution and agent architecture\".", text);
        Assert.DoesNotContain(".\".", text);
    }

    [Fact]
    public void IncludedSetParagraphIsBuiltFromTheRecords()
    {
        var records = new List<IncludedPaperMetricRow>
        {
            new() { Year = 2026, Category = "OpenAlex", VenueType = "Journals" },
            new() { Year = 2026, Category = "OpenAlex", VenueType = "Preprints" },
            new() { Year = 2023, Category = "Arxiv", VenueType = "Conferences" },
        };
        string text = MethodsSectionWriter.IncludedSet(records);
        Assert.Contains("published between 2023 and 2026, most of them in 2026", text);
        Assert.Contains("2 from OpenAlex, 1 from arXiv", text);
        Assert.Contains("1 journal, 0 transactions, 1 conference and 1 preprint or unknown", text);
    }
}
