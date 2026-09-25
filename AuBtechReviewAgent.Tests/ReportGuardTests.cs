using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class ReportGuardTests
{
    [Fact]
    public void StylisticRewriteThatTurnsATitleIntoAParagraphIsRejected()
    {
        string original = "Agentic AI Software Frameworks: A Systematic Review";
        string rewrite = "Agent-based AI frameworks require systematic analysis of their control loops. This review examines how autonomous multi-agent systems implement coordination, recover from failures, and enforce safety constraints. The focus remains on design patterns.";

        Assert.NotNull(StylisticRefinerUtility.CheckRewrite(original, rewrite));
    }

    [Fact]
    public void StylisticRewriteThatDropsACitationIsRejected()
    {
        Assert.Equal("Rewrite changed the inline [n] citations.",
            StylisticRefinerUtility.CheckRewrite("Layers are separated [5].", "Layers are kept apart."));
    }

    [Fact]
    public void StylisticRewriteThatChangesANumberIsRejected()
    {
        Assert.Equal("Rewrite added, removed or changed a number.",
            StylisticRefinerUtility.CheckRewrite("We included 5 studies from 2025.", "We included five studies from 2025."));
    }

    [Fact]
    public void OrdinaryCopyEditIsAccepted()
    {
        Assert.Null(StylisticRefinerUtility.CheckRewrite(
            "Moreover, it is worth noting that the framework is pivotal for 3 domains [2].",
            "The framework matters for 3 domains [2]."));
    }

    [Fact]
    public void ParagraphTitleFallsBackToNeutralTitle()
    {
        string title = PrismaReviewEngine.CleanTitle(
            "Agent-based AI frameworks require analysis. This review examines coordination. The focus remains on patterns.",
            "Agentic AI software frameworks");

        Assert.Equal("Agentic AI software frameworks: A Systematic Literature Review", title);
    }

    [Fact]
    public void NormalTitleIsKeptWithoutTrailingFullStop()
    {
        Assert.Equal("Agentic AI Software Frameworks: A Systematic Review",
            PrismaReviewEngine.CleanTitle("Agentic AI Software Frameworks: A Systematic Review.", "q"));
    }

    [Theory]
    [InlineData("YOUR_IEEE_API_KEY_PLACEHOLDER", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc123realkey", true)]
    public void PlaceholderApiKeysDoNotCountAsKeys(string? key, bool usable)
    {
        Assert.Equal(usable, SourceCatalog.IsUsableKey(key));
    }

    [Fact]
    public void JournalNameNormalisationMatchesAmpersandsAndPunctuation()
    {
        Assert.Equal(JournalRankingMatcher.Normalize("\"IEEE Transactions on Software Engineering\""),
                     JournalRankingMatcher.Normalize("IEEE transactions on software engineering."));
        Assert.Equal("science and engineering", JournalRankingMatcher.Normalize("Science & Engineering"));
    }

    [Fact]
    public void LatexUsesDotDecimalsEvenOnDanishCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("da-DK");
            var engine = new PrismaReviewEngine("test-key", "");
            var records = Enumerable.Range(1, 7).Select(i => new IncludedPaperMetricRow
            {
                Title = $"Paper {i}", ApaCitation = $"Author{i}, A. (2025). Paper {i}.", Year = 2025,
                VenueType = i % 2 == 0 ? "Journals" : "Preprints", Category = i % 3 == 0 ? "IEEE" : "Arxiv", ReferenceNumber = i
            }).ToList();

            byte[] zip = engine.GenerateWorkspaceArchive(Guid.NewGuid(), new PrismaReport(), records);
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
            string tex = new StreamReader(archive.GetEntry("main.tex")!.Open()).ReadToEnd();

            // 360/7 degrees per slice must be written as 51.43..., never 51,43...
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(tex, @"\(\d+,\d+:1\.2cm\)"));
            Assert.Contains("xmax=1.5", tex);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void BibliographyFollowsReferenceNumbers()
    {
        var engine = new PrismaReviewEngine("test-key", "");
        var records = new List<IncludedPaperMetricRow>
        {
            new() { Title = "C", ApaCitation = "Setiawan, L. (2026). C.", Year = 2026, VenueType = "Preprints", Category = "Arxiv", ReferenceNumber = 3 },
            new() { Title = "A", ApaCitation = "Bandi, A. (2025). A.", Year = 2025, VenueType = "Journals", Category = "Scholar", ReferenceNumber = 1 },
            new() { Title = "B", ApaCitation = "Pajo, P. (2025). B.", Year = 2025, VenueType = "Preprints", Category = "ResearchGate", ReferenceNumber = 2 },
        };

        byte[] zip = engine.GenerateWorkspaceArchive(Guid.NewGuid(), new PrismaReport(), records);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        string tex = new StreamReader(archive.GetEntry("main.tex")!.Open()).ReadToEnd();

        int b = tex.IndexOf(@"\bibitem{ref1} Bandi");
        int p = tex.IndexOf(@"\bibitem{ref2} Pajo");
        int s = tex.IndexOf(@"\bibitem{ref3} Setiawan");
        Assert.True(b > 0 && b < p && p < s);
    }
}
