using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// One name per tool (#249): names of an open attribute merge only where the rules in code match, every merge is on
/// file, and a value that names one of the review's own sources is never counted.
/// </summary>
public class OpenNameMergerTests
{
    private const string Tools = "Tool or framework";

    private static MapVersion Map() => new()
    {
        Number = 1,
        By = "reviewer",
        Attributes =
        {
            new MapAttribute { Name = "Practice", Question = "RQ1", Values = { "Retrieval", "Other" }, Multiple = true },
            new MapAttribute { Name = Tools, Question = "RQ1.1", Open = true, Multiple = true },
        },
    };

    private static GreyExtraction Source(string title, params string[] tools) => new()
    {
        Address = title.ToLowerInvariant(),
        Title = title,
        Attributes =
        {
            new ExtractedAttribute { Attribute = "Practice" },
            new ExtractedAttribute { Attribute = Tools, Values = tools.Select(t => new ExtractedValue { Value = t, Quote = "q " + t }).ToList() },
        },
    };

    private static MultivocalExtractionFile Run(params GreyExtraction[] sources)
    {
        var file = new MultivocalExtractionFile();
        file.Sources.AddRange(sources);
        OpenNameMerger.Apply(file, Map(), MultivocalPlan.Example());
        return file;
    }

    [Fact]
    public void TheTwoPairsFromTheLiveRunMergeAndAreOnFile()
    {
        var file = Run(
            Source("A blog post on agents", "MemGPT", "Agent-Skills-for-Context-Engineering"),
            Source("A talk transcript", "Letta (MemGPT)", "Agent Skills for Context Engineering"),
            Source("Another post", "MemGPT"));

        var counts = file.Counts(Tools).Where(c => c.Value != "not stated").ToList();
        Assert.Equal(2, counts.Count);
        Assert.Contains(("MemGPT", 3), counts);
        Assert.Contains(("Agent Skills for Context Engineering", 2), counts);
        var memgpt = Assert.Single(file.NameMerges, m => m.Name == "MemGPT");
        Assert.Equal(new[] { "Letta (MemGPT)" }, memgpt.Merged);
        Assert.Equal(Tools, memgpt.Attribute);
        Assert.Equal(new[] { "Agent-Skills-for-Context-Engineering" }, file.NameMerges.Single(m => m.Name != "MemGPT").Merged);
    }

    [Fact]
    public void ToolsThatOnlyShareAWordStayApart()
    {
        var file = Run(Source("Post one", "Cursor"), Source("Post two", "Cursor Rules"));

        Assert.Empty(file.NameMerges);
        Assert.Equal(new[] { "Cursor", "Cursor Rules" }, file.Counts(Tools).Select(c => c.Value).Where(v => v != "not stated").OrderBy(v => v));
    }

    [Theory]
    [InlineData("LangGraph", "langgraph", true)]
    [InlineData("Lang Graph", "lang-graph", true)]
    [InlineData("Letta (MemGPT)", "Letta", true)]
    [InlineData("Agent", "Agents", false)]
    [InlineData("GPT-4", "GPT-4o", false)]
    public void TheMatchingRules(string a, string b, bool match) =>
        Assert.Equal(match, OpenNameMerger.Keys(a).Intersect(OpenNameMerger.Keys(b)).Any());

    [Fact]
    public void ASourceOfTheReviewIsNeverCountedAsATool()
    {
        var file = Run(
            Source("muratcankoylan/Agent-Skills-for-Context-Engineering", "LangGraph"),
            Source("A blog post", "Agent Skills for Context Engineering", "LangGraph"));

        Assert.DoesNotContain(file.Counts(Tools), c => c.Value.Contains("Agent Skills", StringComparison.OrdinalIgnoreCase));
        var left = Assert.Single(file.OwnSourceValues);
        Assert.Equal("Agent Skills for Context Engineering", left.Value);
        Assert.Equal("muratcankoylan/Agent-Skills-for-Context-Engineering", left.Source);
        Assert.Single(file.Sources[1].Attributes.Single(a => a.Attribute == Tools).OwnSource);
    }

    [Fact]
    public void ASourceThatNamedATwiceKeepsItOnceUnderTheLongerSpellingAndCoverageIsWorkedOutAgain()
    {
        var file = Run(Source("Post one", "MemGPT", "Letta (MemGPT)"), Source("Post two", "Post one"));

        Assert.Equal(new[] { "Letta (MemGPT)" }, file.Sources[0].Attributes.Single(a => a.Attribute == Tools).Values.Select(v => v.Value));
        Assert.Contains("RQ1.1", file.Sources[0].Coverage);
        Assert.DoesNotContain("RQ1.1", file.Sources[1].Coverage); // its only tool was another source's own name
    }

    [Fact]
    public void ClosedAttributesAreLeftAlone()
    {
        var source = Source("Post", "X");
        source.Attributes[0].Values.Add(new ExtractedValue { Value = "Retrieval", Quote = "q" });
        var file = Run(source, Source("Retrieval"));

        Assert.Equal("Retrieval", Assert.Single(file.Sources[0].Attributes[0].Values).Value);
        Assert.Empty(file.OwnSourceValues);
    }

    [Fact]
    public void TheReaderIsToldWhatWasMergedAndLeftOut()
    {
        var file = Run(
            Source("owner/LangGraph", "MemGPT"),
            Source("A blog post", "Letta (MemGPT)", "LangGraph"),
            Source("A talk", "MemGPT"));

        Assert.Equal(
            "Names merged in code as the same tool or thing: \"MemGPT\" for \"Letta (MemGPT)\". " +
            "Not counted, because they name one of the review's own sources: \"LangGraph\" (owner/LangGraph).",
            OpenNameMerger.Summary(file, Tools));
        Assert.Null(OpenNameMerger.Summary(file, "Practice"));
        Assert.Null(OpenNameMerger.Summary(new MultivocalExtractionFile()));
    }
}
