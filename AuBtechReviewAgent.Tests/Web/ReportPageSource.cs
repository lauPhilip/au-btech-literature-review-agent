using System.Text.RegularExpressions;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The report page's source as one text, for tests that read it: SpecMatrix.razor with each section component
/// (&lt;SpecMatrixX Page="this" /&gt;) put back where its tag stands, followed by the page's code-behind files. The sections
/// read the page's state through "Page." (and its static helpers through "SpecMatrix."), which is taken out, so a test
/// sees the markup as the page renders it.
/// </summary>
internal static class ReportPageSource
{
    public static string? Read()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return null; // not run from the repository (packaged tests)

        string pages = Path.Join(root, "AuBtechReviewAgent", "Components", "Pages");
        string page = File.ReadAllText(Path.Join(pages, "SpecMatrix.razor"));
        page = Regex.Replace(page, @"<(SpecMatrix[A-Z]\w*) Page=""this"" />", m => File.ReadAllText(Path.Join(pages, m.Groups[1].Value + ".razor")));
        foreach (string code in new[] { "SpecMatrix.razor.cs", "SpecMatrix.Citations.cs" })
        {
            string path = Path.Join(pages, code);
            if (File.Exists(path)) page += "\n" + File.ReadAllText(path);
        }
        return page.Replace("Page.", "").Replace("SpecMatrix.", "");
    }
}
