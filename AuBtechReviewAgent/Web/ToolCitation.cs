using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// How to cite the tool itself (issue #619), for the "Cite this tool" block on About. The data is that of CITATION.cff:
/// the authors, the title, the version and the concept DOI, which is held once in <see cref="SiteSeo.ConceptDoi"/>.
/// Both texts are built here, in code, and never by a model. BibTeX values are escaped as the gallery's citations are.
/// </summary>
/// <param name="Version">The version number without the commit, as <see cref="ToolVersion.Number"/> gives it; empty when unknown.</param>
/// <param name="Year">The year of this version.</param>
public sealed record ToolCitation(string Version, int Year)
{
    public const string Title = "TraceableAI: an open-source agentic framework for PRISMA-compliant literature synthesis";

    public static string DoiUrl => "https://doi.org/" + SiteSeo.ConceptDoi;

    /// <summary>The citation of the running build.</summary>
    public static ToolCitation ForThisBuild() =>
        new(ToolVersion.Number(ToolVersion.Current), SiteSeo.BuildDate()?.Year ?? System.DateTime.UtcNow.Year);

    private string VersionText => Version.Length == 0 ? "" : $" (Version {Version})";

    /// <summary>The APA 7 reference in parts, so the page can set the title in italics: before the title, the title, after it.</summary>
    public (string Before, string Title, string After) ApaParts() =>
        ($"Lau, P. S. P. Ø. O., & Nidhi. ({Year.ToString(CultureInfo.InvariantCulture)}). ", Title,
         $"{VersionText} [Computer software]. {SiteSeo.Organisation}. {DoiUrl}");

    public string Apa()
    {
        var (before, title, after) = ApaParts();
        return before + title + after;
    }

    /// <summary>A BibTeX @software entry with the DOI and the repository address.</summary>
    public string BibTex() => string.Join("\n", new[]
    {
        "@software{traceableai,",
        $"  author       = {{Lau, Philip S. P. {{\\O}}. O. and Nidhi}},",
        $"  title        = {{{{{GalleryCitation.TeX(Title)}}}}},",
        $"  year         = {{{Year.ToString(CultureInfo.InvariantCulture)}}},",
        Version.Length == 0 ? null : $"  version      = {{{GalleryCitation.TeX(Version)}}},",
        $"  organization = {{{GalleryCitation.TeX(SiteSeo.Organisation)}}},",
        $"  doi          = {{{SiteSeo.ConceptDoi}}},",
        $"  url          = {{{DoiUrl}}},",
        $"  note         = {{Source code: {GalleryCitation.TeX(SiteSeo.RepositoryUrl)}}}",
        "}",
    }.Where(line => line != null));
}
