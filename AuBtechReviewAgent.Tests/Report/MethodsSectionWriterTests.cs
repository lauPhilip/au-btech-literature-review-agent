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

    [Fact]
    public void TheLimitationsOfTheReviewProcessAreWrittenFromWhatTheRunDid()
    {
        var state = new ReviewState
        {
            DualScreeningRequested = true, HumanScreeningReviewRequested = true, MaxResultsPerSource = 5, YearFrom = 2020, YearTo = 2026,
            UnavailableSources = new List<string> { "ieee" },
            Stats = new ReviewStats { DualScreened = 20, HumanReviewed = 4, Included = 8, FullTextRetrieved = 5 },
        };
        string text = MethodsSectionWriter.ReviewProcessLimitations(state, "mistral-small-latest (Mistral API)", "mistral-large-latest (Mistral API)");

        Assert.Contains("screened by the language model mistral-small-latest (Mistral API), twice with independent prompts, and a human reviewer checked 4 of the decisions.", text);
        Assert.Contains("Of the 8 included studies, 3 were read from their abstracts only", text);
        Assert.Contains("at most 5 records per source, published from 2020 to 2026.", text);
        Assert.Contains("IEEE Xplore could not be searched.", text);
        Assert.Contains("written by the language model mistral-large-latest (Mistral API);", text);
    }

    [Fact]
    public void TheLimitationsOfTheReviewProcessSayWhenNoPersonCheckedAndNothingWasReadInFull()
    {
        var single = new ReviewState { MaxResultsPerSource = 3, Stats = new ReviewStats { Included = 1 } };
        string text = MethodsSectionWriter.ReviewProcessLimitations(single, "m", "m");
        Assert.Contains("screened by the language model m, and no human reviewer checked the decisions within the tool.", text);
        Assert.Contains("No open-access full text was found for the included study, so it was read from its abstract only.", text);
        Assert.DoesNotContain("published from", text);
        Assert.DoesNotContain("could not be searched", text);

        var all = new ReviewState { Stats = new ReviewStats { Included = 4, FullTextRetrieved = 4 } };
        Assert.Contains("All 4 included studies were read in full text.", MethodsSectionWriter.ReviewProcessLimitations(all, "m", "m"));
    }
}
