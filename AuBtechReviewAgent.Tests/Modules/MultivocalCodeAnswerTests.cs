using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The multivocal paper's answers to frequency and existence questions, counted in code from the checked findings of
/// the small run in <see cref="MultivocalPaperTests"/>.
/// </summary>
public class MultivocalCodeAnswerTests
{
    private static MultivocalReportInput Input() => MultivocalPaperTests.Input();

    [Fact]
    public void FrequencyAndExistenceQuestionsAreAnsweredInCodeFromTheCheckedFindings()
    {
        var input = Input();
        var plan = input.Planned.Plan;
        Assert.True(MultivocalPaperParts.AnsweredInCode("frequency"));
        Assert.True(MultivocalPaperParts.AnsweredInCode("existence"));
        Assert.False(MultivocalPaperParts.AnsweredInCode("description"));

        var rq2 = plan.Numbered().Single(q => q.Number == "RQ2");
        var answer = MultivocalPaperParts.CodeAnswer(input, rq2.Number, rq2.Question);
        var table = Assert.Single(MultivocalPaperParts.CodeAnswerTables(input, "RQ2"));
        Assert.Equal(new[] { "Value", "Sources", "Share", "Cited" }, table.Headers);
        Assert.Equal(new[] { "Summarisation", "2", "67%", "[1, 2]" }, table.Rows[0]);
        Assert.Equal(new[] { "Retrieval", "1", "33%", "[2]" }, table.Rows[1]);
        Assert.Equal(new[] { "not stated", "1", "33%", "" }, table.Rows[2]); // the forum post states no practice
        Assert.StartsWith("RQ2: How often", answer.Heading);
        Assert.Contains("answered by counting, in code", answer.Text);
        Assert.Contains("A count says how many sources state a value, not how common it is in practice.", answer.Text);
        Assert.Contains(@"\begin{tabular}", answer.Latex);

        var tools = Assert.Single(MultivocalPaperParts.CodeAnswerTables(input, "RQ1.1")); // existence: the tools the sources name
        Assert.Equal(new[] { new[] { "LangChain", "1", "33%", "[3]" }, new[] { "LangGraph", "1", "33%", "[1]" }, new[] { "not stated", "1", "33%", "" } },
            tools.Rows.Select(r => r.ToArray()));
    }

    [Fact]
    public void AFrequencyQuestionCanBeAnsweredByCountingAnotherQuestionsAttribute()
    {
        var input = Input();
        var map = input.Map.Versions[0];
        map.Attributes.RemoveAll(a => a.Name == "Practice mentioned");
        map.Attributes.Single(a => a.Name == "Practice").AlsoFor.Add("RQ2"); // "how often is each practice mentioned?" counts RQ1's practices
        var rq2 = input.Planned.Plan.Numbered().Single(q => q.Number == "RQ2");

        var table = Assert.Single(MultivocalPaperParts.CodeAnswerTables(input, "RQ2"));

        Assert.Equal("Practice", table.Caption);
        Assert.Equal(new[] { "Summarisation", "2", "67%", "[1, 2]" }, table.Rows[0]);
        Assert.Contains("It counts the values of \"Practice\" (RQ1)", MultivocalPaperParts.CodeAnswerLead(input, "RQ2", rq2.Question));
        Assert.True(map.Attributes.Single(a => a.Name == "Practice").Serves("RQ2"));
        var cleaned = Assert.Single(MultivocalMapper.Clean(new[] { new MapAttribute { Name = "Practice", Question = "rq1", AlsoFor = { " rq2 ", "RQ1", "RQ2" }, Values = { "A", "B" } } }));
        Assert.Equal(new[] { "RQ2" }, cleaned.AlsoFor); // its own question and repeats are dropped
        Assert.NotNull(MultivocalMapper.Problem(new[] { new MapAttribute { Name = "Practice", Question = "RQ1", AlsoFor = { "RQ9" }, Values = { "A", "B" } } }, input.Planned.Plan));
    }

    [Fact]
    public void ALongListOfNamesEachFromOneSourceIsShownAsOneRow()
    {
        var counts = new List<(string, int, IReadOnlyList<int>)> { ("LangGraph", 2, new[] { 1, 2 }) };
        counts.AddRange(Enumerable.Range(1, MultivocalPaperParts.SingleSourceRows + 2).Select(i => ($"Tool {i}", 1, (IReadOnlyList<int>)new[] { i })));
        counts.Add(("not stated", 1, Array.Empty<int>()));

        var (shown, once) = MultivocalPaperParts.Compact(counts, open: true);

        Assert.Equal(new[] { "LangGraph", "not stated" }, shown.Select(c => c.Value));
        Assert.Equal(MultivocalPaperParts.SingleSourceRows + 2, once.Count);
        Assert.Equal(counts.Count, MultivocalPaperParts.Compact(counts, open: false).Shown.Count); // a closed attribute keeps every value
        var few = counts.Take(1 + MultivocalPaperParts.SingleSourceRows).ToList(); // as many names from one source as rows allow
        Assert.Empty(MultivocalPaperParts.Compact(few, open: true).NamedOnce);
    }

    [Fact]
    public void TheMapCountsGiveTheSourcesThatNameTheSingleNamesNotHowManyNamesThereAre()
    {
        // Seen in a live run: "94 more, each named by one source" had 94 in the Sources column, where 9 sources named them.
        var input = Input();
        for (int i = 1; i <= MultivocalPaperParts.SingleSourceRows + 2; i++)
            input.Synthesis.Findings.Add(new SynthesisFinding
            {
                Id = $"F9.{i}", Question = "RQ1.1", Attribute = "Tool", Value = $"Tool {i}", Quote = $"Tool {i}",
                Address = i % 2 == 0 ? "blog.example.org/a" : "docs.acme.dev/b", Kind = "blogs", Tier = 3, QualityPoints = 11,
            });

        var rows = MultivocalPaperParts.MapCounts(input).Rows.Where(r => r[0] == "Tool").ToList();

        var more = Assert.Single(rows, r => r[2].EndsWith("more, each named by one source"));
        int names = MultivocalPaperParts.SingleSourceRows + 4; // the added tools, LangGraph and LangChain
        Assert.Equal(new[] { "Tool", "RQ1.1", $"{names} more, each named by one source", "3" }, more);
        int notStated = rows.FindIndex(r => r[2] == "not stated");
        Assert.True(notStated < 0 || notStated > rows.IndexOf(more)); // "not stated" stays the last row
    }
}
