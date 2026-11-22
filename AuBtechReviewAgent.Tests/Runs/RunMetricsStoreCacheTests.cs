using System.Linq;
using System.Threading.Tasks;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #306: the metrics store parses runs.jsonl once and keeps the runs while the file's length and write time
/// stay the same. A new run, or any other change to the file, is read at once.
/// </summary>
public class RunMetricsStoreCacheTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "metrics-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private RunMetricsStore Store() => new(new MetricsOptions { Folder = _root }, _root);

    private static RunMetrics Run(string id, int day, bool grey = false) => new()
    {
        RunId = id,
        CompletedUtc = $"2026-10-{day:00}T09:00:00Z",
        Settings = grey ? new Dictionary<string, string> { ["Module"] = "multivocal" } : new(),
    };

    [Fact]
    public void A_second_read_of_an_unchanged_file_gives_the_same_runs_without_parsing_again()
    {
        var store = Store();
        store.Append(Run("b", 2));
        store.Append(Run("a", 1));

        var first = store.ReadAll();
        var second = store.ReadAll();

        Assert.Equal(new[] { "a", "b" }, first.Select(r => r.RunId));
        Assert.Equal(first.Select(r => r.RunId), second.Select(r => r.RunId));
        // The same objects: the lines were not parsed again.
        Assert.All(first.Zip(second), pair => Assert.Same(pair.First, pair.Second));
        // Each call gets a list of its own.
        Assert.NotSame(first, second);
        first.Clear();
        Assert.Equal(2, store.ReadAll().Count);
    }

    [Fact]
    public void A_new_run_shows_at_once()
    {
        var store = Store();
        store.Append(Run("a", 1));
        Assert.Single(store.ReadAll());

        store.Append(Run("b", 2));
        Assert.Equal(new[] { "a", "b" }, store.ReadAll().Select(r => r.RunId));
    }

    [Fact]
    public void A_file_changed_from_outside_with_the_same_length_is_read_again()
    {
        var store = Store();
        store.Append(Run("a", 1));
        var before = store.ReadAll().Single();

        string text = File.ReadAllText(store.FilePath).Replace("\"RunId\":\"a\"", "\"RunId\":\"z\"");
        var written = File.GetLastWriteTimeUtc(store.FilePath);
        File.WriteAllText(store.FilePath, text);
        File.SetLastWriteTimeUtc(store.FilePath, written.AddSeconds(5));

        var after = store.ReadAll().Single();
        Assert.Equal("z", after.RunId);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void The_two_kinds_stay_apart_and_a_damaged_line_is_still_skipped()
    {
        var store = Store();
        store.Append(Run("s", 1));
        store.Append(Run("g", 2, grey: true));
        File.AppendAllText(store.FilePath, "{ not json\n");

        Assert.Equal(new[] { "s" }, store.ReadAll().Select(r => r.RunId));
        Assert.Equal(new[] { "g" }, store.ReadAll(multivocal: true).Select(r => r.RunId));
    }

    [Fact]
    public void No_file_and_a_switched_off_store_give_no_runs()
    {
        Assert.Empty(Store().ReadAll());
        var off = new RunMetricsStore(new MetricsOptions { Enabled = false, Folder = _root }, _root);
        Assert.Empty(off.ReadAll());
    }

    [Fact]
    public void Reads_and_appends_at_the_same_time_lose_nothing()
    {
        var store = Store();
        Parallel.For(0, 40, i =>
        {
            store.Append(Run("r" + i, 1 + i % 28));
            Assert.NotNull(store.ReadAll());
        });

        Assert.Equal(40, store.ReadAll().Count);
        Assert.Equal(40, store.ReadAll().Select(r => r.RunId).Distinct().Count());
    }
}
