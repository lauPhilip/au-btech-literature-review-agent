using AuBtechReviewAgent.Modules.Multivocal.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The run view of a review with both pools (block I-6), on the whole fake run of <see cref="FormalKit"/>: the counts of
/// each pool, the funnel with the pools side by side, and an academic source's text basis and MMAT appraisal.
/// </summary>
public class MultivocalPoolViewTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrpoolview-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static (MultivocalLedger, MultivocalScreeningFile, MultivocalQualityFile, MultivocalExtractionFile) Files(FormalKit.Run run) => (
        run.Searcher.LoadLedger(run.RunId)!, run.Screener.Load(run.RunId)!, run.Quality.Load(run.RunId)!,
        System.Text.Json.JsonSerializer.Deserialize<MultivocalExtractionFile>(File.ReadAllText(Path.Join(run.Store.FolderOf(run.RunId), MultivocalExtractor.ExtractionFile)))!);

    [Fact]
    public async Task EachPoolIsCountedApartFromTheSearchToTheExtraction()
    {
        var run = await FormalKit.RunAsync(_root);
        var (ledger, screening, quality, extraction) = Files(run);

        var grey = MultivocalPools.For(false, ledger, screening, quality, extraction);
        var formal = MultivocalPools.For(true, ledger, screening, quality, extraction);

        Assert.Equal((2, 0, 0, 2, 2), (grey.Identified, grey.DuplicatesRemoved, grey.NoAddressRemoved, grey.Included, grey.Screened));
        Assert.Equal((1, 1, 1), (grey.Kept, grey.NotAssessed, grey.Extracted)); // the source with a javascript: address has no page to read
        Assert.Equal((4, 1, 1, 2, 2), (formal.Identified, formal.DuplicatesRemoved, formal.NoAddressRemoved, formal.Included, formal.Kept));
        Assert.Equal((2, 1), (formal.Extracted, formal.AbstractOnly));
        Assert.Equal((screening.KappaGrey, screening.KappaFormal), (grey.Kappa, formal.Kappa));
    }

    [Fact]
    public async Task TheFunnelShowsThePoolsSideBySide()
    {
        var run = await FormalKit.RunAsync(_root);
        var (ledger, screening, quality, extraction) = Files(run);

        string html = await RenderAsync<MultivocalPoolFunnel>(new()
        {
            ["Grey"] = MultivocalPools.For(false, ledger, screening, quality, extraction),
            ["Formal"] = MultivocalPools.For(true, ledger, screening, quality, extraction),
            ["Kappa"] = screening.Kappa, ["Screened"] = true, ["Assessed"] = true, ["Extracted"] = true,
        });

        Assert.Contains(">Grey</th>", html);
        Assert.Contains(">Academic</th>", html);
        Assert.Contains("Duplicates by DOI or title", html);
        Assert.Contains("Kept: grey passed Table 7, academic appraised with MMAT", html);
        Assert.Contains("of which on the abstract only</th>", html);
        Assert.DoesNotContain("rounded", html);
    }

    [Fact]
    public async Task AnAcademicSourceShowsItsTextBasisAndItsMmatAppraisal()
    {
        var run = await FormalKit.RunAsync(_root);
        var pages = run.Pages.Load(run.RunId).Pages;
        var quality = run.Quality.Load(run.RunId)!;

        string onAbstract = MultivocalStart.TextBasisLine(pages.Single(p => p.TextBasis == MultivocalPages.AbstractBasis))!;
        Assert.StartsWith("Abstract only: no open full text (no open-access copy known), so quotes come from the abstract", onAbstract);
        Assert.StartsWith("Full text kept ", MultivocalStart.TextBasisLine(pages.Single(p => p.TextBasis == MultivocalPages.FullTextBasis)));
        Assert.Null(MultivocalStart.TextBasisLine(pages.Single(p => p.TextBasis == null && p.NotKept == null)));

        string html = await RenderAsync<MultivocalSourceQuality>(new() { ["Score"] = quality.Sources.First(q => q.Formal), ["Title"] = "A paper" });
        Assert.Contains("MMAT: qualitative, 2 of 5 criteria met", html);
        Assert.Contains("Appraised with MMAT on its full text; the quality threshold is for grey sources only", html);
        Assert.Contains("can&#x27;t tell", html);
        Assert.DoesNotContain("Quality 0 of 20", html);
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
