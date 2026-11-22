using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The paper page of a systematic review (Components/Pages/SpecMatrix.razor), read as source, since the tests do
/// not render components. Issue #139: the tables of the included studies come after the references as Tables A.1 to
/// A.3, each with its note, and section 3 keeps its charts and points to them.
/// </summary>
public class PaperPageTests
{
    private static string? PageSource() => ReportPageSource.Read();

    [Fact]
    public void TheTablesOfTheStudiesComeAfterTheReferencesAsAnAppendix()
    {
        string? page = PageSource();
        if (page == null) return; // not run from the repository (packaged tests)
        int At(string text) { int i = page.IndexOf(text, StringComparison.Ordinal); Assert.True(i >= 0, $"the page has \"{text}\""); return i; }

        int section3 = At(">3. Characteristics of the Included Studies</h2>");
        int results = At(">4. Results & Synthesis</h2>");
        int references = At(">References</h2>");
        int appendix = At(">Appendix</h2>");

        // Section 3 keeps its text and charts; its text names the appendix tables.
        Assert.InRange(At("@DataParagraph()"), section3, results);
        Assert.InRange(At("Figure 3.1: Included Publications per Year"), section3, results);
        Assert.InRange(At("Figure 3.4: Journal Quartile and Venue Tier"), section3, results);
        Assert.Contains("MethodsSectionWriter.AppendixPointer(tableRecords.Count, extractions)", page);

        // The appendix comes after the references, with the three tables in order and each note under its table.
        Assert.True(results < references && references < appendix, "the appendix comes after the references");
        int a1 = At("Table A.1: Included Studies");
        int a2 = At("Table A.2: Extracted Study Data");
        int a2Note = At("Hover over a value to see the quote it was taken from.");
        int a3 = At("Table A.3: Quality Appraisal");
        int a3Note = At("MethodsSectionWriter.NotAppraisedNote(extractions)");
        Assert.True(appendix < a1 && a1 < a2 && a2 < a2Note && a2Note < a3 && a3 < a3Note, "the tables keep their order, each with its note");
        Assert.DoesNotMatch(@"Table 3\.[123]", page);
    }

    [Fact]
    public void SectionThreeOfThePageUsesTheSameSentenceAsMainTex()
    {
        var studies = new[] { new StudyExtraction { ReferenceNumber = 1, AppraisalCategory = "qualitative" } };
        Assert.Equal("Table A.1 in the appendix lists each included study. Table A.2 gives the data extracted from each study, and Table A.3 the quality appraisal of each study with MMAT 2018.",
            MethodsSectionWriter.AppendixPointer(1, studies));
    }

    private static string? SourceOf(params string[] path)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        return root == null ? null : File.ReadAllText(Path.Join(new[] { root, "AuBtechReviewAgent", "Components" }.Concat(path).ToArray()));
    }

    [Fact]
    public void EveryReferenceOnThePageIsShownWithItalicsNotAsterisks()
    {
        // Issue #128: an APA reference carries Markdown asterisks around the journal and its volume. Printed as
        // plain text they show as "*Journal*, *14*(2)", so every place that shows one goes through ApaReference.
        string? page = PageSource();
        string? panel = SourceOf("Dashboard", "ScreeningReviewPanel.razor");
        string? component = SourceOf("Paper", "ApaReference.razor");
        if (page == null || panel == null || component == null) return; // not run from the repository (packaged tests)

        Assert.Contains("<ApaReference Text=\"@citation\" />", page);
        Assert.Contains("<ApaReference Text=\"@paperRow.ApaCitation\" />", page);
        Assert.DoesNotContain(">@paperRow.ApaCitation<", page);
        Assert.DoesNotContain("@includedCitations[i]", page);
        Assert.Contains("<ApaReference Text=\"@row.Citation\" />", panel);
        Assert.DoesNotContain(">@row.Citation<", panel);
        Assert.Contains("ApaCitationBuilder.ItalicSpans(Text)", component);
        Assert.Contains("<em>@span.Text</em>", component);
    }
}
