using System.Text.RegularExpressions;
using AuBtechReviewAgent.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Redesign step 10: the template of the five trust pages (Components/Layout/DocPage.razor, rendered with Blazor's
/// HtmlRenderer), the pages that use it, the glossary's groups and search, Review type and Not found (read as source).
/// </summary>
public class DocPageTests
{
    private static string? Root()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root;
    }

    private static string Page(string root, params string[] path) =>
        File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent", "Components" }.Concat(path).ToArray()));

    [Fact]
    public async Task TheSideListNamesTheFivePagesAndIndentsTheCurrentPagesSections()
    {
        string html = await RenderAsync(new()
        {
            ["Current"] = "/privacy",
            ["Eyebrow"] = "Privacy",
            ["Title"] = "What happens to your data",
            ["Sections"] = new List<(string Id, string Label)> { ("stored", "What is stored"), ("choices", "Your choices") },
        });

        foreach (var (href, label) in DocPage.Pages)
            Assert.Contains($"href=\"{href}\"", html);
        Assert.Equal(1, Regex.Matches(html, "aria-current=\"page\"").Count);
        Assert.Contains("href=\"/privacy\" aria-current=\"page\"", html);
        Assert.Contains("href=\"/privacy#stored\" data-jump-to=\"stored\"", html);
        Assert.Contains("href=\"/privacy#choices\" data-jump-to=\"choices\"", html);
        Assert.Contains("<h1", html);
        Assert.Contains("What happens to your data", html);
        Assert.Contains("max-lg:hidden", html);
    }

    [Fact]
    public async Task OnlyTheCurrentPageShowsItsSections()
    {
        string html = await RenderAsync(new()
        {
            ["Current"] = "/verify",
            ["Eyebrow"] = "Verify",
            ["Title"] = "Check that an archive is unchanged",
        });

        Assert.DoesNotContain("data-jump-to", html);
        Assert.Contains("href=\"/verify\" aria-current=\"page\"", html);
    }

    [Fact]
    public void EveryTrustPageUsesTheTemplateWithItsOwnAddress()
    {
        string? root = Root();
        if (root == null) return; // not run from the repository (packaged tests)

        foreach (var (file, href) in new[] { ("About.razor", "/about"), ("ModelCard.razor", "/model-card"), ("Privacy.razor", "/privacy"), ("GlossaryPage.razor", "/glossary"), ("Verify.razor", "/verify") })
        {
            string page = Page(root, "Pages", file);
            Assert.Contains($"@page \"{href}\"", page);
            Assert.Contains($"<DocPage Current=\"{href}\"", page);
            Assert.DoesNotContain("ui-h1", page);
            Assert.DoesNotContain("text-gray-", page);
        }
        // The footer links to the same five pages.
        string footer = Page(root, "Layout", "SiteFooter.razor");
        foreach (var (href, _) in DocPage.Pages)
            Assert.Contains($"href=\"{href}\"", footer);
    }

    [Fact]
    public void EveryGlossaryEntryHasAGroupAndTheSearchFilters()
    {
        Assert.All(Glossary.All, e => Assert.Contains(Glossary.GroupOf(e), Glossary.Groups));
        Assert.Equal(5, Glossary.All.Count(e => Glossary.GroupOf(e) == "Verdicts"));
        Assert.Contains(Glossary.All, e => Glossary.GroupOf(e) == "Output");

        Assert.Equal(Glossary.All.Count, Glossary.Search("").Count);
        Assert.Equal(Glossary.All.Count, Glossary.Search("   ").Count);
        Assert.Contains(Glossary.Search("KAPPA"), e => e.Key == "kappa");
        Assert.Empty(Glossary.Search("no such word anywhere"));
    }

    [Fact]
    public void ReviewTypeAndNotFoundFollowTheBrief()
    {
        string? root = Root();
        if (root == null) return;

        string start = Page(root, "Pages", "Start.razor");
        Assert.Contains("What kind of review?", start);
        Assert.Contains("Start this review", start);
        Assert.Contains("Coming soon", start);
        Assert.Contains("max-sm:hidden", start);

        string notFound = Page(root, "Pages", "NotFound.razor");
        Assert.Contains("<BrandMark Size=\"96\"", notFound);
        Assert.Contains("This page does not exist", notFound);
        Assert.Contains("Start a new review", notFound);
        Assert.Contains("Go to the overview", notFound);
    }

    private static async Task<string> RenderAsync(Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<DocPage>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }
}
