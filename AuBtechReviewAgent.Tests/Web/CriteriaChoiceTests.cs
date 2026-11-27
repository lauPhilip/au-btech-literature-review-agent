using AuBtechReviewAgent.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The compact choices of the criteria panel that let New review fit one laptop screen (issue #728): the visual
/// summary as one select (#730) and the databases as chips (#731).
/// </summary>
public class CriteriaChoiceTests
{
    private static readonly IReadOnlyDictionary<string, bool> OpenSourcesOnly =
        SourceCatalog.All.ToDictionary(s => s.Key, s => s.KeyKind == SourceKeyKind.None);

    [Fact]
    public async Task TheVisualSummaryIsOneSelectWithTheChosenKindSelected()
    {
        var state = new UiStateContainer { ArtifactKind = ArtifactKinds.Table, SynthesisTargetDirective = ArtifactKinds.DefaultInstruction(ArtifactKinds.Table) };
        string html = await RenderAsync(state, OpenSourcesOnly);

        Assert.Contains("<select id=\"f-artifact-kind\" aria-label=\"Visual summary\" data-fit-control", html);
        Assert.Contains("min-h-[44px]", html[html.IndexOf("f-artifact-kind", StringComparison.Ordinal)..]);
        foreach (var (key, _, _) in ArtifactKinds.All)
            Assert.Contains($"<option value=\"{key}\"", html);
        Assert.Contains("<option value=\"table\" selected>Comparison table</option>", html);
        Assert.DoesNotContain("type=\"radio\"", html);
        // The default note is not shown, so the textarea that took 60 px is gone.
        Assert.DoesNotContain("id=\"f-directive\"", html);
    }

    [Theory]
    [InlineData(ArtifactKinds.Custom, "", true)]
    [InlineData(ArtifactKinds.ConceptMap, null, false)]
    [InlineData(ArtifactKinds.ConceptMap, "", false)]
    [InlineData(ArtifactKinds.ConceptMap, "A map of the safety mechanisms and the threats they answer.", true)]
    [InlineData(ArtifactKinds.None, "Anything", false)]
    public void TheNoteShowsForCustomOrForTheReviewersOwnWords(string kind, string? directive, bool shown) =>
        Assert.Equal(shown, CriteriaPanel.ShowsDirective(kind, directive ?? ArtifactKinds.DefaultInstruction(kind)));

    [Fact]
    public async Task TheOpenDatabasesAreChipsAndTheKeyedOnesWaitForAKey()
    {
        string html = await RenderAsync(new UiStateContainer(), OpenSourcesOnly);

        var open = SourceCatalog.All.Where(s => s.KeyKind == SourceKeyKind.None).ToList();
        Assert.Equal(open.Count, Count(html, "<label data-fit-control"));
        foreach (var source in open) Assert.Contains(source.DisplayName, html);
        Assert.DoesNotContain("Scopus / ScienceDirect", html);
        Assert.DoesNotContain("IEEE Xplore", html);
        Assert.DoesNotContain("(needs key)", html);
        Assert.Contains(">Add a key for Scopus or IEEE</button>", html);
        // Square chips, each a 44 px target; the yellow status box under them is gone.
        Assert.Contains("inline-flex min-h-[44px] cursor-pointer", html);
        Assert.DoesNotContain("rounded", html[html.IndexOf("id=\"f-sources\"", StringComparison.Ordinal)..html.IndexOf("Add a key", StringComparison.Ordinal)]);
        Assert.DoesNotContain("Database status:", html);
    }

    [Fact]
    public async Task WithBothKeysEveryDatabaseIsAChipAndTheLinkIsGone()
    {
        var all = SourceCatalog.All.ToDictionary(s => s.Key, _ => true);
        string html = await RenderAsync(new UiStateContainer(), all);

        Assert.Equal(SourceCatalog.All.Count, Count(html, "<label data-fit-control"));
        Assert.Contains("IEEE Xplore", html);
        Assert.DoesNotContain("Add a key for", html);

        var onlyIeeeMissing = SourceCatalog.All.ToDictionary(s => s.Key, s => s.KeyKind != SourceKeyKind.Ieee);
        Assert.Contains(">Add a key for IEEE</button>", await RenderAsync(new UiStateContainer(), onlyIeeeMissing));
    }

    private static int Count(string text, string part)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { count++; at += part.Length; }
        return count;
    }

    private static async Task<string> RenderAsync(UiStateContainer state, IReadOnlyDictionary<string, bool> sources, EventCallback? openKeys = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(state);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var parameters = new Dictionary<string, object?> { ["SourceAvailability"] = sources };
        if (openKeys is { } callback) parameters["OnOpenKeys"] = callback;
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CriteriaPanel>(ParameterView.FromDictionary(parameters));
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
