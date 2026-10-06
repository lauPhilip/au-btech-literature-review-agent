using System.IO.Compression;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The core archive builder, shared by every kind of review.</summary>
public class RunArchiveTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "archive-" + Guid.NewGuid().ToString("N"));

    public RunArchiveTests()
    {
        Directory.CreateDirectory(Path.Join(_folder, RunArchive.RawResponsesFolder));
        foreach (var name in new[] { "run.json", "protocol.md", "llm-calls.json", "reviewer-notes.json", "module-ledger.json", "owner.json" })
            File.WriteAllText(Path.Join(_folder, name), "{}");
        File.WriteAllText(Path.Join(_folder, "vendor-page.html"), "<p>source text</p>");
        File.WriteAllText(Path.Join(_folder, "half-written.tmp"), "x");
        File.WriteAllText(Path.Join(_folder, RunArchive.RawResponsesFolder, "search-1.json"), "[]");
    }

    public void Dispose() => TestFolders.TryDelete(_folder);

    private static Dictionary<string, byte[]> Unzip(byte[] zip)
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

    private static List<string> Names(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public void AModuleThatListsNothingStillGetsTheCoreFilesAndNeverTheEditKey()
    {
        var generated = new List<(string, byte[])> { ("report.md", Encoding.UTF8.GetBytes("# Report")) };

        var names = Names(RunArchive.Build(Guid.NewGuid(), _folder, generated, Array.Empty<string>(), "abc", "model"));

        Assert.Equal(new[]
        {
            "report.md", "run.json", "protocol.md", "llm-calls.json", "reviewer-notes.json",
            "SourcePapers/vendor-page.html", "SourceResponses/search-1.json", "manifest.json",
        }, names);
    }

    [Fact]
    public void TheModulesFilesComeFirstInItsOrder()
    {
        var names = Names(RunArchive.Build(Guid.NewGuid(), _folder, Array.Empty<(string, byte[])>(), new[] { "module-ledger.json", "protocol.md" }, null, null));

        Assert.Equal(new[] { "module-ledger.json", "protocol.md", "run.json", "llm-calls.json", "reviewer-notes.json" }, names.Take(5));
        Assert.DoesNotContain("owner.json", names);
    }

    [Fact]
    public void TheManifestFingerprintsEveryFile()
    {
        var files = Unzip(RunArchive.Build(Guid.NewGuid(), _folder, Array.Empty<(string, byte[])>(), new[] { "module-ledger.json" }, "abc", "model"));

        string manifest = Encoding.UTF8.GetString(files["manifest.json"]);
        Assert.Empty(RunManifest.Verify(manifest, files.Where(f => f.Key != "manifest.json").ToDictionary(f => f.Key, f => f.Value)));
        Assert.True(RunManifest.ManifestIsIntact(manifest));
        Assert.Contains("\"ProtocolHash\": \"abc\"", manifest);
    }

    [Fact]
    public void TheSystematicReviewListsEveryCoreFileSoItsArchiveKeepsItsOrder() =>
        Assert.All(RunArchive.CoreFiles, core => Assert.Contains(core, ReviewModules.Find("systematic")!.ArchiveFiles));
}
