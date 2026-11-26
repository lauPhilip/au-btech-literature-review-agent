using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The gallery's search, its "Cite this report" text, and the citation and self-check lines of a card.</summary>
public class GalleryFindCiteTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-find-cite-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static GalleryEntry Entry(string title = "AI tutors in secondary schools", int version = 2) => new()
    {
        Id = "0123456789ab",
        RunId = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000001"),
        Card = ReviewMethod.SystematicKey,
        Title = title,
        Questions = new() { "How do teachers use AI tutors?" },
        Keywords = new() { "Teacher workload", "Feedback" },
        CompletedUtc = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc),
        ToolVersion = "0.5.1+abcdef1234567",
        Version = version,
    };

    [Theory]
    [InlineData("tutors", true)]
    [InlineData("AI-Tutors secondary", true)]
    [InlineData("teachers use", true)]
    [InlineData("workload", true)]
    [InlineData("systematic review", true)]
    [InlineData("tutors hospitals", false)]
    [InlineData("grey literature", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void SearchFindsEveryWordInTheTitleQuestionsKeywordsOrType(string query, bool found) =>
        Assert.Equal(found, GallerySearch.Matches(Entry(), query));

    [Fact]
    public void ALongQueryIsCutAndAnEmptyOneIsNone()
    {
        Assert.Null(GallerySearch.Clean("  "));
        Assert.Equal("tutors", GallerySearch.Clean(" tutors "));
        Assert.Equal(GallerySearch.MaxQueryLength, GallerySearch.Clean(new string('a', 500))!.Length);
    }

    [Fact]
    public void TheApaReferenceCitesTheVersionAsSoftwareOutput()
    {
        string apa = GalleryCitation.Apa(Entry(), "https://example.org/");

        Assert.Equal(
            "TraceableAI – artificial researcher. (2026, November 24). AI tutors in secondary schools (Version 2) " +
            "[AI-generated systematic literature review, TraceableAI v0.5.1 (abcdef1), run 1a2b3c4d-0000-0000-0000-000000000001; not peer reviewed]. " +
            "Department of Business Development and Technology, Aarhus University. https://example.org/gallery/0123456789ab/v/2", apa);
    }

    [Fact]
    public void TheBibTexEntryIsEscapedAndNamesTheRunVersionAndAddress()
    {
        string bib = GalleryCitation.BibTex(Entry("Costs & {benefits} of 50% AI_tutors"), "https://example.org");

        Assert.StartsWith("@misc{traceableai_1a2b3c4d,", bib);
        Assert.Contains(@"title        = {{Costs \& \{benefits\} of 50\% AI\_tutors}},", bib);
        Assert.Contains("author       = {{TraceableAI -- artificial researcher}},", bib);
        Assert.Contains("year         = {2026},", bib);
        Assert.Contains("month        = nov,", bib);
        Assert.Contains(@"howpublished = {\url{https://example.org/gallery/0123456789ab/v/2}},", bib);
        Assert.Contains("not peer reviewed. Version 2 in the TraceableAI gallery}", bib); Assert.Contains("1a2b3c4d-0000-0000-0000-000000000001", bib);
        Assert.EndsWith("}", bib);
        Assert.Equal(@"a\textbackslash{}b \textasciitilde{}", GalleryCitation.TeX("a\\b ~"));
        // Every brace the entry opens is closed, so a title cannot end the entry early.
        string bare = bib.Replace(@"\{", "").Replace(@"\}", "");
        Assert.Equal(bare.Count(c => c == '{'), bare.Count(c => c == '}'));
    }

    [Fact]
    public void TheCardSaysHowManyCitationsWereSupportedOnlyWhenTheVerdictsAreKnown()
    {
        Assert.Equal("Citations 118 of 121 supported",
            GalleryCard.CitationSupport(new CitationShares { Checked = 121, Supported = 118, Unverifiable = 3 }));
        Assert.Equal("Citations 110 of 121 supported, 8 partly, 3 not supported",
            GalleryCard.CitationSupport(new CitationShares { Checked = 121, Supported = 110, Partly = 8, NotSupported = 3 }));
        Assert.Null(GalleryCard.CitationSupport(new CitationShares { Checked = 30 }));
        Assert.Null(GalleryCard.SelfCheckLine(null));
        Assert.Equal("Self-check: 8 checks passed, 2 notes, 1 fault", GalleryCard.SelfCheckLine(new GallerySelfCheck(8, 2, 1)));
    }

    [Fact]
    public void AChangeOfOnePercentagePointIsOnePoint()
    {
        string none = Path.Join(_root, "nothing-here");
        var a = new GalleryEntry { Citations = new() { Checked = 10, Supported = 8, Partly = 2 } };
        var b = a with { Citations = new() { Checked = 100, Supported = 81, Partly = 19 } };

        var rows = GalleryComparison.Rows(a, none, b, none).ToDictionary(r => r.Label);

        Assert.Equal("+1 point", rows["Supported"].Change);
        Assert.Equal("−1 point", rows["Partly supported"].Change);
    }

    [Fact]
    public async Task AVersionKeepsTheSelfCheckItsRunWroteAndNoneWhenItWroteNone()
    {
        var runs = new RunStore(Path.Join(_root, "workspace"));
        var gallery = new GalleryStore(new GalleryOptions { Folder = Path.Join(_root, "gallery") }, _root, runs);
        var older = await GalleryStoreTests.FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" }, "Older");
        var newer = await GalleryStoreTests.FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" }, "Newer");
        File.WriteAllText(Path.Join(runs.FolderOf(newer.Id), PaperSelfCheckFile.FileName),
            JsonSerializer.Serialize(new PaperSelfCheckFile { Passed = 8, Notes = 2, Faults = 1 }));

        var entry = (await gallery.AddAsync(older.Id, older.Key, consent: true)).Entry!;
        await gallery.AddAsync(newer.Id, newer.Key, consent: true);

        Assert.Null(gallery.Find(entry.Id, 1)!.SelfCheck);
        Assert.Equal(new GallerySelfCheck(8, 2, 1), gallery.Find(entry.Id)!.SelfCheck);
        Assert.True(File.Exists(Path.Join(gallery.VersionFolderOf(entry.Id, 2), PaperSelfCheckFile.FileName)));
    }

    [Fact]
    public void AnUnreadableSelfCheckIsNone()
    {
        Directory.CreateDirectory(_root);
        Assert.Null(GallerySelfCheck.Read(_root));
        File.WriteAllText(Path.Join(_root, PaperSelfCheckFile.FileName), "{ not json");
        Assert.Null(GallerySelfCheck.Read(_root));
        File.WriteAllText(Path.Join(_root, PaperSelfCheckFile.FileName), "{\"passed\": 10}");
        Assert.Equal(new GallerySelfCheck(10, 0, 0), GallerySelfCheck.Read(_root));
    }
}
