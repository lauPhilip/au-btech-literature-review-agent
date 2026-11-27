using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// /llms.txt (issue #610): what the site is, and which pages matter in which order, for answer engines and agents
/// that look for the convention. It is built in code, like robots.txt, so the addresses use Site:PublicUrl and the
/// API and MCP lines appear only while the API is switched on (ApiSwitch): with it off those addresses answer 404.
/// </summary>
public static class LlmsTxt
{
    public static string Build(string baseUrl, bool apiEnabled)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(SiteSeo.SiteName).Append("\n\n");
        sb.Append("> ").Append(SiteSeo.DefaultDescription).Append("\n\n");
        sb.Append("The pages below matter in this order. Cite the tool by its DOI. A run's own pages (/review/{id}) hold a visitor's research question and are not for indexing.\n\n");
        sb.Append("## Pages\n\n");
        Link(sb, baseUrl, "Start page", "/", "what the tool is and how a review runs");
        Link(sb, baseUrl, "Start a review", "/start", "the kinds of review and their cards");
        Link(sb, baseUrl, "Glossary", "/glossary", "the terms the tool uses, in plain English");
        Link(sb, baseUrl, "About", "/about", "who built it, the paper, and how to cite the tool");
        if (apiEnabled)
        {
            sb.Append("\n## For programs\n\n");
            Link(sb, baseUrl, "API and MCP documentation", ApiSwitch.DocsPath, "start a review and follow it from another tool");
            Link(sb, baseUrl, "OpenAPI description", ApiEndpoints.OpenApiPath, "machine-readable description of the REST API");
            Link(sb, baseUrl, "MCP server", "/mcp", "the same functions as MCP tools");
        }
        sb.Append("\n## Source and citation\n\n");
        Link(sb, baseUrl, "Developer wiki", WikiDocs.BasePath, "how the code fits together, page by page; the canonical address to cite");
        sb.Append("- [Repository](").Append(SiteSeo.RepositoryUrl).Append("): the open-source code and the developer wiki\n");
        sb.Append("- [DOI](https://doi.org/").Append(SiteSeo.ConceptDoi).Append("): the concept DOI on Zenodo, which always resolves to the newest version\n");
        return sb.ToString();
    }

    private static void Link(StringBuilder sb, string baseUrl, string title, string path, string note) =>
        sb.Append("- [").Append(title).Append("](").Append(SiteSeo.Absolute(baseUrl, path)).Append("): ").Append(note).Append('\n');
}
