using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A whole multivocal review with both pools, through the runner with the fake model (<see cref="FormalKit"/>): each step
/// of block I is checked here on a complete run, and the run stops, with its reason, at the first step not built yet.
/// </summary>
public class MultivocalFormalRunTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrformalrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public async Task TheSearchCoversBothPoolsAndKeepsItsRawAnswersInTheArchive()
    {
        var run = await FormalKit.RunAsync(_root);

        var ledger = run.Searcher.LoadLedger(run.RunId)!;
        Assert.Equal(new[] { "context engineering AND agents" }, run.Academic.Queries);
        Assert.Single(ledger.FormalPool);
        Assert.Equal(2, ledger.GreyPool.Count());
        Assert.Contains(ledger.Skipped, s => s.Source == "scopus");

        var zip = RunArchive.Build(run.RunId, run.Store.FolderOf(run.RunId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.Contains(archive.Entries, e => e.FullName == $"{RunArchive.RawResponsesFolder}/formal-openalex-1.json");
        var reread = JsonSerializer.Deserialize<MultivocalLedger>(File.ReadAllText(Path.Join(run.Store.FolderOf(run.RunId), MultivocalSearcher.LedgerFile)))!;
        Assert.Equal("10.1234/agents.1", reread.FormalPool.Single().Paper!.Doi);
    }

    [Fact]
    public async Task TheRunStopsWithItsReasonAtTheFirstStepNotBuilt()
    {
        var run = await FormalKit.RunAsync(_root);

        Assert.Equal("Screening stopped: " + MultivocalScreener.FormalNotBuiltMessage, run.Runner.StatusOf(run.RunId)!.Error);
        Assert.Equal(MultivocalSearcher.StageSearched, run.Store.LoadHeader(run.RunId)!.Stage);
        Assert.Empty(run.Model.Prompts);
    }
}
