using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #447: the front matter's facts say how many included studies were placed in no theme, and an abstract that
/// describes the themes or the included studies may not leave that number out.
/// </summary>
public class AbstractUnplacedTests
{
    [Fact]
    public void AnAbstractThatDescribesTheThemesMustGiveTheStudiesInNoTheme()
    {
        const string hides = "We included 13 studies and grouped them into four themes.";
        Assert.Contains("not the 2 placed in no theme", PrismaReviewEngine.AbstractUnplacedProblem(hides, 13, 2));
        Assert.Contains("not the 2 placed in no theme", PrismaReviewEngine.AbstractUnplacedProblem("The themes show that recovery comes first.", 13, 2));
        Assert.Null(PrismaReviewEngine.AbstractUnplacedProblem("We included 13 studies; 11 formed four themes and 2 were placed in no theme.", 13, 2));
        Assert.Null(PrismaReviewEngine.AbstractUnplacedProblem("We included 13 studies; two fitted no theme.", 13, 2));
    }

    [Fact]
    public void WithEveryStudyInAThemeOrNoWordOnThemesTheAbstractIsLeftAlone()
    {
        Assert.Null(PrismaReviewEngine.AbstractUnplacedProblem("We included 13 studies and grouped them into four themes.", 13, 0));
        Assert.Null(PrismaReviewEngine.AbstractUnplacedProblem("Recovery comes first in safe agent loops.", 13, 2));
    }

    [Fact]
    public void TheFactLineGivesTheNumber() =>
        Assert.StartsWith("Studies placed in no theme: 3 ", PrismaReviewEngine.UnplacedFact(3));
}
