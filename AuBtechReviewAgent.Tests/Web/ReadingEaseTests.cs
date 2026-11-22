using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// ReadingEase is the C# twin of reading_ease in tools/ReleaseCheck/release_notes.py. The expected scores were
/// computed with the Python function, so the two give the same number for the same text.
/// </summary>
public class ReadingEaseTests
{
    [Theory]
    [InlineData("The API is on. You send your key in the X-Mistral-Key header, and the review starts at once.", 89.60000000000004)]
    [InlineData("## Added\n\n- Cancel a review through the API (`DELETE /api/v1/reviews/{id}`).\n- See [the wiki](https://example.org/wiki) for v0.1.0 details.", 83.74867647058825)]
    [InlineData("Organisational interoperability necessitates comprehensive documentation.", -255.07999999999996)]
    public void The_score_is_the_same_as_the_release_checks(string text, double python)
    {
        Assert.Equal(python, ReadingEase.Score(text)!.Value, 9);
    }

    [Theory]
    [InlineData("make", 1)]
    [InlineData("agreed", 2)]
    [InlineData("yearly", 2)]
    [InlineData("X-Mistral-Key", 4)]
    [InlineData("v0.1.0", 1)]
    [InlineData("the", 1)]
    [InlineData("queue", 2)]
    [InlineData("table", 2)]
    public void Syllables_are_counted_as_the_release_check_counts_them(string word, int syllables)
    {
        Assert.Equal(syllables, ReadingEase.CountSyllables(word));
    }

    [Fact]
    public void A_text_without_words_has_no_score()
    {
        Assert.Null(ReadingEase.Score(""));
        Assert.Null(ReadingEase.Score("## Heading only\n\n—"));
    }
}
