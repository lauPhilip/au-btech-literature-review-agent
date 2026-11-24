using AuBtechReviewAgent;
using AuBtechReviewAgent.Modules.Multivocal.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Faults a careful reader found in the paper of a real multivocal run (50e9a8e3): the flow lost the sources that could
/// not be extracted, one reason for not appraising was repeated eight times, a theme on academic sources only had "a
/// mean quality of the grey sources of 0", the page gave two totals of citations checked (#467), and the declarations
/// named the server's version, not the run's (#468).
/// </summary>
public class MultivocalPaperCountsTests : IDisposable
{
    private const string RunVersion = "0.4.1-alpha.0.102+5cb1ea03c4aca1e67ea2f9b2099c0bea4e45afcd";
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrcounts-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string Line(string tikz, string node) =>
        tikz.Split('\n').Single(l => l.Contains($"({node})", StringComparison.Ordinal) && l.Contains(@"\node", StringComparison.Ordinal));

    [Fact]
    public async Task EachArmOfTheFlowHasABoxForTheSourcesThatCouldNotBeExtracted()
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        input.Extraction.Sources.First(s => s.Formal).Error = "The model call failed.";

        string tikz = MultivocalFormalText.FlowTikz(input);

        Assert.Contains("Could not be extracted: 1}", Line(tikz, "fnotextracted"));
        Assert.Contains("right=of fextracted", Line(tikz, "fnotextracted"));
        Assert.Contains("Could not be extracted: 0}", Line(tikz, "gnotextracted"));
        Assert.Contains("left=of gextracted", Line(tikz, "gnotextracted"));
        Assert.Contains(@"\draw[->] (fextracted) -- (fnotextracted);", tikz);

        var grey = MultivocalPools.For(false, input.Ledger, input.Screening, input.Quality, input.Extraction);
        var formal = MultivocalPools.For(true, input.Ledger, input.Screening, input.Quality, input.Extraction);
        Assert.Equal(formal.Kept, formal.Extracted + formal.NotExtracted); // the academic column adds up
        string html = await RenderAsync<MultivocalPoolFunnel>(new()
        {
            ["Grey"] = grey, ["Formal"] = formal, ["Screened"] = true, ["Assessed"] = true, ["Extracted"] = true,
        });
        Assert.Contains("Could not be extracted</th>", html);
    }

    [Fact]
    public void AReasonForNotAppraisingIsGivenOnceWithHowManyItHoldsFor()
    {
        const string noText = "Neither an open full text nor an abstract is available, so there is no text to quote.";
        GreyQuality NotAppraised(string? reason) => new() { Formal = true, Outcome = "Not assessed", NotAssessed = reason };

        Assert.Equal($", all for the same reason ({noText.TrimEnd('.')})",
            MultivocalFormalText.NotAppraisedReasons(Enumerable.Range(0, 8).Select(_ => NotAppraised(noText))));
        Assert.Equal($" ({noText.TrimEnd('.')})", MultivocalFormalText.NotAppraisedReasons(new[] { NotAppraised(noText) }));
        Assert.Equal($" (2: {noText.TrimEnd('.')}; 1: no reason recorded)",
            MultivocalFormalText.NotAppraisedReasons(new[] { NotAppraised(noText), NotAppraised(null), NotAppraised(noText) }));
    }

    [Fact]
    public void AThemeOnAcademicSourcesOnlyHasNoTiersAndNoMeanQuality()
    {
        var input = MultivocalPaperTests.Input();
        var formalOnly = new SynthesisTheme { Name = "Retrieval increases failures", Sources = 4, FormalSources = 4, EvidenceLabel = "formal literature only" };
        var both = new SynthesisTheme { Name = "Summaries", Sources = 3, GreySources = 1, FormalSources = 2, FirstTier = 1, MeanQuality = 12.5, EvidenceLabel = "grey and formal literature" };

        string note = MultivocalPaperParts.ThemeNote(input, formalOnly);

        Assert.Contains("(0 grey and 4 academic), all academic, so without outlet tiers or quality points (formal literature only).", note);
        Assert.DoesNotContain("mean quality", note);
        Assert.DoesNotContain("tier outlets", note);
        Assert.Equal("1 from 1st-tier, 0 from 2nd-tier and 0 from 3rd-tier outlets, with a mean quality of the grey sources of 12.5 of 20 points (grey and formal literature)",
            MultivocalFormalText.ThemeBasis(both, 20));
    }

    [Fact]
    public void TheCitationCardCountsTheCitationsCheckedAsTheRestOfThePageDoes()
    {
        // The real run: 1283 checked after the repair, 136 removed; the card said 1419 checked.
        var counts = new VerdictTally(Supported: 1175, Partly: 105, NotSupported: 0, AbstractOnly: 0, Unverifiable: 3, Removed: 136);

        Assert.Equal("1283 citations checked against the sources, and 136 removed", MultivocalPaperPage.CheckedHeading(counts));
        Assert.Equal("1 citation checked against the sources", MultivocalPaperPage.CheckedHeading(new VerdictTally(1, 0, 0, 0, 0)));
    }

    [Fact]
    public void TheDeclarationsNameTheVersionTheRunWasMadeWith()
    {
        var input = MultivocalPaperTests.Input();
        var paper = new MultivocalPaperFile { Model = "m", WrittenUtc = new DateTime(2026, 11, 23), AppVersion = RunVersion };

        var declarations = MultivocalPaper.Declarations(input, paper);

        Assert.Contains("TraceableAI (version 0.4.1-alpha.0.102), an open-source tool", declarations[0].Text);
        Assert.Contains("This run used version 0.4.1-alpha.0.102 (commit 5cb1ea0), which is not a published release; its code is at https://github.com/lauPhilip/au-btech-literature-review-agent/tree/5cb1ea03c4aca1e67ea2f9b2099c0bea4e45afcd.", declarations[2].Text);
        var unknown = MultivocalPaper.Declarations(input, new MultivocalPaperFile { Model = "m", WrittenUtc = new DateTime(2026, 11, 23) });
        Assert.Contains("TraceableAI (version unknown)", unknown[0].Text);
        Assert.Contains("The run's files do not record which version of the tool made it.", unknown[2].Text);
    }

    [Fact]
    public async Task APaperWrittenBeforeTheVersionWasRecordedTakesItFromTheRunsFiles()
    {
        var run = await FormalKit.RunAsync(_root);
        Assert.Equal(RecordingChatCompletionService.AppVersion, run.Writer.Load(run.RunId)!.AppVersion); // a new run records its own

        string folder = run.Store.FolderOf(run.RunId);
        string paperPath = Path.Join(folder, MultivocalWriter.PaperFile);
        var old = System.Text.Json.JsonSerializer.Deserialize<MultivocalPaperFile>(File.ReadAllText(paperPath))!;
        old.AppVersion = null;
        File.WriteAllText(paperPath, System.Text.Json.JsonSerializer.Serialize(old));
        File.WriteAllText(Path.Join(folder, MultivocalRunLog.MetricsFile), $$"""{"AppVersion":"{{RunVersion}}"}""");
        Assert.Equal(RunVersion, run.Writer.Load(run.RunId)!.AppVersion);
        Assert.Contains("(commit 5cb1ea0)", run.Writer.MainTex(run.RunId));

        File.Delete(Path.Join(folder, MultivocalRunLog.MetricsFile));
        File.WriteAllText(Path.Join(folder, MultivocalRunLog.CallsFile), $$"""{"RunSettings":{"AppVersion":"{{RunVersion}}"},"Calls":[]}""");
        Assert.Equal(RunVersion, MultivocalWriter.RecordedVersion(folder));
        File.Delete(Path.Join(folder, MultivocalRunLog.CallsFile));
        Assert.Null(MultivocalWriter.RecordedVersion(folder));
    }

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
