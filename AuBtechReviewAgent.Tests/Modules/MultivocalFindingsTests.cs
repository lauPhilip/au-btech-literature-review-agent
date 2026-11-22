using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// How a multivocal run's extracted values become findings (MultivocalSynthesiser.Findings): numbered by source,
/// tied to the map attribute's research question and labelled in code with the source's kind, outlet tier and points.
/// </summary>
public class MultivocalFindingsTests
{
    private static GreyRecord Record(string url, string kind) =>
        new("id", "Title", "", url, "", "example.org", kind, null, new Dictionary<string, long>());

    [Fact]
    public void EveryValueBecomesAFindingNumberedBySourceAndLabelledInCode()
    {
        var map = new MapVersion
        {
            Number = 1,
            Attributes = new()
            {
                new MapAttribute { Name = "Practice", Question = "RQ1" },
                new MapAttribute { Name = "Tool", Question = "RQ2" },
            },
        };
        var extraction = new MultivocalExtractionFile
        {
            Sources = new()
            {
                new GreyExtraction
                {
                    Address = "a.example/post", Title = "A post",
                    Attributes = new()
                    {
                        new ExtractedAttribute { Attribute = "Practice", Values = new() { new() { Value = "Summarise", Quote = "we summarise" }, new() { Value = "Prune", Quote = "we prune" } } },
                        new ExtractedAttribute { Attribute = "Tool", Values = new() { new() { Value = "LangChain", Quote = "with LangChain" } } },
                    },
                },
                new GreyExtraction { Address = "broken.example", Error = "The page could not be read." },
                new GreyExtraction
                {
                    Address = "b.example/report", Title = "A report",
                    Attributes = new()
                    {
                        new ExtractedAttribute { Attribute = "Unknown", Values = new() { new() { Value = "Something", Quote = "something" } } },
                        new ExtractedAttribute { Attribute = "Practice" },
                    },
                },
            },
        };
        var records = new Dictionary<string, GreyRecord>
        {
            ["a.example/post"] = Record("https://a.example/post", "blogs"),
            ["b.example/report"] = Record("https://b.example/report", "white-papers"),
        };
        var points = new Dictionary<string, double> { ["a.example/post"] = 11.5 };

        var findings = MultivocalSynthesiser.Findings(extraction, map, records, points);

        Assert.Equal(new[] { "F1.1", "F1.2", "F1.3", "F2.1" }, findings.Select(f => f.Id));
        Assert.Equal(new[] { "RQ1", "RQ1", "RQ2", "" }, findings.Select(f => f.Question));
        Assert.Equal(new[] { "Summarise", "Prune", "LangChain", "Something" }, findings.Select(f => f.Value));
        Assert.Equal("we prune", findings[1].Quote);
        Assert.Equal("A post", findings[0].Title);
        Assert.All(findings.Take(3), f => Assert.Equal("blogs", f.Kind));
        Assert.All(findings.Take(3), f => Assert.Equal(3, f.Tier));
        Assert.All(findings.Take(3), f => Assert.Equal(11.5, f.QualityPoints));
        Assert.Equal("white-papers", findings[3].Kind);
        Assert.Equal(1, findings[3].Tier);
        Assert.Equal(0, findings[3].QualityPoints);
        Assert.All(findings, f => Assert.Equal("grey", f.Literature));
        Assert.All(findings, f => Assert.Equal("page text", f.EvidenceBasis));
    }

    [Fact]
    public void ASourceWithoutARecordHasNoKindAndTheThirdTier()
    {
        var map = new MapVersion { Attributes = new() { new MapAttribute { Name = "Practice", Question = "RQ1" } } };
        var extraction = new MultivocalExtractionFile
        {
            Sources = new() { new GreyExtraction { Address = "x.example", Attributes = new() { new ExtractedAttribute { Attribute = "Practice", Values = new() { new() { Value = "Cache", Quote = "cache" } } } } } },
        };

        var finding = Assert.Single(MultivocalSynthesiser.Findings(extraction, map, new Dictionary<string, GreyRecord>(), new Dictionary<string, double>()));

        Assert.Equal("", finding.Kind);
        Assert.Equal(3, finding.Tier);
        Assert.Equal("x.example", finding.Address);
    }

    [Theory]
    [InlineData("theses", 1)]
    [InlineData("documentation", 2)]
    [InlineData("qa", 2)]
    [InlineData("code", 3)]
    public void TheTierFollowsTheOutletOfTheKind(string kind, int tier) =>
        Assert.Equal(tier, MultivocalSynthesiser.TierOf(kind));
}
