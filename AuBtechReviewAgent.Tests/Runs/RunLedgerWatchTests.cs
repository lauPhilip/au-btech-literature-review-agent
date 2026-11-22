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

    private void Save(int screened, DateTime? written = null, string stage = "Screening")
    {
        var state = new ReviewState { Stats = new ReviewStats { Screened = screened, ProcessingStage = stage } };
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

        var first = Assert.NotNull(watch.Read());
        var second = Assert.NotNull(watch.Read());
        var third = Assert.NotNull(watch.Read());

        Assert.Equal(3, first.State.Stats.Screened);
        Assert.Same(first.State, second.State);
        Assert.Same(first.Raw, third.Raw);
        Assert.Equal(File.ReadAllText(Ledger), first.Raw);
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

        // A longer file with the same write time. The state's timestamps drop trailing zeros when written, so its length
        // varies by a few characters from run to run; a stage name 26 characters longer keeps it longer every time.
        Save(12, start.AddSeconds(1), "Screening the records a second time");
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
