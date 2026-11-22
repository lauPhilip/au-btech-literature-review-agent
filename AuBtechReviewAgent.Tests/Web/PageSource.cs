using System.Text.RegularExpressions;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A page split into a shell, child components and partial code files, read back as one text for tests that read
/// source. Each &lt;{Page}X Page="this" /&gt; tag is replaced by that component's markup, the page's code files
/// ({Page}.razor.cs and {Page}.*.cs) are appended, and "Page." and "{Page}." are taken out, so a test sees the
/// markup and code as if they were still one file.
/// </summary>
internal static class PageSource
{
    public static string? Read(string page)
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        if (root == null) return null; // not run from the repository (packaged tests)

        string pages = Path.Join(root, "AuBtechReviewAgent", "Components", "Pages");
        string text = File.ReadAllText(Path.Join(pages, page + ".razor"));
        text = Regex.Replace(text, "<(" + page + @"[A-Z]\w*) Page=""this"" />", m => File.ReadAllText(Path.Join(pages, m.Groups[1].Value + ".razor")));
        var code = Directory.GetFiles(pages, page + ".*.cs").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText);
        text = string.Join("\n", code.Prepend(text));
        return text.Replace("Page.", "").Replace(page + ".", "");
    }
}
