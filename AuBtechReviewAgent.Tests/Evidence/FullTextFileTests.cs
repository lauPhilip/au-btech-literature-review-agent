using System.Text.Json;
using AuBtechReviewAgent;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Each paper's PDF has a name of its own (#587), so two papers whose titles start alike never read each other's
/// full text, and the same paper finds the same file in every run.
/// </summary>
public class FullTextFileTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "fulltext-file-" + Guid.NewGuid().ToString("N"));

    public FullTextFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TestFolders.TryDelete(_folder);

    private const string SharedStart = "A systematic review of large language model agents in software engineering";

    private static AcademicPaper Paper(string id, string title, string? doi) =>
        new(id, title, "", "2025", new List<string> { "Doe, J." }, "Venue", doi, "https://example.org/" + id);

    /// <summary>A one-page PDF with the given sentence on it, written with PdfPig.</summary>
    public static byte[] PdfSaying(string sentence)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        page.AddText(sentence, 10, new UglyToad.PdfPig.Core.PdfPoint(40, 780), font);
        return builder.Build();
    }

    [Fact]
    public void PapersWhoseTitlesStartAlikeGetDifferentNames()
    {
        var first = Paper("W1", SharedStart + ": part one", "10.1000/one");
        var second = Paper("W2", SharedStart + ": part two", "10.1000/two");

        Assert.Equal(DocumentRAGUtility.PdfFileName(first.Title), DocumentRAGUtility.PdfFileName(second.Title)); // the old collision
        Assert.NotEqual(FullTextFile.PdfName(first), FullTextFile.PdfName(second));
        Assert.StartsWith(FullTextFile.Prefix, FullTextFile.PdfName(first));
    }

    [Fact]
    public void TheSamePaperGetsTheSameNameWhateverFormItsDoiTakes()
    {
        string name = FullTextFile.PdfName(Paper("W1", "Title", "10.1000/ABC"));
        Assert.Equal(name, FullTextFile.PdfName(Paper("other-id", "Another title", "https://doi.org/10.1000/abc")));
        Assert.Equal(name, FullTextFile.PdfName(Paper("W1", "Title", " doi:10.1000/abc ")));
        Assert.NotEqual(name, FullTextFile.PdfName(Paper("W1", "Title", null))); // no DOI: the record id
        Assert.Equal(FullTextFile.PdfName(Paper("W1", "Title", null)), FullTextFile.PdfName(Paper("W1", "Other", "")));
    }

    [Fact]
    public async Task ThePaperIsReadFromItsOwnFileNotFromOneWithTheSameTitleStart()
    {
        var first = Paper("W1", SharedStart + ": part one", "10.1000/one");
        var second = Paper("W2", SharedStart + ": part two", "10.1000/two");
        File.WriteAllBytes(Path.Join(_folder, FullTextFile.PdfName(first)), PdfSaying("This is the first paper and it is about agents writing code for people."));
        File.WriteAllBytes(Path.Join(_folder, FullTextFile.PdfName(second)), PdfSaying("This is the second paper and it is about agents testing the code of others."));

        var one = await DocumentRAGUtility.IngestAndChunkPaperAsync(first, _folder);
        var two = await DocumentRAGUtility.IngestAndChunkPaperAsync(second, _folder);

        Assert.Equal("already downloaded", one.Source);
        Assert.Contains("first paper", one.Chunks.Single().Text);
        Assert.Contains("second paper", two.Chunks.Single().Text);
    }

    [Fact]
    public async Task AnOlderRunsCopyUnderItsTitleIsStillRead()
    {
        var paper = Paper("W1", "Agents in the loop", "10.1000/one");
        File.WriteAllBytes(Path.Join(_folder, DocumentRAGUtility.PdfFileName(paper.Title)), PdfSaying("An older run saved this paper under the first fifty letters of its title."));

        var result = await DocumentRAGUtility.IngestAndChunkPaperAsync(paper, _folder);

        Assert.Equal("already downloaded", result.Source);
        Assert.Contains("older run", result.Chunks.Single().Text);
        Assert.False(File.Exists(Path.Join(_folder, FullTextFile.PdfName(paper))), "nothing is written under either name");
    }

    [Fact]
    public void TheRecordBesideThePdfSaysWhatItIs()
    {
        var paper = Paper("https://openalex.org/W1", "Agents \"in\" the loop", "https://doi.org/10.1000/ONE");
        FullTextFile.WriteRecord(_folder, paper, "https://repo.example.org/one.pdf");

        string name = FullTextFile.RecordName(paper);
        Assert.True(FullTextFile.IsRecordFile(name));
        Assert.False(FullTextFile.IsRecordFile("transparent-process.json"));
        var record = JsonSerializer.Deserialize<FullTextFile.Record>(File.ReadAllText(Path.Join(_folder, name)))!;
        Assert.Equal(("Agents \"in\" the loop", "https://openalex.org/W1", "10.1000/one", "https://repo.example.org/one.pdf", "doi:10.1000/one"),
            (record.Title, record.Id, record.Doi, record.Url, record.Key));
    }
}
