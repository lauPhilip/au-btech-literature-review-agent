using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The citation check as one sequence of counts, in the order the steps ran (issue #679): the figures add up from the
/// first check to the final text, and the paragraph says them in that order.
/// </summary>
public class CitationCheckStepsTests
{
    private static ReferencedPaper Page(int n, string text) =>
        new(n, $"web{n}", $"Paper {n}", "", new[] { new DocumentChunk($"web{n}", 1, text) });

    /// <summary>
    /// Four citations: [1] is supported at once; [2] is partly supported and the second check raises it; [3] and [4] stay
    /// not supported, [3] is deleted by the repair and [4] is rewritten, still not supported, and removed in the second pass.
    /// </summary>
    private static string Answer(string prompt)
    {
        if (prompt.Contains("You are correcting citations"))
            return """{"repairs":[{"id":1,"action":"delete","reason":"nothing backs it"},{"id":2,"action":"rewrite","sentence":"Agents have a cost [4].","reason":"narrowed"}]}""";
        int paper = int.Parse(Regex.Match(prompt, @"CITED PAPER \[(\d+)\]").Groups[1].Value);
        bool second = prompt.Contains("independent second reviewer");
        return paper switch
        {
            1 => """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"Loops recover from faults quickly","reason":"stated"}]}""",
            2 when !second => """{"results":[{"sentence":1,"verdict":"partially_supported","excerpt":"E1","quote":"Supervisors check every step","reason":"not every output"}]}""",
            2 => """{"results":[{"sentence":1,"verdict":"supported","excerpt":"E1","quote":"Supervisors check every step of the loop and every output","reason":"stated"}]}""",
            _ => """{"results":[{"sentence":1,"verdict":"not_supported","reason":"says something else"}]}""",
        };
    }

    [Fact]
    public async Task TheCountsOfARunAddUpFromTheFirstCheckToTheFinalText()
    {
        var chat = new FakeChatService().RespondsWith(Answer);
        var fields = new List<(string Field, string Text)>
        {
            ("synthesis", "Loops recover quickly [1]. Supervisors check every step and every output [2]. Agents never fail [3]. Agents halve the cost [4]."),
        };
        var sources = new[]
        {
            Page(1, "Loops recover from faults quickly in production."),
            Page(2, "Supervisors check every step of the loop and every output."),
            Page(3, "Agents fail in many ways."),
            Page(4, "Agents have a cost that depends on the model."),
        };

        var result = await Grounding.CheckAndRepairAsync(chat, fields, sources, new Dictionary<int, IReadOnlyList<string>>(), 4,
            new GroundingOptions(SecondCheck: true, Repair: true, SenseCheck: false));

        var steps = Assert.IsType<CitationCheckSteps>(result.Steps);
        Assert.Equal((4, 1, 1, 2), (steps.FirstChecked, steps.FirstSupported, steps.FirstPartiallySupported, steps.FirstNotSupported));
        Assert.Equal((3, 1), (steps.SecondChecked, steps.SecondRaised));
        Assert.Equal((true, 2, 1, 1, 1, 2), (steps.RepairRan, steps.ToRepair, steps.Rewritten, steps.Dropped, steps.Removed, steps.FinalChecked));
        // The first count, less what left the text, is the final count, and the final text holds exactly those citations.
        Assert.Equal(steps.FirstChecked - steps.Dropped - steps.Removed - steps.LostInSenseCheck, steps.FinalChecked);
        Assert.Equal(steps.FinalChecked, result.Checks.Count);
        Assert.Equal("Loops recover quickly [1]. Supervisors check every step and every output [2].", Assert.Single(result.Fields).Text);

        string text = steps.Paragraph(CitationSupportSummary.From(result.Checks, result.Removed.Count));
        Assert.Contains("The first check judged 4 citations: 1 was supported, 1 partly supported, 2 not supported, and 0 could not be verified.", text);
        Assert.Contains("The 3 that were not fully supported went through a second, independent check with more of the paper's text, which raised the verdict for 1 of them.", text);
        Assert.Contains("After both checks, 2 citations were still not fully supported and went to the repair.", text);
        Assert.Contains("The repair rewrote 1 cited sentence and dropped 1 citation, with the sentence when nothing else backed it, and the changed sentences were checked again.", text);
        Assert.Contains("1 citation that was still not supported after that was removed", text);
        Assert.Contains("So 2 of the 4 citations first checked left the text, and 2 remain.", text);
        Assert.Contains("Of the 2 citations in the final text, 2 were judged supported, 0 partly supported, 0 not supported, and 0 could not be verified.", text);
        Assert.True(text.IndexOf("first check judged", StringComparison.Ordinal) < text.IndexOf("second, independent check", StringComparison.Ordinal)
            && text.IndexOf("second, independent check", StringComparison.Ordinal) < text.IndexOf("went to the repair", StringComparison.Ordinal)
            && text.IndexOf("went to the repair", StringComparison.Ordinal) < text.IndexOf("in the final text", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEightyTwoCitationRunSaysEightyInTheFinalText()
    {
        var steps = new CitationCheckSteps
        {
            FirstChecked = 82, FirstSupported = 64, FirstPartiallySupported = 9, FirstNotSupported = 3, FirstUnverifiable = 6,
            SecondChecked = 12, SecondRaised = 5, ToRepair = 7, RepairRan = true, Rewritten = 5, Dropped = 1, Removed = 1, FinalChecked = 80,
        };
        var final = new CitationSupportSummary { Checked = 80, Supported = 68, PartiallySupported = 5, NotSupported = 0, Unverifiable = 7 };

        string text = steps.Paragraph(final);

        Assert.Equal(82, steps.FirstSupported + steps.FirstPartiallySupported + steps.FirstNotSupported + steps.FirstUnverifiable);
        Assert.Equal(80, final.Supported + final.PartiallySupported + final.NotSupported + final.Unverifiable);
        Assert.Equal(82 - steps.Dropped - steps.Removed, steps.FinalChecked);
        Assert.Contains("The first check judged 82 citations: 64 were supported, 9 partly supported, 3 not supported, and 6 could not be verified.", text);
        Assert.Contains("The 12 that were not fully supported went through a second, independent check", text);
        Assert.Contains("raised the verdict for 5 of them", text);
        Assert.Contains("After both checks, 7 citations were still not fully supported and went to the repair.", text);
        Assert.Contains("The repair rewrote 5 cited sentences and dropped 1 citation", text);
        Assert.Contains("1 citation that was still not supported after that was removed", text);
        Assert.Contains("So 2 of the 82 citations first checked left the text, and 80 remain.", text);
        Assert.Contains("Of the 80 citations in the final text, 68 were judged supported, 5 partly supported, 0 not supported, and 7 could not be verified.", text);
        Assert.DoesNotContain("Before this final count", text);
    }

    [Fact]
    public void ARunWithoutASecondCheckOrRepairSaysOnlyWhatRan()
    {
        var steps = new CitationCheckSteps { FirstChecked = 3, FirstSupported = 2, FirstPartiallySupported = 1, FinalChecked = 3 };
        string text = steps.Paragraph(new CitationSupportSummary { Checked = 3, Supported = 2, PartiallySupported = 1 });

        Assert.DoesNotContain("second, independent", text);
        Assert.DoesNotContain("went to the repair", text);
        Assert.DoesNotContain("left the text", text);
        Assert.Contains("Of the 3 citations in the final text, 2 were judged supported, 1 partly supported, 0 not supported, and 0 could not be verified.", text);
        Assert.Equal("No inline citations were available for the automated support check.", new CitationCheckSteps().Paragraph(new CitationSupportSummary()));
    }
}
