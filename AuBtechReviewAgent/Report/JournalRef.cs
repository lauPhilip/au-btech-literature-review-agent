using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>A venue string split into its parts: the name, volume, issue, pages or article number, and year.</summary>
public sealed record JournalRefParts(string Name, string? Volume, string? Issue, string? Pages, string? ArticleNumber, int Year);

/// <summary>
/// Reads an arXiv journal reference such as "Auton. Agents Multi Agent Syst. 38(1): 4 (2024)" (issue #566). arXiv gives
/// it as one free string, which used to go into the reference whole, as an italic venue with the volume, issue, article
/// number and year inside it. Only two common shapes are read; anything else is left as it is.
/// </summary>
public static class JournalRef
{
    // "Name 38(1): 4 (2024)", "Name 12, 100-110, 2023", "Name 7: e12 (2022)".
    private static readonly Regex Full = new(
        @"^(?<name>.*?\D)[,\s]+(?<vol>\d+)\s*(?:\((?<iss>[^()]{1,12})\))?\s*[:,]\s*(?:pp?\.\s*)?(?<loc>[A-Za-z]?\d+(?:\s*[-‐-—]+\s*[A-Za-z]?\d+)?)\s*(?:,\s*|\(\s*)(?<year>(?:19|20)\d{2})\s*\)?\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "Name (2024)": only a year in brackets is taken off, so a conference named "AAMAS 2023" keeps its name.
    private static readonly Regex NameAndYear = new(@"^(?<name>.*\S)\s*\((?<year>(?:19|20)\d{2})\)\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The parts of <paramref name="venue"/>, or null when it has neither shape.</summary>
    public static JournalRefParts? Parse(string? venue)
    {
        string v = (venue ?? "").Trim();
        var m = Full.Match(v);
        if (m.Success)
        {
            string name = m.Groups["name"].Value.Trim().TrimEnd(',', ':').Trim();
            if (name.Length == 0) return null;
            string loc = m.Groups["loc"].Value;
            bool range = Regex.IsMatch(loc, @"\d\s*[-‐-—]");
            return new JournalRefParts(name, m.Groups["vol"].Value, m.Groups["iss"].Success ? m.Groups["iss"].Value.Trim() : null,
                range ? ReferenceDetails.PageRange(loc) : null, range ? null : loc, int.Parse(m.Groups["year"].Value));
        }
        var y = NameAndYear.Match(v);
        return y.Success ? new JournalRefParts(y.Groups["name"].Value.Trim().TrimEnd(','), null, null, null, null, int.Parse(y.Groups["year"].Value)) : null;
    }
}
