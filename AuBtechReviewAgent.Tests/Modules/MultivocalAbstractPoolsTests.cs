using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The abstract of a review with both pools states each pool's numbers for that pool (#457), and the paper counts only
/// the themes it shows (#462). In the real run 50e9a8e3 the abstract said "433 grey sources" for both pools together and
/// that 205 passed the grey quality threshold, though 122 of them were academic; and the method counted 18 themes where
/// the paper showed 12, because six were formed for a question the paper answers by counting.
/// </summary>
public class MultivocalAbstractPoolsTests : IDisposable
{
    private const string Rationale = "Agents keep a running summary [1].";
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrpools-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string FrontMatter(string abstractText) =>
        $$"""{"title":"Context engineering: a multivocal literature review","abstract":"{{abstractText}}","rationale":"{{Rationale}}","objectives":"To map the practices."}""";

    [Fact]
    public async Task TheFactsGiveEachPoolItsOwnNumbers()
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;

        string facts = MultivocalWriter.FrontMatterFacts(input);

        Assert.Contains("Grey sources found by 1 grey searches: 2.", facts);
        Assert.Contains("Academic records returned by 1 searches of academic databases: 4; 2 of them set aside before screening as duplicates or without a web address, leaving 2 academic sources.", facts);
        Assert.Contains("Grey and academic sources together, after duplicates: 4.", facts);
        Assert.Contains("The quality check on the grey literature checklist applies to grey sources only: 1 grey sources passed it (10 of 20 points or more)", facts);
        Assert.Contains("Academic sources have no threshold", facts);
        Assert.Contains("Kept for the synthesis, both pools together: 3 (1 grey and 2 academic).", facts);
        Assert.DoesNotContain("Grey sources found by the searches", facts);
        Assert.DoesNotContain("Passed the quality check (", facts);
        Assert.Contains("Grey sources found by the searches: ", MultivocalWriter.FrontMatterFacts(MultivocalPaperTests.Input())); // a grey literature review keeps its facts
    }

    [Theory]
    [InlineData("We identified 4 grey sources through the searches.", "the grey and academic sources together")]
    [InlineData("Of 4 sources found, 3 grey sources were kept.", "3 counts the grey and academic sources kept together")]
    [InlineData("We retained 3 sources after a quality check requiring 10 of 20 points or more.", "only the 1 grey sources were held to it")]
    public async Task AnAbstractThatTiesANumberToTheWrongPoolIsSentBack(string abstractText, string problem)
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;

        Assert.Contains(problem, MultivocalFrontMatter.AbstractProblem(abstractText, input));
    }

    [Theory]
    [InlineData("We identified 2 grey sources and 4 academic records, 4 sources together after duplicates.")]
    [InlineData("Of the 3 sources kept, 1 grey source passed the threshold of 10 of 20 points and 2 were academic.")]
    [InlineData("Of the 3 kept, 2 are academic.")]
    [InlineData("We kept 3 sources from grey and academic searches.")]
    public async Task AnAbstractThatGivesEachPoolItsNumberPasses(string abstractText)
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;

        Assert.Null(MultivocalFrontMatter.AbstractProblem(abstractText, input));
        Assert.Null(MultivocalFrontMatter.AbstractProblem("We identified 4 grey sources.", MultivocalPaperTests.Input())); // one pool: nothing to mix up
    }

    [Fact]
    public async Task TheWriterSendsBackAnAbstractThatCallsBothPoolsGrey()
    {
        int asked = 0;
        string? Respond(string prompt) => prompt.Contains("You are writing the title, abstract, rationale and objectives")
            ? FrontMatter(Interlocked.Increment(ref asked) == 1 ? "We identified 4 grey sources." : "We identified 2 grey sources and 2 academic sources.")
            : null;

        var run = await FormalKit.RunAsync(_root, Respond);
        var paper = run.Writer.Load(run.RunId)!;

        Assert.Equal("We identified 2 grey sources and 2 academic sources.", paper.Abstract);
        Assert.Equal(2, asked); // the first abstract was sent back with the reason, the second kept
    }

    [Fact]
    public async Task AQuestionAnsweredByCountingGetsNoThemesAndOldThemesForItAreNotCounted()
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        var inCode = input.Planned.Plan.Numbered().Where(q => MultivocalPaperParts.AnsweredInCode(q.Question.Type)).Select(q => q.Number).ToList();
        Assert.NotEmpty(inCode);
        Assert.DoesNotContain(input.Synthesis.Themes, t => inCode.Contains(t.Question)); // the synthesis forms none
        Assert.DoesNotContain(run.Model.Prompts, p => p.Contains("Name one to") && inCode.Any(q => p.Contains($"{q}:")));

        // A run synthesised before kept themes for such a question; the paper does not show them, so nothing counts them.
        int shown = input.Synthesis.Themes.Count;
        input.Synthesis.Themes.Add(new SynthesisTheme { Name = "Coding and CLI Agent Tools", Question = inCode[0], Sources = 1, GreySources = 1 });
        input.Synthesis.SecondPlacements.Add(new PlacementAgreement { Question = inCode[0], Kappa = 0.42 });

        Assert.Equal(shown, MultivocalPaperParts.Flow(input).Themes);
        Assert.Contains($"in {shown} theme", MultivocalFormalText.FlowTikz(input));
        Assert.Contains($"of the {shown} theme", MultivocalFormalText.SynthesisText(input));
        string report = string.Join("\n", MultivocalReporter.Write(input).Sections.SelectMany(s => s.Blocks).Select(b => b.Text + string.Join(" ", b.Items) + string.Join(" ", b.Rows.SelectMany(r => r))));
        Assert.Contains($"The review produced {shown} theme", report);
        Assert.DoesNotContain("0.42", report);
        Assert.DoesNotContain("Coding and CLI Agent Tools", report);
        Assert.Contains($"Themes written up as result sections: {shown}.", MultivocalWriter.FrontMatterFacts(input));
    }
}
