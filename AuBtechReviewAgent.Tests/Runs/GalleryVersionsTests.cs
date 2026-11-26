using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Versions of one review in the gallery: stable numbers and folders, withdrawing, the cap, and entries made before versions.</summary>
public class GalleryVersionsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-versions-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Join(_root, "workspace");

    public void Dispose() => TestFolders.TryDelete(_root);

    private GalleryStore Store(RunStore runs, int max = 50) =>
        new(new GalleryOptions { Folder = Path.Join(_root, "gallery"), MaxEntries = max }, _root, runs);

    private static Task<(Guid Id, string Key)> Run(RunStore runs, string topic, string title) =>
        GalleryStoreTests.FinishedRunAsync(runs, topic, new[] { "How is AI used?" }, title);

    [Fact]
    public async Task EveryVersionKeepsItsNumberFolderAndReport()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var one = await Run(runs, "AI in schools", "First");
        var two = await Run(runs, "ai in schools", "Second");
        var three = await Run(runs, "AI in Schools.", "Third");

        var entry = (await gallery.AddAsync(one.Id, one.Key, consent: true)).Entry!;
        string firstFolder = gallery.VersionFolderOf(entry.Id, 1);
        await gallery.AddAsync(two.Id, two.Key, consent: true);
        await gallery.AddAsync(three.Id, three.Key, consent: true);

        var newest = gallery.Find(entry.Id)!;
        Assert.Equal(3, newest.Version);
        Assert.Equal("Third", newest.Title);
        Assert.Equal(new[] { "First", "Second", "Third" }, newest.Versions.Select(v => v.Title));
        Assert.Equal("First", gallery.Find(entry.Id, 1)!.Title);
        Assert.Equal(one.Id, gallery.Find(entry.Id, 1)!.RunId);
        Assert.True(File.Exists(Path.Join(firstFolder, GallerySnapshot.ReportFile)));
        Assert.Contains("First", File.ReadAllText(Path.Join(firstFolder, GallerySnapshot.ReportFile)));
        Assert.Null(gallery.Find(entry.Id, 4));
        Assert.Equal(entry.Id, gallery.ForRun(one.Id)?.Id);

        // The same run again adds nothing.
        var again = await gallery.AddAsync(two.Id, two.Key, consent: true);
        Assert.Contains("version 2", again.Message);
        Assert.Equal(3, gallery.Find(entry.Id)!.Versions.Count);
    }

    [Fact]
    public async Task WithdrawingRemovesEveryVersion()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var one = await Run(runs, "AI in schools", "First");
        var two = await Run(runs, "AI in schools", "Second");
        var entry = (await gallery.AddAsync(one.Id, one.Key, consent: true)).Entry!;
        await gallery.AddAsync(two.Id, two.Key, consent: true);

        Assert.False(await gallery.WithdrawAsync(entry.Id, "not-a-key"));
        // Each run has its own key; the key of the oldest version is enough to remove them all.
        Assert.True(await gallery.WithdrawAsync(entry.Id, one.Key));

        Assert.Empty(gallery.List());
        Assert.Null(gallery.Find(entry.Id, 2));
        Assert.False(Directory.Exists(gallery.FolderOf(entry.Id)));
    }

    [Fact]
    public async Task ANumberDroppedByTheCapIsNeverGivenAgain()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs, max: 2);
        var one = await Run(runs, "AI in schools", "First");
        var two = await Run(runs, "AI in schools", "Second");
        var three = await Run(runs, "AI in schools", "Third");
        var entry = (await gallery.AddAsync(one.Id, one.Key, consent: true)).Entry!;
        await gallery.AddAsync(two.Id, two.Key, consent: true);

        await gallery.AddAsync(three.Id, three.Key, consent: true);

        Assert.Equal(new[] { 2, 3 }, gallery.Find(entry.Id)!.Versions.Select(v => v.Number));
        Assert.Null(gallery.Find(entry.Id, 1));
        Assert.Equal("Second", gallery.Find(entry.Id, 2)!.Title);
        Assert.Equal(two.Id, gallery.Find(entry.Id, 2)!.RunId);
    }

    [Fact]
    public async Task TheCapCountsVersionsAndTrimsTheLongestHistoryFirst()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs, max: 3);
        var a1 = await Run(runs, "Topic A", "A1");
        var a2 = await Run(runs, "Topic A", "A2");
        var b1 = await Run(runs, "Topic B", "B1");
        var c1 = await Run(runs, "Topic C", "C1");
        var a = (await gallery.AddAsync(a1.Id, a1.Key, consent: true)).Entry!;
        await gallery.AddAsync(a2.Id, a2.Key, consent: true);
        await gallery.AddAsync(b1.Id, b1.Key, consent: true);

        await gallery.AddAsync(c1.Id, c1.Key, consent: true);

        // Four versions over a cap of three: A's oldest version goes, and every review stays.
        Assert.Equal(new[] { "A2", "B1", "C1" }, gallery.List().Select(e => e.Title).OrderBy(t => t));
        Assert.Equal(new[] { 2 }, gallery.Find(a.Id)!.Versions.Select(v => v.Number));
        Assert.False(Directory.Exists(gallery.VersionFolderOf(a.Id, 1)));
        Assert.True(Directory.Exists(gallery.VersionFolderOf(a.Id, 2)));
    }

    [Fact]
    public async Task AnEntryMadeBeforeVersionsBecomesVersionOne()
    {
        var runs = new RunStore(Workspace);
        var (id, key) = await Run(runs, "AI in schools", "Old");
        string folder = Path.Join(_root, "gallery", "a1b2c3d4e5f6");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Join(runs.FolderOf(id), GallerySnapshot.ReportFile), Path.Join(folder, GallerySnapshot.ReportFile));
        File.Copy(Path.Join(runs.FolderOf(id), GallerySnapshot.LedgerFile), Path.Join(folder, GallerySnapshot.LedgerFile));
        var old = new GalleryEntry
        {
            Id = "a1b2c3d4e5f6", RunId = id, Module = "systematic", Card = "systematic", Title = "Old",
            AddedUtc = DateTime.UtcNow.AddDays(-2), CitationsChecked = 30, OwnerTokenSha256 = GalleryFingerprint.OwnerToken(key),
        };
        File.WriteAllText(Path.Join(folder, GalleryEntry.FileName), JsonSerializer.Serialize(old));

        var gallery = Store(runs);

        var entry = gallery.Find("a1b2c3d4e5f6")!;
        var v1 = Assert.Single(entry.Versions);
        Assert.Equal(1, v1.Number);
        Assert.Equal(1, entry.LastVersion);
        Assert.True(File.Exists(Path.Join(gallery.VersionFolderOf(entry.Id, 1), GallerySnapshot.ReportFile)));
        Assert.False(File.Exists(Path.Join(folder, GallerySnapshot.ReportFile)));
        Assert.True(GalleryStore.CanWithdraw(entry, key));
        Assert.True(await gallery.WithdrawAsync(entry.Id, key));
    }

    [Fact]
    public void TheComparisonIsWrittenInCodeFromTheStoredFigures()
    {
        string none = Path.Join(_root, "nothing-here");
        var a = new GalleryEntry { Identified = 100, Included = 5, References = 5, Citations = new() { Checked = 10, Supported = 8, Partly = 2 }, ToolVersion = "1.0.0" };
        var b = a with { Identified = 120, Included = 7, Citations = new() { Checked = 20, Supported = 18, Partly = 1, NotSupported = 1 } };

        var rows = GalleryComparison.Rows(a, none, b, none).ToDictionary(r => r.Label);

        Assert.Equal("+20", rows["Records found"].Change);
        Assert.Equal("+2", rows["Studies included"].Change);
        Assert.Equal("same", rows["References"].Change);
        Assert.Equal("8 (80%)", rows["Supported"].From);
        Assert.Equal("18 (90%)", rows["Supported"].To);
        Assert.Equal("+10 points", rows["Supported"].Change);
        Assert.Equal(GalleryComparison.NotRecorded, rows["Model calls"].From);
        Assert.False(rows.ContainsKey("Estimated cost")); // only with a price table
    }
}
