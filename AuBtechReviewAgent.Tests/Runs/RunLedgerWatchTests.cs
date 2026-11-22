using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #306: the dashboard looks at a running review's ledger every second. It is parsed only when it changed;
/// an unchanged ledger gives back the last read, and a changed one is read at once.
/// </summary>
public class RunLedgerWatchTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "ledger-watch-" + Guid.NewGuid().ToString("N"));
    private string Ledger => Path.Join(_root, "transparent-process.json");

    public RunLedgerWatchTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFolders.TryDelete(_root);

    private void Save(int screened, DateTime? written = null)
    {
        var state = new ReviewState { Stats = new ReviewStats { Screened = screened, ProcessingStage = "Screening" } };
        // The engine writes a new file and moves it into place, so the write time changes with every save.
        string part = Ledger + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(part, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(part, Ledger, overwrite: true);
        if (written is { } at) File.SetLastWriteTimeUtc(Ledger, at);
    }

    [Fact]
    public void An_unchanged_ledger_is_parsed_once()
    {
        Save(3);
        var watch = new RunLedgerWatch(Ledger);

        var first = watch.Read();
        var second = watch.Read();
        var third = watch.Read();

        Assert.NotNull(first);
        Assert.Equal(3, first.Value.State.Stats.Screened);
        Assert.Same(first.Value.State, second!.Value.State);
        Assert.Same(first.Value.Raw, third!.Value.Raw);
        Assert.Equal(File.ReadAllText(Ledger), first.Value.Raw);
        Assert.Equal(1, watch.Parses);
    }

    [Fact]
    public void A_saved_ledger_is_read_at_once()
    {
        var start = DateTime.UtcNow.AddMinutes(-5);
        Save(3, start);
        var watch = new RunLedgerWatch(Ledger);
        Assert.Equal(3, watch.Read()!.Value.State.Stats.Screened);

        Save(4, start.AddSeconds(1)); // the same length, a later write time
        Assert.Equal(4, watch.Read()!.Value.State.Stats.Screened);

        Save(12, start.AddSeconds(1)); // a longer file in the same second
        Assert.Equal(12, watch.Read()!.Value.State.Stats.Screened);
        Assert.Equal(3, watch.Parses);
    }

    [Fact]
    public void A_missing_or_unreadable_ledger_gives_nothing_and_is_tried_again()
    {
        var watch = new RunLedgerWatch(Ledger);
        Assert.Null(watch.Read());

        File.WriteAllText(Ledger, "{ half a ledger");
        Assert.Null(watch.Read());

        Save(7);
        Assert.Equal(7, watch.Read()!.Value.State.Stats.Screened);
    }

    [Fact]
    public void The_dashboard_follows_a_run_through_the_watch()
    {
        string? page = PageSource.Read("Home");
        if (page == null) return; // not run from the repository

        Assert.Contains("new AuBtechReviewAgent.RunLedgerWatch(StatePath(runId))", page);
        Assert.Contains("ledger.Read()", page);
    }
}
