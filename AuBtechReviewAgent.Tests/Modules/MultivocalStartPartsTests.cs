using AuBtechReviewAgent.Modules.Multivocal.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The parts of the grey literature review's page that only show data, rendered with Blazor's HtmlRenderer: a planned
/// run's settings (MultivocalRunSettings) and the preview of what the review will do (MultivocalPreviewTab).
/// </summary>
public class MultivocalStartPartsTests
{
    private static readonly Guid RunId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-901a2b3c4d5e");

    [Fact]
    public async Task TheSettingsOfAPlannedRunListThePlanAndLinkToTheRun()
    {
        var plan = MultivocalPlan.Example("grey");
        string html = await RenderAsync<MultivocalRunSettings>(new()
        {
            ["Planned"] = new PlannedRun(RunId, plan, "abc123fingerprint", DateTime.UtcNow),
            ["StageLine"] = "Running: screening",
        });

        Assert.Contains("Review settings", html);
        Assert.Contains("Running: screening", html);
        foreach (string label in new[] { "Topic", "Goal", "Audience", "Questions", "Searches", "Strings", "Threshold" })
            Assert.Contains($"<dt class=\"w-20 flex-none text-muted\">{label}</dt>", html);
        Assert.DoesNotContain(">Databases</dt>", html);
        Assert.Contains("Context engineering for LLM agents", html);
        Assert.Contains(">Both</dd>", html);
        Assert.Contains(">RQ1 RQ1.1 RQ1.2 RQ2 RQ3</dd>", html);
        Assert.Contains(">10 of 20 points</dd>", html);
        Assert.Contains($"href=\"/mlr/{RunId}\"", html);
        Assert.Contains($"href=\"/api/workspace/{RunId:N}/protocol.md\"", html);
        Assert.Contains("href=\"/mlr?card=grey\"", html);
        Assert.Contains("abc123fingerprint", html);
    }

    [Fact]
    public async Task AMultivocalRunAlsoListsItsAcademicDatabases()
    {
        var plan = MultivocalPlan.Example("multivocal");
        plan.FormalDatabases = new List<string> { SourceCatalog.AllKeys[0] };
        plan.FormalSearchStrings = new List<string> { "agent context windows" };
        string html = await RenderAsync<MultivocalRunSettings>(new()
        {
            ["Planned"] = new PlannedRun(RunId, plan, "abc", DateTime.UtcNow),
            ["StageLine"] = "Finished",
        });

        Assert.Contains($">{SourceCatalog.DisplayNameFor(SourceCatalog.AllKeys[0])}</dd>", html);
        Assert.Contains(">agent context windows</dd>", html);
        Assert.Contains("href=\"/mlr?card=multivocal\"", html);
    }

    [Fact]
    public async Task ThePreviewTabNamesEveryStepAndWhichAreBuilt()
    {
        var plan = MultivocalPlan.Example("grey");
        string html = await RenderAsync<MultivocalPreviewTab>(new() { ["Plan"] = plan, ["CardOn"] = false });

        Assert.Contains("What this review will do", html);
        Assert.Contains("The steps marked as built work in this preview", html);
        foreach (string step in new[] { "Protocol", "Search", "Selection", "Quality", "Map and extraction", "Synthesis", "Writing", "Checking" })
            Assert.Contains($"<span class=\"font-semibold text-heading\">{step}</span>", html);
        Assert.Contains("6 grey literature searches, the top 100 hits of each search string.", html);
        Assert.Contains("to answer 5 research questions", html);
        Assert.Contains("below 10 of 20 points it is left out", html);
        Assert.Equal(8, Count(html, ">built</span>")); // every step of a grey literature review, writing and checking included (block G)
        Assert.Equal(0, Count(html, ">not built yet</span>"));
        foreach (string product in new[] { "Review paper", "LaTeX manuscript", "Systematic map", "Verifiable archive" })
            Assert.Contains(product, html);
    }

    [Fact]
    public async Task OnceTheCardIsOnThePreviewDropsItsNoteAndCountsTheAcademicPool()
    {
        var plan = MultivocalPlan.Example("multivocal");
        plan.FormalDatabases = SourceCatalog.AllKeys.Take(2).ToList();
        plan.StoppingRule = "saturation";
        string html = await RenderAsync<MultivocalPreviewTab>(new() { ["Plan"] = plan, ["CardOn"] = true });

        Assert.DoesNotContain("The steps marked as built work in this preview", html);
        Assert.Contains("6 grey literature searches and 2 academic databases, each with its own search strings, until new sources add nothing new.", html);
        Assert.Contains("Grey and academic sources are screened twice", html);
        Assert.Equal(3, Count(html, ">built</span>")); // protocol, and the academic search (I-2) and screening (I-3); the rest follows in I-4 to I-6
        Assert.Equal(5, Count(html, ">not built yet</span>"));
    }

    [Fact]
    public void AnAcademicDatabaseIsNamedAsSuchBesideTheGreySearches()
    {
        Assert.Equal("OpenAlex (academic)", MultivocalStart.SearchLabel("openalex"));
        Assert.Equal("Hacker News", MultivocalStart.SearchLabel("hackernews"));
        Assert.Equal("Academic literature", MultivocalStart.KindLabel(MultivocalSearcher.FormalKind));
        Assert.Equal("Blog posts and practitioner articles", MultivocalStart.KindLabel("blogs"));
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    private static async Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }
}
