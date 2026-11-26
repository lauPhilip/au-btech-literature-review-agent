using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The gallery is public, so its copy of a run holds no abstracts from licensed databases and only short quotes.</summary>
public class GalleryPublicCopyTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-public-" + Guid.NewGuid().ToString("N"));
    private string GalleryFolder => Path.Join(_root, "gallery");

    public void Dispose() => TestFolders.TryDelete(_root);

    private GalleryStore Store(RunStore runs) => new(new GalleryOptions { Folder = GalleryFolder }, _root, runs);

    private static string Words(int n) => string.Join(" ", Enumerable.Range(1, n).Select(i => $"w{i}"));

    // The copied ledger, wherever the gallery keeps it (one folder per entry, or per version).
    private string CopiedFile(string name) =>
        Directory.EnumerateFiles(GalleryFolder, name, SearchOption.AllDirectories).Single(f => !Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith('.'));

    private static void WriteSourceText(string folder)
    {
        string ledgerPath = Path.Join(folder, GallerySnapshot.LedgerFile);
        var state = JsonSerializer.Deserialize<ReviewState>(File.ReadAllText(ledgerPath))!;
        state.Phases.Screening.Add(new ScreeningLog("SCOPUS_ID:85000000001", "Scopus record", "Included", "r", "c", "s", Abstract: "A Scopus abstract that may not be shown."));
        state.Phases.Screening.Add(new ScreeningLog("http://arxiv.org/abs/2401.00001v1", "arXiv record", "Excluded", "r", "c", "s", Abstract: "An arXiv abstract under CC0."));
        File.WriteAllText(ledgerPath, JsonSerializer.Serialize(state));
        File.WriteAllText(Path.Join(folder, "extraction.json"), JsonSerializer.Serialize(new[]
        {
            new StudyExtraction { ReferenceNumber = 1, PaperId = "SCOPUS_ID:85000000001", Method = new EvidencedValue { Value = "survey", Quote = Words(120), QuoteVerified = true } },
        }));
    }

    [Fact]
    public async Task TheCopyDropsLicensedAbstractsShortensQuotesAndSaysSo()
    {
        var runs = new RunStore(Path.Join(_root, "workspace"));
        var gallery = Store(runs);
        var (id, key) = await GalleryStoreTests.FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });
        WriteSourceText(runs.FolderOf(id));

        Assert.True((await gallery.AddAsync(id, key, consent: true)).Ok);

        var ledger = JsonSerializer.Deserialize<ReviewState>(File.ReadAllText(CopiedFile(GallerySnapshot.LedgerFile)))!;
        Assert.Equal(new[] { "", "An arXiv abstract under CC0." }, ledger.Phases.Screening.Select(s => s.Abstract));
        var extraction = JsonSerializer.Deserialize<List<StudyExtraction>>(File.ReadAllText(CopiedFile("extraction.json")))!;
        Assert.StartsWith(Words(50) + " " + PublicCopy.Ellipsis, extraction[0].Method.Quote);
        Assert.EndsWith(PublicCopy.ShortenedNote, extraction[0].Method.Quote);
        var record = JsonSerializer.Deserialize<PublicCopyRecord>(File.ReadAllText(CopiedFile(PublicCopy.RecordFile)))!;
        Assert.Equal(1, record.AbstractsRemoved);
        Assert.Equal(PublicCopy.Sha256(Words(120)), Assert.Single(record.Shortened).Sha256);

        // The run's own files, which the owner's archive is built from, are not touched.
        Assert.Contains(Words(120), File.ReadAllText(Path.Join(runs.FolderOf(id), "extraction.json")));
    }

    [Fact]
    public async Task ACopyTakenBeforeTheRulesIsMadePublicWhenTheGalleryStarts()
    {
        var runs = new RunStore(Path.Join(_root, "workspace"));
        var (id, key) = await GalleryStoreTests.FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });
        Assert.True((await Store(runs).AddAsync(id, key, consent: true)).Ok);
        // As an older copy: the source text written into it, and no record of the rules.
        string folder = Path.GetDirectoryName(CopiedFile(GallerySnapshot.LedgerFile))!;
        File.Delete(Path.Join(folder, PublicCopy.RecordFile));
        WriteSourceText(folder);

        var gallery = Store(runs);

        Assert.DoesNotContain("A Scopus abstract", File.ReadAllText(CopiedFile(GallerySnapshot.LedgerFile)));
        Assert.True(File.Exists(Path.Join(folder, PublicCopy.RecordFile)));
        Assert.Single(gallery.List());
        Assert.Equal(0, GallerySnapshot.MakeOlderCopiesPublic(GalleryFolder)); // once only
    }
}
