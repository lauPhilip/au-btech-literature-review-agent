using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Audit C4: the archive is written one file at a time, never whole in memory. It must hold exactly what the old
/// in-memory builder made: the same entries in the same order, the same bytes and the same manifest. The zip format
/// stores when each entry was written, and a file written in pieces may deflate to a few bytes more or less than one
/// written whole (it did on CI's zlib), so the zips cannot match byte for byte; what they unpack to is compared.
/// </summary>
public class RunArchiveStreamTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "archive-stream-" + Guid.NewGuid().ToString("N"));

    public RunArchiveStreamTests()
    {
        Directory.CreateDirectory(Path.Join(_folder, RunArchive.RawResponsesFolder));
        foreach (var name in new[] { "run.json", "protocol.md", "llm-calls.json", "module-ledger.json", "owner.json" })
            File.WriteAllText(Path.Join(_folder, name), "{\"name\":\"" + name + "\"}");
        var random = new Random(7);
        var pdf = new byte[3 * 1024 * 1024 + 17];
        random.NextBytes(pdf);
        File.WriteAllBytes(Path.Join(_folder, "paper-1.pdf"), pdf);
        File.WriteAllText(Path.Join(_folder, "page-2.html"), string.Concat(Enumerable.Repeat("<p>source text</p>", 20000)));
        File.WriteAllText(Path.Join(_folder, "empty.txt"), "");
        File.WriteAllText(Path.Join(_folder, RunArchive.RawResponsesFolder, "search-1.json"), "[]");
    }

    public void Dispose() => TestFolders.TryDelete(_folder);

    /// <summary>The builder as it was before audit C4: every file read whole, the manifest made from the bytes.</summary>
    private static byte[] OldBuild(Guid runId, string runFolder, IReadOnlyList<(string Path, byte[] Content)> generated,
        IReadOnlyList<string> moduleFiles, string? protocolHash, string? model)
    {
        var entries = new List<(string Path, byte[] Content)>(generated);
        var rootFiles = moduleFiles.Concat(RunArchive.CoreFiles.Where(c => !moduleFiles.Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (var name in rootFiles)
            if (File.Exists(Path.Join(runFolder, name))) entries.Add((name, File.ReadAllBytes(Path.Join(runFolder, name))));
        foreach (var file in Directory.GetFiles(runFolder).OrderBy(f => f, StringComparer.Ordinal))
        {
            string filename = Path.GetFileName(file);
            if (rootFiles.Contains(filename, StringComparer.OrdinalIgnoreCase) || filename.EndsWith(".json") || filename.EndsWith(".tmp")) continue;
            entries.Add(($"{RunArchive.SourceTextsFolder}/{filename}", File.ReadAllBytes(file)));
        }
        foreach (var file in Directory.GetFiles(Path.Join(runFolder, RunArchive.RawResponsesFolder)).OrderBy(f => f, StringComparer.Ordinal))
            entries.Add(($"{RunArchive.RawResponsesFolder}/{Path.GetFileName(file)}", File.ReadAllBytes(file)));
        entries.Add(("manifest.json", new UTF8Encoding(false).GetBytes(RunManifest.Build(runId, protocolHash, model, entries))));
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                target.Write(content, 0, content.Length);
            }
        return memoryStream.ToArray();
    }

    private static List<(string Name, uint Crc, byte[] Content)> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        return archive.Entries.Select(e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return (e.FullName, e.Crc32, m.ToArray());
        }).ToList();
    }

    // CreatedUtc is the time the manifest was made, and ManifestSha256 covers it; everything else must match.
    private static string WithoutTime(byte[] manifest)
    {
        var node = JsonNode.Parse(manifest)!.AsObject();
        node.Remove("CreatedUtc");
        node.Remove("ManifestSha256");
        return node.ToJsonString();
    }

    private static void AssertSameArchive(byte[] before, byte[] after)
    {
        var old = Entries(before);
        var now = Entries(after);
        Assert.Equal(old.Select(e => e.Name), now.Select(e => e.Name));
        foreach (var (o, n) in old.Zip(now).Where(p => p.First.Name != "manifest.json"))
        {
            Assert.Equal(o.Content, n.Content);
            Assert.Equal(o.Crc, n.Crc);
        }
        Assert.Equal(WithoutTime(old.Single(e => e.Name == "manifest.json").Content), WithoutTime(now.Single(e => e.Name == "manifest.json").Content));
        Assert.True(RunManifest.CheckArchive(new MemoryStream(after)).Unchanged);
    }

    [Fact]
    public void TheStreamedArchiveHoldsExactlyWhatTheOldBuilderMade()
    {
        var runId = Guid.NewGuid();
        var generated = new List<(string, byte[])> { ("main.tex", Encoding.UTF8.GetBytes("\\documentclass{article}")), ("references.bib", Array.Empty<byte>()) };
        var module = new[] { "module-ledger.json", "protocol.md" };

        byte[] before = OldBuild(runId, _folder, generated, module, "abc", "model");
        using var file = new MemoryStream();
        RunArchive.Write(file, runId, _folder, generated, module, "abc", "model");

        AssertSameArchive(before, file.ToArray());
        Assert.Equal("main.tex", Entries(file.ToArray())[0].Name);
    }

    [Fact]
    public async Task ASystematicRunsArchiveIsUnchangedAndItsMainTexCanBeBuiltAlone()
    {
        var kit = await ModuleKit.RunAsync("systematic");
        var runId = Guid.ParseExact(Path.GetFileName(kit.Folder), "N");
        var engine = new PrismaReviewEngine("k", "k", "k", "k", null, new RunsOptions()) { WorkspaceRoot = Path.GetDirectoryName(kit.Folder)! };
        var state = engine.LoadState(runId)!;

        using var file = new MemoryStream();
        Assert.True(engine.WriteWorkspaceArchive(runId, file));
        var entries = Entries(file.ToArray());
        string mainTex = Encoding.UTF8.GetString(entries[0].Content);

        Assert.Equal("main.tex", entries[0].Name);
        Assert.Equal(mainTex, engine.MainTexFromDisk(runId));
        var generated = entries.TakeWhile(e => e.Name is "main.tex" or "references.bib" or "references.ris" or "screened.ris")
            .Select(e => (e.Name, e.Content)).ToList();
        AssertSameArchive(OldBuild(runId, kit.Folder, generated, ReviewModules.Find("systematic")!.ArchiveFiles, state.ProtocolHash, state.RunSettings?.Model), file.ToArray());
        Assert.False(engine.WriteWorkspaceArchive(Guid.NewGuid(), file));
        Assert.Null(engine.MainTexFromDisk(Guid.NewGuid()));
    }

    [Fact]
    public void CheckingAnArchiveFindsAChangedLargeFile()
    {
        using var file = new MemoryStream();
        RunArchive.Write(file, Guid.NewGuid(), _folder, Array.Empty<(string, byte[])>(), Array.Empty<string>(), null, null);
        var files = Entries(file.ToArray()).ToDictionary(e => e.Name, e => e.Content);
        files["SourcePapers/paper-1.pdf"][2_000_000] ^= 1;

        using var changed = new MemoryStream();
        using (var archive = new ZipArchive(changed, ZipArchiveMode.Create, true))
            foreach (var (name, content) in files)
            {
                using var target = archive.CreateEntry(name).Open();
                target.Write(content);
            }
        changed.Position = 0;
        var check = RunManifest.CheckArchive(changed);

        Assert.Equal(new[] { "SourcePapers/paper-1.pdf" }, check.Changed);
        Assert.Contains(check.Files!, f => f.Path == "SourcePapers/empty.txt" && f.Status == "Matches");
    }
}
