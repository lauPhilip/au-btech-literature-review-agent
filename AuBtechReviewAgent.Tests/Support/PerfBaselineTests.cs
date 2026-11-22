using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Measurements for issue #306, taken before any change. This test fails on purpose so CI prints the figures;
/// it lives on a measurement branch only and is never merged.
/// </summary>
public class PerfBaselineTests
{
    private static (double Ms, long Kb) Time(Action action, int times)
    {
        action();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < times; i++) action();
        watch.Stop();
        return (watch.Elapsed.TotalMilliseconds / times, (GC.GetAllocatedBytesForCurrentThread() - before) / times / 1024);
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Join(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Join(to, Path.GetFileName(dir)));
    }

    [Fact]
    public async Task Figures()
    {
        var kit = await ModuleKit.RunAsync("systematic");
        string root = Path.Join(Path.GetTempPath(), "perf-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.ParseExact(Path.GetFileName(kit.Folder), "N");
        string folder = Path.Join(root, runId.ToString("N"));
        Copy(kit.Folder, folder);
        var sb = new StringBuilder("FIGURES\n");
        try
        {
            var engine = new PrismaReviewEngine("k", "k", "k", "k", null, new RunsOptions()) { WorkspaceRoot = root };

            // Page loads, on the kit's run: what Review output reads and parses.
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            string reportPath = Path.Join(folder, "prisma-report.json"), auditPath = Path.Join(folder, "citation-audit.json");
            var load = Time(() =>
            {
                JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(reportPath), opts);
                if (File.Exists(auditPath)) using (JsonDocument.Parse(File.ReadAllText(auditPath))) { }
                engine.LoadState(runId);
                engine.Store.LoadHeader(runId);
            }, 50);
            var state = engine.LoadState(runId)!;
            long ledger = new FileInfo(Path.Join(folder, "transparent-process.json")).Length;
            long persisted = JsonSerializer.Serialize(new
            {
                Report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(reportPath), opts),
                state.SynthesizedRecords, state.Extractions, state.ThematicSynthesis, state.Stats, state.ResearchQuestions,
            }).Length;
            sb.AppendLine($"kit page: ledger {ledger} B, report {new FileInfo(reportPath).Length} B, audit {(File.Exists(auditPath) ? new FileInfo(auditPath).Length : 0)} B; read+parse {load.Ms:F2} ms, {load.Kb} KB; state to persist {persisted} B");

            string sampleRoot = Path.Join(root, "sample");
            string sampleFolder = Path.Join(sampleRoot, SampleReport.RunId.ToString("N"));
            SampleReport.Write(sampleFolder);
            var sampleEngine = new PrismaReviewEngine("k", "k", "k", "k", null, new RunsOptions()) { WorkspaceRoot = sampleRoot };
            string sReport = Path.Join(sampleFolder, "prisma-report.json"), sAudit = Path.Join(sampleFolder, "citation-audit.json");
            var sload = Time(() =>
            {
                JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(sReport), opts);
                using (JsonDocument.Parse(File.ReadAllText(sAudit))) { }
                sampleEngine.LoadState(SampleReport.RunId);
            }, 50);
            long sampleBytes = Directory.GetFiles(sampleFolder).Sum(f => new FileInfo(f).Length);
            sb.AppendLine($"sample page: files {sampleBytes} B; read+parse {sload.Ms:F2} ms, {sload.Kb} KB");

            // Downloads, on the kit's run.
            foreach (var format in new[] { "bib", "ris", "screened" })
            {
                string text = engine.ExportReferences(runId, format)!;
                var t = Time(() => engine.ExportReferences(runId, format), 50);
                sb.AppendLine($"references {format}: {Encoding.UTF8.GetByteCount(text)} B, {t.Ms:F2} ms, {t.Kb} KB");
            }
            sb.AppendLine($"protocol.md: {Encoding.UTF8.GetByteCount(engine.ReadProtocol(runId) ?? "")} B");

            // The archive, with what a real run keeps: 25 source PDFs of 400 KB and 25 raw answers of about 60 KB.
            var random = new Random(1);
            for (int i = 0; i < 25; i++)
            {
                var pdf = new byte[400_000];
                random.NextBytes(pdf);
                File.WriteAllBytes(Path.Join(folder, $"paper-{i}.pdf"), pdf);
                File.WriteAllText(Path.Join(folder, RunArchive.RawResponsesFolder, $"openalex-{i}.json"),
                    "[" + string.Join(",", Enumerable.Range(0, 300).Select(n => $"{{\"id\":\"W{i}{n}\",\"title\":\"Record {n} about agents\",\"year\":{2020 + n % 6}}}")) + "]");
            }
            byte[] zip = engine.GenerateWorkspaceArchiveFromDisk(runId);
            var build = Time(() => engine.GenerateWorkspaceArchiveFromDisk(runId), 5);
            long folderBytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            int fileCount = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
            sb.AppendLine($"archive: folder {fileCount} files {folderBytes} B, zip {zip.Length} B; build {build.Ms:F1} ms, {build.Kb} KB");
            string kept = Path.Join(root, "kept.zip");
            File.WriteAllBytes(kept, zip);
            var read = Time(() => File.ReadAllBytes(kept), 20);
            var stamp = Time(() =>
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)) { _ = f.Length; _ = f.LastWriteTimeUtc; }
            }, 50);
            sb.AppendLine($"kept zip read {read.Ms:F2} ms, {read.Kb} KB; folder stamp {stamp.Ms:F3} ms, {stamp.Kb} KB");

            // Metrics: the store read with 50 and 500 runs.
            var samples = SampleMetrics.Runs();
            foreach (int count in new[] { 50, 500 })
            {
                var store = new RunMetricsStore(new MetricsOptions { Folder = Path.Join(root, "metrics" + count) }, root);
                for (int i = 0; i < count; i++) store.Append(samples[i % samples.Count]);
                var t = Time(() => store.ReadAll(), 20);
                sb.AppendLine($"metrics {count} runs: file {new FileInfo(store.FilePath).Length} B, ReadAll {t.Ms:F2} ms, {t.Kb} KB");
            }
        }
        finally { TestFolders.TryDelete(root); }
        Assert.Fail(sb.ToString());
    }
}
