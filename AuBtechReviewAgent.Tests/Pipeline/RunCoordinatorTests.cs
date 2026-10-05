using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class RunCoordinatorTests
{
    [Fact]
    public async Task NeverRunsMoreThanTheLimitAndServesTheLineInOrder()
    {
        var coordinator = new RunCoordinator(maxConcurrent: 2);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();

        var slotA = await coordinator.AcquireSlotAsync(a);
        var slotB = await coordinator.AcquireSlotAsync(b);
        var waitC = coordinator.AcquireSlotAsync(c);
        var waitD = coordinator.AcquireSlotAsync(d);

        Assert.False(waitC.IsCompleted);
        Assert.Equal(1, coordinator.QueuePosition(c));
        Assert.Equal(2, coordinator.QueuePosition(d));

        slotA.Dispose();
        var slotC = await waitC.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waitD.IsCompleted);
        Assert.Equal(1, coordinator.QueuePosition(d));

        slotB.Dispose();
        var slotD = await waitD.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, coordinator.QueuePosition(d));

        slotC.Dispose();
        slotD.Dispose();
        slotD.Dispose(); // disposing twice must not free a second slot

        // Two free slots again, and not three.
        var x = await coordinator.AcquireSlotAsync(Guid.NewGuid());
        var y = await coordinator.AcquireSlotAsync(Guid.NewGuid());
        var z = coordinator.AcquireSlotAsync(Guid.NewGuid());
        Assert.False(z.IsCompleted);
        x.Dispose();
        (await z.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
        y.Dispose();
    }

    [Fact]
    public async Task ScreeningReviewIsHandedToTheWaitingRun()
    {
        var coordinator = new RunCoordinator(1);
        var run = Guid.NewGuid();
        coordinator.OpenScreeningReview(run);
        var wait = coordinator.WaitForScreeningReviewAsync(run, TimeSpan.FromSeconds(5));

        Assert.True(coordinator.IsAwaitingScreeningReview(run));
        Assert.True(coordinator.SubmitScreeningReview(run, new[] { new ScreeningOverride("p1", "Included", null) }));

        var decisions = await wait;
        Assert.NotNull(decisions);
        Assert.Equal("p1", Assert.Single(decisions!).PaperId);
        Assert.False(coordinator.SubmitScreeningReview(run, Array.Empty<ScreeningOverride>()));
    }

    [Fact]
    public async Task ScreeningReviewTimesOutWithNull()
    {
        var coordinator = new RunCoordinator(1);
        var run = Guid.NewGuid();
        coordinator.OpenScreeningReview(run);

        Assert.Null(await coordinator.WaitForScreeningReviewAsync(run, TimeSpan.FromMilliseconds(50)));
        Assert.False(coordinator.IsAwaitingScreeningReview(run));
    }
}
