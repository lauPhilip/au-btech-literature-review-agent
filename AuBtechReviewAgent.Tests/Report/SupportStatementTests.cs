using System.Runtime.CompilerServices;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The support statement printed under Support and Funding comes from Report:SupportStatement in appsettings.json
/// (issue #114). It once named the wrong department; this keeps the right one in place.
/// </summary>
public class SupportStatementTests
{
    private static string AppSettingsPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here)!, "..", "..", "AuBtechReviewAgent", "appsettings.json"));

    private static string Statement()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.GetProperty("Report").GetProperty("SupportStatement").GetString() ?? "";
    }

    [Fact]
    public void TheSupportStatementNamesTheRightDepartment()
    {
        string statement = Statement();
        Assert.Contains("Department of Business Development and Technology (BTECH), Aarhus University", statement);
        Assert.DoesNotContain("Engineering and Technology", statement);
        Assert.DoesNotContain("live stream", statement);
    }
}
