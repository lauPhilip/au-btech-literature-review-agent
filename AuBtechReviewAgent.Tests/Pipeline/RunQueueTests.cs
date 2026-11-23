using System.Diagnostics;
using AuBtechReviewAgent;
using Xunit;
using Xunit.Abstractions;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The one fair line every review shares (RunCoordinator.Queue.cs): a client cannot hold more than its share of slots
/// or of places, a full line refuses new runs politely, and a waiting run sees its place and a rough wait.
/// </summary>
public class RunQueueTests
{
    private readonly ITestOutputHelper _output;

    public RunQueueTests(ITestOutputHelper output) => _output = output;

    private static RunCoordinator Line(int slots = 3, int perClientRunning = 2, int perClientQueued = 3, int maxQueued = 100) =>
        new(new RunsOptions { MaxConcurrentRuns = slots, MaxRunningPerClient = perClientRunning, MaxQueuedPerClient = perClientQueued, MaxQueuedRuns = maxQueued, TypicalRunMinutes = 12 });

    [Fact]
    public async Task OneClientCannotTakeEverySlotWhileOthersWait()
    {
        var line = Line(slots: 3, perClientRunning: 2);
        var greedy = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        foreach (var run in greedy) Assert.True(line.TryAdmit(run, "10.0.0.1").Admitted);
        var other = Guid.NewGuid();
        Assert.True(line.TryAdmit(other, "10.0.0.2").Admitted);

        var g0 = await line.AcquireSlotAsync(greedy[0]).WaitAsync(TimeSpan.FromSeconds(2));
        var g1 = await line.AcquireSlotAsync(greedy[1]).WaitAsync(TimeSpan.FromSeconds(2));
        var waitG2 = line.AcquireSlotAsync(greedy[2]);
        // The third slot passes the greedy client's third run and goes to the other client, who came later.
        var o = await line.AcquireSlotAsync(other).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waitG2.IsCompleted);
        Assert.Equal(1, line.QueuePosition(greedy[2]));

        o.Dispose();
        Assert.False(waitG2.IsCompleted); // the free slot cannot go to a client that holds its share
        g0.Dispose();
        (await waitG2.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
        g1.Dispose();
        foreach (var run in greedy.Append(other)) line.Withdraw(run);
        Assert.Equal(0, line.Running);
        Assert.Equal(0, line.Queued);
    }

    [Fact]
    public void AClientWithItsShareAndAFullLineAreRefusedPolitely()
    {
        var line = Line(slots: 1, perClientRunning: 1, perClientQueued: 1, maxQueued: 2);
        Assert.True(line.TryAdmit(Guid.NewGuid(), "a").Admitted);
        Assert.True(line.TryAdmit(Guid.NewGuid(), "a").Admitted);
        var third = line.TryAdmit(Guid.NewGuid(), "a");
        Assert.False(third.Admitted);
        Assert.False(third.Busy);
        Assert.Contains("You already have 2 reviews running or waiting", third.Refusal);

        Assert.True(line.TryAdmit(Guid.NewGuid(), "b").Admitted);
        var full = line.TryAdmit(Guid.NewGuid(), "c");
        Assert.False(full.Admitted);
        Assert.True(full.Busy);
        Assert.StartsWith("The tool is busy: 2 reviews are waiting already. Try again later", full.Refusal);
        Assert.NotNull(full.RetryAfter);
        Assert.Equal(2, line.Queued);
    }

    [Fact]
    public void AWithdrawnRunGivesItsPlaceAndAnUnclaimedSlotBack()
    {
        var line = Line(slots: 1);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        line.TryAdmit(first, "a");
        line.TryAdmit(second, "b");
        Assert.Equal(1, line.Running); // the first was handed the slot at once
        Assert.Equal(1, line.QueuePosition(second));

        line.Withdraw(first); // it never claimed its slot, so the slot passes on
        Assert.Equal(1, line.Running);
        Assert.Equal(0, line.Queued);
        line.Withdraw(second);
        line.Withdraw(second);
        Assert.Equal(0, line.Running);
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheLine()
    {
        var line = Line(slots: 1);
        var holder = await line.AcquireSlotAsync(Guid.NewGuid());
        var run = Guid.NewGuid();
        line.TryAdmit(run, "x");
        using var stop = new CancellationTokenSource();
        var wait = line.AcquireSlotAsync(run, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, line.Queued);
        holder.Dispose();
        Assert.Equal(0, line.Running);
    }

    [Theory]
    [InlineData(1, 12, "You are 1st in line, about 15 minutes.")]
    [InlineData(4, 12, "You are 4th in line, about 25 minutes.")]
    [InlineData(2, 3, "You are 2nd in line, about 3 minutes.")]
    [InlineData(23, 12, "You are 23rd in line, about 1 hour 40 minutes.")]
    public void ThePlaceInLineReadsPlainly(int position, int typicalMinutes, string expected)
    {
        var line = new RunCoordinator(new RunsOptions { MaxConcurrentRuns = 3, TypicalRunMinutes = typicalMinutes });
        line.TryAdmit(Guid.NewGuid(), "a");
        line.TryAdmit(Guid.NewGuid(), "b");
        line.TryAdmit(Guid.NewGuid(), "c");
        var runs = Enumerable.Range(0, position).Select(i => { var id = Guid.NewGuid(); line.TryAdmit(id, "q" + i); return id; }).ToList();
        Assert.Equal(expected, line.PlaceOf(runs[^1])!.Text);
        Assert.Null(line.PlaceOf(Guid.NewGuid()));
    }

    /// <summary>
    /// Two addresses press Start ten times each, then a hundred visitors from sixty addresses press it at the same
    /// moment. The line admits each address's share and a hundred waiting runs, refuses the rest at once, and then
    /// serves everyone without ever passing three slots, or two for one address. The numbers go to the test output.
    /// </summary>
    [Fact]
    public async Task AHundredVisitorsAtOnceAreQueuedFairlyAndTheRestRefused()
    {
        var line = Line(slots: 3, perClientRunning: 2, perClientQueued: 3, maxQueued: 100);
        long memoryBefore = GC.GetTotalMemory(true);
        int threadsBefore = Process.GetCurrentProcess().Threads.Count;
        var visitors = Enumerable.Range(0, 10).Select(_ => "203.0.113.7")
            .Concat(Enumerable.Range(0, 10).Select(_ => "203.0.113.8"))
            .Concat(Enumerable.Range(0, 100).Select(i => $"10.0.{i % 60}.1")).ToList();

        var admitted = new List<(Guid Id, string Client)>();
        int busy = 0, share = 0;
        string? lastPlace = null;
        foreach (string client in visitors)
        {
            var id = Guid.NewGuid();
            var admission = line.TryAdmit(id, client);
            if (admission.Admitted) { admitted.Add((id, client)); lastPlace = line.PlaceOf(id)?.Text ?? lastPlace; }
            else if (admission.Busy) busy++;
            else share++;
        }
        Assert.Equal((103, 7, 10), (admitted.Count, busy, share));
        Assert.Equal((3, 100), (line.Running, line.Queued));
        Assert.Equal("You are 100th in line, about 6 hours 50 minutes.", lastPlace);

        int peak = 0, peakPerClient = 0;
        var holding = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(admitted.Select(run => Task.Run(async () =>
        {
            using (await line.AcquireSlotAsync(run.Id))
            {
                InterlockedMax(ref peak, line.Running);
                InterlockedMax(ref peakPerClient, holding.AddOrUpdate(run.Client, 1, (_, n) => n + 1));
                await Task.Delay(5);
                holding.AddOrUpdate(run.Client, 0, (_, n) => n - 1);
            }
            line.Withdraw(run.Id);
        }))).WaitAsync(TimeSpan.FromSeconds(60));

        _output.WriteLine($"admitted {admitted.Count}, refused as busy {busy}, refused for their share {share}; " +
            $"served in {clock.ElapsedMilliseconds} ms with at most {peak} slots and {peakPerClient} per address; " +
            $"memory +{(GC.GetTotalMemory(true) - memoryBefore) / 1024} KB, threads {threadsBefore} -> {Process.GetCurrentProcess().Threads.Count}");
        Assert.Equal(3, peak);
        Assert.True(peakPerClient <= 2);
        Assert.Equal((0, 0), (line.Running, line.Queued));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
