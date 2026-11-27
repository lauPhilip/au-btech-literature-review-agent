using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The body of the report in the front page's journal style (issue #736), read as source: the abstract's serif, size
/// and leading; serif section headings; figures with no box and their numbered caption under them; tables with their
/// numbered caption above; and citation numbers as quiet markers in their verdict's colour. The look itself is checked
/// by axe-core and the screenshots of the accessibility job.
/// </summary>
public class ReportBodyStyleTests
{
    private static string Read(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? "" : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent" }.Concat(path).ToArray()));
    }

    [Fact]
    public void TheBodyHasTheAbstractsTypeAndSerifHeadings()
    {
        string page = Read("Components", "Pages", "SpecMatrix.razor");
        if (page.Length == 0) return; // packaged tests
        Assert.Contains("text-base leading-[1.55] text-ink", page); // the abstract's 1rem at 1.55
        Assert.DoesNotContain("max-w-[70ch]", page); // the text fills the sheet, as on paper

        string css = Read("wwwroot", "css", "report-paper.css");
        Assert.Contains(".journal { font-family: var(--paper-serif); }", css);
        Assert.Contains(".journal h2:not(.af-label) { font-family: var(--paper-serif);", css);
        Assert.Contains(".journal h3 { font-family: var(--paper-serif);", css);
        Assert.DoesNotContain("border-radius", css);
    }

    [Fact]
    public void FiguresHaveNoBoxAndTheirCaptionUnderThem()
    {
        string studies = Read("Components", "Pages", "SpecMatrixStudies.razor");
        if (studies.Length == 0) return;
        Assert.DoesNotContain("border border-line p-3 bg-page", studies);
        for (int n = 1; n <= 4; n++)
        {
            int caption = studies.IndexOf($"<figcaption class=\"ui-caption\">Figure 3.{n}:", StringComparison.Ordinal);
            int opens = studies.LastIndexOf("<figure class=\"ui-figure print-keep\">", caption, StringComparison.Ordinal);
            Assert.True(caption > 0 && opens >= 0 && studies.IndexOf("</figure>", opens, StringComparison.Ordinal) > caption, $"Figure 3.{n} has its caption at its foot");
            Assert.DoesNotContain("<div", studies[caption..studies.IndexOf("</figure>", caption, StringComparison.Ordinal)]);
        }

        string methods = Read("Components", "Pages", "SpecMatrixIntroduction.razor");
        Assert.DoesNotContain("rounded-card", methods);
        Assert.True(methods.IndexOf("</ol>", StringComparison.Ordinal) < methods.IndexOf("Figure 2.1: Flow of records through the review (PRISMA 2020)</figcaption>", StringComparison.Ordinal));

        string results = Read("Components", "Pages", "SpecMatrixResults.razor");
        Assert.DoesNotContain("Artifact: ", results);
        Assert.Contains("<figcaption class=\"ui-caption\">Figure 4.1: @artLabel@artCaption</figcaption>", results);
    }

    [Fact]
    public void TablesHaveTheirNumberedCaptionAboveThem()
    {
        string results = Read("Components", "Pages", "SpecMatrixResults.razor");
        if (results.Length == 0) return;
        int map = results.IndexOf("<caption class=\"ui-caption pb-2 text-left\">Table 4.1: Evidence map of studies by theme</caption>", StringComparison.Ordinal);
        Assert.True(map > 0 && map < results.IndexOf("<thead", map, StringComparison.Ordinal));
        Assert.Contains("<caption class=\"ui-caption pb-2 text-left\">Table 4.@artTable: @artLabel@artCaption</caption>", results);
        Assert.Contains("int artTable = Page.codebook is { Themes.Count: > 0 } ? 2 : 1;", results); // numbered after the evidence map
    }

    [Theory]
    [InlineData(CitationSupportChecker.Supported, "text-supported")]
    [InlineData(CitationSupportChecker.Partial, "text-partly")]
    [InlineData(CitationSupportChecker.NotSupported, "text-not-supported")]
    [InlineData(CitationSupportChecker.InsufficientEvidence, "text-abstract-only")]
    [InlineData(CitationSupportChecker.Unverifiable, "text-unverifiable")]
    [InlineData(null, "text-muted")]
    public void ACitationNumberIsAQuietMarkerInItsVerdictsColour(string? verdict, string colour)
    {
        Assert.Equal(colour, CitationView.Marker(verdict));
        Assert.DoesNotContain("bg-", CitationView.Marker(verdict));
        Assert.DoesNotContain("border", CitationView.Marker(verdict));
    }

    [Fact]
    public void TheCitationNumberUsesTheSharedMarkerClass()
    {
        string prose = Read("Components", "Paper", "CitedProse.razor");
        if (prose.Length == 0) return;
        Assert.Contains("class=\"@AuBtechReviewAgent.CitationView.Marker(check?.Verdict)", prose);
        Assert.Contains("ui-cite focus-visible:outline", prose);
        Assert.DoesNotContain("font-mono text-[0.8em]", prose);

        string styles = Read("Styles", "tailwind.input.css");
        foreach (string cls in new[] { ".ui-figure {", ".ui-caption {", ".ui-cite {" })
        {
            int at = styles.IndexOf(cls, StringComparison.Ordinal);
            Assert.True(at > 0, cls);
            Assert.DoesNotContain("rounded", styles[at..styles.IndexOf('\n', at)]);
        }
    }
}
