using AuBtechReviewAgent;
using Xunit;
using Xunit.Abstractions;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A long grey page is sent as selected passages (<see cref="GreyPageText"/>), not as its first 40,000 characters.
/// The quality check keeps the start and the end of the page and, for each Table 7 item, the passage that shows it:
/// measured here with a sentence for each item placed in the middle of long synthetic pages (<see cref="LongGreyPages"/>).
/// </summary>
public class GreyPageTextTests
{
    private readonly ITestOutputHelper _out;
    public GreyPageTextTests(ITestOutputHelper output) => _out = output;

    private const string Topic = "Context engineering for LLM agents. Research questions: RQ1: Which context engineering practices do practitioners and researchers describe?";

    private static MapVersion Map() => new()
    {
        Number = 1,
        Attributes = { new MapAttribute { Name = "Practice", Question = "RQ1", Description = "The practice described.", Values = { "Retrieval", "Summarisation", "Other" }, Multiple = true } },
    };

    [Fact]
    public void AShortPageIsSentWholeToBothSteps()
    {
        string page = LongGreyPages.Text(1, 6_000);
        Assert.Equal(new TextForModel(page, null), GreyPageText.ForExtraction(page, MultivocalPlan.Example("grey"), Map()));
        string longer = LongGreyPages.Text(1, 11_000);
        Assert.True(longer.Length <= GreyPageText.QualityWholeUpTo);
        Assert.Equal(new TextForModel(longer, null), GreyPageText.ForQuality(longer, Topic));
    }

    [Fact]
    public void PassagesAreSpansOfTheKeptTextAndAQuoteCannotJoinTwoOfThem()
    {
        string page = LongGreyPages.Text(3, 30_000);
        var passages = PassageSelection.Lines(page);
        Assert.All(passages, p => Assert.InRange(p.Length, 1, 600));
        Assert.All(passages.Zip(passages.Skip(1)), x => Assert.True(x.First.End < x.Second.Start));

        var sent = GreyPageText.ForQuality(page, Topic);
        Assert.All(sent.Text.Split(PassageSelection.Break), span => Assert.Contains(span, page));
        // The last words of one stretch and the first of the next are not next to each other on the page.
        var spans = sent.Text.Split(PassageSelection.Break);
        string joined = spans[0][^40..] + " " + spans[1][..40];
        Assert.False(PassageSelection.QuoteFound(joined, sent.Text));
        Assert.True(PassageSelection.QuoteFound(spans[1][..60], sent.Text));
    }

    [Theory]
    [InlineData(15_000)]
    [InlineData(30_000)]
    [InlineData(60_000)]
    [InlineData(120_000)]
    public void TheQualityCheckKeepsTheStartTheEndAndASentenceForEveryItemInTheMiddle(int length)
    {
        string page = LongGreyPages.Text(length / 1000, length);
        var sent = GreyPageText.ForQuality(page, Topic);

        Assert.NotNull(sent.Passages);
        Assert.StartsWith("Managing the context of an agent", sent.Text);
        Assert.Contains("By Ana Author. Published 1 May 2025.", sent.Text);
        Assert.Contains("About the author", sent.Text);
        Assert.EndsWith("[2] Example Corp. Agent memory guide.", sent.Text);
        var missed = LongGreyPages.Planted.Where(p => !PassageSelection.QuoteFound(p.Value, sent.Text)).Select(p => p.Key).ToList();
        _out.WriteLine($"{length:N0} characters: sent {sent.Text.Length:N0} ({sent.Passages}); missed: {(missed.Count == 0 ? "none" : string.Join(", ", missed))}");
        Assert.Empty(missed);
        Assert.True(sent.Text.Length < 14_000, $"{sent.Text.Length} characters sent");
    }

    [Fact]
    public void ASentenceForOneItemAloneIsKeptAndTheOthersAreNotNeeded()
    {
        // One sentence at a time, in the middle of a page of ordinary text: each must still be kept.
        var missed = new List<string>();
        foreach (var (item, sentence) in LongGreyPages.Planted)
        {
            string filler = LongGreyPages.Text(7, 40_000, planted: false);
            int middle = filler.IndexOf('\n', filler.Length / 2);
            string page = filler[..middle] + "\n" + sentence + filler[middle..];
            if (!PassageSelection.QuoteFound(sentence, GreyPageText.ForQuality(page, Topic).Text)) missed.Add(item);
        }
        _out.WriteLine("missed alone: " + (missed.Count == 0 ? "none" : string.Join(", ", missed)));
        Assert.Empty(missed);
    }

    [Theory]
    [InlineData("paraphrased")]
    [InlineData("unseen")]
    public void OtherWordingsShowWhichItemsLexicalSelectionCanMiss(string set)
    {
        // Each sentence alone in the middle of a 40,000-character page; the items it misses are reported, not hidden.
        var sentences = set == "unseen" ? LongGreyPages.Unseen : LongGreyPages.Paraphrased;
        var missed = new List<string>();
        foreach (var (item, sentence) in sentences)
        {
            string filler = LongGreyPages.Text(8, 40_000, planted: false);
            int middle = filler.IndexOf('\n', filler.Length / 2);
            string page = filler[..middle] + "\n" + sentence + filler[middle..];
            if (!PassageSelection.QuoteFound(sentence, GreyPageText.ForQuality(page, Topic).Text)) missed.Add(item);
        }
        _out.WriteLine($"{set} missed: " + (missed.Count == 0 ? "none" : string.Join(", ", missed)));
        Assert.True(missed.Count <= (set == "unseen" ? MissedUnseen : MissedParaphrased), $"{set} missed: " + string.Join(", ", missed));
    }

    // How many of each set the selection misses today; lower them when the selection improves.
    private const int MissedParaphrased = 2, MissedUnseen = 3;

    [Fact]
    public void TheExtractionGetsTheFirstAndMostRelevantPassagesOfALongPage()
    {
        string relevant = "Summarisation is the context engineering practice these agents rely on, with retrieval for old documents.";
        string filler = LongGreyPages.Text(9, 40_000, planted: false);
        int middle = filler.IndexOf('\n', filler.Length / 2);
        string page = filler[..middle] + "\n" + relevant + filler[middle..];

        var sent = GreyPageText.ForExtraction(page, MultivocalPlan.Example("grey"), Map());
        Assert.StartsWith("Managing the context of an agent", sent.Text);
        Assert.True(PassageSelection.QuoteFound(relevant, sent.Text));
        Assert.True(sent.Text.Length < 9_000, $"{sent.Text.Length} characters sent");
        Assert.Matches(@"^14 of \d+ passages of the page \([\d,]+ of [\d,]+ characters\)$", sent.Passages);
    }
}
