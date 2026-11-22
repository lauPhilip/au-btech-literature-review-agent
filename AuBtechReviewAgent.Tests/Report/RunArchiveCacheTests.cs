using System.IO.Compression;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #306: a finished run's archive is kept in its folder. The kept zip is byte for byte what the builder made,
/// a change to the run builds a fresh one, and the kept file is never part of an archive or of the run's age.
/// </summary>
public class RunArchiveCacheTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "archive-cache-" + Guid.NewGuid().ToString("N"));
    private int _builds;

    public void Dispose() => TestFolders.TryDelete(_root);

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Join(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Join(to, Path.GetFileName(dir)));
    }

    /// <summary>A copy of the kit's finished systematic run, with an engine that builds its archive.</summary>
    private async Task<(PrismaReviewEngine Engine, Guid RunId, string Folder)> FinishedRunAsync()
    {
        var kit = await ModuleKit.RunAsync("systematic");
        var runId = Guid.ParseExact(Path.GetFileName(kit.Folder), "N");
        var engine = new PrismaReviewEngine("k", "k", "k", "k", null, new RunsOptions()) { WorkspaceRoot = _root };
        Copy(kit.Folder, engine.Store.FolderOf(runId));
        return (engine, runId, engine.Store.FolderOf(runId));
    }

    private byte[] Download(PrismaReviewEngine engine, Guid runId, string folder, bool finished = true)
    {
        using var zip = RunArchiveCache.GetOrBuild(folder, RunFolderStamp.Of(folder)!, finished, () =>
        {
            _builds++;
            return engine.GenerateWorkspaceArchiveFromDisk(runId);
        });
        using var copy = new MemoryStream();
        zip.CopyTo(copy);
        return copy.ToArray();
    }

    private static string[] Kept(string folder) =>
        Directory.GetFiles(folder).Select(Path.GetFileName).Where(f => RunArchiveCache.IsCacheFile(f!)).Select(f => f!).ToArray();

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        });
    }

    [Fact]
    public async Task TheKeptZipIsByteForByteWhatTheBuilderMadeAndIsServedAgain()
    {
        var (engine, runId, folder) = await FinishedRunAsync();

        byte[] first = Download(engine, runId, folder);
        string kept = Assert.Single(Kept(folder));
        Assert.Equal(first, File.ReadAllBytes(Path.Join(folder, kept)));

        byte[] second = Download(engine, runId, folder);
        Assert.Equal(first, second);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task TheKeptZipHasTheSameFilesAsAFreshBuildAndLeavesItselfOut()
    {
        var (engine, runId, folder) = await FinishedRunAsync();
        var kept = Entries(Download(engine, runId, folder));

        var fresh = Entries(engine.GenerateWorkspaceArchiveFromDisk(runId));

        Assert.Equal(fresh.Keys, kept.Keys);
        Assert.DoesNotContain(fresh.Keys, k => k.Contains(RunArchiveCache.Prefix, StringComparison.Ordinal));
        foreach (var name in fresh.Keys.Where(k => k != "manifest.json")) Assert.Equal(fresh[name], kept[name]);
        // The manifest differs only in when it was made: every file's fingerprint is the same.
        string Files(byte[] manifest) => JsonDocument.Parse(manifest).RootElement.GetProperty("Files").GetRawText();
        Assert.Equal(Files(fresh["manifest.json"]), Files(kept["manifest.json"]));
        Assert.True(RunManifest.CheckArchive(new MemoryStream(File.ReadAllBytes(Path.Join(folder, Kept(folder)[0])))).Unchanged);
    }

    [Fact]
    public async Task ANoteBuildsAFreshArchiveAndReplacesTheKeptOne()
    {
        var (engine, runId, folder) = await FinishedRunAsync();
        Download(engine, runId, folder);
        string before = Kept(folder)[0];

        File.WriteAllText(Path.Join(folder, RunStore.NotesFile), "[{\"Text\":\"Checked by hand\"}]");
        var entries = Entries(Download(engine, runId, folder));

        Assert.Equal(2, _builds);
        Assert.Contains("Checked by hand", System.Text.Encoding.UTF8.GetString(entries[RunStore.NotesFile]));
        Assert.NotEqual(before, Assert.Single(Kept(folder)));
    }

    [Fact]
    public async Task ARunThatIsNotFinishedIsNeverKept()
    {
        var (engine, runId, folder) = await FinishedRunAsync();

        Download(engine, runId, folder, finished: false);
        Download(engine, runId, folder, finished: false);

        Assert.Empty(Kept(folder));
        Assert.Equal(2, _builds);
    }

    [Fact]
    public void OnlyAFinishedRunThatIsNotRunningMayBeKept()
    {
        var run = new RunHeader(Guid.NewGuid(), "systematic", "systematic", DateTime.UtcNow, PrismaReviewEngine.StageComplete);
        Assert.True(RunArchiveCache.IsFinished(run, active: false));
        Assert.False(RunArchiveCache.IsFinished(run, active: true));
        Assert.False(RunArchiveCache.IsFinished(run with { Stage = "Screening" }, active: false));
        Assert.False(RunArchiveCache.IsFinished(null, active: false));

        var grey = new RunHeader(Guid.NewGuid(), "multivocal", "grey", DateTime.UtcNow, MultivocalSynthesiser.StageSynthesised);
        Assert.False(RunArchiveCache.IsFinished(grey, active: false));
        Assert.True(RunArchiveCache.IsFinished(grey with { Stage = MultivocalWriter.StageChecked }, active: false));
    }

    [Fact]
    public async Task TheKeptZipDoesNotMakeTheRunLookActive()
    {
        var (engine, runId, folder) = await FinishedRunAsync();
        var old = DateTime.UtcNow.AddDays(-40);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, old);
        Directory.SetLastWriteTimeUtc(folder, old);

        Download(engine, runId, folder);

        Assert.True(SessionCleanupWorker.LastActivityUtc(folder) < DateTime.UtcNow.AddDays(-30));
    }
}
