using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// What sits in the paper's body on the Review output page and what sits beside or under it (Lau, 27 November 2026).
/// Issue #735: the self-check is about the tool, not part of the paper, so its table is under the report, not after the
/// front page; Article info keeps its one line.
/// </summary>
public class ReportBodyTests
{
    [Fact]
    public void TheSelfCheckIsUnderTheReportNotInItsBody()
    {
        string? page = ReportPageSource.Read();
        string? header = PageSource.Read("SpecMatrixHeader");
        if (page == null || header == null) return; // not run from the repository (packaged tests)

        Assert.DoesNotContain("<SelfCheckTable", header);
        int table = page.IndexOf("<SelfCheckTable Result=\"selfCheck\"", StringComparison.Ordinal);
        Assert.True(table > page.LastIndexOf("</article>", StringComparison.Ordinal), "the self-check table comes after the paper");
    }
}
