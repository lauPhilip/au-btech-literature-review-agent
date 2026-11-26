using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The printed report's front page (wiki page 06): keywords derived in code, the article-info facts from the run's
/// files, the running header's CSS and the reference to cite. It has a journal's layout but no journal's identity.
/// </summary>
public class ArticleInfoTests
{
    [Fact]
    public void KeywordsTakeTheTopicThenThemesAndSearchPhrasesInTurn()
    {
        var keywords = ArticleKeywords.From("agentic AI software frameworks",
            new[] { "agentic AI software frameworks", "autonomous agent orchestration platforms", "fault-tolerant multi-agent architectures" },
            new[] { "Defect Reduction and Compliance", "Scenario Handling and Evaluation" });

        Assert.Equal(new[]
        {
            "agentic AI software frameworks", "defect reduction and compliance", "scenario handling and evaluation",
            "autonomous agent orchestration platforms", "fault-tolerant multi-agent architectures",
        }, keywords);
    }

    [Fact]
    public void KeywordsSplitBooleanSearchStringsAndDropRepeats()
    {
        var keywords = ArticleKeywords.From("Context engineering for LLM agents",
            new[] { "TITLE-ABS-KEY(\"context engineering\" OR \"context window management\") AND agent*", "LLM agents; prompt caching" },
            new[] { "Context Window Management", "MCP servers" });

        // "context engineering", "agent" and "LLM agents" sit inside the topic, and the theme repeats a phrase of the search string.
        Assert.Equal(new[] { "context engineering for LLM agents", "context window management", "MCP servers", "prompt caching" }, keywords);
        Assert.DoesNotContain(keywords, k => k.Contains('"') || k.Contains('*') || k.Contains("OR") || k.Contains("TITLE"));
    }

    [Fact]
    public void KeywordsStopAtSevenAndSkipLongOrEmptyPhrases()
    {
        var themes = Enumerable.Range(1, 12).Select(i => $"theme number {i}");
        var keywords = ArticleKeywords.From("a phrase that is far too long to be a keyword", new[] { "the of and", "" }, themes);

        Assert.Equal(ArticleKeywords.Max, keywords.Count);
        Assert.Equal("theme number 1", keywords[0]);
        Assert.Empty(ArticleKeywords.From(null, null, null));
    }

    [Fact]
    public void TheRunningHeaderEscapesEveryCharacterThatCouldEndTheRule()
    {
        var info = new ArticleInfo { RunId = Guid.Parse("c5ff8a41-4f72-43c5-a5bd-631fef922e62"), Title = "Agents \"quoted\" }</style><script>x</script>: a review" };

        string css = info.RunningHeaderCss();

        Assert.DoesNotContain("<", css);
        Assert.DoesNotContain("}</", css);
        Assert.Contains("@page :first { @top-left { content: none; }", css);
        Assert.Contains(ArticleInfo.CssString("TraceableAI · run c5ff8a41"), css);
        Assert.Equal("\"Agents \\22  \\3C \"", ArticleInfo.CssString("Agents \" <"));
    }

    [Fact]
    public void TheShortTitleIsThePartBeforeTheColonOrCutAtAWord()
    {
        Assert.Equal("Safety Mechanisms in Agentic AI Frameworks", new ArticleInfo { Title = "Safety Mechanisms in Agentic AI Frameworks: A Systematic Literature Review" }.ShortTitle);
        Assert.Equal("Short: a title", new ArticleInfo { Title = "Short: a title" }.ShortTitle);
        string cut = new ArticleInfo { Title = string.Join(' ', Enumerable.Repeat("word", 30)) }.ShortTitle;
        Assert.True(cut.Length <= 61);
        Assert.EndsWith("word…", cut);
    }

    [Fact]
    public void TheSystematicFrontPageComesFromTheLedger()
    {
        var runId = Guid.NewGuid();
        var state = new ReviewState
        {
            SearchQuery = "agent loops",
            SearchPerspectives = { "agent loops", "agent safety" },
            Timestamp = new DateTime(2026, 11, 19, 19, 4, 1, DateTimeKind.Utc),
            CompletedUtc = new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc),
            SearchLogs = { new PlatformSearchLog { Timestamp = "2026-11-19 19:04:19 UTC" } },
            Stats = new ReviewStats { TotalIdentified = 6, Included = 4 },
            ProtocolSha256 = new string('a', 64),
            RunSettings = new RunSettingsRecord { Model = "large", FastModel = "small", AppVersion = "1.0.0+40b00a0c59f6b49e121c30d1d6d98ac8f3d3c961" },
            ThematicSynthesis = new ThematicCodebook { Themes = { new Theme { Name = "Defect reduction" } } },
        };

        var info = ArticleInfo.ForSystematic(runId, state, new PrismaReport(), "A title", "An abstract.", hasProtocol: true);

        // Six records found make a pilot search, so the kicker says so (issue #554); a full search keeps "Systematic review".
        Assert.Equal(("Pilot systematic search", "PRISMA 2020"), (info.Kind, info.Guideline));
        state.Stats.TotalIdentified = 400;
        Assert.Equal("Systematic review", ArticleInfo.ForSystematic(runId, state, new PrismaReport(), "A title", "An abstract.", hasProtocol: true).Kind);
        Assert.Equal((6, 4, "studies included"), (info.Found, info.Included, info.IncludedLabel));
        Assert.Equal(new[] { "large", "small" }, info.Models);
        Assert.Equal("19 November 2026", ArticleInfo.Date(info.SearchedUtc));
        Assert.Equal("20 November 2026", ArticleInfo.Date(info.CompletedUtc));
        Assert.Equal(new[] { "agent loops", "defect reduction", "agent safety" }, info.Keywords);
        Assert.Equal(new string('a', 64), info.ProtocolFingerprint);
        Assert.Equal("TraceableAI v1.0.0 (40b00a0)", info.ToolLine);
        Assert.Equal($"https://example.org/api/workspace/{runId}/archive", ArticleInfo.Absolute("https://example.org/", info.ArchivePath));
        Assert.Equal($"TraceableAI. (2026). A title [Pilot systematic search, run {runId.ToString("N")[..8]}; not peer reviewed]. Version 1.0.0. https://example.org/spec-matrix/{runId}",
            info.HowToCite("https://example.org/"));
    }

    [Fact]
    public void TheMultivocalFrontPageComesFromTheRunsFiles()
    {
        var input = MultivocalPaperTests.Input();
        var paper = new MultivocalPaperFile { Title = "Context: a review", Abstract = "Text.", Model = "large", CheckedUtc = new DateTime(2026, 10, 7), AppVersion = "0.4.1" };

        var info = MultivocalArticleInfo.For(input, paper, hasProtocol: false);

        Assert.Equal(("Grey literature review", "Garousi et al. (2019)"), (info.Kind, info.Guideline));
        Assert.Equal(5, info.Found);
        Assert.Equal(MultivocalPaperParts.Flow(input).Extracted, info.Included);
        Assert.Equal("abc123", info.ProtocolFingerprint);
        Assert.Contains("short contexts", info.Keywords);
        Assert.Equal($"mlr-paper/{input.Planned.RunId}", info.ReportPath);
        Assert.False(info.HasProtocol);
        Assert.DoesNotContain("doi", info.HowToCite("https://example.org/"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AGalleryCopyNamesItsOwnAddressAndLinksNowhereIntoTheRun()
    {
        var runId = Guid.NewGuid();
        var own = ArticleInfo.ForSystematic(runId, null, new PrismaReport(), "T", "A.", hasProtocol: false);
        var copy = ArticleInfo.ForSystematic(runId, null, new PrismaReport(), "T", "A.", hasProtocol: false, galleryPath: "/gallery/abc/v/2");
        Assert.True(own.LinksToRun);
        Assert.Equal($"spec-matrix/{runId}", own.ReportPath);
        Assert.False(copy.LinksToRun);
        Assert.Equal("gallery/abc/v/2", copy.ReportPath);
    }
}
