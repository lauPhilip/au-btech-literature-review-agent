using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>Something the clean-up took out of, or changed in, a section's text.</summary>
public record FormattingChange(string Field, string Kind, string Text);

/// <summary>
/// Keeps the report's prose plain. The writing prompts ask for paragraphs only, but models still write
/// Markdown: "### Technical Challenges" headings, bullet lists and pipe tables, which the report page and
/// main.tex would show as raw symbols. This turns them back into prose:
///  - heading lines are removed (the paragraph that follows stays);
///  - list items become sentences of one paragraph, so their text and citations are kept and checked;
///  - tables are taken out of the prose (a table belongs in the artifact) and logged, never silently lost;
///  - emphasis, inline code and links lose their markup but keep their words.
/// It runs before the citation check, so the checked sentences are exactly the ones shown.
/// </summary>
public static class ProseCleaner
{
    /// <summary>The instruction every prose-writing prompt carries.</summary>
    public const string PlainProseRule =
        "Write plain paragraphs separated by a blank line: no Markdown, no headings, no bullet or numbered lists, no tables and no bold or italics.";

    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s{0,6}(?:[-*+•]|\d{1,2}[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.Compiled);

    public static (string Text, List<FormattingChange> Changes) Clean(string? text, string field)
    {
        var changes = new List<FormattingChange>();
        if (string.IsNullOrWhiteSpace(text)) return (text ?? "", changes);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraphs = new List<string>();
        var current = new List<string>();
        var list = new List<string>();
        void FlushList()
        {
            if (list.Count == 0) return;
            current.Add(string.Join(" ", list.Select(EndAsSentence)));
            list.Clear();
        }
        void FlushParagraph()
        {
            FlushList();
            if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
            current.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) { FlushParagraph(); continue; }

            // A pipe table: a row followed by a |---|---| separator, plus all rows after it. A caption line
            // right before it ("Table 1. ...") goes with it.
            if (line.Count(c => c == '|') >= 2 && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]))
            {
                FlushList();
                var table = new List<string>();
                if (current.Count > 0 && Regex.IsMatch(current[^1], @"^Table\s+\d+", RegexOptions.IgnoreCase))
                {
                    table.Add(current[^1]);
                    current.RemoveAt(current.Count - 1);
                }
                int j = i;
                while (j < lines.Length && lines[j].Count(c => c == '|') >= 2) table.Add(lines[j++].Trim());
                changes.Add(new FormattingChange(field, "table removed", string.Join("\n", table)));
                i = j - 1;
                FlushParagraph();
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                changes.Add(new FormattingChange(field, "heading removed", heading.Groups[1].Value));
                continue;
            }

            var item = ListItem.Match(line);
            if (item.Success)
            {
                if (list.Count == 0) changes.Add(new FormattingChange(field, "list turned into prose", line));
                list.Add(item.Groups[1].Value.Trim());
                continue;
            }

            FlushList();
            current.Add(line);
        }
        FlushParagraph();

        string result = string.Join("\n\n", paragraphs.Where(p => p.Length > 0));
        result = Regex.Replace(result, @"`([^`\n]+)`", "$1");                       // inline code
        result = Regex.Replace(result, @"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", "$1");  // links, not [n] markers
        result = Regex.Replace(result, @"(?<=\s|^)#{1,6}\s+", "");                     // a heading marker left inside a line
        result = MethodsSectionWriter.StripMarkdownEmphasis(result);
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        return (result.Trim(), changes);
    }

    private static string EndAsSentence(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return s;
        // "Technical: Prioritise ..." stays as it is; a fragment without an end mark gets a full stop.
        return Regex.IsMatch(s, @"[.!?]$|[.!?]\s*\[[\d,\s–-]+\]$") ? s : s + ".";
    }
}
