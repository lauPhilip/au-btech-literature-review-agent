using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The answers of Crossref, OpenAlex and arXiv, in the shapes their APIs document, read into reference parts.</summary>
public class ReferenceAnswersTests
{
    internal const string CrossrefJson = """
        {"status":"ok","message-type":"work","message":{"DOI":"10.1145/3796507","type":"journal-article",
         "title":["Large Language Model-Based Agents for Software Engineering: A Survey"],
         "author":[{"given":"Junwei","family":"Liu","sequence":"first"},{"given":"Kaixin","family":"Wang","sequence":"additional"},{"name":"SE Agents Consortium","sequence":"additional"}],
         "container-title":["ACM Transactions on Software Engineering and Methodology"],"volume":"35","issue":"2","page":"1-79","publisher":"Association for Computing Machinery (ACM)"}}
        """;

    internal const string OpenAlexJson = """
        {"id":"https://openalex.org/W7160705205","title":"AGENTIC AI FRAMEWORKS FOR AUTONOMOUS AUTOMOTIVE SOFTWARE VALIDATION","type":"article",
         "authorships":[{"author":{"display_name":"Abhishek Devgan"}}],
         "primary_location":{"source":{"display_name":"International Journal of Engineering Technology Research & Management","type":"journal","host_organization_name":"IJETRM"}},
         "biblio":{"volume":"9","issue":"5","first_page":"12","last_page":"30"}}
        """;

    internal const string ArxivXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom"><entry><id>http://arxiv.org/abs/2607.02703v1</id>
        <title>LLMoxie: Exploring Agentic AI for
          Scientific Software Development</title>
        <author><name>Landung Setiawan</name></author><author><name>Anant Mittal</name></author></entry></feed>
        """;

    [Fact]
    public void CrossrefGivesNamesFromTheirPartsAndTheJournalsVolumeIssueAndPages()
    {
        var a = ReferenceAnswers.FromCrossref(CrossrefJson)!;

        Assert.Equal(new[] { "Liu, Junwei", "Wang, Kaixin", "SE Agents Consortium" }, a.Authors);
        Assert.Equal("ACM Transactions on Software Engineering and Methodology", a.Venue);
        Assert.Equal(("35", "2", "1–79"), (a.Details.Volume, a.Details.Issue, a.Details.Pages));
        Assert.Null(a.Details.Publisher); // printed for proceedings only
        Assert.Equal("Liu, J., Wang, K., & SE Agents Consortium", ApaCitationBuilder.FormatAuthorList(a.Authors));
    }

    [Fact]
    public void CrossrefGivesThePublisherOfAProceedingsPaper()
    {
        string json = """{"message":{"type":"proceedings-article","title":["T"],"container-title":["2025 ASEW Proceedings"],"page":"421-424","publisher":"IEEE"}}""";

        Assert.Equal("IEEE", ReferenceAnswers.FromCrossref(json)!.Details.Publisher);
    }

    [Fact]
    public void OpenAlexGivesTheVenueAndTheBiblio()
    {
        var a = ReferenceAnswers.FromOpenAlex(OpenAlexJson)!;

        Assert.Equal(new[] { "Abhishek Devgan" }, a.Authors);
        Assert.False(a.StructuredNames);
        Assert.Equal("International Journal of Engineering Technology Research & Management", a.Venue);
        Assert.Equal(("9", "5", "12–30"), (a.Details.Volume, a.Details.Issue, a.Details.Pages));
    }

    [Fact]
    public void ArxivGivesTheAuthorsAndTheNumber()
    {
        var a = ReferenceAnswers.FromArxiv(ArxivXml)!;

        Assert.Equal(new[] { "Landung Setiawan", "Anant Mittal" }, a.Authors);
        Assert.Equal("LLMoxie: Exploring Agentic AI for Scientific Software Development", a.Title);
        Assert.Equal("2607.02703", a.Details.ArxivId);
    }

    [Theory]
    [InlineData("Agent loops: a survey", "AGENT LOOPS: A SURVEY", true)]
    [InlineData("Large language model based agents for software engineering", "Large Language Model-Based Agents for Software Engineering: A Survey", true)]
    [InlineData("Agent loops", "Crop yields in wheat", false)]
    public void TitlesMatchWhenTheyNameTheSameWork(string a, string b, bool same) =>
        Assert.Equal(same, ReferenceAnswers.SameTitle(a, b));
}
