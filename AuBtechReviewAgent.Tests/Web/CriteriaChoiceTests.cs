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
