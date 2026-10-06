using AuBtechReviewAgent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The preview setting for modules being built, and sending a run to its own module's pages.</summary>
public class ReviewModulesOptionsTests
{
    private static IConfiguration Settings(params string[] preview) => new ConfigurationBuilder()
        .AddInMemoryCollection(preview.Select((key, i) => new KeyValuePair<string, string?>($"ReviewModules:Preview:{i}", key)))
        .Build();

    [Fact]
    public void APreviewOpensAModuleOnlyInDevelopment()
    {
        var multivocal = ReviewModules.Find("multivocal")!;

        var development = ReviewModulesOptions.From(Settings("multivocal"), isDevelopment: true);
        Assert.True(development.IsPreview("multivocal"));
        Assert.True(development.CanOpen(multivocal));

        var production = ReviewModulesOptions.From(Settings("multivocal"), isDevelopment: false);
        Assert.False(production.IsPreview("multivocal"));
        Assert.False(production.CanOpen(multivocal));
    }

    [Fact]
    public void AModuleWithAnAvailableCardCanAlwaysBeOpened()
    {
        var none = ReviewModulesOptions.From(Settings(), isDevelopment: false);
        Assert.True(none.CanOpen(ReviewModules.Find("systematic")!));
        Assert.False(none.CanOpen(ReviewModules.Find("multivocal")!));
    }

    [Fact]
    public void ARunOpensOnTheModuleThatMadeIt()
    {
        var runId = Guid.NewGuid();
        var mlrRun = new RunHeader(runId, "multivocal", "grey", DateTime.UtcNow);
        var slrRun = new RunHeader(runId, "systematic", "rapid", DateTime.UtcNow);

        Assert.Equal($"/mlr/{runId}", ReviewModules.ElsewhereFor(mlrRun, "systematic"));
        Assert.Equal($"/review/{runId}", ReviewModules.ElsewhereFor(slrRun, "multivocal"));
        Assert.Null(ReviewModules.ElsewhereFor(slrRun, "systematic"));
        Assert.Null(ReviewModules.ElsewhereFor(null, "systematic"));                                            // no such run
        Assert.Null(ReviewModules.ElsewhereFor(mlrRun with { Module = "unknown" }, "systematic"));              // unknown module: stay
    }
}
