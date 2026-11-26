using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The paper of a review with both pools (block I-6), on the module kit's whole formal run: the method, the data with
/// a flow of two arms, the theme notes, the references and the archive cover both pools, and a source that rests on
/// its abstract alone is marked where it is cited.
/// </summary>
public class MultivocalFormalPaperTests
{
    private static async Task<(KitRun Run, string Tex)> Paper()
    {
        var run = await ModuleKit.RunAsync("multivocal-formal");
        return (run, ModuleKit.Text(run.Archive[MultivocalPaper.MainTexFile]));
    }

    [Fact]
    public async Task TheMethodCoversTheFormalPoolInCode()
    {
        var (_, tex) = await Paper();

        Assert.Contains("The formal pool was searched in 1 academic database (OpenAlex), the bibliographic databases of the systematic review", tex);
        Assert.Contains("1 was set aside as duplicates by identifier, DOI or title", tex);
        Assert.Contains("Scopus / ScienceDirect: not run (needs a key this server does not have).", tex);
        Assert.Contains("Grey and academic sources were screened with the same criteria, steps and answer format", tex);
        Assert.Contains("Every included grey source was scored on the 20 items", tex);
        Assert.Contains("with the Mixed Methods Appraisal Tool (MMAT, 2018), not with Table 7", tex);
        Assert.Contains("1 rests on a full text and 1 on the abstract alone", tex);
        Assert.Contains("The quality threshold applies to grey sources only", tex);
        Assert.Contains("Each finding was labelled in code as grey or formal literature", tex);
    }

    [Fact]
    public async Task TheDataSectionHasAFlowWithAnArmForEachPool()
    {
        var (_, tex) = await Paper();

        Assert.Contains("The grey searches found 2 sources and the academic databases returned 4 records, of which 2 were set aside before screening.", tex);
        Assert.Contains("Grey literature found: 2", tex);
        Assert.Contains("Academic records found: 4", tex);
        Assert.Contains("Appraised with MMAT: 2", tex);
        Assert.Contains("of which on the abstract only: 1", tex);
        Assert.Contains(@"\legend{1st tier, 2nd tier, 3rd tier, academic}", tex);
        Assert.Contains("{Academic literature}", tex);
    }

    [Fact]
    public async Task EachThemeNoteSaysWhichPoolsItRestsOn()
    {
        var (_, tex) = await Paper();

        Assert.Matches(@"What this theme rests on: 3 sources~\\cite\{[^,}]+,[^,}]+,[^,}]+\} \(1 grey and 2 academic\)", tex);
        Assert.Contains("with a mean quality of the grey sources of", tex);
    }

    [Fact]
    public async Task FormalSourcesAreCitedAsInTheSystematicReviewAndTheAbstractOnlyOneIsMarked()
    {
        var (run, tex) = await Paper();

        Assert.Contains(@"} Doe, J., \& Lee, K. (2025). Context summaries for LLM agents.", tex); // its key is that of references.bib
        Assert.Contains($"(abstract only: no open full text was found, so the review read the abstract)", tex);
        string bib = ModuleKit.Text(run.Archive[GreyReferences.BibFile]);
        Assert.Contains("@article{doe2025context,", bib);
        Assert.Contains("@misc{", bib); // the grey source keeps its own entry
        Assert.Contains("KW  - formal literature", ModuleKit.Text(run.Archive[GreyReferences.ScreenedFile]));
        Assert.Contains(@"AI-GENERATED MULTIVOCAL LITERATURE REVIEW", tex);
    }

    [Fact]
    public void TheMethodNamesADatabaseWhoseRawAnswerWasNotKept()
    {
        var kept = new GreySearchLog { Source = "openalex", Formal = true, Returned = 20, RawFile = "SourceResponses/formal-openalex-1.json" };
        var failed = new GreySearchLog { Source = "crossref", Formal = true, Error = "timed out" };
        var notKept = new[]
        {
            new GreySearchLog { Source = "scopus", Formal = true, Returned = 20 },
            new GreySearchLog { Source = "ieee", Formal = true, Returned = 20 },
        };

        Assert.Equal("Each raw answer was saved as received, with its SHA-256 fingerprint.", MultivocalFormalText.RawAnswersSentence(new[] { kept, failed }));
        string sentence = MultivocalFormalText.RawAnswersSentence(notKept.Append(kept));
        Assert.StartsWith("Each raw answer was saved as received, with its SHA-256 fingerprint, except those of ", sentence);
        Assert.Contains(SourceCatalog.DisplayNameFor("scopus") + " and " + SourceCatalog.DisplayNameFor("ieee") + ", which were not kept.", sentence);
    }
}
