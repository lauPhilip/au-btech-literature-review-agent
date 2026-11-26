using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Where a reader and the owner see the paper's self-check (issue #532): the collapsed table under the citation summary
/// with its one summary line, and the same line, with a link to the table, on the Done card of a finished run.
/// </summary>
public class SelfCheckViewTests
{
    private static PaperSelfCheckFile Result() => new()
    {
        CheckedUtc = "2026-11-26 09:00:00 UTC", Passed = 1, Notes = 1, Faults = 1,
        Checks =
        {
            new PaperCheck { Number = 1, Title = "Numbers", Outcome = CheckOutcome.Pass, Summary = "Every number matches a count.", Explanation = "Looks for numbers on the wrong count." },
            new PaperCheck { Number = 2, Title = "Answers", Outcome = CheckOutcome.Note, Summary = "One answer is short.", Explanation = "Looks for questions without an answer.",
                Evidence = { new CheckEvidence("Answer to RQ2", "researchAnswer2", "<b>Too short</b>", "Three words.") } },
            new PaperCheck { Number = 3, Title = "Parts", Outcome = CheckOutcome.Fault, Summary = "No discussion.", Explanation = "Looks for missing parts." },
        },
    };

    [Fact]
    public async Task TheTableIsClosedByDefaultSaysHowTheChecksWentAndShowsEveryCheck()
    {
        string html = await RenderAsync<AuBtechReviewAgent.Components.Paper.SelfCheckTable>(new() { ["Result"] = Result() });

        Assert.Contains("<details id=\"self-check\"", html);
        Assert.DoesNotContain(" open", html.Split('>')[0]);
        Assert.Contains("The paper checked itself: 1 check passed, 1 note, 1 fault", html);
        Assert.Contains("Every number matches a count.", html);
        Assert.Contains("ui-chip-ok", html);
        Assert.Contains("ui-chip-partial", html);
        Assert.Contains("ui-chip-bad", html);
        Assert.Contains("Looks for missing parts.", html);
        Assert.DoesNotContain("rounded", html); // square corners: only buttons are rounded
    }

    [Fact]
    public async Task TheEvidenceIsShownAsTextNeverAsMarkup()
    {
        string html = await RenderAsync<AuBtechReviewAgent.Components.Paper.SelfCheckTable>(new() { ["Result"] = Result() });

        Assert.Contains("&lt;b&gt;Too short&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Too short</b>", html);
        Assert.Contains("Three words.", html);
    }

    [Fact]
    public void TheDoneCardLineComesFromTheLedgerAndIsMissingForARunBeforeTheSelfCheck()
    {
        Assert.Equal("8 checks passed, 2 notes, 1 fault", RunSummaryCardLine(new ReviewStats { SelfCheckPassed = 8, SelfCheckNotes = 2, SelfCheckFaults = 1 }));
        Assert.Equal("10 checks passed", RunSummaryCardLine(new ReviewStats { SelfCheckPassed = 10, SelfCheckNotes = 0, SelfCheckFaults = 0 }));
        Assert.Null(RunSummaryCardLine(new ReviewStats()));
    }

    [Fact]
    public async Task TheDoneCardLinksToTheTable()
    {
        var run = Guid.Parse("596541db-400c-4b99-9a8a-a5dc0ae9a9d3");
        string html = await RenderAsync<AuBtechReviewAgent.Components.Dashboard.RunSummaryCard>(new()
        {
            ["Stats"] = new ReviewStats { Included = 3, SelfCheckPassed = 9, SelfCheckNotes = 1, SelfCheckFaults = 0 }, ["RunId"] = run,
        });

        Assert.Contains("The paper checked itself:", html);
        Assert.Contains("9 checks passed, 1 note", html);
        Assert.Contains($"href=\"/spec-matrix/{run}#self-check\"", html);

        string before = await RenderAsync<AuBtechReviewAgent.Components.Dashboard.RunSummaryCard>(new() { ["Stats"] = new ReviewStats { Included = 3 }, ["RunId"] = run });
        Assert.DoesNotContain("checked itself", before);
    }

    private static string? RunSummaryCardLine(ReviewStats s) => AuBtechReviewAgent.Components.Dashboard.RunSummaryCard.SelfCheckLine(s);

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
}
