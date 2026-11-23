using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The live line under a running systematic review's title (LiveActivity): one sentence per stage, built from the
/// detail lines the engine reports, with the running counts; the announcement depends only on the kind of work.
/// </summary>
public class LiveActivityTests
{
    private static ReviewStats Stats(string step, string detail = "", string subject = "", int found = 0, int screened = 0, int included = 0) => new()
    {
        ProgressPlan = RunProgress.Plan(true, true),
        ProgressStep = step,
        ProgressDetail = detail,
        ProgressSubject = subject,
        ProcessingStage = "Screening",
        TotalIdentified = found,
        Screened = screened,
        Included = included,
    };

    private static LiveLine Line(ReviewStats stats, string stage = "Screening", int queue = 0) =>
        LiveActivity.ForSystematic(stats, stage, queue) ?? throw new Xunit.Sdk.XunitException("no line");

    [Fact]
    public void BeforeTheSearchTheProtocolIsWritten() =>
        Assert.Equal("Writing the protocol and fixing its fingerprint", Line(Stats("")).Text);

    [Fact]
    public void TheSearchNamesTheDatabaseAndTheStringItAsksNow()
    {
        var line = Line(Stats(RunProgress.Search, "3 of 12 searches", LiveActivity.SearchSubjectOf("OpenAlex", "agentic AI fault tolerance")));
        Assert.Equal("Searching OpenAlex for “agentic AI fault tolerance”", line.Text);
        Assert.Equal("search 3 of 12", line.Count);
        Assert.Equal("search", line.Kind);
    }

    [Fact]
    public void TheSearchWithoutASubjectSaysWhatItDoes()
    {
        Assert.Equal("Preparing the search strings", Line(Stats(RunProgress.Search, "Preparing search strings")).Text);
        Assert.Equal("Searching the databases", Line(Stats(RunProgress.Search, "Searching 4 source(s) with 3 search string(s)")).Text);
    }

    [Fact]
    public void ScreeningNamesTheRecordItIsOnAndCountsWhatIsIncluded()
    {
        var line = Line(Stats(RunProgress.Screening, "119 of 431 records screened", found: 431, screened: 119, included: 42));
        Assert.Equal("Screening record 120 of 431", line.Text);
        Assert.Equal("431 found · 119 screened · 42 included", line.Count);
        Assert.Equal("Screening record 431 of 431", Line(Stats(RunProgress.Screening, "431 of 431 records screened")).Text);
    }

    [Fact]
    public void CitationChainingLooksUpThenScreens()
    {
        Assert.Equal("Looking up the references and citing papers of the included studies",
            Line(Stats(RunProgress.Chaining, "Looking up references and citing papers of 12 included studies")).Text);
        Assert.Equal("Following citations: look-up 4 of 12", Line(Stats(RunProgress.Chaining, "3 of 12 look-ups")).Text);
        Assert.Equal("Building out from the most-cited included studies", Line(Stats(RunProgress.Chaining, "Building out from the 5 most-cited included studies")).Text);
        Assert.Equal("Following the citations of seed study 2 of 5", Line(Stats(RunProgress.Chaining, "1 of 5 seed studies")).Text);
        Assert.Equal("Screening record 6 of 80 found by citation chaining", Line(Stats(RunProgress.Chaining, "5 of 80 records screened")).Text);
    }

    [Fact]
    public void TheHumanCheckAndTheQueueAreWaitsNotWork()
    {
        Assert.Equal("your-check", Line(Stats(RunProgress.Review, "Waiting for your screening decisions"), PrismaReviewEngine.StageAwaitingReview).Kind);
        var queued = Line(Stats(""), PrismaReviewEngine.StageQueued, 3);
        Assert.Equal("Waiting in line for a free run slot", queued.Text);
        Assert.Equal("number 3 in line", queued.Count);
        Assert.Equal("queue", queued.Kind);
        Assert.Null(Line(Stats(""), PrismaReviewEngine.StageQueued).Count);
        // The shared line's own words, with the rough wait, win over the bare number.
        var worded = LiveActivity.ForSystematic(Stats(""), PrismaReviewEngine.StageQueued, 4, "You are 4th in line, about 25 minutes.")!;
        Assert.Equal("You are 4th in line, about 25 minutes.", worded.Count);
    }

    [Fact]
    public void FullTextsNameThePaperBeingRead()
    {
        string cite = LiveActivity.ShortCite(new List<string> { "Lotte Bosschaerts", "A. Peeters", "C. Diaz" }, "2025-03-01", "A title");
        Assert.Equal("Bosschaerts et al., 2025", cite);
        Assert.Equal("Reading the full text of Bosschaerts et al., 2025", Line(Stats(RunProgress.FullText, "4 of 14 papers", cite)).Text);
        Assert.Equal("Reading full text 5 of 14", Line(Stats(RunProgress.FullText, "4 of 14 papers")).Text);
        Assert.Equal("Fetching the open-access full texts", Line(Stats(RunProgress.FullText, "Fetching open-access full texts of 14 studies")).Text);
    }

    [Fact]
    public void ShortCitesFollowTheAuthorsInTheRecord()
    {
        Assert.Equal("Doe, 2024", LiveActivity.ShortCite(new List<string> { "Doe, Jane" }, "2024", "T"));
        Assert.Equal("Doe and Roe", LiveActivity.ShortCite(new List<string> { "Jane Doe", "Rick Roe" }, "", "T"));
        Assert.Equal("“Agents in the loop”", LiveActivity.ShortCite(new List<string>(), "2024", "Agents in   the\nloop"));
    }

    [Fact]
    public void ExtractionCountsTheStudies() =>
        Assert.Equal("Extracting and appraising study 10 of 14", Line(Stats(RunProgress.Extraction, "9 of 14 studies")).Text);

    [Theory]
    [InlineData("Outline and report draft", "Drafting the outline of the report", "draft")]
    [InlineData("Coding 14 studies", "Coding the included studies", "code")]
    [InlineData("Coding studies: 2 of 5 batches", "Coding the studies: batch 3 of 5", "code")]
    [InlineData("Grouping 80 codes into themes", "Grouping the codes into themes", "themes")]
    [InlineData("Writing 4 theme subsections", "Writing the theme subsections", "theme-write")]
    [InlineData("Theme subsections: 1 of 4 written", "Writing theme 2 of 4", "theme-write")]
    [InlineData("Writing the discussion", "Writing the discussion", "discussion")]
    [InlineData("Writing the synthesis and discussion", "Writing the discussion", "discussion")]
    [InlineData("Automated peer review", "Reading the draft as a peer reviewer would", "peer-review")]
    [InlineData("Title, abstract and introduction", "Writing the title, abstract and introduction", "front")]
    [InlineData("Building the artifact", "Building the artifact", "artifact")]
    public void WritingSaysWhichPartIsWritten(string detail, string text, string kind)
    {
        var line = Line(Stats(RunProgress.Synthesis, detail));
        Assert.Equal(text, line.Text);
        Assert.Equal(kind, line.Kind);
    }

    [Fact]
    public void AThemeNameIsShownWhenTheEngineGivesOne() =>
        Assert.Equal("Writing the theme “Runtime governance”", Line(Stats(RunProgress.Synthesis, "Theme subsections: 1 of 4 written", "Runtime governance")).Text);

    [Theory]
    [InlineData("Checking 64 citations", "Checking every citation against its paper", "check")]
    [InlineData("First check: 17 of 64 papers", "Checking the citations: paper 18 of 64", "check")]
    [InlineData("Second check: 2 of 9 papers", "Checking the citations again: paper 3 of 9", "check-second")]
    [InlineData("Repairing 5 citations and checking them again", "Repairing the citations that failed the check", "repair")]
    public void CheckingCountsThePapers(string detail, string text, string kind)
    {
        var line = Line(Stats(RunProgress.Checking, detail));
        Assert.Equal(text, line.Text);
        Assert.Equal(kind, line.Kind);
    }

    [Fact]
    public void AFinishedOrStoppedRunHasNoLine()
    {
        Assert.Null(LiveActivity.ForSystematic(Stats(RunProgress.Done), PrismaReviewEngine.StageComplete, 0));
        Assert.Null(LiveActivity.ForSystematic(Stats(RunProgress.Screening), PrismaReviewEngine.StageFailed, 0));
        Assert.Null(LiveActivity.ForSystematic(Stats(RunProgress.Screening), PrismaReviewEngine.StageCancelled, 0));
    }

    [Fact]
    public void TheAnnouncementChangesWithTheKindNotTheCount()
    {
        var a = Line(Stats(RunProgress.Screening, "1 of 431 records screened"));
        var b = Line(Stats(RunProgress.Screening, "300 of 431 records screened", included: 40));
        Assert.NotEqual(a.Text, b.Text);
        Assert.Equal(a.Announce, b.Announce);
        Assert.DoesNotMatch(@"\d", a.Announce);
        Assert.NotEqual(a.Announce, Line(Stats(RunProgress.FullText, "1 of 4 papers")).Announce);
    }

    [Fact]
    public void TheAnnouncerSpeaksOnAChangeOfSentenceAtMostEveryFewSeconds()
    {
        var announcer = new LiveAnnouncer();
        var t = new DateTime(2026, 11, 23, 22, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Now screening the records.", announcer.Next("Now screening the records.", t));
        Assert.Equal("Now screening the records.", announcer.Next("Now screening the records.", t.AddSeconds(1)));
        // A change within the gap is held back, and given on the next call after it.
        Assert.Equal("Now screening the records.", announcer.Next("Now following citations.", t.AddSeconds(1)));
        Assert.Equal("Now following citations.", announcer.Next("Now following citations.", t.AddSeconds(4)));
        Assert.Equal("Now following citations.", announcer.Next("Now screening the records.", t.AddSeconds(5)));
        Assert.Equal("Now screening the records.", announcer.Next("Now screening the records.", t.AddSeconds(8)));
    }

    [Fact]
    public void LongTitlesAndQueriesAreCutOnAWord()
    {
        string query = string.Join(" ", Enumerable.Repeat("governance", 12));
        string text = Line(Stats(RunProgress.Search, "", LiveActivity.SearchSubjectOf("OpenAlex", query))).Text;
        Assert.EndsWith("…”", text);
        Assert.True(LiveActivity.Cut(query).Length <= LiveActivity.MaxSubject + 1);
        Assert.Equal("short", LiveActivity.Cut("  short \n"));
    }

    [Fact]
    public void TitlesAreKeptAsTheyAreForTheirPageToEncode()
    {
        // The line is text: markup in a title is not removed or turned into markup here; Razor encodes it (LiveActivityLineTests).
        string cite = LiveActivity.ShortCite(new List<string>(), "", "<script>alert(1)</script>");
        Assert.Equal("Reading the full text of “<script>alert(1)</script>”", Line(Stats(RunProgress.FullText, "", cite)).Text);
    }

    [Fact]
    public void TheSubjectIsLiveOnlyAndARereadOfTheLedgerKeepsIt()
    {
        var live = Stats(RunProgress.FullText, "4 of 14 papers", "Doe, 2024");
        live.RunStartedUtc = DateTime.UtcNow;
        string json = System.Text.Json.JsonSerializer.Serialize(live);
        Assert.DoesNotContain("ProgressSubject", json);
        var reread = System.Text.Json.JsonSerializer.Deserialize<ReviewStats>(json)!;
        Assert.Equal("", reread.ProgressSubject);
        Assert.Equal("Doe, 2024", RunProgress.KeepFurthest(live, reread).ProgressSubject);
        var later = Stats(RunProgress.Extraction, "1 of 14 studies");
        later.RunStartedUtc = live.RunStartedUtc;
        Assert.Equal("", RunProgress.KeepFurthest(live, later).ProgressSubject);
    }
}
