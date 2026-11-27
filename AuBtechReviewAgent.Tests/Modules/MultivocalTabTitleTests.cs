using AuBtechReviewAgent.Modules.Multivocal.Pages;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #693: the multivocal run page's tab kept its percentage, or went back to the review's name, when the run
/// finished. It now says the review is ready, or that it stopped, as the systematic review's page does.
/// </summary>
public class MultivocalTabTitleTests
{
    private const string Idle = "Multivocal literature review (preview)";

    [Fact]
    public void TheTitleFollowsTheRunAndSaysWhenItFinishedHere()
    {
        Assert.Equal("42% · Your review", MultivocalStart.TabTitleFor(42, null, Idle));
        Assert.Equal("✓ Review ready", MultivocalStart.TabTitleFor(null, true, Idle));
        Assert.Equal("Run stopped · Your review", MultivocalStart.TabTitleFor(null, false, Idle));
        Assert.Equal(Idle, MultivocalStart.TabTitleFor(null, null, Idle));
    }

    [Fact]
    public void ARunStartedAgainShowsItsProgressOverAnEarlierFinish()
    {
        Assert.Equal("3% · Your review", MultivocalStart.TabTitleFor(3, true, Idle));
        Assert.Equal("3% · Your review", MultivocalStart.TabTitleFor(3, false, Idle));
    }
}
