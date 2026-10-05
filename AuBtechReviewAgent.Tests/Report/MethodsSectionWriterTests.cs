using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class MethodsSectionWriterTests
{
    [Fact]
    public void SearchStrategyKeepsEveryQueryStringVerbatim()
    {
        var perspectives = new List<string>
        {
            "Agentic AI software frameworks",
            "multi-agent coordination fault tolerance",
            "agentic system orchestration design patterns",
        };

        string text = MethodsSectionWriter.SearchStrategy(perspectives[0], perspectives, 3, 2, 0);

        foreach (var p in perspectives) Assert.Contains($"\"{p}\"", text);
        Assert.Contains("at most 3 records", text);
        Assert.Contains("2 removed", text);
    }

    [Fact]
    public void SelectionProcessNeverClaimsHumanScreening()
    {
        var stats = new ReviewStats { Screened = 5, Included = 4, Excluded = 1 };

        string text = MethodsSectionWriter.SelectionProcess(false, stats);

        Assert.Contains("No human reviewer took part in screening", text);
        Assert.DoesNotContain("manually", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Of 5 records screened, 4 were included and 1 excluded", text);
    }

    [Fact]
    public void EligibilityStatesPeerReviewFilterHonestly()
    {
        Assert.Contains("filter was disabled", MethodsSectionWriter.Eligibility("inc", "exc", false));
        Assert.Contains("filter was enabled", MethodsSectionWriter.Eligibility("inc", "exc", true));
    }

    [Fact]
    public void InformationSourcesListsOnlyWhatWasSearchedAndFlagsMissingKeys()
    {
        var logs = new List<PlatformSearchLog>
        {
            new() { SourceName = "arXiv API", Status = "Completed Successfully" },
        };

        string text = MethodsSectionWriter.InformationSources(
            new[] { "arXiv", "IEEE Xplore" }, new[] { "IEEE Xplore" }, logs, new DateTime(2026, 9, 25));

        Assert.Contains("searched on 2026-09-25 (UTC): arXiv.", text);
        Assert.Contains("IEEE Xplore was selected but could not be searched", text);
        Assert.DoesNotContain("Scopus", text);
    }

    [Fact]
    public void ProtocolHashChangesWithTheProtocol()
    {
        string a = MethodsSectionWriter.ComputeProtocolHash("q", "o", "i", "e", new[] { "q" }, new[] { "arxiv" }, 3, false);
        string b = MethodsSectionWriter.ComputeProtocolHash("q", "o", "i", "e", new[] { "q" }, new[] { "arxiv" }, 3, false);
        string c = MethodsSectionWriter.ComputeProtocolHash("q", "o", "i", "e", new[] { "q" }, new[] { "arxiv", "ieee" }, 3, false);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(8, a.Length);
    }
}
