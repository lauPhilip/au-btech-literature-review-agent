using System.Reflection;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The two databases that need a key, Scopus (ElsevierSource) and IEEE Xplore, tested on stored answers in the documented
/// formats: their raw answers are kept like every other database's (issue #469), and Scopus's first author, author list
/// and year are read as a reference needs them (issue #470).
/// </summary>
public class KeyedSourceTests
{
    private static readonly DateTime SearchDay = new(2026, 11, 23);

    [Fact]
    public void EveryAcademicSourceKeepsItsRawAnswers()
    {
        var sources = typeof(IAcademicSource).Assembly.GetTypes()
            .Where(t => typeof(IAcademicSource).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToList();

        Assert.Contains(typeof(ElsevierSource), sources);
        Assert.Contains(typeof(IeeeXploreSource), sources);
        // The interface's default is an empty list: a source that does not declare its own saves nothing.
        var missing = sources.Where(t => t.GetProperty(nameof(IAcademicSource.LastRawResponses), BindingFlags.Public | BindingFlags.Instance) == null)
            .Select(t => t.Name).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void ScopusStandardViewGivesTheFirstAuthorAndMarksTheListAsPartial()
    {
        const string json = """
        {"search-results":{"opensearch:totalResults":"2","entry":[
          {"dc:identifier":"SCOPUS_ID:105055666438","dc:title":"SMARF: A semantic memory compression framework","dc:creator":"Liu J.",
           "prism:publicationName":"Reliability Engineering and System Safety","prism:coverDate":"2027-03-01",
           "prism:coverDisplayDate":"March 2027","prism:doi":"10.1016/j.ress.2026.113770","subtypeDescription":"Article"},
          {"dc:identifier":"SCOPUS_ID:2","dc:title":"Older work","dc:creator":"Ab C.","prism:coverDate":"2024-05-01",
           "author":[{"authname":"Ab C.","surname":"Ab","given-name":"Carla"},{"authname":"De F.","surname":"De","given-name":"Finn"}]}
        ]}}
        """;
        var papers = ElsevierSource.Parse(json, SearchDay);

        Assert.Equal(new[] { "Liu J.", ApaCitationBuilder.EtAl }, papers[0].Authors);
        Assert.Equal("Published: 2026", papers[0].PublishedDate);
        Assert.Equal("Liu, J., et al. (2026). SMARF: A semantic memory compression framework. *Reliability Engineering and System Safety*. https://doi.org/10.1016/j.ress.2026.113770",
            ApaCitationBuilder.Build(papers[0]).Citation);
        Assert.Equal(new[] { "Ab, Carla", "De, Finn" }, papers[1].Authors);
        Assert.Equal("Published: 2024", papers[1].PublishedDate);
    }

    [Theory]
    [InlineData("2025-06-01", "10.1016/j.ress.2025.1", "2025")]
    [InlineData("2027-03-01", "10.1016/j.ress.2026.113770", "2026")]
    [InlineData("2027-03-01", "10.1109/ACCESS.2026.1", "in press")]
    [InlineData("2027-03-01", "10.1016/j.ress.2027.1", "in press")]
    [InlineData("2027", null, "2027")]
    [InlineData("", null, "N/A")]
    public void AnIssueDatedAfterTheSearchIsCitedByItsOnlineYear(string coverDate, string? doi, string expected) =>
        Assert.Equal(expected, ElsevierSource.Year(coverDate, doi, SearchDay));

    [Fact]
    public void AnArticleInPressWithoutAnOnlineYearIsCitedWithoutAYear()
    {
        var paper = new AcademicPaper("SCOPUS_ID:3", "In press", "", $"Published: {ElsevierSource.Year("2027-01-01", null, SearchDay)}", new() { "Ab C." }, "Journal");

        Assert.Contains("(n.d.)", ApaCitationBuilder.Build(paper).Citation);
    }

    [Fact]
    public void IeeeXploreAnswersAreParsedWithEveryAuthor()
    {
        const string json = """
        {"total_records":1,"articles":[{"article_number":"10123456","title":"Agents in practice","abstract":"We study agents.",
          "publication_title":"IEEE Access","publication_year":2025,"doi":"10.1109/ACCESS.2025.1",
          "authors":{"authors":[{"full_name":"Jane Doe"},{"full_name":"Kim Lee"}]}}]}
        """;
        var paper = Assert.Single(IeeeXploreSource.Parse(json));

        Assert.Equal("IEEE_10123456", paper.Id);
        Assert.Equal(new[] { "Jane Doe", "Kim Lee" }, paper.Authors);
        Assert.Equal("Published: 2025", paper.PublishedDate);
    }
}
