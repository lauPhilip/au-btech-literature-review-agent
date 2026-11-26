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
        Assert.Contains("At most 3 records from each source went on to screening", text);
        Assert.Contains("2 removed", text);
    }

    [Fact]
    public void ThePerSourceLimitSaysItCountsRecordsSentToScreening()
    {
        // Issue #150: "contributed at most 5 records in total" read as if it contradicted the flow diagram,
        // which counts every record identified, duplicates and records outside the years included.
        string withYears = MethodsSectionWriter.SearchStrategy("lego serious play", new[] { "lego serious play" }, 5, 4, 0,
            yearFrom: 2020, yearTo: 2026, outsideDateRange: 2);
        Assert.Contains("At most 5 records from each source went on to screening, across all search strings.", withYears);
        Assert.Contains("Duplicates and records outside the year range did not count towards this limit", withYears);
        Assert.DoesNotContain("in total across", withYears);

        string noYears = MethodsSectionWriter.SearchStrategy("lego serious play", new[] { "lego serious play" }, 1, 0, 0);
        Assert.Contains("At most 1 record from each source went on to screening", noYears);
        Assert.Contains("Duplicates did not count towards this limit", noYears);
    }

    [Fact]
    public void TheSearchTextNamesOnlyTheStringsThatRanAsInRun289a0d3e()
    {
        // Run 289a0d3e (#562): the text said all four strings ran against every source, but the last two never ran.
        var strings = new[] { "agentic AI software frameworks", "autonomous multi-agent system frameworks", "agent orchestration error recovery", "evaluating framework fault tolerance" };
        var yields = new[]
        {
            new SearchStringYield(strings[0], 6, 6, 5), new SearchStringYield(strings[1], 6, 6, 6),
            new SearchStringYield(strings[2], 0, 0, 0), new SearchStringYield(strings[3], 0, 0, 0),
        };
        string text = MethodsSectionWriter.SearchStrategy(strings[0], strings, 2, 1, 0, 2020, 2026, 1, yields: yields);

        Assert.DoesNotContain("run against every source", text);
        Assert.Contains("\"autonomous multi-agent system frameworks\" was run against all 6 sources", text);
        Assert.Contains("\"agent orchestration error recovery\" and \"evaluating framework fault tolerance\" were not run, because every source had already filled its cap", text);
        Assert.Contains("the search strings added 5, 6 records not found by an earlier string, so the last string that was run still added new records", text);
        Assert.Contains("Duplicates and records outside the year range did not count towards this limit", text);

        var partly = new[] { yields[0], yields[1], new SearchStringYield(strings[2], 2, 2, 2), yields[3] };
        Assert.Contains("\"agent orchestration error recovery\" was run against 2 of the 6 sources, as the others had already filled their cap",
            MethodsSectionWriter.SearchStrategy(strings[0], strings, 2, 1, 0, yields: partly));
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

    [Fact]
    public void OneRecordPerSourceIsSingularInTheLimitationsAndTheProtocol()
    {
        // Issue #119: "at most 1 records per source".
        var state = new ReviewState { MaxResultsPerSource = 1, Stats = new ReviewStats { Included = 1 } };
        string limits = MethodsSectionWriter.ReviewProcessLimitations(state, "m", "m");
        Assert.Contains("The search took at most 1 record per source.", limits);

        var request = new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 1,
            SelectedSources: new[] { "arxiv" });
        string protocol = ProtocolWriter.Write(Guid.NewGuid(), request, new[] { "arXiv" }, "m", new RunsOptions(), DateTime.UtcNow);
        Assert.Contains("- At most 1 record per source;", protocol);
        Assert.DoesNotContain("records per source", protocol);
    }

    [Fact]
    public void TheCitationsOutsideAThemeAgreeWithTheirCount()
    {
        // Issue #119: "1 citation ... refer ... are flagged".
        Assert.Equal("1 citation in a theme subsection refers to a study not coded under that theme and is flagged in the audit.",
            PrismaReviewEngine.OutsideThemeSentence(1));
        Assert.Equal("3 citations in a theme subsection refer to a study not coded under that theme and are flagged in the audit.",
            PrismaReviewEngine.OutsideThemeSentence(3));
    }

    [Fact]
    public void TheProtocolItemNamesTheResearchQuestionsOnlyWhenTheRunHasThem()
    {
        // Issue #121: section 7.1 left out the research questions.
        var created = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
        string with = MethodsSectionWriter.Protocol("0123456789abcdef0123", created, hasResearchQuestions: true);
        Assert.Contains("Its protocol (search query, objective, research questions, eligibility criteria, sources, limits and screening set-up)", with);

        string without = MethodsSectionWriter.Protocol("0123456789abcdef0123", created);
        Assert.Contains("Its protocol (search query, objective, eligibility criteria, sources, limits and screening set-up)", without);
    }
}
