using AuBtechReviewAgent.Modules.Multivocal.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The parts of the grey literature review's panel that only show data, rendered with Blazor's HtmlRenderer: a
/// source's quality score (MultivocalSourceQuality) and extraction (MultivocalSourceExtraction), and the Map tab.
/// </summary>
public class MultivocalPanelPartsTests
{
    [Fact]
    public async Task ASourceNotAssessedOffersTryAgainOnlyWhenAllowed()
    {
        var score = new GreyQuality { Address = "example.org/post", Outcome = "Not assessed", NotAssessed = "no readable page" };

        string allowed = await RenderAsync<MultivocalSourceQuality>(new() { ["Score"] = score, ["Title"] = "A post", ["CanRetry"] = true });
        string busy = await RenderAsync<MultivocalSourceQuality>(new() { ["Score"] = score, ["Title"] = "A post", ["CanRetry"] = true, ["Retrying"] = "example.org/post" });
        string closed = await RenderAsync<MultivocalSourceQuality>(new() { ["Score"] = score, ["Title"] = "A post", ["CanRetry"] = false });

        Assert.Contains("Quality not assessed", allowed);
        Assert.Contains("Not assessed: no readable page.", allowed);
        Assert.Contains(">Try again</button>", allowed);
        Assert.DoesNotContain("disabled", allowed);
        Assert.Contains("disabled", busy);
        Assert.Contains("Assessing", busy);
        Assert.DoesNotContain("<button", closed);
        Assert.Contains("ui-chip border-line bg-soft text-muted", allowed);
    }

    [Fact]
    public async Task AScoredSourceShowsEveryChecklistItemWithItsReasonAndQuote()
    {
        var first = GreyQualityChecklist.Items[0];
        var second = GreyQualityChecklist.Items[1];
        var score = new GreyQuality
        {
            Address = "example.org/post",
            Outcome = "Passed",
            Points = 12,
            Answers = new()
            {
                new QualityAnswer { Id = first.Id, Score = 1, Reason = "Known vendor", Quote = "We are a vendor", Basis = "model" },
                new QualityAnswer { Id = second.Id, Score = 0.5, Reason = "Decided from the site", Quote = "not shown", Basis = "code" },
            },
            Argument = new GreyArgument
            {
                Claim = "Agents need small contexts",
                ClaimFound = false,
                Questions = new() { new ArgumentAnswer { Id = "backup", Question = "Is it based on evidence?", Answer = "Partly", Quote = "we measured", Basis = "model" } },
            },
        };

        string html = await RenderAsync<MultivocalSourceQuality>(new() { ["Score"] = score, ["Title"] = "A post" });

        Assert.Contains($"Quality 12 of {GreyQualityChecklist.MaxPoints}", html);
        Assert.Contains("ui-chip border-supported-border bg-supported-fill text-supported", html);
        Assert.Contains("Quality checklist answers for A post", html);
        Assert.Equal(GreyQualityChecklist.Items.Count, Count(html, "<th scope=\"row\""));
        Assert.Contains("Known vendor", html);
        Assert.Contains("<q class=\"block text-muted italic\">We are a vendor</q>", html);
        Assert.DoesNotContain("not shown", html); // a quote is shown only for the model's answers
        Assert.Contains(" (code)</td>", html);
        Assert.Contains("Opinion piece: its claim and the critical questions (G13)", html);
        Assert.Contains("Agents need small contexts", html);
        Assert.Contains("This claim was not found word for word in the page.", html);
        Assert.Contains("<dd><q class=\"italic text-muted\">we measured</q></dd>", html);
    }

    [Fact]
    public async Task AnExtractedSourceShowsItsValuesWithTheirQuotes()
    {
        var extracted = new GreyExtraction
        {
            Coverage = new() { "RQ1", "RQ2" },
            Purpose = new ExtractedValue { Value = "Explain memory", Quote = "this post explains" },
            Known = new() { new KnownFact { Name = "Site", Value = "example.org" } },
            Attributes = new()
            {
                new ExtractedAttribute { Attribute = "Practice", Values = new() { new ExtractedValue { Value = "Summarise", Quote = "we summarise" } }, Unsupported = new() { new ExtractedValue { Value = "Prune", Quote = "missing" } } },
                new ExtractedAttribute { Attribute = "Tool", NotStated = true },
            },
        };

        string html = await RenderAsync<MultivocalSourceExtraction>(new() { ["Extracted"] = extracted });
        string failed = await RenderAsync<MultivocalSourceExtraction>(new() { ["Extracted"] = new GreyExtraction { Error = "The page could not be read." } });

        Assert.Contains("covers RQ1, RQ2", html);
        Assert.Contains("Explain memory <q class=\"italic text-muted\">this post explains</q>", html);
        Assert.Contains("Site: example.org", html);
        Assert.Contains("<dd>Summarise <q class=\"italic text-muted\">we summarise</q></dd>", html);
        Assert.Contains("Left out, as the quote was not found in the page: Prune", html);
        Assert.Contains("<dd class=\"text-muted\">not stated</dd>", html);
        Assert.Contains("Not extracted", failed);
        Assert.Contains("The page could not be read.", failed);
    }

    [Fact]
    public async Task TheMapTabShowsTheFixedVersionAndCountsEachValue()
    {
        var attributes = new List<MapAttribute>
        {
            new() { Name = "Practice", Question = "RQ1", Description = "What is done", Values = new() { "Summarise", "Prune" }, Multiple = true },
            new() { Name = "Notes", Question = "RQ2", Description = "Free text", Open = true },
        };
        var map = new MultivocalMapFile
        {
            SourcesSeen = 7,
            FixedVersion = 1,
            Versions = new()
            {
                new MapVersion { Number = 1, By = "model", CreatedUtc = new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc), Attributes = attributes },
                new MapVersion { Number = 2, By = "reviewer", CreatedUtc = new DateTime(2026, 11, 2, 9, 30, 0, DateTimeKind.Utc), Attributes = new() },
            },
        };
        var extraction = new MultivocalExtractionFile
        {
            Sources = new()
            {
                new GreyExtraction { Attributes = new() { new ExtractedAttribute { Attribute = "Practice", Values = new() { new ExtractedValue { Value = "Summarise" }, new ExtractedValue { Value = "Prune" } } } } },
                new GreyExtraction { Attributes = new() { new ExtractedAttribute { Attribute = "Practice", Values = new() { new ExtractedValue { Value = "Summarise" } } } } },
                new GreyExtraction { Attributes = new() { new ExtractedAttribute { Attribute = "Practice", NotStated = true } } },
            },
        };

        string before = await RenderAsync<MultivocalMapTab>(new() { ["Map"] = map });
        string after = await RenderAsync<MultivocalMapTab>(new() { ["Map"] = map, ["Extraction"] = extraction });

        Assert.Contains("generalised over 7 sources", before);
        Assert.Contains("Version 1 is fixed as proposed", before);
        Assert.Contains("2026-11-01 09:30 UTC", before);
        Assert.Contains("proposed by the model", before);
        Assert.Contains("edited by you", before);
        foreach (var (name, _) in MultivocalMapper.KnownFacts)
            Assert.Contains($"<span class=\"font-medium\">{name}</span>", before);
        Assert.Contains("Summarise, Prune (several)", before);
        Assert.Contains("open text", before);
        Assert.DoesNotContain("How many sources have each value", before);

        Assert.Contains("How many sources have each value", after);
        Assert.Contains("From the 3 extracted sources", after);
        Assert.Contains("style=\"width:100%\"", after);
        Assert.Contains("style=\"width:50%\"", after);
        Assert.Contains("text-left text-muted w-1/2\">not stated</th>", after);
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
