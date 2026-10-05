using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class JournalRankingMatcherTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNAForBlankJournalName(string journal)
    {
        Assert.Equal("N/A", JournalRankingMatcher.LookupQuartile(journal, 2024));
    }

    [Fact]
    public void ReturnsNAForNullJournalName()
    {
        Assert.Equal("N/A", JournalRankingMatcher.LookupQuartile(null!, 2024));
    }

    [Fact]
    public void ReturnsNAForUnknownJournal()
    {
        // A name that will not appear in any Scimago dataset, so the lookup falls through to N/A
        // whether or not the ranking CSVs are present in the test run.
        var result = JournalRankingMatcher.LookupQuartile("ZZZ Nonexistent Journal Of Nothing 9999", 2024);

        Assert.Equal("N/A", result);
    }
}
