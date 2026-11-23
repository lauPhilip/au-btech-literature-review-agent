using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The readable text of a web page, as kept in its snapshot (decision 2): the page's main content when it marks
/// one (&lt;main&gt;, else the longest &lt;article&gt;), else the body without navigation, headers, footers, sidebars and
/// forms. Paragraphs stay on their own lines, so a quote can be found again. The same HTML always gives the same
/// text, so the SHA-256 of a snapshot only changes when the page does. Text a reader never sees is taken out first
/// (#369, <see cref="HiddenText"/>): hidden elements and invisible characters, with comments, scripts and the like.
/// </summary>
public static class PageText
{
    /// <summary>Snapshots longer than this are cut: a source the review quotes is never this long.</summary>
    public const int MaxCharacters = 1_000_000;

    private const RegexOptions Options = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static string TitleOf(string html)
    {
        var m = Regex.Match(html, @"<title\b[^>]*>(.*?)</title>", Options, Timeout);
        return m.Success ? Clean(HiddenText.RemoveInvisible(WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "", Options, Timeout))).Text) : "";
    }

    public static string FromHtml(string html) => Read(html).Text;

    /// <summary>
    /// The page's readable text, with what was taken out because a reader never sees it. The text of comments,
    /// noscript and template blocks was never kept; it is returned with the hidden text for the scan, but not counted.
    /// </summary>
    public static CleanedText Read(string html)
    {
        var hidden = new System.Text.StringBuilder();
        foreach (Match m in Regex.Matches(html, @"<!--(.*?)-->|<(noscript|template)\b[^>]*>(.*?)</\2\s*>", Options, Timeout))
            if (HiddenText.TextOf(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value) is { Length: > 0 } t) hidden.Append(t).Append('\n');

        // Never part of the text: comments, scripts, styles and embedded things; then what the page hides.
        string page = Regex.Replace(html, @"<!--.*?-->", " ", Options, Timeout);
        page = Regex.Replace(page, @"<(script|style|noscript|template|svg|iframe|object|canvas)\b[^>]*>.*?</\1\s*>", " ", Options, Timeout);
        var dropped = HiddenText.DropHiddenHtml(page);
        page = dropped.Text;
        hidden.Append(dropped.Hidden);

        string content;
        var main = Regex.Match(page, @"<main\b[^>]*>(.*)</main\s*>", Options, Timeout);
        if (main.Success) content = main.Groups[1].Value;
        else
        {
            var articles = Regex.Matches(page, @"<article\b[^>]*>(.*?)</article\s*>", Options, Timeout);
            if (articles.Count > 0) content = articles.Select(a => a.Groups[1].Value).OrderByDescending(a => a.Length).First();
            else
            {
                var body = Regex.Match(page, @"<body\b[^>]*>(.*)</body\s*>", Options, Timeout);
                content = body.Success ? body.Groups[1].Value : page;
                content = Regex.Replace(content, @"<(header|footer)\b[^>]*>.*?</\1\s*>", " ", Options, Timeout);
            }
        }
        content = Regex.Replace(content, @"<(nav|aside|form|button|select|dialog)\b[^>]*>.*?</\1\s*>", " ", Options, Timeout);

        // Block elements end a line; inline ones disappear without a gap; then the entities are decoded.
        content = Regex.Replace(content, @"<(br|hr)\b[^>]*>", "\n", Options, Timeout);
        content = Regex.Replace(content, @"</?(p|div|section|article|li|ul|ol|dl|dt|dd|tr|table|thead|tbody|h[1-6]|blockquote|pre|figure|figcaption|header|footer|main)\b[^>]*>", "\n", Options, Timeout);
        content = Regex.Replace(content, @"</?(td|th)\b[^>]*>", " ", Options, Timeout);
        content = Regex.Replace(content, "<[^>]+>", "", Options, Timeout);
        var clean = HiddenText.RemoveInvisible(WebUtility.HtmlDecode(content));
        return new CleanedText(Lines(clean.Text), clean.InvisibleCharacters, dropped.HiddenElements, hidden.Append(clean.Hidden).ToString());
    }

    /// <summary>Plain text (a .txt page or a PDF's text) in the same line form as an HTML page's.</summary>
    public static string FromPlainText(string text) => ReadPlain(text).Text;

    /// <summary>Plain text in line form, with its invisible characters taken out and counted.</summary>
    public static CleanedText ReadPlain(string text)
    {
        var clean = HiddenText.RemoveInvisible(text);
        return clean with { Text = Lines(clean.Text) };
    }

    private static string Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(Clean)
            .Where(l => l.Length > 0);
        string joined = string.Join("\n", lines);
        return joined.Length <= MaxCharacters ? joined : joined[..MaxCharacters];
    }

    private static string Clean(string line) => Regex.Replace(line.Replace(' ', ' '), @"\s+", " ", RegexOptions.None, Timeout).Trim();
}
