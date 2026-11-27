using AuBtechReviewAgent.Components.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// New review in the Ask state (redesign step 6): the composer and the criteria panel, rendered to HTML with
/// Blazor's HtmlRenderer as the server first sends them.
/// </summary>
public class ReviewComposerTests
{
    private static readonly IReadOnlyDictionary<string, bool> OpenSourcesOnly =
        SourceCatalog.All.ToDictionary(s => s.Key, s => s.KeyKind == SourceKeyKind.None);

    [Fact]
    public async Task TheComposerAsksTheQuestionInOneCardWithOneStartButton()
    {
        string html = await RenderAsync<ReviewComposer>(new UiStateContainer(), new() { ["Method"] = ReviewMethods.Default });

        Assert.Contains("What should the review find out?", html);
        Assert.Contains($"{ReviewMethods.Default.Name} · {ReviewMethods.Default.Standard}", html);
        Assert.Contains("href=\"/start\"", html);
        Assert.Contains("<label for=\"f-objective\"", html);
        Assert.Contains("<label for=\"f-query\"", html);
        Assert.Contains("id=\"f-query\" type=\"text\" aria-required=\"true\"", html);
        Assert.Contains("aria-describedby=\"h-query\"", html);
        Assert.Contains("id=\"h-query\"", html);
        Assert.Equal(1, Count(html, ">Start review</button>"));
        Assert.Contains(">Try an example</button>", html);
        Assert.Contains(">API keys</button>", html);
        // The research questions stay folded behind one button until asked for.
        Assert.DoesNotContain("id=\"f-questions\"", html);
        Assert.Contains($"Add research questions <span class=\"font-normal text-muted max-sm:hidden\">(optional, up to {ReviewInputGuard.MaxResearchQuestions})", html);
        Assert.Contains("min-h-[44px] justify-start", html);
    }

    [Fact]
    public async Task TheEmptyFormHasNoStepStripAndShowsTheBadgeAboveAPhone()
    {
        var quota = new QuotaStatus(QuotaTier.Free, true, 1, 3, "");
        string html = await RenderAsync<ReviewComposer>(new UiStateContainer(), new() { ["Method"] = ReviewMethods.Default, ["Quota"] = quota });

        // The seven steps show when the run starts, not on the empty form (issue #732).
        Assert.DoesNotContain("What the review does", html);
        foreach (var (_, line) in ReviewSteps.All) Assert.DoesNotContain(line, html);
        Assert.Contains("Free tier · 2 of 3 left today", html);
        // Try an example, the badge, API keys and Start share one row from 640 px.
        int row = html.IndexOf("sm:flex-row", StringComparison.Ordinal);
        Assert.True(row >= 0 && row < html.IndexOf(">Try an example<", StringComparison.Ordinal));
        Assert.True(html.IndexOf(">API keys<", StringComparison.Ordinal) < html.IndexOf(">Start review<", StringComparison.Ordinal));
        Assert.Contains("max-sm:w-full\">Start review", html);
    }

    [Fact]
    public async Task TheObjectiveAndQueryHaveNoHelpLineThatRepeatsTheirLabel()
    {
        string html = await RenderAsync<ReviewComposer>(new UiStateContainer(), new() { ["Method"] = ReviewMethods.Default });

        // Issue #729: the title already asks the question, and the query's hint is its placeholder.
        Assert.DoesNotContain("The question the review answers", html);
        Assert.DoesNotContain("The words sent to the databases", html);
        Assert.Contains("placeholder=\"Short and specific works best, e.g. agentic AI software frameworks\"", html);
        // The line stays for the character count, folded away until the text nears the limit.
        Assert.Contains("id=\"h-objective\" class=\"hidden mt-1", html);
        Assert.Contains("id=\"h-query\" class=\"hidden mt-1", html);

        var near = new UiStateContainer { SearchQuery = new string('a', ReviewInputGuard.MaxQuery) };
        string counted = await RenderAsync<ReviewComposer>(near, new() { ["Method"] = ReviewMethods.Default });
        Assert.Contains($"id=\"h-query\" class=\"flex mt-1", counted);
        Assert.Contains($"{ReviewInputGuard.MaxQuery} / {ReviewInputGuard.MaxQuery}", counted);
    }

    [Fact]
    public async Task TheResearchQuestionsShowWhenTheyHaveTextOrWereRejected()
    {
        var state = new UiStateContainer { ResearchQuestions = "Which mechanisms are used?" };
        string withText = await RenderAsync<ReviewComposer>(state, new() { ["Method"] = ReviewMethods.Default });
        Assert.Contains("id=\"f-questions\"", withText);
        Assert.DoesNotContain("Add research questions", withText);

        var check = new InputCheck(null!, new[] { "Research questions: there are 5; the limit is 4." }, Array.Empty<InputFlag>());
        string rejected = await RenderAsync<ReviewComposer>(new UiStateContainer(), new() { ["Method"] = ReviewMethods.Default, ["InputCheck"] = check });
        Assert.Contains("id=\"f-questions\"", rejected);
        Assert.Contains("aria-invalid=\"true\"", rejected);
    }

    [Fact]
    public async Task TheComposerShowsTheNoticesThePageGivesIt()
    {
        RenderFragment notice = b => b.AddContent(0, "An example review was filled in.");
        RenderFragment problems = b => b.AddContent(0, "Add a search query.");
        string html = await RenderAsync<ReviewComposer>(new UiStateContainer(),
            new() { ["Method"] = ReviewMethods.Default, ["Notice"] = notice, ["Problems"] = problems });

        int noticeAt = html.IndexOf("An example review was filled in.", StringComparison.Ordinal);
        int problemAt = html.IndexOf("Add a search query.", StringComparison.Ordinal);
        int startAt = html.IndexOf(">Start review<", StringComparison.Ordinal);
        Assert.True(noticeAt >= 0 && noticeAt < html.IndexOf("id=\"f-objective\"", StringComparison.Ordinal));
        Assert.True(problemAt > noticeAt && problemAt < startAt);
    }

    [Fact]
    public async Task TheCriteriaPanelIsOpenFromALaptopUntilTheReviewerChooses()
    {
        string html = await RenderAsync<CriteriaPanel>(new UiStateContainer(), new() { ["SourceAvailability"] = OpenSourcesOnly });

        Assert.Contains("data-open=\"auto\"", html);
        Assert.Contains("id=\"criteria-body\" class=\"hidden lg:block", html);
        // One toggle per width, each saying truthfully whether the panel is open there.
        Assert.Contains("aria-expanded=\"false\" aria-controls=\"criteria-body\" aria-label=\"Show the criteria\" class=\"lg:hidden", html);
        Assert.Contains("aria-expanded=\"true\" aria-controls=\"criteria-body\" aria-label=\"Fold the criteria\" class=\"hidden lg:inline-flex", html);
        Assert.Contains("lg:hidden\" data-criteria-summary", html);
        foreach (var group in new[] { ">Scope</legend>", ">Include and exclude</legend>", ">Visual summary<" })
            Assert.Contains(group, html);
        Assert.DoesNotContain("max-sm:fixed", html);
    }

    [Fact]
    public async Task OpenedOnAPhoneThePanelIsAFullScreenSheetWithDone()
    {
        string open = await RenderAsync<CriteriaPanel>(new UiStateContainer(), new() { ["Open"] = (bool?)true, ["SourceAvailability"] = OpenSourcesOnly });
        Assert.Contains("max-sm:fixed max-sm:inset-0", open);
        Assert.Contains("id=\"criteria-body\" class=\"block", open);
        Assert.Contains(">Done</button>", open);
        Assert.Equal(1, Count(open, "aria-controls=\"criteria-body\""));
        Assert.Contains("aria-expanded=\"true\"", open);

        string folded = await RenderAsync<CriteriaPanel>(new UiStateContainer(), new() { ["Open"] = (bool?)false, ["SourceAvailability"] = OpenSourcesOnly });
        Assert.Contains("id=\"criteria-body\" class=\"hidden space-y-3", folded);
        Assert.Contains("aria-expanded=\"false\"", folded);
        Assert.DoesNotContain("max-sm:fixed", folded);
    }

    [Fact]
    public async Task TheFoldedSummaryNamesTheReviewYearsDatabasesAndVisual()
    {
        var state = new UiStateContainer { DateFrom = new DateTime(2020, 1, 1), DateTo = new DateTime(2026, 12, 31) };
        var panel = await RenderPanelAsync(state, "Systematic review");
        int open = OpenSourcesOnly.Count(s => s.Value);
        Assert.Contains($"Systematic review · 2020–2026 · {open} databases · concept map", panel);

        state.ArtifactKind = ArtifactKinds.None;
        state.SelectedSources = new HashSet<string>(new[] { "arxiv", "scopus" }, StringComparer.OrdinalIgnoreCase);
        panel = await RenderPanelAsync(state, "Systematic review");
        // Scopus needs a key here, so only arXiv counts.
        Assert.Contains("Systematic review · 2020–2026 · 1 database · no visual summary", panel);
        Assert.Contains($"(at least one · 1 of {open})", panel);
    }

    [Fact]
    public async Task ThePanelKeepsEveryCriterionField()
    {
        var check = new InputCheck(null!, new[] { "Inclusion criteria: too long." }, Array.Empty<InputFlag>());
        string html = await RenderAsync<CriteriaPanel>(new UiStateContainer(), new() { ["SourceAvailability"] = OpenSourcesOnly, ["InputCheck"] = check });

        Assert.Contains("aria-label=\"From year\"", html);
        Assert.Contains("aria-label=\"To year\"", html);
        Assert.Contains(">Last 5 years</button>", html);
        Assert.Contains("id=\"f-sources\" tabindex=\"-1\"", html);
        foreach (var source in SourceCatalog.All.Where(s => OpenSourcesOnly[s.Key]))
            Assert.Contains(source.DisplayName, html);
        Assert.Contains("aria-label=\"Add a key for Scopus or IEEE\"", html);
        Assert.Contains("id=\"f-inclusion\" rows=\"2\" aria-required=\"true\" aria-describedby=\"h-inclusion\" aria-invalid=\"true\"", html);
        Assert.Contains("id=\"f-exclusion\"", html);
        Assert.Equal(ArtifactKinds.All.Count, Count(html, "<option value="));
        Assert.Contains("id=\"f-artifact-kind\"", html);

        var custom = new UiStateContainer { ArtifactKind = ArtifactKinds.Custom, SynthesisTargetDirective = "" };
        string withNote = await RenderAsync<CriteriaPanel>(custom, new() { ["SourceAvailability"] = OpenSourcesOnly });
        Assert.Contains("id=\"f-directive\"", withNote);
    }

    [Fact]
    public async Task TheCriteriaHaveNoHelpLineThatRepeatsTheirLabel()
    {
        string html = await RenderAsync<CriteriaPanel>(new UiStateContainer(), new() { ["SourceAvailability"] = OpenSourcesOnly });

        // Issue #729: the labels say it; the exclusion label says it is optional, as the query says it is required.
        Assert.DoesNotContain("What a study must be about or contain.", html);
        Assert.DoesNotContain("What rules a study out.", html);
        Assert.Contains("Exclude studies that… <span class=\"font-normal text-muted\">(optional)</span>", html);
        // The visual summary's note lives in its (?) tooltip, which screen readers read as its description.
        Assert.DoesNotContain("id=\"h-directive\" class=\"flex", html);
        Assert.Contains("What it cites is checked like the text.", Glossary.Find("artifact")!.Explanation);
        Assert.Contains("What it cites is checked like the text.", html);
    }

    [Fact]
    public async Task ThePanelShowsTheExtraOptionsThePageGivesIt()
    {
        RenderFragment more = b => b.AddMarkupContent(0, "<details id=\"advanced\">Advanced options</details>");
        string html = await RenderAsync<CriteriaPanel>(new UiStateContainer(), new() { ["SourceAvailability"] = OpenSourcesOnly, ["MoreOptions"] = more });

        Assert.True(html.IndexOf("id=\"advanced\"", StringComparison.Ordinal) > html.IndexOf("id=\"f-artifact\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ArtifactKinds.ConceptMap, "Concept map")]
    [InlineData(ArtifactKinds.Flowchart, "Flowchart")]
    [InlineData(ArtifactKinds.Table, "Comparison table")]
    [InlineData(ArtifactKinds.Bullets, "Bullet-point list")]
    [InlineData(ArtifactKinds.Custom, "Custom")]
    [InlineData(ArtifactKinds.None, "None")]
    public void EveryVisualSummaryHasAShortName(string kind, string label) => Assert.Equal(label, CriteriaPanel.ShortLabel(kind));

    [Fact]
    public void TheHelpLineMarksOnlyTheFieldTheCheckRejected()
    {
        var check = new InputCheck(null!, new[] { "Search query: too long." }, Array.Empty<InputFlag>());
        Assert.Equal("true", FieldHelp.Invalid(check, "Search query"));
        Assert.Null(FieldHelp.Invalid(check, "Review objective"));
        Assert.Null(FieldHelp.Invalid(null, "Search query"));
    }

    private static Task<string> RenderPanelAsync(UiStateContainer state, string methodName) =>
        RenderAsync<CriteriaPanel>(state, new() { ["SourceAvailability"] = OpenSourcesOnly, ["MethodName"] = methodName });

    private static int Count(string text, string part)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { count++; at += part.Length; }
        return count;
    }

    private static async Task<string> RenderAsync<TComponent>(UiStateContainer state, Dictionary<string, object?> parameters) where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(state);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            // Text written from code is HTML-encoded ("·" as &#xB7;); decoded, the tests read as the page does.
            return System.Net.WebUtility.HtmlDecode(output.ToHtmlString());
        });
    }
}
