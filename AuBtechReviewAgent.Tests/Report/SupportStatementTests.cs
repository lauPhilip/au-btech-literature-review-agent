using System.Runtime.CompilerServices;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The support statement printed under Support and Funding comes from Report:SupportStatement in appsettings.json
/// (issue #114). It once named the wrong department; this keeps the right one in place while it is set. Without it,
/// the paper asks the authors for their funding and never prints advice on configuration (the publisher audit, batch A).
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
    public void TheSupportStatementNamesTheRightDepartment()
    {
        string statement = Statement();
        if (statement.Length == 0) return; // not set: every paper gets the placeholder above
        Assert.Contains("Department of Business Development and Technology (BTECH), Aarhus University", statement);
        Assert.DoesNotContain("Engineering and Technology", statement);
        Assert.DoesNotContain("live stream", statement);
    }
}
