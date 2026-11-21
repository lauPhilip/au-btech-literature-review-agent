using AuBtechReviewAgent.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// New review while a run is going and when it is done (redesign step 7): RunProgressPanel and RunSummaryCard,
/// rendered to HTML with Blazor's HtmlRenderer as the server first sends them.
/// </summary>
public class RunViewTests
{
    private static readonly Guid Run = Guid.Parse("195564c6-31be-4be8-a09d-d7967c426c74");

    private static ReviewStats Running(string step, double fraction, string detail = "") => new()
    {
        ProgressPlan = RunProgress.Plan(true, false),
        ProgressStep = step,
        ProgressFraction = fraction,
        ProgressDetail = detail,
        ProcessingStage = PrismaReviewEngine.StageSynthesizing,
    };

    private static ReviewStats Finished() => new()
    {
        ProgressPlan = RunProgress.Plan(false, false),
        ProgressStep = RunProgress.Done,
        ProcessingStage = PrismaReviewEngine.StageComplete,
        TotalIdentified = 412,
        Included = 14,
        ThemesIdentified = 4,
        StudiesCitedInSynthesis = 12,
        CitationsChecked = 60,
        CitationsSupported = 45,
        CitationsPartiallySupported = 9,
        CitationsNotSupported = 3,
        CitationsInsufficientEvidence = 2,
        CitationsUnverifiable = 1,
    };

    [Fact]
    public async Task ARunningRunShowsItsStepTheObjectiveTheBarAndTheSevenSteps()
    {
        string html = await RenderAsync<RunProgressPanel>(new()
        {
            ["Stats"] = Running(RunProgress.Extraction, 0.5, "7 of 14 studies"),
            ["Stage"] = PrismaReviewEngine.StageSynthesizing,
            ["Objective"] = "Which safety mechanisms do agent frameworks use?",
            ["SourceCount"] = 4,
        });

        Assert.Contains("Review running · step 4 of 7", html);
        Assert.Contains("<h1 id=\"run-title\"", html);
        Assert.Contains("Which safety mechanisms do agent frameworks use?</h1>", html);
        Assert.Contains("role=\"progressbar\"", html);
        Assert.Contains($"aria-valuenow=\"{RunProgress.Percent(Running(RunProgress.Extraction, 0.5))}\"", html);
        foreach (var (title, _) in ReviewSteps.All) Assert.Contains(title, html);
        Assert.Equal(3, Count(html, "(done)"));
        Assert.Equal(1, Count(html, "(running)"));
        Assert.Equal(3, Count(html, "(waiting)"));
        Assert.Equal(1, Count(html, "aria-current=\"step\""));
        // The active step shows what it is doing; Search names how many sources the run asked.
        Assert.Contains("7 of 14 studies", html);
        Assert.Contains("4 sources", html);
        Assert.Contains("On record so far", html);
        Assert.Contains(">View the live log</button>", html);
        Assert.Contains("aria-expanded=\"false\"", html);
    }

    [Fact]
    public async Task OnAPhoneOnlyTheActiveStepKeepsItsLineAndTheSideCardsAreHidden()
    {
        string html = await RenderAsync<RunProgressPanel>(new()
        {
            ["Stats"] = Running(RunProgress.Search, 0.2),
            ["Stage"] = PrismaReviewEngine.StageScreening,
            ["Running"] = true,
        });

        Assert.Equal(ReviewSteps.Count - 1, Count(html, "text-muted max-sm:hidden\">"));
        Assert.Contains("You can close this page. The review keeps running.", html);
        Assert.Contains("You can close this page</h2>", html);
        Assert.Equal(2, Count(html, "ui-card p-4 max-sm:hidden sm:p-5"));
        Assert.Contains("Your review</h1>", html);
    }

    [Fact]
    public async Task AStoppedRunMarksTheStepItStoppedAtAndShowsTheNotice()
    {
        RenderFragment notice = b => b.AddMarkupContent(0, "<p>The model did not answer.</p>");
        string html = await RenderAsync<RunProgressPanel>(new()
        {
            ["Stats"] = Running(RunProgress.Screening, 0.4),
            ["Stage"] = PrismaReviewEngine.StageFailed,
            ["Notice"] = notice,
        });

        Assert.Contains("Review stopped · step 3 of 7", html);
        Assert.Contains("(stopped here)", html);
        Assert.Contains("The model did not answer.", html);
        Assert.Contains(">stopped", html);
        Assert.DoesNotContain("You can close this page", html);
    }

    [Fact]
    public async Task AQueuedRunSaysItWaitsAndHasNoStepRunning()
    {
        string html = await RenderAsync<RunProgressPanel>(new()
        {
            ["Stats"] = new ReviewStats(),
            ["Stage"] = PrismaReviewEngine.StageQueued,
            ["QueuePosition"] = 2,
        });

        Assert.Contains("Review waiting · number 2 in line", html);
        Assert.Equal(ReviewSteps.Count, Count(html, "(waiting)"));
    }

    [Fact]
    public async Task AFinishedRunOpensWithTheOutcomeAndOnePrimaryAction()
    {
        string html = await RenderAsync<RunSummaryCard>(new()
        {
            ["Stats"] = Finished(),
            ["RunId"] = Run,
            ["Objective"] = "Which safety mechanisms do agent frameworks use?",
        });

        Assert.Contains("Review ready", html);
        Assert.Contains("Which safety mechanisms do agent frameworks use?</h1>", html);
        Assert.Contains("14 studies included from 412 records · 4 themes · 12 of 14 cited", html);
        Assert.Contains("60 citations checked against the papers", html);
        Assert.Contains("role=\"img\" aria-label=\"Of 60 citations: 45 supported, 9 partly supported, 3 not supported, 2 abstract only, 1 could not be verified.\"", html);
        Assert.Equal(1, Count(html, "ui-btn-primary"));
        Assert.Contains($"href=\"/spec-matrix/{Run}\" class=\"ui-btn-primary min-h-[52px] px-7 text-base max-sm:w-full\">Open the report</a>", html);
        Assert.Contains(">Review the 12 flagged citations</a>", html);
    }

    [Fact]
    public async Task AFinishedRunListsEverythingItProducedAndFoldsTheListOnAPhone()
    {
        string html = await RenderAsync<RunSummaryCard>(new() { ["Stats"] = Finished(), ["RunId"] = Run });

        Assert.Contains("Everything from this run", html);
        Assert.Contains($"All {RunSummaryCard.FileCount} files from this run</summary>", html);
        foreach (string href in new[] { "protocol.md", "references.bib", "references.ris", "screened.ris", "archive" })
            Assert.Equal(2, Count(html, $"href=\"/api/workspace/{Run}/{href}\""));
        Assert.Contains("Download<span class=\"sr-only\"> the archive</span>", html);
        Assert.Contains("Your review</h1>", html);
    }

    [Fact]
    public async Task AFinishedRunWithNothingFlaggedHasNoSecondButton()
    {
        var stats = Finished();
        stats.CitationsPartiallySupported = 0;
        stats.CitationsNotSupported = 0;
        string html = await RenderAsync<RunSummaryCard>(new() { ["Stats"] = stats, ["RunId"] = Run });

        Assert.DoesNotContain("flagged", html);
        Assert.Contains("Every checked citation is backed by a passage in the paper it cites.", html);
    }

    private static int Count(string text, string part)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { count++; at += part.Length; }
        return count;
    }

    private static async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters) where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            // Text written from code is HTML-encoded ("·" as &#xB7;); decoded, the tests read as the page does.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
