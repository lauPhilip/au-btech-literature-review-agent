using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A multivocal paper's statement on the use of AI says how many sources' kept text was flagged as possible prompt
/// injection (#363), on the paper page (its declarations) and in main.tex. The kit's blog post carries an injection on
/// purpose (ModuleKit.Injection), so a kit run has one flagged source.
/// </summary>
public class MultivocalAiUseFlagTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlraiuse-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static string Expected(MultivocalPagesFile pages)
    {
        var kept = pages.Pages.Where(p => !string.IsNullOrEmpty(p.TextFile)).ToList();
        Assert.Equal(1, kept.Count(p => p.InjectionFlags != null));
        return $"The kept text of 1 of {kept.Count} {(kept.Count == 1 ? "source" : "sources")} contained phrases addressed to a model as instructions; it was flagged for the reviewer and changed no decision.";
    }

    [Theory]
    [InlineData("multivocal")]
    [InlineData("multivocal-formal")]
    public async Task TheKitsRunCountsItsFlaggedSourceInMainTex(string module)
    {
        var run = await ModuleKit.RunAsync(module);
        var pages = JsonSerializer.Deserialize<MultivocalPagesFile>(run.Archive[MultivocalPages.PagesFile])!;
        string tex = ModuleKit.Text(run.Archive[MultivocalPaper.MainTexFile]);

        Assert.Contains(PaperLatex.Escape(Expected(pages)), tex);
        Assert.Equal(1, tex.Split("addressed to a model").Length - 1);
    }

    [Fact]
    public async Task ThePaperPagesDeclarationOnAiUseCountsTheFlaggedSource()
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        var paper = run.Writer.Load(run.RunId)!;

        var aiUse = MultivocalPaper.Declarations(input, paper).Single(d => d.Heading == "Use of artificial intelligence");

        Assert.Contains(Expected(input.Pages), aiUse.Text);
        Assert.EndsWith("changed no decision. The authors reviewed the output and take full responsibility for the content.", aiUse.Text);
    }

    [Fact]
    public async Task WithoutAFlaggedSourceTheStatementSaysNothingAboutIt()
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        foreach (var page in input.Pages.Pages) page.InjectionFlags = null;

        string text = MultivocalPaper.AiUse(input, run.Writer.Load(run.RunId)!);

        Assert.DoesNotContain("addressed to a model", text);
    }
}
