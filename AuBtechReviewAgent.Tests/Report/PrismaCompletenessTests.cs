using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The systematic review's main.tex is complete against PRISMA 2020 (issue #552): a whole offline run has the flow
/// diagram's retrieval and eligibility boxes, the statement on what the tool does not assess, the search strategy per
/// source, and ends with the checklist, whose every named table exists in the paper.
/// </summary>
public class PrismaCompletenessTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "prisma-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    [Fact]
    public async Task AWholeRunWritesEveryPrismaPartInCode()
    {
        string tex = await ReviewPaperTests.SystematicMainTexAsync(_root);

        Assert.Contains("Reports sought for retrieval (n = 2)", tex);
        Assert.Contains("Reports assessed for eligibility (n = 2)", tex);
        Assert.Contains("Reports excluded (n = 0)", tex);
        Assert.Contains(@"\subsection{What This Review Did Not Assess}", tex);
        Assert.Contains(@"\caption{Search strategy per source}", tex);
        Assert.Contains("& agent loops &", tex);
        Assert.Contains(@"\caption{PRISMA 2020 checklist}", tex);
    }

    [Fact]
    public async Task TheChecklistEndsThePaperHasEveryItemAndNamesOnlyTablesThatExist()
    {
        string tex = await ReviewPaperTests.SystematicMainTexAsync(_root);

        int checklist = tex.IndexOf(@"\caption{PRISMA 2020 checklist}", StringComparison.Ordinal);
        Assert.True(checklist > tex.IndexOf(@"\section*{Appendix}", StringComparison.Ordinal));
        Assert.True(checklist > tex.IndexOf("Statements on the Use of AI for Submission", StringComparison.Ordinal));
        Assert.DoesNotContain(@"\caption", tex[(checklist + 10)..]);

        string table = tex[checklist..tex.IndexOf(@"\end{tabularx}", checklist, StringComparison.Ordinal)];
        var items = Regex.Matches(table, @"^(\d+[a-f]?) & ", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(42, items.Count);
        Assert.Equal(new[] { "1", "2", "3" }, items.Take(3));
        Assert.Equal("27", items[^1]);

        foreach (Match reference in Regex.Matches(table, @"\\ref\{([^}]+)\}"))
            Assert.Contains(@"\label{" + reference.Groups[1].Value + "}", tex);
        // Nothing the run did not do is claimed: the statistical items are all marked as not done.
        foreach (string item in new[] { "12", "13e", "14", "20b", "20c", "21" })
            Assert.Matches($@"(?m)^{item} & .* & Not done by the tool: ", table);
    }
}
