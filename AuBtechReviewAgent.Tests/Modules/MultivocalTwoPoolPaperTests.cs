using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Three faults a reader of a two-pool paper would notice (#362): "1 themes" in the flow, the grey arm's side box
/// listing quality exclusions beside the screening step, and a statement on AI use that covered grey sources only.
/// The grey literature review's text is pinned alongside, so the fixes do not change it beyond the plural.
/// </summary>
public class MultivocalTwoPoolPaperTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrtwopool-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private async Task<MultivocalReportInput> TwoPoolInput()
    {
        var run = await FormalKit.RunAsync(_root);
        Assert.Null(run.Runner.StatusOf(run.RunId)!.Error);
        return run.Reporter.LoadInput(run.RunId)!;
    }

    private static string Line(string tikz, string node) =>
        tikz.Split('\n').Single(l => l.Contains($"({node})", StringComparison.Ordinal) && l.Contains(@"\node", StringComparison.Ordinal));

    [Fact]
    public async Task EachSideBoxOfTheTwoArmFlowSitsBesideTheStepItsSourcesLeftAt()
    {
        string tikz = MultivocalFormalText.FlowTikz(await TwoPoolInput());

        string screening = Line(tikz, "gout");
        Assert.Contains("left=of gscreened", screening);
        Assert.DoesNotContain("Below the threshold", screening);
        Assert.DoesNotContain("Not assessed", screening);
        Assert.Contains("left=of gkept", Line(tikz, "glow"));
        Assert.Contains("Below the threshold", Line(tikz, "glow"));
        Assert.DoesNotContain("Not appraised", Line(tikz, "fout"));
        Assert.Contains("right=of fkept", Line(tikz, "fnotappraised"));
        Assert.Contains(@"\draw[->] (gkept) -- (glow);", tikz);
        Assert.Contains(@"\draw[->] (fkept) -- (fnotappraised);", tikz);
    }

    [Fact]
    public async Task TheStatementOnAiUseCoversBothPoolsAndGivesTheAgreementForEach()
    {
        var input = await TwoPoolInput();
        string text = MultivocalPaper.AiUse(input, new MultivocalPaperFile { Model = "kit-model", WrittenUtc = new DateTime(2026, 11, 23) });

        Assert.Contains("screening the grey and the academic sources twice", text);
        Assert.Contains("appraising the academic sources with the Mixed Methods Appraisal Tool", text);
        Assert.Contains("the kept page of a grey source, and the open-access full text or else the abstract of an academic one", text);
        Assert.Contains("for the grey sources,", text);
        Assert.Contains("for the academic sources and", text);
        Assert.DoesNotContain("screening the grey sources twice", text);
        Assert.DoesNotContain("against the kept page of the source it cites", text);
    }

    [Fact]
    public void AGreyLiteratureReviewKeepsItsStatementOnAiUse()
    {
        var input = MultivocalPaperTests.Input();
        string text = MultivocalPaper.AiUse(input, new MultivocalPaperFile { Model = "m", WrittenUtc = new DateTime(2026, 11, 23) });

        Assert.Contains("The model was used for screening the grey sources twice against the criteria, scoring their quality on the guidelines' checklist,", text);
        Assert.Contains("checking each citation against the kept page of the source it cites. ", text);
        Assert.DoesNotContain("academic", text);
    }

    [Theory]
    [InlineData(1, 1, "Synthesised: 1 finding in 1 theme}")]
    [InlineData(3, 1, "Synthesised: 3 findings in 1 theme}")]
    [InlineData(5, 2, "Synthesised: 5 findings in 2 themes}")]
    public void TheGreyFlowCountsFindingsAndThemesInTheRightNumber(int findings, int themes, string expected)
    {
        var input = MultivocalPaperTests.Input();
        var flow = MultivocalPaperParts.Flow(input) with { Findings = findings, Themes = themes };

        Assert.Contains(expected, MultivocalPaperParts.FlowTikz(flow));
    }
}
