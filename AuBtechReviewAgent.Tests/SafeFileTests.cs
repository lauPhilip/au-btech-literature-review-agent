using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Saving the ledger survives a reader that briefly holds the file (antivirus, indexer).</summary>
public class SafeFileTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "safefile-" + Guid.NewGuid().ToString("N"));
    public SafeFileTests() => Directory.CreateDirectory(_root);
    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public async Task WritesAndReplacesWithoutLeavingTemporaryFiles()
    {
        string path = Path.Join(_root, "ledger.json");
        await SafeFile.WriteAllTextAsync(path, "one");
        await SafeFile.WriteAllTextAsync(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AReaderHoldingTheFileDoesNotStopTheSave()
    {
        string path = Path.Join(_root, "ledger.json");
        await SafeFile.WriteAllTextAsync(path, "one");
        // A reader that does not allow deletion: on Windows this makes replacing the file fail until it lets go.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(async () => { await Task.Delay(150); reader.Dispose(); });

        await SafeFile.WriteAllTextAsync(path, "two");
        await release;

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }
}
