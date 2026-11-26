using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>One paragraph of an abstract: its heading ("Background", "Methods", ...) when the abstract is structured, and its text.</summary>
public sealed record AbstractPart(string? Heading, string Text);

/// <summary>
/// Splits an abstract into the paragraphs the front page shows (Components/Paper/ArticleFront.razor). A structured
/// abstract, with at least two of the usual headings and one at the very start, gives one part per heading, the
/// heading without its colon or full stop. Any other abstract keeps its own paragraphs, without headings. Blank lines
/// and spaces at the ends are dropped, so no paragraph is empty and the gaps between them are all the same.
/// </summary>
public static partial class AbstractParts
{
    /// <summary>The headings of a structured abstract, in the forms PRISMA 2020 (item 2) and the journals use.</summary>
    [GeneratedRegex(@"(?:^|(?<=[.!?]\s+)|(?<=\n))\s*(Background|Objectives?|Aims?|Purpose|Methods|Design|Results|Findings|Limitations|Interpretation|Conclusions?|Discussion|Implications|Funding|Registration)\s*[:.]\s+",
        RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    public static IReadOnlyList<AbstractPart> Split(string? text)
    {
        string body = (text ?? "").Replace("\r\n", "\n").Trim();
        if (body.Length == 0) return Array.Empty<AbstractPart>();

        var headings = Heading().Matches(body);
        if (headings.Count >= 2 && headings[0].Index == 0)
        {
            var parts = new List<AbstractPart>();
            for (int i = 0; i < headings.Count; i++)
            {
                var m = headings[i];
                int start = m.Index + m.Length;
                int end = i + 1 < headings.Count ? headings[i + 1].Index : body.Length;
                string part = Clean(body[start..end]);
                if (part.Length > 0) parts.Add(new AbstractPart(m.Groups[1].Value, part));
            }
            return parts;
        }

        return BlankLine().Split(body).Select(Clean).Where(p => p.Length > 0).Select(p => new AbstractPart(null, p)).ToList();
    }

    private static string Clean(string text) => Space().Replace(text, " ").Trim();
}
