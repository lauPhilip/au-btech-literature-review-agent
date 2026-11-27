using AuBtechReviewAgent.Components.Paper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A model paragraph is its own &lt;p&gt; on the report page, not a run of &lt;br&gt;, so the page layout can break it as a
/// paragraph; and Table A.1's citations break by word at every width (issue #700).
/// </summary>
public class ReportParagraphsTests
{
    private const string TwoParagraphs = "Checks before an action are common [1]. Rollback is rarely tested [2].\n\nHow the two combine is not yet known.";

    [Fact]
    public void ABlankLineBetweenSentencesStartsANewParagraph()
    {
        var sentences = CitationSupportChecker.SplitSentences(TwoParagraphs);

        var paragraphs = CitedProse.Paragraphs(TwoParagraphs, sentences);

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal(new[] { 0, 1 }, paragraphs[0]);
        Assert.Equal(new[] { 2 }, paragraphs[1]);
        // A blank line before the first sentence starts no empty paragraph.
        Assert.Single(CitedProse.Paragraphs("\n\nOne line.", CitationSupportChecker.SplitSentences("\n\nOne line.")));
        Assert.Empty(CitedProse.Paragraphs("", Array.Empty<string>()));
    }

    [Fact]
    public async Task EachParagraphIsItsOwnElementWithNoLineBreaks()
    {
        string html = await RenderAsync(new() { ["Field"] = "discussionItem", ["Text"] = TwoParagraphs });

        Assert.Equal(1, Count(html, "<p class=\"text-ink\">"));
        Assert.Equal(1, Count(html, "<p class=\"pt-2 text-ink\">"));
        Assert.DoesNotContain("<br", html);
        Assert.True(html.IndexOf("Rollback", StringComparison.Ordinal) < html.IndexOf("pt-2 text-ink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneParagraphStaysOneElement()
    {
        string html = await RenderAsync(new() { ["Field"] = "discussionItem", ["Text"] = "Checks are common [1]. Rollback is rare [2]." });

        Assert.Equal(1, Count(html, "<p "));
    }

    [Fact]
    public void TableA1CitationsBreakByWordAtEveryWidth()
    {
        string css = Read("wwwroot", "css", "report-paper.css");
        string appendix = Read("Components", "Pages", "SpecMatrixAppendix.razor");
        if (css.Length == 0 || appendix.Length == 0) return; // packaged tests

        // The rule stands outside the narrow-sheet container query, so it holds at 1440 px and on paper too.
        int rule = css.IndexOf(".ui-studies-table .ui-studies-cite { word-break: normal; overflow-wrap: anywhere; }", StringComparison.Ordinal);
        Assert.True(rule >= 0);
        Assert.True(rule < css.IndexOf("@container", StringComparison.Ordinal));
        string cell = appendix.Split('\n').Single(l => l.Contains("ui-studies-cite", StringComparison.Ordinal));
        Assert.DoesNotContain("break-all", cell);
    }

    private static string Read(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? "" : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent" }.Concat(path).ToArray()));
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    private static async Task<string> RenderAsync(Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CitedProse>(ParameterView.FromDictionary(parameters));
            return output.ToHtmlString();
        });
    }
}
