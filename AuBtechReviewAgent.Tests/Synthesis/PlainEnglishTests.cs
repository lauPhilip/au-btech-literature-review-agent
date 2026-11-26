using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The code checks of plain technical English (issue #599): sentence splitting, word counts, passive, noun clusters.</summary>
public class PlainEnglishTests
{
    [Fact]
    public void SentencesAreNotSplitAtCitationsAbbreviationsOrInitials()
    {
        var sentences = PlainEnglish.Sentences(
            "Doe et al. [3] screened 1,234 records, e.g. reports and theses. Agents recover from faults [1, 2]. Tools (i.e. APIs) vary, cf. J. Doe [4]. Loops stop.");

        Assert.Equal(new[]
        {
            "Doe et al. [3] screened 1,234 records, e.g. reports and theses.",
            "Agents recover from faults [1, 2].",
            "Tools (i.e. APIs) vary, cf. J. Doe [4].",
            "Loops stop.",
        }, sentences);
    }

    [Fact]
    public void WordsAreCountedWithoutCitationMarkersAndANumberIsOneWord()
    {
        Assert.Equal(7, PlainEnglish.Words("We screened 1,234 records in 3.5 days [1, 2]."));
        Assert.Equal(0, PlainEnglish.Words("[3]"));
    }

    [Fact]
    public void ASentenceOverThirtyWordsIsLong()
    {
        string thirty = string.Join(" ", Enumerable.Repeat("word", 30)) + " [1].";
        string thirtyOne = string.Join(" ", Enumerable.Repeat("word", 31)) + ".";

        Assert.Empty(PlainEnglish.LongSentences(thirty));
        var longOne = Assert.Single(PlainEnglish.LongSentences(thirty + " " + char.ToUpper(thirtyOne[0]) + thirtyOne[1..]));
        Assert.Equal(31, longOne.Words);
    }

    [Fact]
    public void ThePassiveIsFoundAndAnActiveSentenceIsNot()
    {
        Assert.True(PlainEnglish.IsPassive("Records were screened twice [2]."));
        Assert.True(PlainEnglish.IsPassive("The protocol was not changed."));
        Assert.True(PlainEnglish.IsPassive("Findings are shown in Table 2."));
        Assert.False(PlainEnglish.IsPassive("We screened the records twice."));
        Assert.False(PlainEnglish.IsPassive("Agents need supervision."));
        Assert.False(PlainEnglish.IsPassive("More studies are needy."));
    }

    [Fact]
    public void TheMeasuresGiveMeanLengthLongShareAndPassiveShare()
    {
        string longOne = string.Join(" ", Enumerable.Repeat("word", 26)) + ".";
        var m = PlainEnglish.Measure("We screened records. Records were screened twice [1].\n\n" + char.ToUpper(longOne[0]) + longOne[1..])!;

        Assert.Equal(3, m.Sentences);
        Assert.Equal(11.0, m.MeanSentenceWords); // (3 + 4 + 26) / 3
        Assert.Equal(0.33, m.ShareOver25Words);
        Assert.Equal(0.33, m.SharePassive);
        Assert.Equal(26, m.LongestSentenceWords);
        Assert.Null(PlainEnglish.Measure("  "));
    }

    [Fact]
    public void ALongNounClusterIsFlagged()
    {
        Assert.Equal(new[] { "software defect prediction model accuracy" },
            PlainEnglish.NounClusters("The software defect prediction model accuracy improved [1]."));
        Assert.Empty(PlainEnglish.NounClusters("The prediction model improved [1]."));
    }

    [Fact]
    public void NounClustersSkipCitationsNumbersNamesAndQuotes()
    {
        Assert.Empty(PlainEnglish.NounClusters("The GPT-4 Turbo API rate limit held [1, 2]."));
        Assert.Empty(PlainEnglish.NounClusters("One source calls it \"agent context window budget management\" [3]."));
        Assert.Empty(PlainEnglish.NounClusters("We included 12 2025 agent loop studies."));
        Assert.Empty(PlainEnglish.NounClusters("Agents keep a running summary of the conversation history [1]."));
        Assert.Empty(PlainEnglish.NounClusters("Studies report software defect reduction [2]."));
    }

    [Fact]
    public void FlagsNameLongParagraphsAndNounClustersButNeverFail()
    {
        string paragraph = string.Join(" ", Enumerable.Range(1, 7).Select(i => $"Sentence {i} ends here."));
        var flags = PlainEnglish.Flags(paragraph + "\n\nThe software defect prediction model accuracy improved.");

        Assert.Equal(new[]
        {
            "Paragraph 1 has 7 sentences (more than 6).",
            "Noun cluster of 5 words: \"software defect prediction model accuracy\".",
        }, flags);
        Assert.Empty(PlainEnglish.Flags("Agents recover from faults [1]."));
    }
}
