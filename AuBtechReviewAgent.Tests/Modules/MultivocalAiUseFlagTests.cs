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

    [Fact]
    public async Task AnOlderRunsFlagsAreJudgedAgainFromTheKeptTextWhenThePaperIsRebuilt()
    {
        var run = await FormalKit.RunAsync(_root);
        string file = Path.Join(run.Store.FolderOf(run.RunId), MultivocalPages.PagesFile);
        var pages = JsonSerializer.Deserialize<MultivocalPagesFile>(File.ReadAllText(file))!;
        var kept = pages.Pages.Where(p => !string.IsNullOrEmpty(p.TextFile)).ToList();
        Assert.All(kept.Where(p => p.InjectionFlags != null), p => Assert.Equal(PromptSafety.ScanVersion, p.FlagScan));

        // As the scan before #472 wrote them: a strong name on a page whose text holds no phrase aimed at the model, and no version.
        var clean = kept.First(p => p.InjectionFlags == null);
        clean.InjectionFlags = new() { new("ignore-instructions", "you can ignore these rules in a .gitignore") };
        var flagged = kept.Single(p => p.InjectionFlags != null && p != clean);
        flagged.FlagScan = null;
        File.WriteAllText(file, JsonSerializer.Serialize(pages));

        var input = run.Reporter.LoadInput(run.RunId)!;
        Assert.Null(input.Pages.Pages.Single(p => p.Url == clean.Url).InjectionFlags);
        Assert.True(PromptSafety.AnyStrong(input.Pages.Pages.Single(p => p.Url == flagged.Url).InjectionFlags));
        Assert.Contains(Expected(input.Pages), MultivocalPaper.AiUse(input, run.Writer.Load(run.RunId)!));
        Assert.Equal("ignore-instructions", JsonSerializer.Deserialize<MultivocalPagesFile>(File.ReadAllText(file))!.Pages.Single(p => p.Url == clean.Url).InjectionFlags![0].Pattern);
    }

    [Fact]
    public async Task APageWithOnlyWeakMentionsIsNotCountedAsFlagged()
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        var clean = input.Pages.Pages.First(p => !string.IsNullOrEmpty(p.TextFile) && p.InjectionFlags == null);
        clean.InjectionFlags = new() { new("mentions-system-prompt", "the system prompt holds the role") };

        string text = MultivocalPaper.AiUse(input, run.Writer.Load(run.RunId)!);

        Assert.Contains("The kept text of 1 of", text);
        Assert.Contains("The kept text of 1 more only named prompts or model instructions as a subject", text);
    }
}
