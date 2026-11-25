using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The gallery of example reports: consent, owner only, one entry per review, the cap, withdrawing and hiding.</summary>
public class GalleryStoreTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Join(_root, "workspace");

    public void Dispose() => TestFolders.TryDelete(_root);

    private GalleryStore Store(RunStore runs, int max = 50, params string[] hidden) =>
        new(new GalleryOptions { Folder = Path.Join(_root, "gallery"), MaxEntries = max, HiddenIds = hidden.ToList() }, _root, runs);

    /// <summary>A finished systematic run with an owner; returns its id and edit key.</summary>
    internal static async Task<(Guid Id, string Key)> FinishedRunAsync(RunStore runs, string objective, string[] questions, string title = "A review")
    {
        var id = Guid.NewGuid();
        string key = runs.ClaimRun(id);
        string folder = runs.FolderOf(id);
        await RunHeader.WriteAsync(folder, new RunHeader(id, "systematic", "systematic", DateTime.UtcNow, PrismaReviewEngine.StageComplete, DateTime.UtcNow));
        var state = new ReviewState
        {
            ReviewObjective = objective,
            ResearchQuestions = questions.ToList(),
            CompletedUtc = DateTime.UtcNow,
            Stats = new ReviewStats { Included = 7, TotalIdentified = 120, ThemesIdentified = 2, CitationsChecked = 30 },
            RunSettings = new RunSettingsRecord { AppVersion = "1.4.0+abcdef1234567" },
            ThematicSynthesis = new ThematicCodebook { Themes = { new Theme { Name = "Teacher workload" }, new Theme { Name = "Feedback" } } },
        };
        File.WriteAllText(Path.Join(folder, GallerySnapshot.LedgerFile), JsonSerializer.Serialize(state));
        File.WriteAllText(Path.Join(folder, GallerySnapshot.ReportFile), JsonSerializer.Serialize(new PrismaReport { TitleItem = title }));
        File.WriteAllText(Path.Join(folder, RunStore.NotesFile), "[]");
        File.WriteAllText(Path.Join(folder, "llm-calls.json"), "[]");
        return (id, key);
    }

    [Fact]
    public async Task NothingIsAddedWithoutConsent()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var (id, key) = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });

        var result = await gallery.AddAsync(id, key, consent: false);

        Assert.False(result.Ok);
        Assert.Empty(gallery.List());
        Assert.False(Directory.Exists(gallery.Root));
    }

    [Fact]
    public async Task OnlyTheOwnerOfAFinishedRunCanAddIt()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var (id, key) = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });

        Assert.False((await gallery.AddAsync(id, null, consent: true)).Ok);
        Assert.False((await gallery.AddAsync(id, "someone-else", consent: true)).Ok);
        var active = new RunStore(Workspace, isActive: _ => true);
        Assert.False((await Store(active).AddAsync(id, key, consent: true)).Ok);

        var unfinished = Guid.NewGuid();
        string unfinishedKey = runs.ClaimRun(unfinished);
        await RunHeader.WriteAsync(runs.FolderOf(unfinished), new RunHeader(unfinished, "systematic", "systematic", DateTime.UtcNow));
        Assert.False((await gallery.AddAsync(unfinished, unfinishedKey, consent: true)).Ok);
        Assert.Empty(gallery.List());

        Assert.True((await gallery.AddAsync(id, key, consent: true)).Ok);
        Assert.Single(gallery.List());
    }

    [Fact]
    public async Task TheCopyHoldsTheReportFilesButNeverTheNotesOrTheKey()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var (id, key) = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" }, title: "AI <script>alert(1)</script> in schools");
        File.WriteAllText(Path.Join(runs.FolderOf(id), "thematic-codebook.json"), "{}");
        Directory.CreateDirectory(Path.Join(runs.FolderOf(id), "SourceResponses"));

        var entry = (await gallery.AddAsync(id, key, consent: true)).Entry!;

        Assert.True(File.Exists(Path.Join(gallery.FolderOf(entry.Id), GalleryEntry.FileName)));
        // The folder that holds the report: the entry's own, or a version's inside it.
        string copy = Path.GetDirectoryName(Directory.EnumerateFiles(gallery.FolderOf(entry.Id), GallerySnapshot.ReportFile, SearchOption.AllDirectories).Single())!;
        var files = Directory.GetFiles(copy).Select(Path.GetFileName).ToHashSet();
        Assert.Contains(GallerySnapshot.ReportFile, files);
        Assert.Contains(GallerySnapshot.LedgerFile, files);
        Assert.Contains("thematic-codebook.json", files); // a print render written from the run's files finds what main.tex uses
        Assert.Empty(Directory.GetDirectories(copy)); // no papers, no raw answers
        Assert.DoesNotContain(RunStore.NotesFile, files);
        Assert.DoesNotContain(RunStore.OwnerFile, files);
        Assert.DoesNotContain("llm-calls.json", files);
        // The copy is data, never a page: no HTML is stored, and the title stays text for the page to escape.
        Assert.DoesNotContain(files, f => f!.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("AI <script>alert(1)</script> in schools", gallery.Find(entry.Id)!.Title);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Join(gallery.FolderOf(entry.Id), GalleryEntry.FileName)));

        Assert.Equal(new[] { "Teacher workload", "Feedback" }, entry.Keywords);
        Assert.Equal(7, entry.Included);
        Assert.Equal("1.4.0+abcdef1234567", entry.ToolVersion);
        Assert.Equal(id.ToString("N")[..8], entry.ShortRunId);
        Assert.Equal(entry.Id, gallery.ForRun(id)?.Id);
    }

    [Fact]
    public async Task TheSameReviewKeepsItsEntryAndAnotherReviewAddsOne()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var first = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" }, title: "First");
        var again = await FinishedRunAsync(runs, "  ai IN schools!", new[] { "how is AI used" }, title: "Second");
        var other = await FinishedRunAsync(runs, "AI in hospitals", new[] { "How is AI used?" }, title: "Other");

        var added = (await gallery.AddAsync(first.Id, first.Key, consent: true)).Entry!;
        var replaced = await gallery.AddAsync(again.Id, again.Key, consent: true);

        Assert.True(replaced.Updated);
        Assert.Equal(added.Id, replaced.Entry!.Id);
        Assert.Equal(added.AddedUtc, replaced.Entry.AddedUtc);
        Assert.NotNull(replaced.Entry.UpdatedUtc);
        var shown = Assert.Single(gallery.List());
        Assert.Equal("Second", shown.Title);
        Assert.Equal(again.Id, shown.RunId);

        Assert.False((await gallery.AddAsync(other.Id, other.Key, consent: true)).Updated);
        Assert.Equal(2, gallery.List().Count);
    }

    [Fact]
    public async Task TheEntryRecordsWhenTheOwnerSaidYesEachTime()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var first = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });
        var again = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });

        var before = DateTime.UtcNow;
        var added = (await gallery.AddAsync(first.Id, first.Key, consent: true)).Entry!;
        Assert.NotNull(added.ConsentUtc);
        Assert.InRange(added.ConsentUtc!.Value, before, DateTime.UtcNow);
        Assert.Equal(added.ConsentUtc, Assert.Single(gallery.List()).ConsentUtc); // read back from entry.json

        var replaced = (await gallery.AddAsync(again.Id, again.Key, consent: true)).Entry!;
        Assert.True(replaced.ConsentUtc >= added.ConsentUtc);
        Assert.Equal(replaced.UpdatedUtc, replaced.ConsentUtc);
        Assert.Equal(added.AddedUtc, replaced.AddedUtc);
    }

    [Fact]
    public async Task AMultivocalRunIsNotOfferedYet()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var (id, key) = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });
        await RunHeader.WriteAsync(runs.FolderOf(id), new RunHeader(id, "multivocal", "grey", DateTime.UtcNow, "Checked", DateTime.UtcNow));

        Assert.False((await gallery.AddAsync(id, key, consent: true)).Ok);
        Assert.Empty(GallerySnapshot.FilesFor("multivocal"));
        Assert.DoesNotContain("llm-calls.json", GallerySnapshot.SystematicFiles);
        Assert.DoesNotContain(RunStore.NotesFile, GallerySnapshot.SystematicFiles);
    }

    [Fact]
    public void TheFingerprintFoldsCaseAndPunctuationButNotWords()
    {
        string a = GalleryFingerprint.Of("systematic", "AI in Schools?", new[] { "How is AI used?" });
        Assert.Equal(a, GalleryFingerprint.Of("SYSTEMATIC", "ai   in schools", new[] { "how, is AI used" }));
        Assert.NotEqual(a, GalleryFingerprint.Of("grey", "AI in Schools?", new[] { "How is AI used?" }));
        Assert.NotEqual(a, GalleryFingerprint.Of("systematic", "AI in Schools?", new[] { "How is AI taught?" }));
    }

    [Fact]
    public async Task TheGalleryKeepsAtMostItsCapAndDropsTheOldest()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs, max: 2);
        var titles = new[] { "One", "Two", "Three" };
        foreach (var t in titles)
        {
            var run = await FinishedRunAsync(runs, "Topic " + t, Array.Empty<string>(), title: t);
            Assert.True((await gallery.AddAsync(run.Id, run.Key, consent: true)).Ok);
            await Task.Delay(20); // distinct times, so "oldest" is well defined
        }

        Assert.Equal(new[] { "Three", "Two" }, gallery.List().Select(e => e.Title));
    }

    [Fact]
    public async Task OnlyTheEditKeyWithdrawsAnEntry()
    {
        var runs = new RunStore(Workspace);
        var gallery = Store(runs);
        var (id, key) = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });
        var entry = (await gallery.AddAsync(id, key, consent: true)).Entry!;

        Assert.False(await gallery.WithdrawAsync(entry.Id, "not-the-key"));
        Assert.False(await gallery.WithdrawAsync("../" + entry.Id, key));
        Assert.True(await gallery.WithdrawAsync(entry.Id, key));
        Assert.Empty(gallery.List());
        Assert.False(Directory.Exists(Path.Join(gallery.Root, entry.Id)));
    }

    [Fact]
    public async Task AHiddenEntryIsNeitherListedNorOpenedNorAddedAgain()
    {
        var runs = new RunStore(Workspace);
        var (id, key) = await FinishedRunAsync(runs, "AI in schools", new[] { "How is AI used?" });
        var entry = (await Store(runs).AddAsync(id, key, consent: true)).Entry!;

        var hiding = Store(runs, 50, entry.Id.ToUpperInvariant());
        Assert.Empty(hiding.List());
        Assert.Null(hiding.Find(entry.Id));
        Assert.Null(hiding.ForRun(id));
        Assert.False((await hiding.AddAsync(id, key, consent: true)).Ok);
    }

    [Fact]
    public void ABadlyFormedIdIsNeverAFolder()
    {
        var gallery = Store(new RunStore(Workspace));
        Assert.Null(gallery.Find("../../etc"));
        Assert.Null(gallery.Find("ABCDEF123456"));
        Assert.Throws<ArgumentException>(() => gallery.FolderOf(".."));
    }

    [Fact]
    public void AddingToTheGalleryIsLimitedToFiveAnHourPerAddress()
    {
        var limiter = new ActionLimiter();
        Assert.Equal(5, limiter.PerHour(ActionLimiter.GallerySubmit));
        for (int i = 0; i < 5; i++) Assert.True(limiter.TryTake(ActionLimiter.GallerySubmit, "203.0.113.9", out _));
        Assert.False(limiter.TryTake(ActionLimiter.GallerySubmit, "203.0.113.9", out var refusal));
        Assert.Contains("network address", refusal);
    }
}
