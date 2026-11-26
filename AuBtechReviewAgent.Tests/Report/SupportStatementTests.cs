using System.Runtime.CompilerServices;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The support statement printed under Support and Funding comes from Report:SupportStatement in appsettings.json
/// (issue #114). The site no longer sets one: the paper asks the authors for their funding and never prints advice on
/// configuration, and an old run that recorded a site-wide statement shows the placeholder (the publisher audit, batch A).
/// </summary>
public class SupportStatementTests
{
    private static string AppSettingsPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here)!, "..", "..", "AuBtechReviewAgent", "appsettings.json"));

    private static string Statement()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.TryGetProperty("Report", out var report) && report.TryGetProperty("SupportStatement", out var value) ? value.GetString() ?? "" : "";
    }

    [Fact]
    public void WithoutASupportStatementThePaperAsksTheAuthors()
    {
        Assert.Equal("To be completed by the authors.", PrismaReviewEngine.DefaultSupportStatement);
        Assert.DoesNotContain("configuration", PrismaReviewEngine.DefaultSupportStatement);
    }

    [Fact]
    public void TheSiteSetsNoSupportStatementForEveryonesPaper()
    {
        // Lau removed the site-wide statement: whoever runs a review declares their own funding (the publisher audit).
        Assert.Equal("", Statement());
    }

    [Fact]
    public void AnOldRunsSiteWideStatementIsShownAsThePlaceholder()
    {
        foreach (string old in PaperDeclarations.OldSiteStatements)
            Assert.Equal(PaperDeclarations.ToComplete, PaperDeclarations.Funding(old));
        // Only the exact old text is replaced; a statement the authors wrote themselves is kept, even if it names BTECH.
        string own = "This review was carried out at the Department of Business Development and Technology (BTECH), Aarhus University, which supported the work.";
        Assert.Equal(own, PaperDeclarations.Funding(own));
    }
}
