using System;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// "Cite this report": an APA 7 reference and a BibTeX entry for one version of a gallery report, cited as the output
/// of a piece of software. The author is the tool as the printed front page names it (ArticleInfo.Author), never a
/// person; the date is the day the run finished; the address is the version's own, which never changes when newer
/// versions are added; and the note says which TraceableAI build and run made it and that it is not peer reviewed.
/// Every value comes from the entry's stored figures.
/// </summary>
public static class GalleryCitation
{
    /// <summary>The version's lasting address on this site.</summary>
    public static string Url(string baseUrl, GalleryEntry e) => SiteSeo.Absolute(baseUrl, GalleryCard.VersionPath(e.Id, e.Version));

    /// <summary>
    /// The address a citation gives: the DOI's landing page (https://doi.org/...) when the version has a valid DOI, which
    /// resolves to wherever the deposit lives, and otherwise the version's own address on this site.
    /// </summary>
    public static string Address(GalleryEntry e, string baseUrl) => GalleryDoi.Landing(e.Doi) ?? Url(baseUrl, e);

    /// <summary>What the bracket after the title says the report is, with the tool's version and the run.</summary>
    public static string Description(GalleryEntry e) =>
        $"AI-generated {GalleryCard.TypeLabel(e.Card).ToLowerInvariant()}, TraceableAI {ToolVersion.Display(e.ToolVersion)}, run {e.RunId:D}; not peer reviewed";

    /// <summary>
    /// The APA 7 reference in parts, so a page can set the title in italics: before the title, the title, and after it.
    /// "TraceableAI – artificial researcher. (2026, November 24). | Title | (Version 2) [AI-generated ...]. Publisher. URL"
    /// </summary>
    public static (string Before, string Title, string After) ApaParts(GalleryEntry e, string baseUrl)
    {
        string date = e.CompletedUtc.ToString("yyyy, MMMM d", CultureInfo.InvariantCulture);
        string title = e.Title.Trim().TrimEnd('.');
        return ($"{ArticleInfo.Author}. ({date}). ", title,
            $" (Version {e.Version}) [{Description(e)}]. {SiteSeo.Organisation}. {Address(e, baseUrl)}");
    }

    /// <summary>The APA 7 reference as plain text.</summary>
    public static string Apa(GalleryEntry e, string baseUrl)
    {
        var (before, title, after) = ApaParts(e, baseUrl);
        return before + title + after;
    }

    /// <summary>A BibTeX @misc entry; the title is escaped for LaTeX, and the author is braced so it is not read as a person's name.</summary>
    public static string BibTex(GalleryEntry e, string baseUrl)
    {
        string month = e.CompletedUtc.ToString("MMM", CultureInfo.InvariantCulture).ToLowerInvariant();
        string url = Address(e, baseUrl);
        var doi = GalleryDoi.Normalize(e.Doi);
        return string.Join("\n", new string?[]
        {
            $"@misc{{traceableai_{e.ShortRunId},",
            $"  author       = {{{{{TeX(ArticleInfo.Author.Replace("–", "--"))}}}}},",
            $"  title        = {{{{{TeX(e.Title.Trim().TrimEnd('.'))}}}}},",
            $"  year         = {{{e.CompletedUtc.Year.ToString(CultureInfo.InvariantCulture)}}},",
            $"  month        = {month},",
            $"  howpublished = {{\\url{{{url}}}}},",
            doi == null ? null : $"  doi          = {{{doi}}},",
            $"  organization = {{{TeX(SiteSeo.Organisation)}}},",
            $"  note         = {{{TeX(Description(e))}. Version {e.Version} in the TraceableAI gallery}},",
            "}",
        }.OfType<string>());
    }

    /// <summary>
    /// Escapes text for a BibTeX field: every character LaTeX reads as a command is written as text, so a title cannot
    /// close the field or the entry, or run a command.
    /// </summary>
    public static string TeX(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '\\' => @"\textbackslash{}",
                '{' or '}' or '&' or '%' or '$' or '#' or '_' => "\\" + c,
                '~' => @"\textasciitilde{}",
                '^' => @"\textasciicircum{}",
                _ when char.IsControl(c) => " ",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
