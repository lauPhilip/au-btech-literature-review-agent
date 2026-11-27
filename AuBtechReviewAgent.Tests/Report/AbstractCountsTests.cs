using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The abstract's record counts (issue #118): the front-matter facts list every record removed before screening, and
/// an abstract that gives both the records identified and the records screened must give every removal between them.
/// </summary>
public class AbstractCountsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "abstract-counts-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static ReviewStats Stats(int identified, int duplicates, int outsideYears, int capped, int errors, int screened) => new()
    {
        TotalIdentified = identified, DuplicatesRemoved = duplicates, OutsideDateRange = outsideYears,
        CappedBeyondMaxResults = capped, ScreeningErrors = errors, Screened = screened,
    };

    [Fact]
    public void AnAbstractThatGivesIdentifiedAndScreenedMustGiveEveryRemovalBetweenThem()
    {
        var s = Stats(identified: 40, duplicates: 6, outsideYears: 3, capped: 0, errors: 0, screened: 31);

        string? problem = PrismaReviewEngine.AbstractCountsProblem("Of 40 records identified, 6 duplicates were removed and 31 were screened.", s);

        Assert.NotNull(problem);
        Assert.Contains("leaves out the records outside the year range (3)", problem);
        Assert.DoesNotContain("duplicates (6)", problem);
        Assert.Null(PrismaReviewEngine.AbstractCountsProblem("Of 40 records identified, 6 duplicates and 3 records outside the year range were removed, and 31 were screened.", s));
    }

    [Fact]
    public void EveryMissingRemovalIsNamed()
    {
        var s = Stats(identified: 50, duplicates: 4, outsideYears: 2, capped: 7, errors: 1, screened: 36);

        string? problem = PrismaReviewEngine.AbstractCountsProblem("We identified 50 records and screened 36.", s);

        Assert.Equal("the abstract gives the 50 records identified and the 36 screened but leaves out duplicates (4) and the records outside the year range (2) " +
                     "and the records over the per-source cap (7) and the records that could not be screened (1); give every removal between them, as the facts list them.", problem);
    }

    [Fact]
    public void AnAbstractWithoutBothCountsOrWithNothingRemovedIsLeftAlone()
    {
        var s = Stats(identified: 40, duplicates: 6, outsideYears: 3, capped: 0, errors: 0, screened: 31);

        Assert.Null(PrismaReviewEngine.AbstractCountsProblem("We screened 31 records and included 12 studies.", s));
        Assert.Null(PrismaReviewEngine.AbstractCountsProblem("We identified 40 records and included 12 studies.", s));
        Assert.Null(PrismaReviewEngine.AbstractCountsProblem("We identified 9 records and screened 9.", Stats(9, 0, 0, 0, 0, 9)));
        Assert.Null(PrismaReviewEngine.AbstractCountsProblem("Of the records found [40], 31 were screened.", s)); // a citation marker is not a count
    }

    /// <summary>
    /// The front-matter answers: the first abstract leaves out the record removed for its year, the repair gives it. The
    /// first one must not use the number 1 anywhere, or the check would count it as the removal.
    /// </summary>
    private static string Respond(string prompt)
    {
        if (prompt.Contains("You are writing the title, abstract, rationale and objectives"))
            return prompt.Contains("leaves out the records outside the year range (1)")
                ? """{"title":"Recovery and oversight in agent loops: a systematic review","abstract":"We identified 3 records, removed 1 published outside the year range and screened 2, then included 2 studies in 1 theme.","rationale":"Agent loops recover from faults through supervisor checks [1].","objectives":"To map how agent loops are kept safe."}"""
                : """{"title":"Recovery and oversight in agent loops: a systematic review","abstract":"We identified 3 records and screened 2, then included 2 studies in a single theme.","rationale":"Agent loops recover from faults through supervisor checks [1].","objectives":"To map how agent loops are kept safe."}""";
        return ReviewWriterTests.Respond(prompt);
    }

    [Fact]
    public async Task ARunWithARecordOutsideTheYearRangeGivesItInTheAbstract()
    {
        var model = new FakeChatService().RespondsWith(Respond);
        var engine = new PrismaReviewEngine("test-key", "", null, null, null, new RunsOptions { MaxConcurrentRuns = 1 })
        {
            WorkspaceRoot = _root,
            ChatFactory = _ => model,
            SourceFactory = _ => new PipelineTests.FakeSource(),
            FullTextFetcher = (_, _) => Task.FromResult(new FullTextResult(Array.Empty<DocumentChunk>(), "none (test)", null)),
        };
        var runId = Guid.NewGuid();

        // The fake source returns papers from 2024, 2025 and 2026; the 2024 one is outside the range. The query alone is
        // searched: with extra strings the source, short of its cap, would be asked again and return only duplicates (#562).
        await engine.RunReviewAsync(runId, new ReviewRequest("agent loops", "Map agent loop safety", "Agent architecture", "Agronomy", 3,
            SelectedSources: new[] { "arxiv" }, YearFrom: 2025, YearTo: 2026, SearchStrings: Array.Empty<string>()));

        var state = engine.LoadState(runId)!;
        Assert.Equal(1, state.Stats.OutsideDateRange);
        List<string> prompts;
        lock (model.Prompts) prompts = model.Prompts.ToList();
        string front = prompts.First(p => p.Contains("You are writing the title, abstract, rationale and objectives"));
        Assert.Contains("Records identified: 3; no duplicates were found; removed because their publication year was outside the year range (2025 to 2026): 1; records screened: 2;", front);
        Assert.Contains("Wherever both the records identified and the records screened are given, every removal between them is given too", front);
        Assert.Contains(prompts, p => p.Contains("YOUR PREVIOUS ANSWER WAS REJECTED: the abstract gives the 3 records identified and the 2 screened but leaves out the records outside the year range (1)"));

        var report = System.Text.Json.JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(Path.Join(_root, runId.ToString("N"), "prisma-report.json")))!;
        Assert.Contains("removed 1 published outside the year range and screened 2", report.AbstractItem);
    }
}
