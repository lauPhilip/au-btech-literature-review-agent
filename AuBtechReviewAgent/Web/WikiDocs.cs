using System.Reflection;
using System.Text;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AuBtechReviewAgent;

/// <summary>One page of the developer wiki as the site serves it (issue #613).</summary>
/// <param name="Slug">The file name without ".md"; empty for README.md, which is the wiki's front page.</param>
/// <param name="Sections">The page's second-level headings (id, text), for the side list.</param>
public sealed record WikiPage(string Slug, string FileName, string Title, string Description, string Html,
    IReadOnlyList<(string Id, string Label)> Sections);

/// <summary>
/// The developer wiki on the site's own address, under /docs (issue #613). docs/wiki/ is the only source: its files
/// are built into the app as embedded resources, so the site and GitHub's Wiki tab (wiki.yml) always show the same
/// text. Raw HTML in the Markdown is shown as text, links to other wiki pages stay on the site, links to other files
/// in the repository go to GitHub, and any other address that is not http(s) is dropped, keeping its words. Pictures
/// become links, because the Content-Security-Policy allows no images from other sites.
/// </summary>
public static class WikiDocs
{
    public const string BasePath = "/docs";
    private const string ResourcePrefix = "wiki/";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .DisableHtml()
        .Build();

    private static readonly Lazy<IReadOnlyList<WikiPage>> All = new(Load);

    /// <summary>The front page first, then the numbered pages in order.</summary>
    public static IReadOnlyList<WikiPage> Pages => All.Value;

    /// <summary>The page with this slug; null for an unknown one. Null or empty is the front page.</summary>
    public static WikiPage? Find(string? slug) =>
        Pages.FirstOrDefault(p => string.Equals(p.Slug, slug ?? "", StringComparison.OrdinalIgnoreCase));

    public static string PathOf(WikiPage page) => page.Slug.Length == 0 ? BasePath : $"{BasePath}/{page.Slug}";

    /// <summary>The page's Markdown file on GitHub, where it is edited.</summary>
    public static string SourceUrl(WikiPage page) => $"{SiteSeo.RepositoryUrl}/blob/master/docs/wiki/{page.FileName}";

    private static IReadOnlyList<WikiPage> Load()
    {
        var assembly = typeof(WikiDocs).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(n => n[ResourcePrefix.Length..])
            .OrderBy(n => n.Equals("README.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.Ordinal)
            .ToList();
        return files.Select(f => Render(f, Read(assembly, ResourcePrefix + f), files)).ToList();
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The slug of a wiki file: its name without ".md", and empty for README.md.</summary>
    public static string SlugOf(string fileName) =>
        fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase) ? "" : Path.GetFileNameWithoutExtension(fileName);

    /// <summary>Renders one wiki file; <paramref name="wikiFiles"/> are the file names its links may point to.</summary>
    public static WikiPage Render(string fileName, string markdown, IReadOnlyCollection<string> wikiFiles)
    {
        string slug = SlugOf(fileName);
        var doc = Markdown.Parse(markdown, Pipeline);

        // The page's own header shows the title, so the first-level heading is taken out of the body.
        var h1 = doc.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1);
        string title = h1 != null ? PlainText(h1.Inline) : slug;
        h1?.Parent?.Remove(h1);

        var sections = doc.Descendants<HeadingBlock>()
            .Where(h => h.Level == 2 && h.TryGetAttributes()?.Id is { Length: > 0 })
            .Select(h => (h.TryGetAttributes()!.Id!, PlainText(h.Inline)))
            .ToList();

        string description = Shorten(doc.Descendants<ParagraphBlock>().Select(p => PlainText(p.Inline)).FirstOrDefault(t => t.Length > 0) ?? title);

        foreach (var link in doc.Descendants<LinkInline>().ToList())
        {
            link.IsImage = false;
            string? url = Rewrite(link.Url, slug, wikiFiles);
            if (url == null) link.ReplaceBy(new LiteralInline(PlainText(link)), false);
            else link.Url = url;
        }
        foreach (var auto in doc.Descendants<AutolinkInline>().ToList())
        {
            if (!IsWebAddress(auto.Url)) auto.ReplaceBy(new LiteralInline(auto.Url), false);
        }
        // A code block or table that scrolls sideways must take keyboard focus (axe: scrollable-region-focusable).
        foreach (var block in doc.Descendants<CodeBlock>()) block.GetAttributes().AddProperty("tabindex", "0");
        foreach (var table in doc.Descendants<Table>()) table.GetAttributes().AddProperty("tabindex", "0");

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(doc);
        writer.Flush();
        return new WikiPage(slug, fileName, title, description, writer.ToString(), sections);
    }

    /// <summary>
    /// Where a link in a wiki page should go: an anchor stays on this page, another wiki page stays on the site,
    /// another file in the repository goes to GitHub, an http(s) address is kept, and anything else is dropped (null).
    /// The anchors carry the page's path because the site's pages have &lt;base href="/"&gt;.
    /// </summary>
    public static string? Rewrite(string? url, string slug, IReadOnlyCollection<string> wikiFiles)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        string here = slug.Length == 0 ? BasePath : $"{BasePath}/{slug}";
        if (url.StartsWith('#')) return here + url;
        if (IsWebAddress(url)) return url;
        if (url.Contains(':', StringComparison.Ordinal) || url.StartsWith('/') || url.StartsWith('\\')) return null;

        int hash = url.IndexOf('#', StringComparison.Ordinal);
        string path = hash < 0 ? url : url[..hash];
        string fragment = hash < 0 ? "" : url[hash..];
        if (path.Length == 0) return here + fragment;

        string? wikiFile = wikiFiles.FirstOrDefault(f => f.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (wikiFile != null)
        {
            string target = SlugOf(wikiFile);
            return (target.Length == 0 ? BasePath : $"{BasePath}/{target}") + fragment;
        }

        // Another file in the repository, relative to docs/wiki/.
        var parts = new List<string> { "docs", "wiki" };
        foreach (string part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(Uri.EscapeDataString(Uri.UnescapeDataString(part)));
        }
        return parts.Count == 0 ? null : $"{SiteSeo.RepositoryUrl}/blob/master/{string.Join('/', parts)}{fragment}";
    }

    private static bool IsWebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string PlainText(Inline? inline)
    {
        if (inline == null) return "";
        var sb = new StringBuilder();
        void Walk(Inline node)
        {
            switch (node)
            {
                case LiteralInline literal: sb.Append(literal.Content.AsSpan()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case AutolinkInline auto: sb.Append(auto.Url); break;
                case LineBreakInline: sb.Append(' '); break;
                case ContainerInline container:
                    foreach (var child in container) Walk(child);
                    break;
            }
        }
        Walk(inline);
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The first paragraph as a meta description: at most 160 characters, cut at a word.</summary>
    private static string Shorten(string text)
    {
        const int max = 160;
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > 80 ? cut : max - 1)].TrimEnd(',', ';', ':', '.') + "…";
    }
}
