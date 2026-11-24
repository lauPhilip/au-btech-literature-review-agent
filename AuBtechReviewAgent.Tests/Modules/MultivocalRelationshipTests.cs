using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issues #458 and #459, from the real multivocal run 50e9a8e3. RQ3 asked whether using retrieval is related to fewer
/// failures, and was answered with every finding that reported fewer failures, whether or not retrieval was used; now a
/// relationship is answered in code by pairing its two sides per source, or said in code to be unanswerable. RQ2 asked
/// how often each practice is named in grey and in academic sources, and its table did not split the pools; now it does.
/// </summary>
public class MultivocalRelationshipTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrrelation-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static SynthesisFinding F(string id, string attribute, string value, string address) =>
        new() { Id = id, Question = "RQ3", Attribute = attribute, Value = value, Quote = "q", Address = address, Kind = "blogs", Tier = 3 };

    /// <summary>The paper tests' run with RQ3 served by the attributes given and their findings.</summary>
    private static MultivocalReportInput WithRelationship(IEnumerable<MapAttribute> attributes, IEnumerable<SynthesisFinding> findings)
    {
        var input = MultivocalPaperTests.Input();
        input.Map.Versions[0].Attributes.AddRange(attributes);
        input.Synthesis.Findings.AddRange(findings);
        return input;
    }

    private static MapAttribute Used => new() { Name = "Retrieval used", Question = "RQ3", Values = { "Yes", "No" } };
    private static MapAttribute Outcome => new() { Name = "Reported failure outcome", Question = "RQ3", Values = { "Fewer failures", "No change" } };

    [Fact]
    public void ARelationshipWithOneSideIsSaidInCodeToBeUnanswerable()
    {
        // As in the real run: the map recorded only the outcome.
        var input = WithRelationship(new[] { Outcome }, new[] { F("F1.9", "Reported failure outcome", "Fewer failures", "blog.example.org/a") });

        Assert.True(MultivocalPaperParts.AnsweredInCode("relationship"));
        string lead = MultivocalPaperParts.CodeAnswerLead(input, "RQ3", input.Planned.Plan.Numbered().Single(q => q.Number == "RQ3").Question);
        Assert.StartsWith("The systematic map records only \"Reported failure outcome\" for this question", lead);
        Assert.Empty(MultivocalPaperParts.CodeAnswerTables(input, "RQ3"));
        Assert.Equal(lead, MultivocalPaperParts.CodeAnswerSummary(input, "RQ3"));
        Assert.Contains("RQ3 asks about a relationship that the sources do not allow to be answered", MultivocalFrontMatter.Facts(input));

        // The abstract may not claim it, but may say it could not be answered; a sentence on the topic alone is not a claim.
        Assert.Contains("RQ3", MultivocalRelationship.AbstractProblem("The review maps practices. Retrieval generally reduces failures.", input));
        Assert.Null(MultivocalRelationship.AbstractProblem("Whether retrieval reduces failures could not be answered from the sources.", input));
        Assert.Null(MultivocalRelationship.AbstractProblem("Context engineering improves how LLM agents use their context window.", input));
    }

    [Fact]
    public void ARelationshipWithBothSidesIsAnsweredByPairingThemPerSource()
    {
        var input = WithRelationship(new[] { Used, Outcome }, new[]
        {
            F("F1.8", "Retrieval used", "No", "blog.example.org/a"), F("F1.9", "Reported failure outcome", "Fewer failures", "blog.example.org/a"),
            F("F2.8", "Retrieval used", "Yes", "docs.acme.dev/b"), F("F2.9", "Reported failure outcome", "Fewer failures", "docs.acme.dev/b"),
            F("F3.9", "Reported failure outcome", "No change", "forum.example.org/c"), // the outcome alone: not paired
        });

        var pairing = MultivocalRelationship.Pair(input, "RQ3");
        Assert.True(pairing.Answerable);
        Assert.Equal(new[] { 1, 2 }, pairing.Both);
        var table = Assert.Single(MultivocalPaperParts.CodeAnswerTables(input, "RQ3"));
        Assert.Equal(new[] { "Retrieval used", "Reported failure outcome", "Sources", "Cited" }, table.Headers);
        Assert.Equal(new[] { "No|Fewer failures|1|[1]", "Yes|Fewer failures|1|[2]" }, table.Rows.Select(r => string.Join("|", r)));
        Assert.Contains("2 sources of the 3 extracted sources state both", MultivocalRelationship.Lead(input, "RQ3"));
        Assert.Contains("not that one causes the other", MultivocalRelationship.Lead(input, "RQ3"));
        Assert.Contains("\"No\" with \"Fewer failures\" (1 source)", MultivocalPaperParts.CodeAnswerSummary(input, "RQ3"));
        Assert.Null(MultivocalRelationship.AbstractProblem("Retrieval generally reduces failures.", input));
    }

    [Fact]
    public void ARelationshipNoSourceReportsBothSidesOfIsUnanswerable()
    {
        var input = WithRelationship(new[] { Used, Outcome }, new[]
        {
            F("F1.8", "Retrieval used", "Yes", "blog.example.org/a"),
            F("F3.9", "Reported failure outcome", "Fewer failures", "forum.example.org/c"),
        });

        Assert.False(MultivocalRelationship.Pair(input, "RQ3").Answerable);
        Assert.StartsWith("No extracted source states both a value of \"Retrieval used\" and a value of \"Reported failure outcome\"", MultivocalRelationship.Lead(input, "RQ3"));
        Assert.Empty(MultivocalPaperParts.CodeAnswerTables(input, "RQ3"));
    }

    [Fact]
    public void APaperWithThemesForARelationshipShowsTheAnswerInCodeOnce()
    {
        // The real run's paper file held three theme sections for RQ3 grouped by outcome.
        var input = WithRelationship(new[] { Outcome }, Array.Empty<SynthesisFinding>());
        var paper = new MultivocalPaperFile
        {
            Results =
            {
                new PaperResult { Question = "RQ1", Kind = "theme", Field = "synthesis-1", Heading = "Short contexts", Text = "Text [1]." },
                new PaperResult { Question = "RQ3", Kind = "theme", Field = "synthesis-2", Heading = "Retrieval reduces failures", Text = "Fewer failures [2]." },
                new PaperResult { Question = "RQ3", Kind = "theme", Field = "synthesis-3", Heading = "Retrieval increases failures", Text = "More failures [1]." },
            },
        };

        var shown = MultivocalPaper.ShownResults(input, paper);

        Assert.Equal(new[] { ("RQ1", "theme"), ("RQ3", "code") }, shown.Select(r => (r.Question, r.Kind)));
        Assert.StartsWith("RQ3: Is the use of retrieval", shown[1].Heading);
        Assert.Contains("do not allow the relationship to be answered", shown[1].Text);
    }

    [Fact]
    public void TheModelsMapMustGiveARelationshipBothSides()
    {
        var plan = MultivocalPlan.Example("grey");
        var practice = new MapAttribute { Name = "Practice", Question = "RQ1", Values = { "Retrieval", "Summarisation" } };
        var tools = new MapAttribute { Name = "Tool", Question = "RQ1.1", Open = true };
        var how = new MapAttribute { Name = "Approach", Question = "RQ1.2", Values = { "Local", "Cloud" } };

        string? oneSide = MultivocalMapper.ModelProblem(new[] { practice, tools, how, Outcome }, plan);
        Assert.Contains("RQ3 asks whether two things are related", oneSide);
        Assert.Contains("\"alsoFor\"", oneSide);

        practice.AlsoFor.Add("RQ3"); // the practice attribute is the other side
        Assert.Null(MultivocalMapper.ModelProblem(new[] { practice, tools, how, Outcome }, plan));
        var unrelated = new MapAttribute { Name = "Practice", Question = "RQ1", Values = { "Retrieval", "Summarisation" } };
        Assert.Null(MultivocalMapper.ModelProblem(new[] { unrelated, tools, how }, plan)); // no side at all: said in the map's notes, as before
    }

    [Fact]
    public async Task TheCountsOfATwoPoolRunAreSplitByPoolInTheTableTheMapAndTheAnswer()
    {
        var run = await FormalKit.RunAsync(_root);
        var input = run.Reporter.LoadInput(run.RunId)!;
        var split = MultivocalPaperParts.Split(input)!;
        Assert.True(split.Grey > 0 && split.Academic > 0);
        var question = input.Map.Latest.Attributes[0].Question;

        var table = MultivocalPaperParts.CodeAnswerTables(input, question)[0];
        Assert.Equal(new[] { "Value", "Grey", "Academic", "Both", "Cited" }, table.Headers);
        foreach (var row in table.Rows)
        {
            int Count(string cell) => int.Parse(cell.Split(' ')[0]);
            Assert.Equal(Count(row[3]), Count(row[1]) + Count(row[2])); // each source is in one pool
        }
        Assert.Contains($"Each share is of its own pool: {split.Grey} grey and {split.Academic} academic sources were extracted", table.Note);
        Assert.Contains("counted apart", MultivocalPaperParts.CodeAnswerLead(input, question, new PlannedQuestion { Type = "frequency" }));

        var map = MultivocalPaperParts.MapCounts(input);
        Assert.Equal(new[] { "Attribute", "Question", "Value", "Grey", "Academic", "Both" }, map.Headers);
        Assert.All(map.Rows, r => Assert.Equal(int.Parse(r[5]), int.Parse(r[3]) + int.Parse(r[4])));

        string summary = MultivocalPaperParts.CodeAnswerSummary(input, question);
        Assert.Matches(@"\d+ grey \(\d+%\) and \d+ academic \(\d+%\)", summary);
        Assert.Contains($"each pool's own {split.Grey} grey and {split.Academic} academic sources", summary);

        // A grey literature review keeps its one column.
        var grey = MultivocalPaperTests.Input();
        Assert.Null(MultivocalPaperParts.Split(grey));
        Assert.Equal(new[] { "Value", "Sources", "Share", "Cited" }, MultivocalPaperParts.CodeAnswerTables(grey, "RQ2")[0].Headers);
    }
}
