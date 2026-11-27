using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The tool's DOI and "Cite this tool" block on About (issue #619).</summary>
[Collection(ApiCollection.Name)]
public class ToolCitationTests
{
    [Fact]
    public void The_references_carry_the_DOI_the_version_and_the_year()
    {
        var citation = new ToolCitation("0.6.0", 2026);

        Assert.Equal("Lau, P. S. P. Ø. O., & Nidhi. (2026). " + ToolCitation.Title + " (Version 0.6.0) [Computer software]. " +
                     SiteSeo.Organisation + ". https://doi.org/10.5281/zenodo.24379094", citation.Apa());
        string bib = citation.BibTex();
        Assert.StartsWith("@software{traceableai,", bib);
        Assert.Contains("doi          = {10.5281/zenodo.24379094}", bib);
        Assert.Contains("version      = {0.6.0}", bib);
        Assert.Contains(@"{\O}", bib);
        Assert.EndsWith("}", bib);
    }

    [Fact]
    public void BibTeX_escapes_the_characters_LaTeX_reads_as_commands_and_an_unknown_version_is_left_out()
    {
        string bib = new ToolCitation("1_0&beta%{x}", 2026).BibTex();

        Assert.Contains(@"version      = {1\_0\&beta\%\{x\}}", bib);
        Assert.DoesNotContain("1_0&beta%", bib);
        Assert.DoesNotContain("version", new ToolCitation("", 2026).BibTex());
        Assert.DoesNotContain("\n\n", new ToolCitation("", 2026).BibTex());
        Assert.DoesNotContain("(Version", new ToolCitation("", 2026).Apa());
    }

    [Fact]
    public async Task About_links_the_DOI_and_the_block_needs_no_script()
    {
        using var host = new ApiTestHost();
        string page = await host.CreateClient().GetStringAsync("/about");
        string block = page[page.IndexOf("data-tool-cite", System.StringComparison.Ordinal)..];
        block = block[..block.IndexOf("</details>", System.StringComparison.Ordinal)];

        Assert.Contains("<a href=\"https://doi.org/10.5281/zenodo.24379094\"", page);
        Assert.Contains("Cite this tool", block);
        Assert.Contains("[Computer software]", block);
        Assert.Contains("@software{traceableai,", block);
        Assert.DoesNotContain("<script", block);
        Assert.DoesNotContain("onclick", block);
        Assert.DoesNotContain("rounded", block);
    }

    [Fact]
    public void The_landing_page_data_names_the_DOI()
    {
        using var doc = JsonDocument.Parse(SiteSeo.JsonLd("https://traceable.example.org"));
        Assert.Equal("https://doi.org/10.5281/zenodo.24379094", doc.RootElement.GetProperty("identifier").GetString());
        Assert.Contains("https://doi.org/10.5281/zenodo.24379094", doc.RootElement.GetProperty("sameAs").EnumerateArray().Select(e => e.GetString()));
    }
}
