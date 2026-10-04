using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Answers the model really gives (seen in a run's log) are still read correctly, and full texts are looked
/// for where they can actually be found.
/// </summary>
public class ModelOutputAndFullTextTests
{
    private sealed class Section
    {
        public string Heading { get; set; } = "";
        public string Text { get; set; } = "";
    }

    [Fact]
    public async Task RealLineBreaksInsideAJsonStringAreRead()
    {
        // '0x0A' is invalid within a JSON string: the model wrote paragraphs with real line breaks.
        string answer = "{\"heading\": \"Human oversight\", \"text\": \"First paragraph [1].\n\nSecond paragraph\twith a tab [2].\"}";
        var chat = new FakeChatService().Returns(answer);

        var section = await LlmJson.GetAsync<Section>(chat, "Write.", null, s => s.Text.Length > 0 ? null : "empty", repairAttempts: 0);

        Assert.Equal("First paragraph [1].\n\nSecond paragraph\twith a tab [2].", section.Text);
    }

    [Fact]
    public async Task OnlyTheFirstOfTwoObjectsIsRead()
    {
        // '{' is invalid after a single JSON value: the model sent two objects one after the other.
        var chat = new FakeChatService().Returns("{\"heading\": \"A\", \"text\": \"one\"}\n{\"heading\": \"B\", \"text\": \"two\"}");

        var section = await LlmJson.GetAsync<Section>(chat, "Write.", null, _ => null, repairAttempts: 0);

        Assert.Equal("A", section.Heading);
    }

    [Theory]
    [InlineData("```json\n{\"a\": 1}\n```", "{\"a\": 1}")]
    [InlineData("Here you go: {\"a\": \"x}y{z\"} Thanks!", "{\"a\": \"x}y{z\"}")]
    [InlineData("{\"a\": \"she said \\\"hi\\\" {\"}", "{\"a\": \"she said \\\"hi\\\" {\"}")]
    [InlineData("{\"a\": {\"b\": [1, {\"c\": 2}]}} trailing", "{\"a\": {\"b\": [1, {\"c\": 2}]}}")]
    [InlineData("no json here", "no json here")]
    public void ExtractObjectFindsTheFirstCompleteObject(string raw, string expected)
    {
        Assert.Equal(expected, LlmJson.ExtractObject(raw));
    }

    [Fact]
    public void ACutOffAnswerIsStillReportedAsInvalid()
    {
        string extracted = LlmJson.ExtractObject("{\"text\": \"the answer stops here");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonDocument.Parse(extracted));
    }

    [Theory]
    [InlineData("10.5281/zenodo.18448272", false)]
    [InlineData("10.48550/arXiv.2401.00001", false)]
    [InlineData("10.6084/m9.figshare.123", false)]
    [InlineData("10.1145/3597503.3639187", true)]
    public void UnpaywallIsOnlyAskedAboutCrossrefDois(string doi, bool asked)
    {
        Assert.Equal(asked, DocumentRAGUtility.UnpaywallKnows(doi));
    }

    [Theory]
    [InlineData("10.5281/zenodo.18448272", "18448272")]
    [InlineData("10.5281/ZENODO.42", "42")]
    [InlineData("10.1145/3597503.3639187", null)]
    [InlineData(null, null)]
    public void ZenodoRecordNumbersAreReadFromTheDoi(string? doi, string? record)
    {
        Assert.Equal(record, DocumentRAGUtility.ZenodoRecordId(doi));
    }

    [Fact]
    public void ZenodoPdfIsTakenOnlyFromOpenRecords()
    {
        string open = """
            {"metadata": {"access_right": "open"},
             "files": [{"key": "data.csv", "links": {"self": "https://zenodo.org/api/records/1/files/data.csv/content"}},
                       {"key": "paper.PDF", "links": {"self": "https://zenodo.org/api/records/1/files/paper.PDF/content"}}]}
            """;
        string restricted = """{"metadata": {"access_right": "restricted"}, "files": [{"key": "paper.pdf", "links": {"self": "https://x/p.pdf"}}]}""";

        Assert.Equal("https://zenodo.org/api/records/1/files/paper.PDF/content", DocumentRAGUtility.ParseZenodoPdfUrl(open));
        Assert.Null(DocumentRAGUtility.ParseZenodoPdfUrl(restricted));
        Assert.Null(DocumentRAGUtility.ParseZenodoPdfUrl("""{"metadata": {"access_right": "open"}, "files": []}"""));
    }
}
