using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The report page's contents list and its citation-check filters (Components/Paper/ReportOutline.cs), and the
/// counts behind the stacked verdict bar (Components/Paper/VerdictCounts.cs). Redesign step 8, issues #198 to #200.
/// </summary>
public class ReportOutlineTests
{
    private static PrismaReport Report(bool conclusion, params string[] themes)
    {
        var report = new PrismaReport { ConclusionItem = conclusion ? "We conclude [1]." : "" };
        for (int i = 0; i < themes.Length; i++)
            report.SynthesisSections.Add(new SynthesisSection { Field = $"synthesis-{i + 1}", Heading = themes[i], Text = "Text [1]." });
        return report;
    }

    [Fact]
    public void TheContentsListEverySectionInOrderWithTheThemesUnderSectionFour()
    {
        var outline = ReportOutline.Systematic(Report(true, "Guardrails", "Recovery"));

        Assert.Equal(new[]
        {
            "Abstract", "1 Introduction", "2 Methodology", "3 Included studies", "4 Results and synthesis",
            "4.2 Guardrails", "4.3 Recovery", "5 Discussion", "6 Conclusion", "7 Declarations", "References", "Appendix",
        }, outline.Select(e => e.Label));
        Assert.Equal(new[] { "s4-2", "s4-3" }, outline.Where(e => e.Level == 2).Select(e => e.Id));
        Assert.Equal(outline.Count, outline.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void AnOlderRunWithoutAConclusionHasDeclarationsAsSectionSix()
    {
        var report = Report(false);
        var outline = ReportOutline.Systematic(report);

        Assert.DoesNotContain(outline, e => e.Id == ReportOutline.ConclusionId);
        Assert.Contains(outline, e => e.Label == "6 Declarations");
        Assert.Equal(6, ReportOutline.DeclarationsNumber(report));
    }

    [Fact]
    public void AThemeWithoutAHeadingIsNamedByItsNumber()
    {
        var outline = ReportOutline.Systematic(Report(true, " "));
        Assert.Contains(outline, e => e.Label == "4.2 Theme 1");
    }

    private static CitationSupportResult Check(string field, int sentence, int reference, string verdict, string basis = "full text") =>
        new() { Field = field, SentenceIndex = sentence, Reference = reference, Verdict = verdict, EvidenceBasis = basis };

    [Fact]
    public void TheFiltersPickTheirCitationsInReadingOrder()
    {
        var checks = new[]
        {
            Check("discussionItem", 0, 2, CitationSupportChecker.NotSupported),
            Check("synthesis-1", 1, 3, CitationSupportChecker.Partial),
            Check("synthesis-1", 0, 1, CitationSupportChecker.Supported, "abstract only"),
            Check("synthesisResultsItem", 0, 4, CitationSupportChecker.InsufficientEvidence, "abstract only"),
        };
        int Rank(string f) => CitationView.FieldRank(f, new[] { "synthesis-1" });

        Assert.Equal(new[] { 3, 2 }, ReportOutline.InOrder(checks, ReportOutline.Flagged, Rank).Select(c => c.Reference));
        Assert.Equal(new[] { 4, 1 }, ReportOutline.InOrder(checks, ReportOutline.AbstractOnly, Rank).Select(c => c.Reference));
        Assert.Equal(new[] { 4, 1, 3, 2 }, ReportOutline.InOrder(checks, ReportOutline.All, Rank).Select(c => c.Reference));
        Assert.Equal("Next flagged citation", ReportOutline.NextLabel(ReportOutline.Flagged));
        Assert.Equal("Next citation", ReportOutline.NextLabel(ReportOutline.All));
    }

    [Fact]
    public void TheVerdictCountsComeFromTheAuditAndReadAsOneSentence()
    {
        var counts = VerdictCounts.From(new[]
        {
            Check("a", 0, 1, CitationSupportChecker.Supported),
            Check("a", 0, 2, CitationSupportChecker.Supported),
            Check("a", 1, 1, CitationSupportChecker.Partial),
            Check("a", 2, 1, CitationSupportChecker.NotSupported),
            Check("a", 3, 1, CitationSupportChecker.Unverifiable),
        });

        Assert.Equal(new VerdictCounts(2, 1, 1, 0, 1), counts);
        Assert.Equal(5, counts.Total);
        Assert.Equal(2, counts.Flagged);
        Assert.Equal("Of 5 citations: 2 supported, 1 partly supported, 1 not supported, 1 could not be verified.", counts.Sentence());
    }

    [Fact]
    public void TheDoneCardStillReadsTheRunsOwnTotal()
    {
        var stats = new ReviewStats { CitationsChecked = 60, CitationsSupported = 45, CitationsPartiallySupported = 9, CitationsNotSupported = 3, CitationsInsufficientEvidence = 2, CitationsUnverifiable = 1 };
        Assert.Equal("Of 60 citations: 45 supported, 9 partly supported, 3 not supported, 2 abstract only, 1 could not be verified.",
            VerdictCounts.From(stats).Sentence(stats.CitationsChecked));
    }
}
