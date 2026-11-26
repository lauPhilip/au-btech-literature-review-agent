using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The Checks and Records lines of the front page's Article info that come from the run's own checks (wiki page 06):
/// the self-check from self-check.json, and the hooks for the reviews among the included studies and the retraction
/// check, which show a line only when the run recorded the data. No line is ever a guess.
/// </summary>
public class ArticleInfoChecksTests
{
    private static ArticleInfo Info(GallerySelfCheck? selfCheck = null, int? reviews = null, RetractionTally? retractions = null) => new()
    {
        RunId = Guid.Parse("596541db-400c-4b99-9a8a-a5dc0ae9a9d3"), Found = 1284, Included = 41,
        Citations = new VerdictTally(118, 2, 1, 0, 0, Removed: 4),
        SelfCheck = selfCheck, ReviewsAmongIncluded = reviews, Retractions = retractions,
    };

    private static IReadOnlyList<string> Lines(ArticleInfo info, string label) =>
        info.InfoGroups().Single(g => g.Label == label).Lines;

    [Fact]
    public void TheSelfCheckIsShownWithItsNotesAndFaults()
    {
        Assert.Equal(new[] { "Citations 118 of 121 supported", "Self-check: 8 of 11 passed; 2 notes, 1 fault" },
            Lines(Info(new GallerySelfCheck(8, 2, 1)), "Checks"));
        Assert.Equal("Self-check: 10 of 10 passed", ArticleInfo.SelfCheckLine(new GallerySelfCheck(10, 0, 0)));
        Assert.Equal("Self-check: 9 of 10 passed; 1 note", ArticleInfo.SelfCheckLine(new GallerySelfCheck(9, 1, 0)));
        Assert.Null(ArticleInfo.SelfCheckLine(new GallerySelfCheck(0, 0, 0)));
    }

    [Fact]
    public void ARunWithoutTheDataShowsNoSelfCheckReviewsOrRetractions()
    {
        var info = Info();

        Assert.Equal(new[] { "Citations 118 of 121 supported" }, Lines(info, "Checks"));
        Assert.Equal(new[] { "1,284 identified", "41 included" }, Lines(info, "Records"));
        Assert.Null(new RetractionTally(0, 0, 0, 0).Line);
    }

    [Fact]
    public void TheReviewsAmongTheIncludedFollowTheIncludedCount()
    {
        Assert.Contains("41 included (3 reviews)", Lines(Info(reviews: 3), "Records"));
        Assert.Contains("41 included (1 review)", Lines(Info(reviews: 1), "Records"));
        // Recorded as none is data, and says so.
        Assert.Contains("41 included (0 reviews)", Lines(Info(reviews: 0), "Records"));
    }

    [Fact]
    public void TheRetractionCheckSaysWhatItFoundAndHowManyItChecked()
    {
        Assert.Equal("Retractions: none in 38 checked", new RetractionTally(38, 0, 0, 0).Line);
        Assert.Equal("Retractions: 1 retracted, 2 corrected, of 38 checked", new RetractionTally(38, 1, 0, 2).Line);
        Assert.Equal("Retractions: 1 with an expression of concern, of 38 checked", new RetractionTally(38, 0, 1, 0).Line);
        Assert.Equal(new[] { "Citations 118 of 121 supported", "Self-check: 11 of 11 passed", "Retractions: none in 38 checked" },
            Lines(Info(new GallerySelfCheck(11, 0, 0), retractions: new RetractionTally(38, 0, 0, 0)), "Checks"));
    }

    [Fact]
    public void TheRetractionTallyIsReadFromTheChecksOwnFile()
    {
        var file = new RetractionCheckFile { Studies =
        {
            new() { Status = RetractionStatus.None }, new() { Status = RetractionStatus.Retracted }, new() { Status = RetractionStatus.Corrected },
            new() { Status = RetractionStatus.Concern }, new() { Status = RetractionStatus.NotChecked },
        } };

        // The study that could not be checked is not counted, so the line never claims a check that was not made.
        Assert.Equal(new RetractionTally(4, 1, 1, 1), RetractionTally.From(file));
        Assert.Null(RetractionTally.From(null));
        Assert.Null(RetractionTally.From(new RetractionCheckFile()));
        Assert.Null(RetractionTally.From(new RetractionCheckFile { Studies = { new() { Status = RetractionStatus.NotChecked } } }));
    }

    [Fact]
    public void TheSelfCheckIsReadFromTheRunsFileOnly()
    {
        string folder = Path.Join(Path.GetTempPath(), "front-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // A run made before the self-check has no file, and its front page shows no line rather than a fresh check.
            Assert.Null(GallerySelfCheck.Read(folder));
            File.WriteAllText(Path.Join(folder, PaperSelfCheckFile.FileName), "{\"Passed\":7,\"Notes\":1,\"Faults\":2,\"Checks\":[]}");
            var info = ArticleInfo.ForSystematic(Guid.NewGuid(), new ReviewState(), new PrismaReport(), "T", "A.", hasProtocol: false,
                selfCheck: GallerySelfCheck.Read(folder));
            Assert.Contains("Self-check: 7 of 10 passed; 1 note, 2 faults", Lines(info, "Checks"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
