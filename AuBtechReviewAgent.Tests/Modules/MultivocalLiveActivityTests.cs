using AuBtechReviewAgent;
using AuBtechReviewAgent.Modules.Multivocal.Pages;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The live line of a running multivocal review (MultivocalLiveActivity), from the runner's step and the progress
/// lines its steps report, and the head of the run view ("Review running · step n of 8").
/// </summary>
public class MultivocalLiveActivityTests
{
    private static LiveLine Line(string step, string? progress, string? found = null) =>
        MultivocalLiveActivity.For(new MultivocalRunStatus(true, step, progress, null), found) ?? throw new Xunit.Sdk.XunitException("no line");

    [Theory]
    [InlineData("Searching", "3 of 12 searches · GitHub: \"agent runtime\"", "Searching GitHub for “agent runtime”", "search")]
    [InlineData("Searching", "2 of 6 academic searches · OpenAlex: \"agentic AI\"", "Searching OpenAlex for “agentic AI”", "search")]
    [InlineData("Searching", null, "Searching the chosen sources", "search")]
    [InlineData("Screening", "119 of 312 sources screened", "Screening source 120 of 312", "screen")]
    [InlineData("Screening", "4 of 40 second screenings", "Screening source 5 of 40 a second time", "screen")]
    [InlineData("Scoring the quality", "9 of 41 included sources assessed", "Scoring the quality of source 10 of 41", "quality")]
    [InlineData("Proposing the map", null, "Proposing the systematic map from the research questions", "map")]
    [InlineData("Fixing the map", null, "Fixing the systematic map", "map")]
    [InlineData("Extracting", "12 of 30 sources extracted", "Extracting source 13 of 30", "extract")]
    [InlineData("Synthesising", "Checking that each of 210 quotes supports its value", "Checking that each quote supports its value", "synthesis")]
    [InlineData("Synthesising", "Which tools are used?: placing values 1–20 of 45", "Placing values 1–20 of 45 for “Which tools are used?”", "synthesis")]
    [InlineData("Synthesising", "Which tools are used?: second placement of values 1–20", "Placing the values of “Which tools are used?” a second time", "synthesis")]
    [InlineData("Synthesising", "Which tools are used?: 60 findings in 45 values; naming the themes", "Naming the themes for “Which tools are used?”", "synthesis")]
    [InlineData("Writing the paper", "1 of 6 writing steps · the grounded outline", "Writing the grounded outline", "writing")]
    [InlineData("Writing the paper", "2 of 6 writing steps · theme sections: 3 of 7 written", "Writing theme section 4 of 7", "writing")]
    [InlineData("Writing the paper", "4 of 6 writing steps · the automated peer review", "Reading the draft as a peer reviewer would", "peer-review")]
    [InlineData("Checking the citations", "Checking 64 citations against the kept pages", "Checking every citation against its kept page", "check")]
    [InlineData("Checking the citations", "First check: 17 of 30 sources", "Checking the citations: source 18 of 30", "check")]
    [InlineData("Checking the citations", "Second check: 1 of 5 sources", "Checking the citations again: source 2 of 5", "check-second")]
    [InlineData("Checking the citations", "Repairing 3 citations and checking them again", "Repairing the citations that failed the check", "repair")]
    public void EachStepSaysWhatItDoesNow(string step, string? progress, string text, string kind)
    {
        var line = Line(step, progress);
        Assert.Equal(text, line.Text);
        Assert.Equal(kind, line.Kind);
        Assert.DoesNotMatch(@"\d", line.Announce);
    }

    [Fact]
    public void TheCountComesFromTheFilesOnRecord()
    {
        Assert.Equal("312 found", Line("Screening", "1 of 312 sources screened", "312 found").Count);
        Assert.Equal("search 3 of 12", Line("Searching", "3 of 12 searches · GitHub: \"x\"").Count);
        Assert.Equal("writing step 1 of 6", Line("Writing the paper", "1 of 6 writing steps · the grounded outline", "9 found").Count);
        Assert.Null(MultivocalLiveActivity.Counts(null, null));
    }

    [Fact]
    public void AQuestionOrSearchStringIsCutAndKeptAsText()
    {
        string question = "<b>" + string.Join(" ", Enumerable.Repeat("governance", 12)) + "</b>";
        string text = Line("Synthesising", $"{question}: placing values 1–5 of 9").Text;
        Assert.StartsWith("Placing values 1–5 of 9 for “<b>governance", text);
        Assert.EndsWith("…”", text);
    }

    [Fact]
    public void ARunThatIsNotRunningHasNoLine()
    {
        Assert.Null(MultivocalLiveActivity.For(null));
        Assert.Null(MultivocalLiveActivity.For(new MultivocalRunStatus(false, "Done", null, null)));
    }

    [Fact]
    public void TheHeadNamesTheStepOfEight()
    {
        Assert.Equal("Review running · step 2 of 8", MultivocalRunHeader.EyebrowFor(MultivocalScreener.StageScreening, new MultivocalRunStatus(true, "Screening", null, null)));
        Assert.Equal("Review running · step 1 of 8", MultivocalRunHeader.EyebrowFor(MultivocalSearcher.StageSearching, new MultivocalRunStatus(true, "Searching", null, null)));
        Assert.Equal("Review stopped · step 8 of 8", MultivocalRunHeader.EyebrowFor(MultivocalWriter.StageChecking, new MultivocalRunStatus(false, "Checking the citations", null, "stopped")));
        Assert.Equal("Review not finished · step 3 of 8", MultivocalRunHeader.EyebrowFor(MultivocalScreener.StageScreened, null));
        Assert.Equal("Review complete", MultivocalRunHeader.EyebrowFor(MultivocalWriter.StageChecked, null));
    }
}
